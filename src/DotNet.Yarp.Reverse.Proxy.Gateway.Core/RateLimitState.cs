using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 限流共享状态（单例）：持有全局令牌桶与 per-IP 分桶，并支持运行时热更新配置。
/// 配置 + 全局桶打包为不可变代快照，一次 Volatile.Write 发布，请求线程一次读取整代，
/// 避免"新配置 + 旧令牌桶"跨代组合。纯 BCL 实现。
/// </summary>
public sealed class RateLimitState
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, IpBucket> _buckets = new();

    /// <summary>按 IP 分桶的条目：令牌桶 + 最近访问时间（用于空闲回收）。</summary>
    private sealed class IpBucket
    {
        private long _lastAccessTicks;

        public IpBucket(SimpleRateLimiter limiter, long ticks)
        {
            Limiter = limiter;
            _lastAccessTicks = ticks;
        }

        public SimpleRateLimiter Limiter { get; }
        public long LastAccessTicks
        {
            get => Volatile.Read(ref _lastAccessTicks);
            set => Volatile.Write(ref _lastAccessTicks, value);
        }
    }

    /// <summary>一代完整限流配置：选项与由它构造的全局令牌桶必须配套。</summary>
    private sealed class Generation
    {
        public readonly RateLimitOptions Options;
        public readonly SimpleRateLimiter Global;

        public Generation(RateLimitOptions options)
        {
            Options = options;
            Global = CreateLimiter(options);
        }
    }

    private Generation _generation;
    private long _lastEvictTicks = DateTime.UtcNow.Ticks;

    public RateLimitState(RateLimitOptions options) => _generation = new Generation(options);

    /// <summary>当前每分钟配额（用于 X-Rate-Limit-Limit 头）。</summary>
    public int PerMinute => Volatile.Read(ref _generation).Options.PerMinute;

    /// <summary>当前生效的限流配置（热重载后为最新代）。</summary>
    public RateLimitOptions Options => Volatile.Read(ref _generation).Options;

    /// <summary>
    /// 尝试获取令牌；返回是否放行与限流头数据。
    /// <paramref name="ip"/> 为空或 PerIp=false 时使用全局桶。
    /// </summary>
    public SimpleRateLimiter.RateLimitInfo Acquire(string? ip)
    {
        // 一次读取整代快照：后续 PerIp / MaxIpBuckets / 全局桶都来自同一代配置。
        var generation = Volatile.Read(ref _generation);
        var options = generation.Options;

        if (options.PerIp && !string.IsNullOrEmpty(ip))
        {
            var now = DateTime.UtcNow.Ticks;
            if (!_buckets.TryGetValue(ip, out var bucket))
            {
                // 只对"新 IP"触发回收，且回收按 1 分钟节流，避免满桶后每请求 O(n) 全表扫描。
                TryEvictIdleBuckets(options, now);
                if (_buckets.Count < options.MaxIpBuckets)
                {
                    bucket = _buckets.GetOrAdd(ip, _ => new IpBucket(CreateLimiter(options), now));
                }
            }

            if (bucket is not null)
            {
                bucket.LastAccessTicks = now;
                return bucket.Limiter.TryAcquireDetailed();
            }
        }

        return generation.Global.TryAcquireDetailed();
    }

    /// <summary>热重载：构造新代（新配置 + 新全局桶）后原子发布，并清空按 IP 分桶按新参数重建。</summary>
    public void ApplyOptions(RateLimitOptions options)
    {
        var next = new Generation(options);
        lock (_gate)
        {
            Volatile.Write(ref _generation, next);
            _buckets.Clear();
        }
    }

    /// <summary>回收空闲 IP 桶；每分钟最多真正扫描一次。</summary>
    private void TryEvictIdleBuckets(RateLimitOptions options, long now)
    {
        const long scanIntervalTicks = TimeSpan.TicksPerMinute;
        var last = Interlocked.Read(ref _lastEvictTicks);
        if (now - last < scanIntervalTicks) return;
        if (Interlocked.CompareExchange(ref _lastEvictTicks, now, last) != last) return;

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
    private readonly GatewayMetrics? _metrics;

    public RateLimitMiddleware(RequestDelegate next, RateLimitState state, GatewayMetrics? metrics = null)
    {
        _next = next;
        _state = state;
        _metrics = metrics;
    }

    public async Task Invoke(HttpContext context)
    {
        var options = _state.Options;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        // delay 平滑模式：最多重试等待 MaxDelayMs
        var deadline = Environment.TickCount64 + Math.Max(0, options.MaxDelayMs);
        var delayed = false;
        SimpleRateLimiter.RateLimitInfo info;
        while (true)
        {
            info = _state.Acquire(ip);
            if (info.Allowed) break;

            var delayMs = options.DelayEnabled ? info.WaitMs : int.MaxValue;
            if (delayMs > 0 && Environment.TickCount64 + delayMs <= deadline)
            {
                delayed = true;
                await Task.Delay(delayMs);
                continue;
            }
            break;
        }

        SetRateLimitHeaders(context, info);

        if (!info.Allowed)
        {
            _metrics?.RecordRateLimitRejected();
            GatewayLogger.Warn($"限流触发：{context.Connection.RemoteIpAddress} -> {context.Request.Path}");
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = info.ResetSeconds > 0 ? info.ResetSeconds.ToString() : "60";
            await context.Response.WriteAsync("Too Many Requests");
            return;
        }

        if (delayed) _metrics?.RecordRateLimitDelayed();
        _metrics?.RecordRateLimitAllowed();
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
