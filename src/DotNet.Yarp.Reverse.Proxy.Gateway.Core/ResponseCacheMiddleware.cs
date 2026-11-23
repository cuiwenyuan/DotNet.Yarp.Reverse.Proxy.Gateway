using System.Buffers.Binary;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 响应缓存中间件（对应 nginx 的 proxy_cache）。
/// 复用 SimpleMemoryCache（纯 BCL ConcurrentDictionary + TTL）缓存 GET/HEAD 的 200 响应：
///   - 命中缓存：直接以缓存的状态码/Content-Type/缓存体回复，不再转发到后端；
///   - 未命中：转发后端，响应完成后按 Cache-Control max-age（无则用 DefaultTtlSeconds）写入缓存；
///   - 保守策略：带 Authorization / Cookie 的请求不缓存（避免缓存私有数据）；
///   - 防膨胀：超过 MaxCacheEntries 后不再写入新缓存；过期条目定期回收，不会永久占满配额。
///
/// 元数据（状态码/Content-Type/Cache-Control）与响应体打包进同一个缓存条目，
/// 消除"body 已写入、meta 尚未写入"的跨代窗口，也避免 meta 字典只增不减导致缓存永久失效。
/// 纯 BCL 实现，不引入任何第三方 NuGet。
/// </summary>
public class ResponseCacheMiddleware
{
    // 单条缓存体上限：大响应即使已缓冲也不写入缓存，避免缓存大对象导致内存放大。
    private const long MaxCacheResponseBytes = 8 * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly CacheOptions _options;
    private readonly SimpleMemoryCache _cache;
    private readonly GatewayMetrics? _metrics;

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

    public ResponseCacheMiddleware(
        RequestDelegate next,
        CacheOptions options,
        SimpleMemoryCache cache,
        GatewayMetrics? metrics = null)
    {
        _next = next;
        _options = options;
        _cache = cache;
        _metrics = metrics;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled || !IsCacheableRequest(context))
        {
            _metrics?.RecordCacheBypass();
            await _next(context);
            return;
        }

        var key = BuildKey(context);

        // 命中缓存：body 与 meta 来自同一条目，不会错配。
        if (_cache.TryGet(key, out var cached) && TryUnpack(cached, out var meta, out var body))
        {
            _metrics?.RecordCacheHit();
            context.Response.StatusCode = meta.StatusCode;
            context.Response.ContentType = meta.ContentType;
            if (!string.IsNullOrEmpty(meta.CacheControl))
            {
                context.Response.Headers.CacheControl = meta.CacheControl;
            }
            await context.Response.Body.WriteAsync(body);
            return;
        }

        _metrics?.RecordCacheMiss();
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);

            if (Array.IndexOf(_options.CacheableStatusCodes, context.Response.StatusCode) >= 0)
            {
                var ttl = ResolveTtl(context) ?? TimeSpan.FromSeconds(Math.Max(1, _options.DefaultTtlSeconds));
                PurgeExpiredIfNeeded();
                if (ttl > TimeSpan.Zero && buffer.Length <= MaxCacheResponseBytes
                    && _cache.Count < _options.MaxCacheEntries)
                {
                    var newMeta = new CacheMeta(
                        context.Response.StatusCode,
                        context.Response.ContentType ?? string.Empty,
                        context.Response.Headers.CacheControl.ToString());
                    _cache.Set(key, Pack(newMeta, buffer.GetBuffer().AsMemory(0, (int)buffer.Length)), ttl);
                    _metrics?.RecordCacheStore();
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

    /// <summary>
    /// 仅在配额已满时扫描过期条目：平时零开销，满额后能立即释放过期 key，
    /// 避免高基数 query 把配额占满导致缓存永久停止写入。
    /// </summary>
    private void PurgeExpiredIfNeeded()
    {
        if (_cache.Count >= _options.MaxCacheEntries) _cache.PurgeExpired();
    }

    /// <summary>[status:int32][ctLen:int32][contentType UTF8][ccLen:int32][cacheControl UTF8][body...]</summary>
    private static ReadOnlyMemory<byte> Pack(CacheMeta meta, ReadOnlyMemory<byte> body)
    {
        var ct = Encoding.UTF8.GetBytes(meta.ContentType ?? string.Empty);
        var cc = Encoding.UTF8.GetBytes(meta.CacheControl ?? string.Empty);
        var packed = new byte[4 + 4 + ct.Length + 4 + cc.Length + body.Length];
        var dest = packed.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(dest, meta.StatusCode);
        BinaryPrimitives.WriteInt32LittleEndian(dest[4..], ct.Length);
        ct.CopyTo(dest[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(dest[(8 + ct.Length)..], cc.Length);
        cc.CopyTo(dest[(12 + ct.Length)..]);
        body.Span.CopyTo(dest[(12 + ct.Length + cc.Length)..]);
        return packed;
    }

    private static bool TryUnpack(ReadOnlyMemory<byte> packed, out CacheMeta meta, out ReadOnlyMemory<byte> body)
    {
        meta = default;
        body = default;
        var span = packed.Span;
        if (span.Length < 12) return false;

        var statusCode = BinaryPrimitives.ReadInt32LittleEndian(span);
        var ctLen = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        if (ctLen < 0 || span.Length < 8 + ctLen + 4) return false;
        var contentType = Encoding.UTF8.GetString(span.Slice(8, ctLen));

        var pos = 8 + ctLen;
        var ccLen = BinaryPrimitives.ReadInt32LittleEndian(span[pos..]);
        pos += 4;
        if (ccLen < 0 || span.Length < pos + ccLen) return false;
        var cacheControl = Encoding.UTF8.GetString(span.Slice(pos, ccLen));
        pos += ccLen;

        meta = new CacheMeta(statusCode, contentType, cacheControl);
        body = packed[pos..];
        return true;
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
