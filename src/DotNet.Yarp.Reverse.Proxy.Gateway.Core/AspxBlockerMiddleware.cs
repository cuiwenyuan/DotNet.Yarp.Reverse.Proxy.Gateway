using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 扩展名黑名单拦截中间件（对应 Gitee GoReverseProxy 的 AspxBlockerMiddleware / SecurityConfig）。
/// 拦截 URL 路径以黑名单扩展名结尾的请求（如 .aspx / .asp / .php 等），直接拒绝，
/// 防止恶意请求穿透到后端。等价于在 nginx 入口做：
///   location ~* \.(aspx|asp|php|...)$ { return 403; }
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class AspxBlockerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;

    public AspxBlockerMiddleware(RequestDelegate next, SecurityOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_options.Enabled && _options.BlockedExtensions.Length > 0)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            for (var i = 0; i < _options.BlockedExtensions.Length; i++)
            {
                var ext = _options.BlockedExtensions[i];
                if (ext.Length == 0) continue;
                var cmp = ext[0] == '.' ? ext : "." + ext;
                if (path.EndsWith(cmp, StringComparison.OrdinalIgnoreCase))
                {
                    GatewayLogger.Warn($"扩展名黑名单拦截：{context.Connection.RemoteIpAddress} -> {path}");
                    context.Response.StatusCode = _options.StatusCode;
                    await context.Response.WriteAsync("Forbidden");
                    return;
                }
            }
        }

        await _next(context);
    }
}
