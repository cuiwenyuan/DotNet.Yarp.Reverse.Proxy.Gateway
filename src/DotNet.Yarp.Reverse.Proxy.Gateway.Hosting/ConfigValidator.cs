using DotNet.Yarp.Reverse.Proxy.Gateway.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Hosting;

/// <summary>
/// 配置预检工具（对应 GoReverseProxy 的 --test-config）。
/// 启动前校验 appsettings.json：
///   1) "Gateway" 治理配置可成功绑定（GatewayOptions）；
///   2) ReverseProxy 路由 / 集群 Id 唯一；
///   3) 每个 Destination.Address 必须是合法的 http/https 绝对 URI；
///   4) 每条路由的 Transforms 必须由 YARP 自身校验器认可（拦住 Unknown transform 等会在启动时崩溃的配置）。
/// 任何一项不通过即返回错误，避免服务启动后才发现配置问题。
/// </summary>
public static class ConfigValidator
{
    // 复用 YARP 自带校验器，而非手写 transform key 白名单——后者会随 YARP 版本过时。
    // IConfigValidator 无状态，静态缓存一次即可，供 --test-config 与热重载闸门共用。
    private static readonly Lazy<IConfigValidator> YarpValidator = new(() =>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReverseProxy();
        // 注册本项目自定义策略，否则配置 "Weighted" 会被 YARP 校验器误判为未知策略。
        services.AddSingleton<ILoadBalancingPolicy, WeightedLoadBalancingPolicy>();
        return services.BuildServiceProvider().GetRequiredService<IConfigValidator>();
    });

    public static bool Validate(string configPath, out string? error)
    {
        try
        {
            // 相对路径必须先转绝对：ConfigurationBuilder.SetBasePath 只接受绝对路径，
            // 否则运维执行 "--test-config --configPath candidate.json" 会得到
            // "The path must be absolute" 这种与真实问题无关的报错。
            configPath = Path.GetFullPath(configPath);

            if (!File.Exists(configPath))
            {
                error = $"Config file not found: {configPath}";
                return false;
            }

            var builder = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(configPath) ?? ".")
                .AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: false);
            var config = builder.Build();

            // 1) 治理配置可绑定
            try
            {
                _ = config.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
            }
            catch (Exception ex)
            {
                error = $"Gateway config binding failed: {ex.Message}";
                return false;
            }

            // ConfigurationBinder 对非法数值会静默取默认值，必须显式校验关键项，
            // 否则 "PerMinute": "abc" 这类错误会通过闸门并让运行时行为出乎意料。
            (string Path, int Min)[] numericSettings =
            {
                ("Gateway:RateLimit:PerMinute", 1),
                ("Gateway:RateLimit:MaxIpBuckets", 1),
                ("Gateway:ConcurrencyLimit:MaxConcurrent", 1),
                ("Gateway:Cache:MaxCacheEntries", 1),
                ("Gateway:Retry:MaxRetries", 0),
                ("Gateway:CircuitBreaker:FailureThreshold", 1)
            };
            foreach (var (path, min) in numericSettings)
            {
                var raw = config[path];
                if (string.IsNullOrEmpty(raw)) continue;
                if (!int.TryParse(raw, out var number) || number < min)
                {
                    error = $"Invalid gateway setting {path}='{raw}' (expected integer >= {min})";
                    return false;
                }
            }

            // 2) 路由 / 集群 Id 唯一性
            var routeIds = config.GetSection("ReverseProxy:Routes").GetChildren().Select(c => c.Key).ToList();
            var clusterIds = config.GetSection("ReverseProxy:Clusters").GetChildren().Select(c => c.Key).ToList();

            if (routeIds.Count == 0 || clusterIds.Count == 0)
            {
                error = "No routes or clusters defined in ReverseProxy section";
                return false;
            }

            var dupRoutes = routeIds.GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                                    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupRoutes.Count > 0)
            {
                error = $"Duplicate route IDs: {string.Join(", ", dupRoutes)}";
                return false;
            }

            var dupClusters = clusterIds.GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                                        .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupClusters.Count > 0)
            {
                error = $"Duplicate cluster IDs: {string.Join(", ", dupClusters)}";
                return false;
            }

            // 路由必须引用已定义的 cluster，避免启动时才发现 "unknown cluster"。
            var clusterKeySet = new HashSet<string>(clusterIds, StringComparer.OrdinalIgnoreCase);
            foreach (var route in config.GetSection("ReverseProxy:Routes").GetChildren())
            {
                var clusterId = route["ClusterId"];
                if (string.IsNullOrWhiteSpace(clusterId) || !clusterKeySet.Contains(clusterId))
                {
                    error = $"Route '{route.Key}' references unknown ClusterId '{clusterId}'";
                    return false;
                }
            }

            // 3) 目的地地址合法性
            foreach (var cluster in config.GetSection("ReverseProxy:Clusters").GetChildren())
            {
                foreach (var dest in cluster.GetSection("Destinations").GetChildren())
                {
                    var address = dest["Address"];
                    if (string.IsNullOrWhiteSpace(address)
                        || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        error = $"Invalid backend address '{address}' in cluster '{cluster.Key}', destination '{dest.Key}'";
                        return false;
                    }
                }
            }

            var validator = YarpValidator.Value;

            // 4) 用 YARP 自带校验器验证 Cluster（负载均衡策略、请求超时格式、会话保持等）。
            //    此前只校验路由 transform，非法 cluster 要到启动或首次请求才暴露。
            foreach (var cluster in config.GetSection("ReverseProxy:Clusters").GetChildren())
            {
                var destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var dest in cluster.GetSection("Destinations").GetChildren())
                {
                    var metadata = dest.GetSection("Metadata");
                    var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (metadata.Exists())
                    {
                        foreach (var kv in metadata.GetChildren()) items[kv.Key] = kv.Value ?? string.Empty;
                    }
                    var destination = new DestinationConfig
                    {
                        Address = dest["Address"] ?? string.Empty,
                        Metadata = items // YARP 的 Metadata 是 init-only，必须在构造时赋值
                    };
                    destinations[dest.Key] = destination;
                }

                var clusterConfig = new ClusterConfig
                {
                    ClusterId = cluster.Key,
                    LoadBalancingPolicy = cluster["LoadBalancingPolicy"],
                    Destinations = destinations
                };
                var clusterErrors = validator.ValidateClusterAsync(clusterConfig).AsTask().GetAwaiter().GetResult();
                if (clusterErrors.Count > 0)
                {
                    error = $"Invalid cluster '{cluster.Key}': "
                          + string.Join("; ", clusterErrors.Select(e => e.Message));
                    return false;
                }
            }

            // 5) 用 YARP 自带校验器验证每条路由的 Transforms。
            //    此前非法的 transform（如 "RequestHeaders" 拼写错误）能通过预检，
            //    却在 YARP 启动时抛 "Unknown transform" 崩溃，这里提前拦截。
            foreach (var route in config.GetSection("ReverseProxy:Routes").GetChildren())
            {
                var transforms = ReadTransforms(route.GetSection("Transforms"));
                if (transforms is null) continue;

                var routeConfig = new RouteConfig
                {
                    RouteId = route.Key,
                    ClusterId = route["ClusterId"] ?? string.Empty,
                    Match = new RouteMatch { Path = route["Match:Path"] },
                    Transforms = transforms
                };
                // 校验为纯内存 CPU 操作（无 I/O），同步等待不会死锁。
                var routeErrors = validator.ValidateRouteAsync(routeConfig).AsTask().GetAwaiter().GetResult();
                if (routeErrors.Count > 0)
                {
                    error = $"Invalid transforms in route '{route.Key}': "
                          + string.Join("; ", routeErrors.Select(e => e.Message));
                    return false;
                }
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把配置里的 Transforms 节点还原为 YARP 期望的 IReadOnlyList&lt;IReadOnlyDictionary&lt;string,string&gt;&gt;。
    /// 返回 null 表示该路由未配置 Transforms。
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string>>? ReadTransforms(IConfigurationSection section)
    {
        if (!section.Exists()) return null;

        var list = new List<IReadOnlyDictionary<string, string>>();
        foreach (var item in section.GetChildren())
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in item.GetChildren())
                dict[kv.Key] = kv.Value ?? string.Empty;
            if (dict.Count > 0) list.Add(dict);
        }
        return list;
    }
}
