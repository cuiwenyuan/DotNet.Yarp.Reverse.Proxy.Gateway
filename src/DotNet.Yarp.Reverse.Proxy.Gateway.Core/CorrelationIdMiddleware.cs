using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 链路 ID 访问器：让拿不到 HttpContext 的静态组件（如 GatewayLogger/XTrace）也能读取当前请求的 ID。
/// 用 AsyncLocal 存储，随 async 调用链在请求作用域内自动流动，天然线程隔离、无需锁。
/// 与 HttpContext.Items 双写：中间件持有 context 时优先读 Items，静态组件读 AsyncLocal。
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>HttpContext.Items 中存放链路 ID 的键。</summary>
    public const string ItemsKey = "__GatewayCorrelationId";

    /// <summary>当前 async 上下文的链路 ID；非请求上下文（如启动日志）为 null。</summary>
    public static string? RequestId => Current.Value;

    internal static void Set(string? id) => Current.Value = id;

    /// <summary>从 HttpContext 取链路 ID（供持有 context 的中间件使用）。</summary>
    public static string? FromHttpContext(HttpContext context)
    {
        return context.Items.TryGetValue(ItemsKey, out var value) ? value as string : null;
    }
}

/// <summary>
/// 请求链路 ID 中间件（对应 X-Request-ID / Correlation-Id 行业惯例）：
///   - 每个请求生成或透传一个 ID，写入 HttpContext.Items 与 AsyncLocal；
///   - 回写响应头，客户端可据此追踪；
///   - ProxyLogging / NginxAccessLog / XTrace 读取该 ID 并附带在日志行上，把一次请求的日志串起来。
///
/// 应挂载在管线最外层（ForwardedHeaders 之后、其他日志/治理之前），确保所有下游组件都能读到。
/// 纯 BCL 实现（Guid + AsyncLocal + HttpContext.Items），零第三方 NuGet。
/// </summary>
public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CorrelationIdOptions _options;
    private readonly string _headerName;

    public CorrelationIdMiddleware(RequestDelegate next, CorrelationIdOptions options)
    {
        _next = next;
        _options = options;
        _headerName = string.IsNullOrWhiteSpace(options.HeaderName) ? "X-Request-ID" : options.HeaderName;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var requestId = ResolveRequestId(context);
        context.Items[CorrelationContext.ItemsKey] = requestId;
        CorrelationContext.Set(requestId);

        if (_options.EchoResponseHeader && !context.Response.HasStarted)
            context.Response.Headers[_headerName] = requestId;

        try
        {
            await _next(context);
        }
        finally
        {
            // 请求结束清理 AsyncLocal，避免在非请求上下文（如后台任务）中残留上一个请求的 ID。
            CorrelationContext.Set(null);
        }
    }

    /// <summary>确定本请求的链路 ID：优先透传合法的入站 ID，否则生成新 ID。</summary>
    private string ResolveRequestId(HttpContext context)
    {
        if (_options.TrustIncoming
            && context.Request.Headers.TryGetValue(_headerName, out var incoming)
            && IsValidId(incoming.ToString()))
        {
            return incoming.ToString();
        }
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// 入站 ID 合法性校验（防日志注入）：非空、长度 ≤ 128、不含控制字符或空白。
    /// 超长或含控制字符的 ID 一律拒绝并重新生成。
    /// </summary>
    private static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
        foreach (var c in id)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return false;
        }
        return true;
    }
}
