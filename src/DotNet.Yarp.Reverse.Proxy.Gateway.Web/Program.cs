using DotNet.Yarp.Reverse.Proxy.Gateway.Core;
using DotNet.Yarp.Reverse.Proxy.Gateway.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.LoadBalancing;

// (0) 配置预检模式（对应 nginx -t / GoReverseProxy --test-config）：
//     dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config
//     校验 appsettings.json（治理配置绑定、路由/集群唯一性、后端地址合法性），通过输出 OK 并退出 0。
if (args.Contains("--test-config") || args.Contains("-t"))
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    var ok = ConfigValidator.Validate(configPath, out var configError);
    Console.WriteLine(ok ? "Configuration OK" : $"Configuration error: {configError}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

// 在 IIS / 前置代理后正确还原客户端真实 IP 与协议（对应 nginx 的 set_real_ip / X-Forwarded-*）。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
});

// (2.5) 监控指标（最外层）：/metrics 与 /health 短路返回，并统计全部请求。
app.UseGatewayMetrics();

// (2.6) 轻量管理端点：/admin/status 返回运行状态 JSON 摘要。
app.UseGatewayAdmin();

// (2.7) 响应压缩：置于日志/重试外层，保证响应被压缩而日志记录的是原始字节。
app.UseResponseCompression();

// (3) 代理请求/响应详情日志（记录请求体/响应体）。
app.UseMiddleware<ProxyLoggingMiddleware>();

// (3.5) 失败重试（包装其后所有中间件 + YARP 转发）：幂等请求连接失败/命中可重试状态码时换节点重试。
app.UseGatewayRetry();

// (4) 入口统一治理：
//     AspxBlocker -> SecurityHeaders -> IpFilter -> RequestSize -> CORS -> 限流 -> 熔断
//     -> ApiKey 认证 -> JWT 认证 -> 响应缓存 -> LocationRewrite。
app.UseGatewayGovernance();

// (4) 静态文件托管（等价 nginx 的 location { root ... }）。
app.UseFileServer();

// (5) YARP 接管其余请求，按路由转发到后端集群（负载均衡 + 健康检查 + 会话保持 + 头改写 + WebSocket/gRPC 透传）。
app.MapReverseProxy();

app.Run();
