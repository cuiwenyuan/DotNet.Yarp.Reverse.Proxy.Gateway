using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 限流共享状态（单例）：持有全局令牌桶与 per-IP 分桶，并支持运行时热更新配置。
/// 中间件每次请求经 Acquire 获取令牌并输出 X-Rate-Limit-* 响应头数据；
/// 管理端点热重载时调用 ApplyOptions 重建限流器（桶参数变更后旧 IP 桶清空重建）。
/// 纯 BCL 实现。
/// </summary>
public sealed class RateLimitState
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, IpBucket> _buckets = new();

    /// <summary>按 IP 分桶的条目：令牌桶 + 最近访问时间（用于惰性回收）。</summary>
    private sealed class IpBucket
    {
        public SimpleRateLimiter Limiter { get; }
        public long LastAccessTicks { get; set; }

        public IpBucket(SimpleRateLimiter limiter, long ticks) => (Limiter, LastAccessTicks) = (limiter, ticks);
    }

    private RateLimitOptions _options;
    private SimpleRateLimiter _global;

    public RateLimitState(RateLimitOptions options)
    {
        _options = options;
        _global = CreateLimiter(options);
    }

    /// <summary>当前每分钟配额（用于 X-Rate-Limit-Limit 头）。</summary>
    public int PerMinute => _options.PerMinute;

    /// <summary>当前生效的限流配置（热重载后为最新值）。</summary>
    public RateLimitOptions Options => _options;

    /// <summary>
    /// 尝试获取令牌；返回是否放行与限流头数据。
    /// <paramref name="ip"/> 为空或 PerIp=false 时使用全局桶。
    /// </summary>
    public SimpleRateLimiter.RateLimitInfo Acquire(string? ip)
    {
        var options = _options;
        if (options.PerIp && !string.IsNullOrEmpty(ip))
        {
            var now = DateTime.UtcNow.Ticks;
            if (_buckets.Count < options.MaxIpBuckets)
            {
                var bucket = _buckets.GetOrAdd(ip,
                    _ => new IpBucket(CreateLimiter(options), now));
                bucket.LastAccessTicks = now;
                return bucket.Limiter.TryAcquireDetailed();
            }

            TryEvictIdleBuckets(options, now);
            if (_buckets.Count < options.MaxIpBuckets)
            {
                var bucket = _buckets.GetOrAdd(ip,
                    _ => new IpBucket(CreateLimiter(options), now));
                bucket.LastAccessTicks = now;
                return bucket.Limiter.TryAcquireDetailed();
            }
        }

        return _global.TryAcquireDetailed();
    }

    /// <summary>热重载：替换配置并重建限流器（清空 IP 桶使其按新参数重建）。</summary>
    public void ApplyOptions(RateLimitOptions options)
    {
        lock (_gate)
        {
            _options = options;
            _global = CreateLimiter(options);
            _buckets.Clear();
        }
    }

    private void TryEvictIdleBuckets(RateLimitOptions options, long now)
    {
        var idleThresholdTicks = TimeSpan.FromMinutes(Math.Max(1, options.BucketIdleMinutes)).Ticks;
        foreach (var kv in _buckets)
        {
            if (now - kv.Value.LastAccessTicks > idleThresholdTicks)
            {
                _buckets.TryRemove(kv.Key, out _);
            }
        }
    }

    private static SimpleRateLimiter CreateLimiter(RateLimitOptions options)
    {
        var burst = options.Burst > options.PerMinute ? options.Burst : options.PerMinute;
        return new SimpleRateLimiter(options.PerMinute, TimeSpan.FromMinutes(1), burst);
    }
}

/// <summary>
/// 限流中间件：基于 SimpleRateLimiter（纯 C# 令牌桶）实现，等价于 nginx 的 limit_req。
/// - 默认按客户端 IP 分桶（limit_req 按 remote_addr 计 key），桶数量有上限，超出回退全局桶；
/// - 输出 X-Rate-Limit-Limit / Remaining / Reset 标准响应头（对齐 Ocelot）；
/// - DelayEnabled=true 时超额请求最多等待 MaxDelayMs 毫秒平滑放行（对齐 nginx limit_req delay）；
/// - 超限（等待超时或非平滑模式）返回 429 + Retry-After。
/// 客户端 IP 取自 Connection.RemoteIpAddress——Program.cs 已通过 UseForwardedHeaders 还原。
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitState _state;

    public RateLimitMiddleware(RequestDelegate next, RateLimitState state)
    {
        _next = next;
        _state = state;
    }

    public async Task Invoke(HttpContext context)
    {
        var options = _state.Options;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        // delay 平滑模式：最多重试等待 MaxDelayMs
        var deadline = Environment.TickCount64 + Math.Max(0, options.MaxDelayMs);
        SimpleRateLimiter.RateLimitInfo info;
        while (true)
        {
            info = _state.Acquire(ip);
            if (info.Allowed) break;

            var delayMs = options.DelayEnabled ? info.WaitMs : int.MaxValue;
            if (delayMs > 0 && Environment.TickCount64 + delayMs <= deadline)
            {
                await Task.Delay(delayMs);
                continue;
            }
            break;
        }

        SetRateLimitHeaders(context, info);

        if (!info.Allowed)
        {
            GatewayLogger.Warn($"限流触发：{context.Connection.RemoteIpAddress} -> {context.Request.Path}");
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = info.ResetSeconds > 0 ? info.ResetSeconds.ToString() : "60";
            await context.Response.WriteAsync("Too Many Requests");
            return;
        }

        await _next(context);
    }

    private void SetRateLimitHeaders(HttpContext context, SimpleRateLimiter.RateLimitInfo info)
    {
        // 对齐 Ocelot 的 X-Rate-Limit-* 标准响应头
        context.Response.Headers["X-Rate-Limit-Limit"] = _state.PerMinute.ToString();
        context.Response.Headers["X-Rate-Limit-Remaining"] = ((long)info.Remaining).ToString();
        context.Response.Headers["X-Rate-Limit-Reset"] = info.ResetSeconds.ToString();
    }
}
