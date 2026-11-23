namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>熔断器状态：Closed 正常 / Open 熔断打开 / HalfOpen 半开（放行探测）。</summary>
public enum CircuitBreakerState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// 纯 C# 熔断器状态机（对应 Ocelot QoS、Kong 熔断插件）。
/// Closed：连续失败达阈值后转 Open；Open：在 OpenSeconds 内快速失败（不再放行请求）；
/// 到期后转 HalfOpen：放行少量探测请求，成功即恢复 Closed，失败则重新 Open。
/// 半开探测带"代编号"：上一代探测的迟到结果会被丢弃，不会误伤已恢复的熔断器。
/// 线程安全（lock 保护状态，Volatile 发布配置），无定时器，按需惰性判断。
/// 兼容 .NET 6 / 8 / 10 三目标框架。
/// </summary>
public sealed class CircuitBreaker
{
    private CircuitBreakerOptions _options;
    private readonly object _gate = new();
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private long _openedAtTicks;
    private int _halfOpenInFlight;
    private long _probeGeneration;
    private long _lastAccessTicks = DateTime.UtcNow.Ticks;

    public CircuitBreaker(CircuitBreakerOptions options) => _options = options;

    /// <summary>热重载：替换配置引用（不重置当前状态与在途探测计数）。</summary>
    public void ApplyOptions(CircuitBreakerOptions options) => Volatile.Write(ref _options, options);

    /// <summary>最近一次请求访问时间（供注册表回收空闲分桶）。</summary>
    internal long LastAccessTicks => Volatile.Read(ref _lastAccessTicks);

    /// <summary>标记本分桶刚被访问。</summary>
    internal void Touch() => Volatile.Write(ref _lastAccessTicks, DateTime.UtcNow.Ticks);

    public CircuitBreakerState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>请求是否被允许进入；熔断打开时返回 false（快速失败）。兼容旧签名。</summary>
    public bool AllowRequest() => AllowRequest(out _);

    /// <summary>
    /// 请求是否被允许进入，并输出本次探测的代编号（0 = 非半开探测）。
    /// 调用方在请求结束时把 generation 回传给 Complete，确保结果归属正确的半开代。
    /// </summary>
    public bool AllowRequest(out long generation)
    {
        generation = 0;
        lock (_gate)
        {
            // ApplyOptions 走 Volatile.Write；这里必须用 Volatile.Read 配对。
            var options = Volatile.Read(ref _options);
            switch (_state)
            {
                case CircuitBreakerState.Closed:
                    return true;

                case CircuitBreakerState.Open:
                    var openSpan = TimeSpan.FromSeconds(Math.Max(1, options.OpenSeconds)).Ticks;
                    if (DateTime.UtcNow.Ticks - _openedAtTicks >= openSpan)
                    {
                        _state = CircuitBreakerState.HalfOpen;
                        // 触发转换的这一请求本身就是第一个探测，必须计入配额，
                        // 否则半开期实际可放行 HalfOpenMaxRequests + 1 个请求。
                        _halfOpenInFlight = 1;
                        generation = ++_probeGeneration;
                        return true;
                    }
                    return false;

                default: // HalfOpen
                    if (_halfOpenInFlight < Math.Max(1, options.HalfOpenMaxRequests))
                    {
                        _halfOpenInFlight++;
                        generation = _probeGeneration;
                        return true;
                    }
                    return false;
            }
        }
    }

    /// <summary>记录一次成功：清零失败计数；半开状态下成功则恢复 Closed。兼容旧签名。</summary>
    public void RecordSuccess() => Complete(0, success: true);

    /// <summary>记录一次失败：半开失败立即重新 Open；Closed 连续失败达阈值转 Open。兼容旧签名。</summary>
    public void RecordFailure() => Complete(0, success: false);

    /// <summary>带代编号的成功回执（供半开探测使用）。</summary>
    public void RecordSuccess(long generation) => Complete(generation, success: true);

    /// <summary>带代编号的失败回执（供半开探测使用）。</summary>
    public void RecordFailure(long generation) => Complete(generation, success: false);

    private void Complete(long generation, bool success)
    {
        lock (_gate)
        {
            // 上一代半开探测的迟到结果必须丢弃：否则刚恢复 Closed 的熔断器会被旧探测打回 Open。
            if (generation != 0 && generation != _probeGeneration) return;

            if (success)
            {
                _failureCount = 0;
                if (_state == CircuitBreakerState.HalfOpen)
                {
                    _state = CircuitBreakerState.Closed;
                    _halfOpenInFlight = 0;
                }
                return;
            }

            var options = Volatile.Read(ref _options);
            if (_state == CircuitBreakerState.HalfOpen)
            {
                _state = CircuitBreakerState.Open;
                _openedAtTicks = DateTime.UtcNow.Ticks;
                _halfOpenInFlight = 0;
                return;
            }

            _failureCount++;
            if (_failureCount >= Math.Max(1, options.FailureThreshold))
            {
                _state = CircuitBreakerState.Open;
                _openedAtTicks = DateTime.UtcNow.Ticks;
                _failureCount = 0;
            }
        }
    }
}
