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
/// 热重载：ApplyOptions 只替换配置引用，不重置计数器——正在飞行的请求持有的槽位不会被"吞掉"。
/// 请求的启用判断与占槽必须使用同一代配置快照，否则 Enabled 翻转会造成槽位误归还。
/// </summary>
public sealed class ConcurrencyLimitState
{
    private ConcurrencyLimitOptions _options;
    private int _inFlight;      // 当前在途请求数
    private int _peakInFlight;  // 历史峰值（观察用）
    private long _rejected;     // 累计拒绝次数

    public ConcurrencyLimitState(ConcurrencyLimitOptions options) => _options = options;

    /// <summary>当前生效配置（每次热重载原子发布一份新引用）。</summary>
    public ConcurrencyLimitOptions Options => Volatile.Read(ref _options);

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

    /// <summary>尝试占用一个并发槽位，使用当前配置快照。</summary>
    public bool TryAcquire() => TryAcquire(Options);

    /// <summary>
    /// 尝试占用一个并发槽位。必须传入调用方本次请求已读取的配置快照，
    /// 保证"是否启用"与"上限值"来自同一代配置，且只有真正占槽成功才返回 true。
    /// CAS 循环保证并发下不会超过上限（不会超卖）。
    /// </summary>
    public bool TryAcquire(ConcurrencyLimitOptions options)
    {
        if (!options.Enabled) return false; // 未启用不占槽；由中间件在入口直接放行

        var max = Math.Max(1, options.MaxConcurrent);
        while (true)
        {
            var current = Volatile.Read(ref _inFlight);
            if (current >= max) return false;

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
        // 整段只使用这一代配置快照：热重载不会让"判断启用"与"占槽/归还"跨代错位。
        var options = _state.Options;
        if (!options.Enabled)
        {
            await _next(context);
            return;
        }

        var waitStartedAt = Environment.TickCount64;
        var acquired = _state.TryAcquire(options);
        if (!acquired && options.WaitEnabled)
        {
            // 平滑模式：轮询等空位，最多 MaxWaitMs。用 10ms 粒度避免忙等。
            var deadline = waitStartedAt + Math.Max(0, options.MaxWaitMs);
            while (!acquired && Environment.TickCount64 < deadline)
            {
                await Task.Delay(Math.Min(10, Math.Max(1, (int)(deadline - Environment.TickCount64))));
                acquired = _state.TryAcquire(options);
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
            // 关键：只有真正占到了槽位才归还；任何路径（含下游异常、客户端断连）都必须归还。
            _state.Release();
        }
    }
}
