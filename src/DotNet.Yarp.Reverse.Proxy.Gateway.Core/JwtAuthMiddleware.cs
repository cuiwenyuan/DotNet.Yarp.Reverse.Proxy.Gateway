using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// JWT Bearer 认证中间件（对应 Kong jwt 插件 / nginx plus JWT 校验）。
/// 纯 BCL 实现：System.Security.Cryptography（HMAC/RSA）+ System.Text.Json + Base64Url 解码，
/// 不引入 System.IdentityModel.Tokens.Jwt 等任何第三方 NuGet，严格满足依赖约束。
/// 支持：
///   - HS256：Jwt.Secret 对称密钥
///   - RS256：Jwt.PublicKeyPem（PEM 或裸 Base64 SubjectPublicKeyInfo）
///   - 校验 exp（默认必填）、nbf、iss、aud（配置后才校验）、签名（恒定时间比较）
/// 校验通过后把 sub 声明写入配置的请求头（默认 X-Jwt-Sub）透传给后端，对应 nginx 的 proxy_set_header。
/// </summary>
public class JwtAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly JwtOptions _options;

    public JwtAuthMiddleware(RequestDelegate next, AuthOptions authOptions)
    {
        _next = next;
        _options = authOptions.Jwt;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context);
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (!JwtToken.TryValidate(token, _options, out var sub))
        {
            GatewayLogger.Warn($"JWT 校验失败：{context.Connection.RemoteIpAddress} -> {context.Request.Path}");
            await RejectAsync(context);
            return;
        }

        // 透传用户标识给后端（对应 nginx 的 proxy_set_header X-Jwt-Sub）
        var headerName = string.IsNullOrWhiteSpace(_options.ClaimsHeader) ? "X-Jwt-Sub" : _options.ClaimsHeader;
        context.Request.Headers[headerName] = sub;
        context.Items["JwtSubject"] = sub;

        await _next(context);
    }

    private async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync("Unauthorized");
    }
}

/// <summary>纯 BCL 的 JWT 校验器（HS256 / RS256，恒定时间签名比较）。</summary>
public static class JwtToken
{
    /// <summary>校验 token；成功返回 true 并输出 sub 声明。</summary>
    public static bool TryValidate(string token, JwtOptions options, out string subject)
    {
        subject = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            // 1) 签名验证（header.payload 的签名）
            var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var signature = Base64UrlDecode(parts[2]);
            var signedData = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);

            if (!VerifySignature(signedData, signature, headerJson, options))
            {
                return false;
            }

            // 2) 声明校验（exp/nbf/iss/aud）
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow;
            var skew = TimeSpan.FromSeconds(Math.Max(0, options.ClockSkewSeconds));

            if (root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var exp))
            {
                if (DateTimeOffset.FromUnixTimeSeconds(exp) < now.AddSeconds(-skew.TotalSeconds))
                {
                    return false; // 已过期
                }
            }
            else if (options.RequireExpiry)
            {
                return false; // 要求 exp 但缺失
            }

            if (root.TryGetProperty("nbf", out var nbfEl) && nbfEl.TryGetInt64(out var nbf))
            {
                if (DateTimeOffset.FromUnixTimeSeconds(nbf) > now.AddSeconds(skew.TotalSeconds))
                {
                    return false; // 尚未生效
                }
            }

            if (!string.IsNullOrEmpty(options.Issuer))
            {
                if (!root.TryGetProperty("iss", out var issEl) || issEl.GetString() != options.Issuer)
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(options.Audience))
            {
                if (!root.TryGetProperty("aud", out var audEl))
                {
                    return false;
                }
                var aud = audEl.ValueKind == JsonValueKind.Array
                    ? audEl.EnumerateArray().Any(e => e.GetString() == options.Audience)
                    : audEl.GetString() == options.Audience;
                if (!aud) return false;
            }

            if (root.TryGetProperty("sub", out var subEl))
            {
                subject = subEl.GetString() ?? string.Empty;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>根据 header 的 alg 选择 HS256 / RS256 校验签名，恒定时间比较。</summary>
    private static bool VerifySignature(byte[] signedData, byte[] signature, string headerJson, JwtOptions options)
    {
        using var headerDoc = JsonDocument.Parse(headerJson);
        var alg = headerDoc.RootElement.TryGetProperty("alg", out var algEl)
            ? algEl.GetString()
            : null;

        byte[] expected = Array.Empty<byte>();
        switch (alg)
        {
            case "HS256":
            {
                var key = ResolveSecret(options.Secret);
                if (key.Length == 0) return false;
                using var hmac = new HMACSHA256(key);
                expected = hmac.ComputeHash(signedData);
                break;
            }
            case "RS256":
            {
                using var rsa = ResolveRsa(options.PublicKeyPem);
                if (rsa is null) return false;
                return rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            default:
                return false; // 只允许 HS256/RS256，拒绝 none 与其它算法
        }

        return expected.Length == signature.Length
            && CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    /// <summary>密钥解析：优先按 Base64 解码，失败则按 UTF-8 原文。</summary>
    private static byte[] ResolveSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return Array.Empty<byte>();
        var trimmed = secret.Trim();
        if (trimmed.Length % 4 == 0 && trimmed.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '='))
        {
            try
            {
                return Convert.FromBase64String(trimmed);
            }
            catch (FormatException)
            {
                // 落入 UTF-8
            }
        }
        return Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>RSA 公钥解析：优先 PEM（ImportFromPem），回退裸 Base64 SubjectPublicKeyInfo。</summary>
    private static RSA? ResolveRsa(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        var rsa = RSA.Create();
        try
        {
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (ArgumentException)
            {
                var bytes = Convert.FromBase64String(pem.Trim());
                rsa.ImportSubjectPublicKeyInfo(bytes, out _);
            }
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            return null;
        }
    }

    /// <summary>Base64Url 解码（RFC 7515）：-/_ 还原为 +//，补齐 padding。</summary>
    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
