using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 失败重试中间件（对应 nginx 的 proxy_next_upstream）。
/// 仅对幂等方法（GET/HEAD/OPTIONS/TRACE）生效；WebSocket 升级请求跳过。
/// 通过临时接管 Response.Body 为缓冲流，在首次转发失败（连接异常或命中可重试状态码）时
/// 重置响应并重新执行下游管线——YARP 会按负载均衡策略换一个目标节点，等效"失败后换 upstream 重试"。
/// 防风暴：最多重试 MaxRetries 次（默认 1），立即重试无等待；503（熔断/过载）默认不重试。
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class RetryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RetryOptions _options;

    public RetryMiddleware(RequestDelegate next, RetryOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled
            || !IsRetryableMethod(context.Request.Method)
            || context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        var attempt = 0;
        Exception? lastError = null;
        try
        {
            while (true)
            {
                buffer.SetLength(0);
                lastError = null;
                try
                {
                    await _next(context);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                var shouldRetry = lastError is not null
                    ? _options.RetryOnConnectionError
                    : IsRetryableStatus(context.Response.StatusCode);

                if (shouldRetry && attempt < _options.MaxRetries && TryResetResponse(context))
                {
                    attempt++;
                    continue;
                }
                break;
            }
        }
        finally
        {
            context.Response.Body = originalBody;
            buffer.Position = 0;
            if (buffer.Length > 0)
            {
                await buffer.CopyToAsync(originalBody);
            }
        }

        // 最终尝试仍抛异常（且不可重试/重试耗尽）时，向上层透传，交由外层日志中间件处理。
        if (lastError is not null)
        {
            throw lastError;
        }
    }

    private static bool IsRetryableMethod(string method)
    {
        return HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method);
    }

    private bool IsRetryableStatus(int statusCode)
    {
        if (statusCode == StatusCodes.Status500InternalServerError && !_options.RetryOnHttp500)
        {
            return false;
        }
        return Array.IndexOf(_options.RetryableStatusCodes, statusCode) >= 0;
    }

    /// <summary>
    /// 尝试重置响应以便重试：仅在响应尚未发送给客户端（HasStarted=false）时可重置。
    /// 重置失败（如已开始发送）则放弃本次重试，避免损坏已开始的响应。
    /// </summary>
    private static bool TryResetResponse(HttpContext context)
    {
        try
        {
            if (context.Response.HasStarted) return false;
            context.Response.Clear();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
