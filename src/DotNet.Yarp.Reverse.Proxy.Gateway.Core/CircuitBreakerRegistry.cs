using System.Collections.Concurrent;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 熔断器注册表（单例）：按路径前缀维护 CircuitBreaker 实例，供中间件与指标端点共享。
/// 这样 /metrics 可以输出每个分桶的当前状态（Closed/Open/HalfOpen），而不需要反向引用中间件。
/// 分桶数量有硬上限，超限归入 overflow 桶；空闲桶按 30 分钟惰性回收，避免任意路径导致无界增长。
/// 纯 BCL 实现，零第三方 NuGet。
/// </summary>
public sealed class CircuitBreakerRegistry
{
    /// <summary>分桶硬上限；超过后新 key 统一归入 overflow 桶，防止内存无界增长。</summary>
    public const int MaxBuckets = 512;

    /// <summary>分桶空闲回收阈值。</summary>
    private static readonly long IdleThresholdTicks = TimeSpan.FromMinutes(30).Ticks;

    private const string OverflowKey = "__overflow__";

    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CircuitBreakerOptions _options;
    private long _lastEvictTicks = DateTime.UtcNow.Ticks;

    public CircuitBreakerRegistry(CircuitBreakerOptions options) => _options = options;

    /// <summary>当前生效的熔断配置（与 Volatile.Write 配对）。</summary>
    public CircuitBreakerOptions Options => Volatile.Read(ref _options);

    /// <summary>获取或创建指定分桶的熔断器。</summary>
    public CircuitBreaker GetOrAdd(string key)
    {
        // 快速路径：已存在的桶只更新访问时间，无需进锁。
        if (_breakers.TryGetValue(key, out var found))
        {
            found.Touch();
            return found;
        }

        lock (_gate)
        {
            if (_breakers.TryGetValue(key, out found))
            {
                found.Touch();
                return found;
            }

            // 创建与配置发布在同一把锁下串行，避免新分桶长期持有旧配置。
            var options = Volatile.Read(ref _options);
            var now = DateTime.UtcNow.Ticks;
            // 为 overflow 桶预留一个槽位，保证总数严格不超过 MaxBuckets。
            if (_breakers.Count >= MaxBuckets - 1)
            {
                EvictIdleLocked(now);
            }
            if (_breakers.Count >= MaxBuckets - 1)
            {
                key = OverflowKey;
            }

            var breaker = _breakers.GetOrAdd(key, _ => new CircuitBreaker(options));
            breaker.Touch();
            return breaker;
        }
    }

    /// <summary>当前所有分桶的状态快照（供 /metrics 与排障使用）。</summary>
    public IReadOnlyDictionary<string, CircuitBreakerState> Snapshot()
    {
        var result = new Dictionary<string, CircuitBreakerState>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _breakers)
        {
            result[kv.Key] = kv.Value.State;
        }
        return result;
    }

    /// <summary>清空所有分桶状态（配置结构发生实质变化时可选调用）。</summary>
    public void Clear()
    {
        lock (_gate) _breakers.Clear();
    }

    /// <summary>热重载：在锁内发布新配置并同步到所有已存在的熔断器，避免新旧配置交错。</summary>
    public void ApplyOptions(CircuitBreakerOptions options)
    {
        lock (_gate)
        {
            Volatile.Write(ref _options, options);
            foreach (var breaker in _breakers.Values)
            {
                breaker.ApplyOptions(options);
            }
        }
    }

    /// <summary>每分钟最多扫描一次，回收长时间未访问的空闲分桶。调用方持有 _gate。</summary>
    private void EvictIdleLocked(long now)
    {
        if (now - _lastEvictTicks < TimeSpan.TicksPerMinute) return;
        _lastEvictTicks = now;

        foreach (var kv in _breakers)
        {
            if (now - kv.Value.LastAccessTicks > IdleThresholdTicks)
            {
                _breakers.TryRemove(kv.Key, out _);
            }
        }
    }
}
