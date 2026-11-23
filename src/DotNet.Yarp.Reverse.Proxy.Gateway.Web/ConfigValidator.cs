using DotNet.Yarp.Reverse.Proxy.Gateway.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Web;

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
        return services.BuildServiceProvider().GetRequiredService<IConfigValidator>();
    });

    public static bool Validate(string configPath, out string? error)
    {
        try
        {
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

            // 4) 用 YARP 自带校验器验证每条路由的 Transforms。
            //    这一步是 --test-config 的关键补强：此前非法的 transform（如 "RequestHeaders"
            //    拼写错误）能通过预检，却在 YARP 启动时抛 "Unknown transform" 崩溃。
            //    现在预检阶段即拦截，且与热重载闸门共用同一标准。
            var validator = YarpValidator.Value;
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
