using System;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 纯 C# 令牌桶限流器，兼容 .NET 6 / .NET 8 双目标框架。
/// 等价于 nginx 的 limit_req（rate=补充速率 / burst=桶容量 / delay=平滑模式）：
///   - 补充速率（refill）：窗口内允许的请求数按时间均匀补充；
///   - 桶容量（burst）：允许瞬时突发（capacity ≥ rate 时，突发量 = capacity - rate）；
///   - 平滑延迟（delay）：令牌不足时返回需要等待的时长，由调用方决定"等待放行"或"直接拒绝"。
/// 刻意不依赖 System.Threading.RateLimiting（.NET 7+ 专属），以严格满足"仅 YARP + NewLife.Core，
/// 其余纯 C# 标准类库实现"的约束。线程安全、无定时器、按需惰性补充令牌。
/// </summary>
public sealed class SimpleRateLimiter
{
    private readonly double _capacity;
    private readonly double _refillPerMs;
    private double _tokens;
    private long _lastRefillTicks;
    private readonly object _gate = new();

    /// <summary>
    /// 构造令牌桶。
    /// </summary>
    /// <param name="rate">窗口内允许补充的请求数（补充速率）。</param>
    /// <param name="window">补充满 rate 个令牌所需的时间窗口。</param>
    /// <param name="burst">桶容量（允许的瞬时最大积压令牌数）。小于 rate 时取 rate；0 或省略时等于 rate（等价原行为）。</param>
    public SimpleRateLimiter(int rate, TimeSpan window, int? burst = null)
    {
        var safeRate = Math.Max(1, rate);
        _capacity = Math.Max(safeRate, burst ?? 0);
        _tokens = _capacity;
        var windowMs = Math.Max(1, window.TotalMilliseconds);
        _refillPerMs = safeRate / windowMs;
        _lastRefillTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>尝试获取一个令牌；成功返回 true，被限流返回 false。</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            Refill();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 详细获取：返回是否放行、当前剩余令牌数、以及（被限流时）等到下一个令牌就绪所需的毫秒数。
    /// 供限流中间件输出 X-Rate-Limit-* 响应头与实现 delay 平滑模式。
    /// </summary>
    public RateLimitInfo TryAcquireDetailed()
    {
        lock (_gate)
        {
            Refill();
            var allowed = false;
            double remaining;
            int waitMs;
            int resetSeconds;

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                allowed = true;
                remaining = Math.Floor(_tokens);
                waitMs = 0;
            }
            else
            {
                remaining = 0;
                // 距离下一个令牌就绪的毫秒数（delay 模式用）
                waitMs = _refillPerMs > 0 ? (int)Math.Ceiling((1.0 - _tokens) / _refillPerMs) : int.MaxValue;
            }

            // 重置时间（秒）：桶恢复到满所需时间，用于 X-Rate-Limit-Reset
            resetSeconds = _refillPerMs > 0
                ? (int)Math.Ceiling((_capacity - _tokens) / _refillPerMs / 1000.0)
                : 0;

            return new RateLimitInfo(allowed, remaining, waitMs, resetSeconds);
        }
    }

    /// <summary>限流快照信息。</summary>
    public readonly struct RateLimitInfo
    {
        public RateLimitInfo(bool allowed, double remaining, int waitMs, int resetSeconds)
        {
            Allowed = allowed;
            Remaining = remaining;
            WaitMs = waitMs;
            ResetSeconds = resetSeconds;
        }

        /// <summary>本次请求是否放行。</summary>
        public bool Allowed { get; }

        /// <summary>放行后桶内剩余令牌数（用于 X-Rate-Limit-Remaining）。</summary>
        public double Remaining { get; }

        /// <summary>被限流时，等到下一个令牌就绪所需的毫秒数（delay 模式）。</summary>
        public int WaitMs { get; }

        /// <summary>桶恢复到满所需秒数（用于 X-Rate-Limit-Reset）。</summary>
        public int ResetSeconds { get; }
    }

    /// <summary>按当前时间惰性补充令牌。</summary>
    private void Refill()
    {
        var now = DateTime.UtcNow.Ticks;
        var elapsedMs = (now - _lastRefillTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsedMs * _refillPerMs);
            _lastRefillTicks = now;
        }
    }
}
