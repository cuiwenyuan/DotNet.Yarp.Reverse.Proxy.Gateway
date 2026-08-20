using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 跨域中间件：纯 BCL 实现，等价于 nginx 的 add_header Access-Control-Allow-*。
/// 未使用 Microsoft.AspNetCore.Cors 包，便于完全掌控头逻辑且不引入额外依赖。
/// </summary>
public class CorsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CorsOptions _options;

    public CorsMiddleware(RequestDelegate next, CorsOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        var requestOrigin = context.Request.Headers.Origin.ToString();
        var allowOrigin = "*";

        if (_options.AllowedOrigins.Length > 0)
        {
            allowOrigin = _options.AllowedOrigins.Contains(requestOrigin, StringComparer.OrdinalIgnoreCase)
                ? requestOrigin
                : "null";
        }

        context.Response.Headers.AccessControlAllowOrigin = allowOrigin;
        context.Response.Headers.AccessControlAllowMethods =
            string.Join(",", _options.AllowedMethods);
        context.Response.Headers.AccessControlAllowHeaders =
            string.Join(",", _options.AllowedHeaders);

        // 预检请求直接返回 204，不再向后传递
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await _next(context);
    }
}
