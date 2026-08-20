using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 简易认证中间件：校验 X-Api-Key 请求头，等价于 nginx 的 auth_request 轻量版。
/// 刻意不引入 System.IdentityModel.Tokens.Jwt（第三方 NuGet），用纯 BCL 实现，
/// 满足"除 YARP 外只允许 NewLife.Core"的约束。生产可替换为更复杂签名/密钥校验。
/// </summary>
public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthOptions _options;

    public ApiKeyAuthMiddleware(RequestDelegate next, AuthOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_options.Required)
        {
            var key = context.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrEmpty(key) || Array.IndexOf(_options.Keys, key) < 0)
            {
                GatewayLogger.Warn($"认证失败：{context.Connection.RemoteIpAddress} -> {context.Request.Path}");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            // 透传客户端标识给后端（等价于 nginx 的 proxy_set_header X-Client-Id）
            context.Request.Headers["X-Client-Id"] = key;
        }

        await _next(context);
    }
}
