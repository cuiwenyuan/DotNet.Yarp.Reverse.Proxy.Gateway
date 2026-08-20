using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 请求体大小限制中间件（对应 nginx 的 client_max_body_size）。
/// 基于 Content-Length 头快速判断并拒绝超限请求（返回 413），避免大体积请求打爆网关内存与带宽。
/// 说明：流式/分块（无 Content-Length）的请求体不在本中间件限制范围，如需兜底可在 IIS 层
/// 配置 requestLimits maxAllowedContentLength，或在 Kestrel 层配置 MaxRequestBodySize。
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class RequestSizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestSizeOptions _options;

    public RequestSizeLimitMiddleware(RequestDelegate next, RequestSizeOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_options.Enabled && context.Request.ContentLength is long length && length > _options.MaxBytes)
        {
            GatewayLogger.Warn(
                $"请求体超限：{context.Connection.RemoteIpAddress} -> {context.Request.Path} " +
                $"({length} bytes > {_options.MaxBytes})");
            context.Response.StatusCode = _options.StatusCode;
            await context.Response.WriteAsync("Payload Too Large");
            return;
        }

        await _next(context);
    }
}
