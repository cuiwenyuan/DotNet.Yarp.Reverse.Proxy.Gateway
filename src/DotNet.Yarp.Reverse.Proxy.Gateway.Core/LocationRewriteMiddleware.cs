using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 重定向 Location 改写中间件（对应 Gitee GoReverseProxy 的 LocationRewriteMiddleware）。
/// 等价于 nginx 的 `proxy_redirect`：后端在 3xx 响应里返回的 Location 往往带后端内部主机/端口，
/// 经网关对外暴露前需要改写为网关的公网入口，避免浏览器被重定向到内网地址。
/// 支持两种模式：
///   1) UseRequestHostAsPublic=true：把绝对 Location 的 origin 直接替换为请求自身的 scheme://host（proxy_redirect default 行为）；
///   2) Rules：显式前缀替换（Match -> Replace），首个匹配生效，可用于相对路径或固定映射。
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class LocationRewriteMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LocationRewriteOptions _options;

    public LocationRewriteMiddleware(RequestDelegate next, LocationRewriteOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        // 必须先 await，让 YARP/后端产生响应（含 3xx 的 Location），再在回程改写。
        await _next(context);

        if (!_options.Enabled) return;

        var location = context.Response.Headers.Location.ToString();
        if (string.IsNullOrEmpty(location)) return;

        // 模式1：用请求公网入口（scheme://host）替换后端 origin
        if (_options.UseRequestHostAsPublic &&
            Uri.TryCreate(location, UriKind.Absolute, out var locUri))
        {
            var pub = new Uri($"{context.Request.Scheme}://{context.Request.Host.Value}");
            context.Response.Headers.Location = new Uri(pub, locUri.PathAndQuery).ToString();
            return;
        }

        // 模式2：显式规则前缀替换（首个匹配生效）
        for (var i = 0; i < _options.Rules.Length; i++)
        {
            var rule = _options.Rules[i];
            if (string.IsNullOrEmpty(rule.Match) ||
                !location.StartsWith(rule.Match, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.Headers.Location = rule.Replace + location.Substring(rule.Match.Length);
            return;
        }
    }
}
