using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 响应缓存中间件（对应 nginx 的 proxy_cache）。
/// 复用 SimpleMemoryCache（纯 BCL ConcurrentDictionary + TTL）缓存 GET/HEAD 的 200 响应：
///   - 命中缓存：直接以缓存的状态码/Content-Type/缓存体回复，不再转发到后端；
///   - 未命中：转发后端，响应完成后按 Cache-Control max-age（无则用 DefaultTtlSeconds）写入缓存；
///   - 保守策略：带 Authorization / Cookie 的请求不缓存（避免缓存私有数据）；
///   - 防膨胀：超过 MaxCacheEntries 后不再写入新缓存。
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class ResponseCacheMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CacheOptions _options;
    private readonly SimpleMemoryCache _cache;
    private readonly ConcurrentDictionary<string, CacheMeta> _meta = new();

    private readonly struct CacheMeta
    {
        public CacheMeta(int statusCode, string contentType, string cacheControl)
        {
            StatusCode = statusCode;
            ContentType = contentType;
            CacheControl = cacheControl;
        }

        public int StatusCode { get; }
        public string ContentType { get; }
        public string CacheControl { get; }
    }

    public ResponseCacheMiddleware(RequestDelegate next, CacheOptions options, SimpleMemoryCache cache)
    {
        _next = next;
        _options = options;
        _cache = cache;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled || !IsCacheableRequest(context))
        {
            await _next(context);
            return;
        }

        var key = BuildKey(context);

        // 命中缓存：直接回包
        if (_cache.TryGet(key, out var cached) && _meta.TryGetValue(key, out var meta))
        {
            context.Response.StatusCode = meta.StatusCode;
            context.Response.ContentType = meta.ContentType;
            if (!string.IsNullOrEmpty(meta.CacheControl))
            {
                context.Response.Headers.CacheControl = meta.CacheControl;
            }
            await context.Response.Body.WriteAsync(cached);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);

            if (Array.IndexOf(_options.CacheableStatusCodes, context.Response.StatusCode) >= 0)
            {
                var ttl = ResolveTtl(context) ?? TimeSpan.FromSeconds(Math.Max(1, _options.DefaultTtlSeconds));
                if (ttl > TimeSpan.Zero && _meta.Count < _options.MaxCacheEntries)
                {
                    var newMeta = new CacheMeta(
                        context.Response.StatusCode,
                        context.Response.ContentType ?? string.Empty,
                        context.Response.Headers.CacheControl.ToString());
                    _meta[key] = newMeta;
                    _cache.Set(key, buffer.ToArray(), ttl);
                }
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
    }

    /// <summary>仅缓存幂等且无敏感头的请求。</summary>
    private static bool IsCacheableRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            return false;
        }
        if (context.Request.Headers.ContainsKey("Cookie"))
        {
            return false;
        }
        return true;
    }

    private static string BuildKey(HttpContext context)
    {
        return context.Request.Method + "|" +
               context.Request.Path.Value + "|" +
               context.Request.QueryString.Value;
    }

    /// <summary>优先取响应 Cache-Control max-age，无则返回 null（用默认 TTL）。</summary>
    private static TimeSpan? ResolveTtl(HttpContext context)
    {
        var cc = context.Response.Headers.CacheControl.ToString();
        if (string.IsNullOrEmpty(cc)) return null;
        foreach (var part in cc.Split(','))
        {
            var kv = part.Trim().Split('=');
            if (kv.Length == 2 && kv[0].Trim().Equals("max-age", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(kv[1].Trim(), out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
        }
        return null;
    }
}
