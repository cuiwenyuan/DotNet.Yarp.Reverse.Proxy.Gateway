using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 安全响应头中间件（对应各网关的 SecurityHeaders 能力）。
/// 为所有响应统一附加 X-Frame-Options / X-Content-Type-Options / Referrer-Policy /
/// Content-Security-Policy / X-Permitted-Cross-Domain-Policies；后端或上游已设置同名头时不覆盖。
/// 纯 BCL 实现，不引入任何第三方 NuGet。HSTS 头由框架自带 UseHsts（仅 HTTPS 生效）另行处理。
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_options.Enabled)
        {
            var headers = context.Response.Headers;
            SetIfAbsent(headers, "X-Frame-Options", _options.XFrameOptions);
            SetIfAbsent(headers, "X-Content-Type-Options", _options.XContentTypeOptions);
            SetIfAbsent(headers, "Referrer-Policy", _options.ReferrerPolicy);
            SetIfAbsent(headers, "Content-Security-Policy", _options.ContentSecurityPolicy);
            SetIfAbsent(headers, "X-Permitted-Cross-Domain-Policies", _options.XPermittedCrossDomainPolicies);
        }

        await _next(context);
    }

    private static void SetIfAbsent(IHeaderDictionary headers, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (headers.ContainsKey(name)) return;
        headers[name] = value;
    }
}
