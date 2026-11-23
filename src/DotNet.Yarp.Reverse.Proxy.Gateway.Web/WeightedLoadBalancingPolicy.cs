using System.Buffers;
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

        // 从 ArrayPool 租借权重数组：一次解析、零每请求分配。
        var weights = ArrayPool<long>.Shared.Rent(availableDestinations.Count);
        try
        {
            long total = 0;
            for (var i = 0; i < availableDestinations.Count; i++)
            {
                weights[i] = GetWeight(availableDestinations[i]);
                total += weights[i];
            }

            // 全部节点权重为 0（显式摘除）时返回 null，由 YARP 按无可用节点处理（503），
            // 而不是退化成均分——后者会让已摘除节点继续接流量。
            if (total <= 0) return null;

            var pick = Random.Shared.NextInt64(total);
            long acc = 0;
            for (var i = 0; i < availableDestinations.Count; i++)
            {
                acc += weights[i];
                if (pick < acc) return availableDestinations[i];
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(weights);
        }

        return null; // 异常情形，交由 YARP 处理
    }

    private static long GetWeight(DestinationState destination)
    {
        if (destination.Model?.Config?.Metadata is { } metadata
            && metadata.TryGetValue("Weight", out var raw))
        {
            // 显式声明 Weight 时，0 / 负数 / 非法值一律视为权重 0（节点摘除），对齐 nginx weight=0 语义。
            return long.TryParse(raw, out var weight) && weight > 0 ? weight : 0;
        }
        // 未声明权重默认 1。
        return 1;
    }
}
