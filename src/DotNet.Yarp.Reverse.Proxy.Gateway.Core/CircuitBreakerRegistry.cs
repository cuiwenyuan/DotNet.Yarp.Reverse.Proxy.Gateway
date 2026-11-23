using System.Collections.Concurrent;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 熔断器注册表（单例）：按路径前缀维护 CircuitBreaker 实例，供中间件与指标端点共享。
/// 这样 /metrics 可以输出每个分桶的当前状态（Closed/Open/HalfOpen），而不需要反向引用中间件。
/// 纯 BCL 实现，零第三方 NuGet。
/// </summary>
public sealed class CircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.OrdinalIgnoreCase);
    private CircuitBreakerOptions _options;

    public CircuitBreakerRegistry(CircuitBreakerOptions options) => _options = options;

    /// <summary>获取或创建指定分桶的熔断器。</summary>
    public CircuitBreaker GetOrAdd(string key)
    {
        return _breakers.GetOrAdd(key, _ => new CircuitBreaker(_options));
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
    public void Clear() => _breakers.Clear();

    /// <summary>热重载：更新配置引用并同步到已存在的熔断器实例。</summary>
    public void ApplyOptions(CircuitBreakerOptions options)
    {
        Volatile.Write(ref _options, options);
        foreach (var breaker in _breakers.Values)
        {
            breaker.ApplyOptions(options);
        }
    }
}
