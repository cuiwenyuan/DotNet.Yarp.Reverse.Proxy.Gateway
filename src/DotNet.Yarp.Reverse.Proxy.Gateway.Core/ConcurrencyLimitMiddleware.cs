using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 全局并发上限共享状态（单例）：用 Interlocked CAS 维护"当前在途请求数"，
/// 等价于 nginx 的 limit_conn（连接/并发数限制，区别于 rate limit 的速率限制）。
///
/// 与 SimpleRateLimiter 的区别：令牌桶限制"单位时间请求数"，并发上限限制"同时在途数"——
/// 后端响应变慢时，令牌桶仍可能放行，而并发上限会把堆积挡在入口，是后端承压的最后一道保险。
///
/// 纯 BCL（Interlocked/Volatile）实现，线程安全、无锁、零第三方 NuGet。
/// 热重载：ApplyOptions 只替换配置引用，不重置计数器——正在飞行的请求持有的槽位不会被"吞掉"，
/// 避免重载瞬间计数错乱导致提前放行超额请求。
/// </summary>
public sealed class ConcurrencyLimitState
{
    private ConcurrencyLimitOptions _options;
    private int _inFlight;      // 当前在途请求数
    private int _peakInFlight;  // 历史峰值（观察用）
    private long _rejected;     // 累计拒绝次数

    public ConcurrencyLimitState(ConcurrencyLimitOptions options) => _options = options;

    /// <summary>当前生效配置（热重载后为同一对象引用上的新值）。</summary>
    public ConcurrencyLimitOptions Options => _options;

    /// <summary>当前在途请求数。</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>历史峰值并发数。</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>累计因并发超限被拒绝的请求数（只统计最终拒绝，不含排队重试）。</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>记录一次最终拒绝。仅供中间件在确定返回 503 时调用。</summary>
    public void RecordReject() => Interlocked.Increment(ref _rejected);

    /// <summary>热重载：替换配置引用。不重置计数器，保护在途请求的槽位。</summary>
    public void ApplyOptions(ConcurrencyLimitOptions options) => Volatile.Write(ref _options, options);

    /// <summary>
    /// 尝试占用一个并发槽位。CAS 循环保证不会超过上限（并发下不会超卖）。
    /// 上限变化时即时生效：读取的是最新 Options.MaxConcurrent。
    /// </summary>
    public bool TryAcquire()
    {
        if (!_options.Enabled) return true; // 未启用时不占槽位、不计数

        var max = Math.Max(1, _options.MaxConcurrent);
        while (true)
        {
            var current = Volatile.Read(ref _inFlight);
            if (current >= max)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
            {
                // 更新峰值（只增不减，CAS 避免并发写丢失）
                while (true)
                {
                    var peak = Volatile.Read(ref _peakInFlight);
                    if (current + 1 <= peak ||
                        Interlocked.CompareExchange(ref _peakInFlight, current + 1, peak) == peak)
                        break;
                }
                return true;
            }
            // CAS 失败说明其他线程抢先占用，重试
        }
    }

    /// <summary>释放一个并发槽位。用 CAS 防止误释放导致计数变负。</summary>
    public void Release()
    {
        while (true)
        {
            var current = Volatile.Read(ref _inFlight);
            if (current <= 0) return; // 防御：不应发生，避免计数为负
            if (Interlocked.CompareExchange(ref _inFlight, current - 1, current) == current) return;
        }
    }
}

/// <summary>
/// 全局并发上限中间件（对应 nginx 的 limit_conn）：
/// - 未启用时零开销放行（仅一次 bool 判断）；
/// - 启用后每个请求进入时占用一个槽位，退出时（含异常路径）在 finally 中释放；
/// - WaitEnabled=true 时超额请求最多排队 MaxWaitMs 毫秒等空位（平滑模式）；
/// - 仍无法获得槽位则返回 503（可配）+ Retry-After，并记录告警。
///
/// 典型挂载位置：RateLimit 之后、CircuitBreaker 之前（先限速再限并发，最后熔断）。
/// 纯 BCL 实现，零第三方 NuGet。
/// </summary>
public class ConcurrencyLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConcurrencyLimitState _state;
    private readonly GatewayMetrics? _metrics;

    public ConcurrencyLimitMiddleware(RequestDelegate next, ConcurrencyLimitState state, GatewayMetrics? metrics = null)
    {
        _next = next;
        _state = state;
        _metrics = metrics;
    }

    public async Task Invoke(HttpContext context)
    {
        var options = _state.Options;
        if (!options.Enabled)
        {
            await _next(context);
            return;
        }

        var waitStartedAt = Environment.TickCount64;
        var acquired = _state.TryAcquire();
        if (!acquired && options.WaitEnabled)
        {
            // 平滑模式：轮询等空位，最多 MaxWaitMs。用 10ms 粒度避免忙等。
            var deadline = waitStartedAt + Math.Max(0, options.MaxWaitMs);
            while (!acquired && Environment.TickCount64 < deadline)
            {
                await Task.Delay(Math.Min(10, Math.Max(1, (int)(deadline - Environment.TickCount64))));
                acquired = _state.TryAcquire();
            }
        }

        var waitedMs = Environment.TickCount64 - waitStartedAt;
        if (waitedMs > 0) _metrics?.RecordConcurrencyWait(waitedMs);

        if (!acquired)
        {
            _state.RecordReject();
            _metrics?.RecordConcurrencyRejected();
            if (options.WaitEnabled) _metrics?.RecordConcurrencyWaitTimeout();
            GatewayLogger.Warn($"并发上限触发：inFlight={_state.InFlight}/{options.MaxConcurrent} -> {context.Request.Path}");
            context.Response.StatusCode = options.StatusCode;
            context.Response.Headers.RetryAfter = "1";
            context.Response.Headers["X-Concurrency-Limit"] = options.MaxConcurrent.ToString();
            await context.Response.WriteAsync("Service Unavailable: too many concurrent requests");
            return;
        }

        _metrics?.RecordConcurrencyAcquired();
        try
        {
            await _next(context);
        }
        finally
        {
            // 关键：任何路径（含下游异常、客户端断连）都必须归还槽位，否则会永久泄漏配额。
            _state.Release();
        }
    }
}
