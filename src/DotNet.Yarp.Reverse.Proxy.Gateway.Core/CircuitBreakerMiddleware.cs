using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 熔断中间件：按路径前缀（如 /api、/web）分桶维护熔断器，观测下游（YARP 转发）结果。
/// - 熔断打开时快速失败，返回配置的状态码（默认 503）+ Retry-After，避免请求继续堆积到故障后端；
/// - 下游返回 5xx（FailureStatusCodes 命中）记失败，其余（2xx/3xx/4xx）视为后端存活记成功；
/// - 半开状态放行少量探测请求，成功即恢复、失败则重新打开。
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class CircuitBreakerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CircuitBreakerOptions _options;
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new();

    public CircuitBreakerMiddleware(RequestDelegate next, CircuitBreakerOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var key = GetBucketKey(context);
        var breaker = _breakers.GetOrAdd(key, _ => new CircuitBreaker(_options));

        if (!breaker.AllowRequest())
        {
            GatewayLogger.Warn(
                $"熔断触发（{breaker.State}）：{context.Connection.RemoteIpAddress} -> {context.Request.Path}");
            context.Response.StatusCode = _options.StatusCode;
            context.Response.Headers.RetryAfter = _options.OpenSeconds.ToString();
            await context.Response.WriteAsync("Service Unavailable");
            return;
        }

        try
        {
            await _next(context);
            if (Array.IndexOf(_options.FailureStatusCodes, context.Response.StatusCode) >= 0)
                breaker.RecordFailure();
            else
                breaker.RecordSuccess();
        }
        catch
        {
            breaker.RecordFailure();
            throw;
        }
    }

    /// <summary>取路径首段作为熔断分桶 key（/api/health -> /api，/web/ -> /web，/ -> /）。</summary>
    private static string GetBucketKey(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.Length <= 1) return "/";
        var idx = path.IndexOf('/', 1);
        return idx > 0 ? path[..idx] : path;
    }
}
