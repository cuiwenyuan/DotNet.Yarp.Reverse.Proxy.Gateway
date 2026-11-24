using System.Net;
using DotNet.Yarp.Reverse.Proxy.Gateway.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.LoadBalancing;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Hosting;

/// <summary>
/// 网关应用层的唯一组装入口：一份服务注册 + 一份中间件顺序，供 Web（IIS/ANCM）与
/// Service（Windows 服务）两个宿主共用。
///
/// 分层约定：
///   Core     —— 治理组件（仅 NewLife.Core，不依赖 YARP）
///   Hosting  —— 应用层（依赖 Core + YARP）：本类、ConfigValidator、WeightedLoadBalancingPolicy、CLI
///   Web / Service —— 宿主层：只决定「怎么跑起来」，不复制任何管线代码
///
/// 之所以把管线抽到这里，是因为两个宿主若各自维护一份中间件顺序，迟早会漂移成
/// 「IIS 下走这条管、服务下走那条管」的隐性差异——这类差异在故障排查时极难定位。
/// </summary>
public static class GatewayHostFactory
{
    /// <summary>按给定宿主选项构建网关（不启动监听）。</summary>
    public static WebApplication Build(WebApplicationOptions options)
    {
        var builder = WebApplication.CreateBuilder(options);

        // (1) YARP 反向代理核心：从 appsettings.json 的 "ReverseProxy" 节点加载路由与集群。
        //     LoadFromConfig 借助 IConfiguration 的 reloadOnChange，修改 appsettings.json 后无需重启即可热加载（等价 nginx -s reload）。
        //     自定义负载策略 Weighted（按 Metadata.Weight 加权随机）也在此时注册进 DI，YARP 按名自动选用。
        var gatewayOptions = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .ConfigureHttpClient((context, handler) =>
            {
                // 上游连接超时（等价 nginx proxy_connect_timeout）；活动超时见集群配置 HttpRequest.ActivityTimeout。
                if (gatewayOptions.HttpTimeout.Enabled && handler is SocketsHttpHandler sockets)
                {
                    sockets.ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, gatewayOptions.HttpTimeout.ConnectSeconds));
                }
            });
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedLoadBalancingPolicy>();

        // (1.5) 响应压缩（等价 nginx gzip）：框架自带 ResponseCompression，非第三方 NuGet。
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<GzipCompressionProvider>();
        });

        // (2) 自研入口治理组件（限流/跨域/认证/缓存/熔断/重试/JWT/指标/管理端点），纯 BCL + NewLife.Core 实现。
        builder.Services.AddGateway(builder.Configuration);

        // (2.1) 配置热重载：基于 NewLife.Core 的 JsonConfigProvider 监听 appsettings.json。
        //       校验委托复用 --test-config 的 ConfigValidator，保证热载配置与启动预检同标准；
        //       非法变更被闸门拦截并保留旧配置，服务不中断。
        // 必须与 YARP / IConfiguration 使用同一个 ContentRoot，
        // 否则 "--contentRoot" 启动时治理热重载仍监听发布目录里的另一份配置。
        var hotReloadConfigPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
        builder.Services.AddGatewayHotReload(hotReloadConfigPath, path =>
        {
            var ok = ConfigValidator.Validate(path, out var error);
            return (ok, error);
        });

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    /// <summary>从命令行参数构建网关。ContentRoot 按 --contentRoot → ASPNETCORE_CONTENTROOT → 程序目录 解析。</summary>
    public static WebApplication Build(string[] args) => Build(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = GatewayCommandLine.ResolveContentRoot(args),
    });

    /// <summary>
    /// 中间件顺序（由外到内）。两个宿主必须走同一份，不允许在宿主里增删中间件。
    /// </summary>
    private static void ConfigurePipeline(WebApplication app)
    {
        // 先保存原始连接 IP：管理端点必须用它做 localhost 判定，不能被 X-Forwarded-For 污染。
        app.Use(async (context, next) =>
        {
            context.Items[AdminStatusMiddleware.OriginalRemoteIpItemsKey] = context.Connection.RemoteIpAddress;
            await next();
        });

        // 在 IIS / 前置代理后正确还原客户端真实 IP 与协议（对应 nginx 的 set_real_ip / X-Forwarded-*）。
        // 默认信任列表只有 ::1；同机 IIS ANCM 走 127.0.0.1，不显式加入会导致 IP 黑白名单与 per-IP 限流失效。
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            KnownProxies = { IPAddress.IPv6Loopback, IPAddress.Loopback }
        });

        // (2.3) 请求链路 ID（X-Request-ID）：最外层生成/透传 ID，贯穿 proxy log / access log / XTrace。
        app.UseGatewayCorrelationId();

        // (2.4) nginx 兼容访问日志（可选）：挂在压缩外层，记录压缩后字节数，统计全部请求。
        app.UseGatewayAccessLog();

        // (2.5) 监控指标（最外层）：/metrics 与 /health 短路返回，并统计全部请求。
        app.UseGatewayMetrics();

        // (2.55) 全局异常兜底：统一记录异常并返回 JSON 500，避免 Kestrel 裸 500；
        // 放在 Metrics 之内，外层指标中间件仍能看到最终 500 状态码。
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                GatewayLogger.Error($"未处理异常：{context.Request.Method} {context.Request.Path}", ex);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Internal Server Error\"}");
                }
            }
        });

        // (2.6) 轻量管理端点：/admin/status 返回运行状态 JSON 摘要。
        app.UseGatewayAdmin();

        // (2.7) 响应压缩：置于日志/重试外层，保证响应被压缩而日志记录的是原始字节。
        app.UseResponseCompression();

        // (3) 代理请求/响应详情日志（记录请求体/响应体）。
        app.UseMiddleware<ProxyLoggingMiddleware>();

        // (3.5) 失败重试（包装其后所有中间件 + YARP 转发）：幂等请求连接失败/命中可重试状态码时换节点重试。
        app.UseGatewayRetry();

        // (4) 入口统一治理：
        //     AspxBlocker -> SecurityHeaders -> IpFilter -> RequestSize -> CORS -> 限流 -> 并发上限 -> 熔断
        //     -> ApiKey 认证 -> JWT 认证 -> 响应缓存 -> LocationRewrite。
        app.UseGatewayGovernance();

        // (4) 静态文件托管（等价 nginx 的 location { root ... }）。
        app.UseFileServer();

        // (5) YARP 接管其余请求，按路由转发到后端集群（负载均衡 + 健康检查 + 会话保持 + 头改写 + WebSocket/gRPC 透传）。
        app.MapReverseProxy();
    }
}
