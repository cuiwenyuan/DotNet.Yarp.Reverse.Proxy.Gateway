using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 网关自研组件的注册与挂载扩展。仅使用 BCL / NewLife.Core / ASP.NET Core 框架自带能力，
/// 不引用任何第三方 NuGet。
/// </summary>
public static class GatewayExtensions
{
    /// <summary>把 "Gateway" 配置节点绑定为各个选项单例，供中间件通过 DI 注入。</summary>
    public static IServiceCollection AddGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        services.AddSingleton(options);
        services.AddSingleton(options.RateLimit);
        services.AddSingleton(options.Cors);
        services.AddSingleton(options.Auth);
        services.AddSingleton(options.Cache);
        services.AddSingleton(options.Security);
        services.AddSingleton(options.LocationRewrite);
        services.AddSingleton(options.ProxyLogging);
        services.AddSingleton(options.IpFilter);
        services.AddSingleton(options.RequestSize);
        services.AddSingleton(options.Retry);
        services.AddSingleton(options.CircuitBreaker);
        services.AddSingleton(options.SecurityHeaders);
        services.AddSingleton(options.Metrics);
        services.AddSingleton(options.HttpTimeout);
        services.AddSingleton(options.Admin);
        services.AddSingleton(options.NginxAccessLog);
        services.AddSingleton(options.CorrelationId);
        services.AddSingleton(options.ConcurrencyLimit);
        services.AddSingleton<NginxAccessLogWriter>();
        services.AddSingleton<SimpleMemoryCache>();
        services.AddSingleton<GatewayMetrics>();

        // 有派生状态的组件：限流器令牌桶、IP 规则集。热重载时会经 ApplyOptions 原地重建。
        services.AddSingleton(new RateLimitState(options.RateLimit));
        services.AddSingleton(new IpFilterState(options.IpFilter));
        services.AddSingleton(new ConcurrencyLimitState(options.ConcurrencyLimit));
        services.AddSingleton<GatewayConfigReloader>();
        return services;
    }

    /// <summary>
    /// 注册基于 NewLife.Core 的配置热重载器。由 Web 层在 AddGateway 之后调用，
    /// 传入 appsettings.json 路径与校验委托（复用 --test-config 的 ConfigValidator）。
    /// 从 DI 解析已注册的 GatewayOptions / RateLimitState / IpFilterState 单例，保证原地更新同一对象。
    /// </summary>
    public static IServiceCollection AddGatewayHotReload(
        this IServiceCollection services,
        string configPath,
        Func<string, (bool ok, string? error)> validate)
    {
        services.AddSingleton(sp =>
        {
        var options = sp.GetRequiredService<GatewayOptions>();
        var rateLimitState = sp.GetRequiredService<RateLimitState>();
        var ipFilterState = sp.GetRequiredService<IpFilterState>();
        var concurrencyState = sp.GetRequiredService<ConcurrencyLimitState>();
        return new GatewayHotReload(configPath, options, rateLimitState, ipFilterState, concurrencyState, validate);
        });
        return services;
    }

    /// <summary>
    /// 失败重试中间件（独立于治理管线挂载，包装其后所有中间件 + YARP 转发）：
    /// 对幂等请求在连接失败 / 命中可重试状态码时，重置响应并重新执行下游管线（换节点重试）。
    /// 应在 ProxyLogging 之后、UseGatewayGovernance 之前调用。
    /// </summary>
    public static IApplicationBuilder UseGatewayRetry(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RetryMiddleware>();
    }

    /// <summary>
    /// 监控指标中间件：应作为最外层挂载（UseForwardedHeaders 之后、压缩/日志之前），
    /// 使 /metrics 与 /health 短路返回、绕过治理与限流，并统计全部请求。
    /// </summary>
    public static IApplicationBuilder UseGatewayMetrics(this IApplicationBuilder app)
    {
        return app.UseMiddleware<MetricsMiddleware>();
    }

    /// <summary>
    /// 轻量管理端点中间件：/admin/status 返回运行状态 JSON 摘要（版本/启动时间/内存/指标快照）。
    /// 挂载在 Metrics 之后，绕过治理与限流。
    /// </summary>
    public static IApplicationBuilder UseGatewayAdmin(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AdminStatusMiddleware>();
    }

    /// <summary>
    /// nginx 兼容访问日志中间件（combined/common 格式）。应挂载在 ForwardedHeaders 之后、
    /// 治理/压缩之外层，以还原真实客户端 IP 并记录全部请求。默认关闭，由配置开关控制。
    /// </summary>
    public static IApplicationBuilder UseGatewayAccessLog(this IApplicationBuilder app)
    {
        return app.UseMiddleware<NginxAccessLogMiddleware>();
    }

    /// <summary>
    /// 请求链路 ID 中间件：生成/透传 X-Request-ID，写入 Items/AsyncLocal/响应头，
    /// 贯穿 proxy log、access log 与 XTrace。应挂在管线最外层（其他日志与治理之前），
    /// 确保所有下游组件都能读到 ID。
    /// </summary>
    public static IApplicationBuilder UseGatewayCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }

    /// <summary>
    /// nginx.conf 转换工具：把 nginx 配置转换为 YARP ReverseProxy JSON 片段。
    /// 供 Web 宿主在 --convert-nginx 命令行模式下调用（纯 BCL，不依赖 YARP）。
    /// </summary>
    public static NginxConversionResult ConvertNginxConfig(string nginxConfText)
    {
        return NginxConfigConverter.Convert(nginxConfText);
    }

    /// <summary>
    /// 入口统一治理中间件顺序（最外层到最内层）：
    ///   AspxBlocker(扩展名黑名单) -> SecurityHeaders(安全响应头) -> IpFilter(IP黑白名单)
    ///   -> RequestSize(请求体大小限制) -> CORS -> RateLimit(限流) -> ConcurrencyLimit(并发上限)
    ///   -> CircuitBreaker(熔断) -> ApiKeyAuth(API Key) -> JwtAuth(JWT Bearer)
    ///   -> ResponseCache(响应缓存) -> LocationRewrite(重定向改写)
    /// 说明：安全头/黑名单/体积限制尽早拦截；先限速再限并发最后熔断；认证支持 Key 或 JWT 任一通过；
    /// ResponseCache 在认证之后缓存已授权响应的幂等结果；LocationRewrite 放最内层，
    /// 在 YARP 产生 3xx 响应后改写 Location。
    /// </summary>
    public static IApplicationBuilder UseGatewayGovernance(this IApplicationBuilder app)
    {
        return app
            .UseMiddleware<AspxBlockerMiddleware>()
            .UseMiddleware<SecurityHeadersMiddleware>()
            .UseMiddleware<IpFilterMiddleware>()
            .UseMiddleware<RequestSizeLimitMiddleware>()
            .UseMiddleware<CorsMiddleware>()
            .UseMiddleware<RateLimitMiddleware>()
            .UseMiddleware<ConcurrencyLimitMiddleware>()
            .UseMiddleware<CircuitBreakerMiddleware>()
            .UseMiddleware<ApiKeyAuthMiddleware>()
            .UseMiddleware<JwtAuthMiddleware>()
            .UseMiddleware<ResponseCacheMiddleware>()
            .UseMiddleware<LocationRewriteMiddleware>();
    }
}
