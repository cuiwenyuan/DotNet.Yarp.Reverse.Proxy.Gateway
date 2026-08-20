using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Web;

/// <summary>
/// 加权随机负载均衡策略（对应 nginx upstream 的 weight 指令）。
/// 每个 destination 可在配置的 Metadata 中声明权重，如：
///   "api1": { "Address": "http://localhost:5101/", "Metadata": { "Weight": "3" } }
///   "api2": { "Address": "http://localhost:5102/", "Metadata": { "Weight": "1" } }
/// 未声明权重时默认 1。集群设置 "LoadBalancingPolicy": "Weighted" 即启用。
/// 配合按 header 匹配的 canary 路由（YARP Match.Headers）可实现灰度发布。
/// 依赖 YARP 类型，故放在 Web 宿主项目（Core 类库保持不引 YARP）。
/// </summary>
public sealed class WeightedLoadBalancingPolicy : ILoadBalancingPolicy
{
    public const string PolicyName = "Weighted";

    /// <summary>策略名：集群 LoadBalancingPolicy 填 "Weighted"。</summary>
    public string Name => PolicyName;

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0) return null;
        if (availableDestinations.Count == 1) return availableDestinations[0];

        var weights = new long[availableDestinations.Count];
        long total = 0;
        for (var i = 0; i < availableDestinations.Count; i++)
        {
            weights[i] = GetWeight(availableDestinations[i]);
            total += weights[i];
        }

        if (total <= 0) return availableDestinations[0];

        var pick = Random.Shared.NextInt64(total);
        long acc = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            acc += weights[i];
            if (pick < acc) return availableDestinations[i];
        }

        return availableDestinations[^1];
    }

    private static long GetWeight(DestinationState destination)
    {
        if (destination.Model?.Config?.Metadata is { } metadata
            && metadata.TryGetValue("Weight", out var raw)
            && long.TryParse(raw, out var weight)
            && weight > 0)
        {
            return weight;
        }
        return 1;
    }
}
