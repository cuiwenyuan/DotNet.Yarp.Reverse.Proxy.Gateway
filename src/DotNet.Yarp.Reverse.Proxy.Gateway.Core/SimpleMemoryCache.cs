using System.Collections.Concurrent;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 轻量内存响应缓存：纯 BCL（System.Collections.Concurrent）实现，带 TTL 过期，
/// 等价于 nginx 的 proxy_cache 基础能力。未引入 Microsoft.Extensions.Caching.Memory 等包。
/// 除惰性过期外提供 PurgeExpired，供缓存中间件定期回收冷 key，避免条目数被过期项占满。
/// </summary>
public sealed class SimpleMemoryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();

    /// <summary>当前条目数（含尚未回收的过期项）。</summary>
    public int Count => _store.Count;

    public void Set(string key, ReadOnlyMemory<byte> data, TimeSpan ttl)
    {
        _store[key] = new CacheEntry(data, DateTime.UtcNow.Add(ttl));
    }

    public bool TryGet(string key, out ReadOnlyMemory<byte> data)
    {
        data = default;
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.Expiry > DateTime.UtcNow)
            {
                data = entry.Data;
                return true;
            }

            _store.TryRemove(key, out _);
        }

        return false;
    }

    /// <summary>主动清除所有已过期条目，返回清除数量。</summary>
    public int PurgeExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var kv in _store)
        {
            if (kv.Value.Expiry <= now && _store.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    private readonly struct CacheEntry
    {
        public CacheEntry(ReadOnlyMemory<byte> data, DateTime expiry)
        {
            Data = data;
            Expiry = expiry;
        }

        public ReadOnlyMemory<byte> Data { get; }
        public DateTime Expiry { get; }
    }
}
