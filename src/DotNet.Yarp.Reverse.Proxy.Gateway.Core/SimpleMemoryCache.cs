using System.Collections.Concurrent;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 轻量内存响应缓存：纯 BCL（System.Collections.Concurrent）实现，带 TTL 过期，
/// 等价于 nginx 的 proxy_cache 基础能力。未引入 Microsoft.Extensions.Caching.Memory 等包。
/// 可作为响应缓存组件挂载到自定义中间件中复用。
/// </summary>
public sealed class SimpleMemoryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();

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
