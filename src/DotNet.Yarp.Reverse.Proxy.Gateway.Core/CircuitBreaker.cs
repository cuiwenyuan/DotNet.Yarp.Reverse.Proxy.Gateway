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
/// 线程安全（lock 保护），无定时器，按需惰性判断。兼容 .NET 6 / 8 / 10 三目标框架。
/// </summary>
public sealed class CircuitBreaker
{
    private CircuitBreakerOptions _options;
    private readonly object _gate = new();
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private long _openedAtTicks;
    private int _halfOpenInFlight;

    public CircuitBreaker(CircuitBreakerOptions options) => _options = options;

    /// <summary>热重载：替换配置引用（不重置当前状态与在途探测计数）。</summary>
    public void ApplyOptions(CircuitBreakerOptions options) => Volatile.Write(ref _options, options);

    public CircuitBreakerState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>请求是否被允许进入；熔断打开时返回 false（快速失败）。</summary>
    public bool AllowRequest()
    {
        lock (_gate)
        {
            // ApplyOptions 走 Volatile.Write；这里必须用 Volatile.Read 配对，保证热重载后读到的
            // 是新配置引用（lock 只保护状态，不保护来自锁外的配置写入）。
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
                        _halfOpenInFlight = 0;
                        return true;
                    }
                    return false;

                default: // HalfOpen
                    if (_halfOpenInFlight < Math.Max(1, options.HalfOpenMaxRequests))
                    {
                        _halfOpenInFlight++;
                        return true;
                    }
                    return false;
            }
        }
    }

    /// <summary>记录一次成功：清零失败计数；半开状态下成功则恢复 Closed。</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failureCount = 0;
            if (_state == CircuitBreakerState.HalfOpen)
            {
                _state = CircuitBreakerState.Closed;
                _halfOpenInFlight = 0;
            }
        }
    }

    /// <summary>记录一次失败：半开失败立即重新 Open；Closed 连续失败达阈值转 Open。</summary>
    public void RecordFailure()
    {
        lock (_gate)
        {
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
