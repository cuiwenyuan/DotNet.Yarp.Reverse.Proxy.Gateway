using DotNet.Yarp.Reverse.Proxy.Gateway.Core;
using Microsoft.Extensions.Configuration;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Web;

/// <summary>
/// 配置预检工具（对应 GoReverseProxy 的 --test-config）。
/// 启动前校验 appsettings.json：
///   1) "Gateway" 治理配置可成功绑定（GatewayOptions）；
///   2) ReverseProxy 路由 / 集群 Id 唯一；
///   3) 每个 Destination.Address 必须是合法的 http/https 绝对 URI。
/// 任何一项不通过即返回错误，避免服务启动后才发现配置问题。
/// </summary>
public static class ConfigValidator
{
    public static bool Validate(string configPath, out string error)
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

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
