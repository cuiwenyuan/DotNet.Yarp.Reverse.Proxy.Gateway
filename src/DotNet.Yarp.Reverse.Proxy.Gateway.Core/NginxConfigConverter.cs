using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>nginx.conf 转换结果：JSON 片段 + 需人工复核的提示。</summary>
public sealed record NginxConversionResult(
    string Json,
    IReadOnlyList<string> Notes,
    int RouteCount,
    int ClusterCount);

/// <summary>
/// nginx.conf → YARP ReverseProxy 配置转换器（纯 BCL，零第三方依赖）。
/// 支持常见 http/upstream/server/location 结构与 proxy_pass / proxy_set_header / server_name 指令。
/// 输出可直接合并进 appsettings.json 的 ReverseProxy 节点；不能自动等价的指令写入 Notes 供人工复核。
/// </summary>
public static class NginxConfigConverter
{
    private enum TokenKind { Word, Symbol, Quoted }
    private readonly record struct Token(TokenKind Kind, string Value);

    private sealed class Directive
    {
        public string Name = string.Empty;
        public List<string> Args = new();
        public List<Directive> Children = new();
    }

    /// <summary>把 nginx.conf 文本转换为 YARP 配置 JSON。</summary>
    public static NginxConversionResult Convert(string text)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            notes.Add("输入为空，未生成任何路由。");
            return new NginxConversionResult(BuildPayload(new Dictionary<string, object?>(),
                new Dictionary<string, object?>(), notes), notes, 0, 0);
        }

        var tokens = Tokenize(text);
        var pos = 0;
        var root = ParseBlock(tokens, ref pos, stopAtBrace: false);

        // 收集所有 upstream（含 http 块内外）
        var upstreams = new Dictionary<string, List<UpstreamServer>>(StringComparer.OrdinalIgnoreCase);
        foreach (var up in FindAll(root, "upstream"))
        {
            if (up.Args.Count == 0) continue;
            var name = up.Args[0];
            var servers = new List<UpstreamServer>();
            foreach (var s in up.Children.Where(c => c.Name.Equals("server", StringComparison.OrdinalIgnoreCase)))
            {
                if (s.Args.Count == 0) continue;
                var addr = s.Args[0];
                if (addr.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"upstream '{name}' 包含 unix socket '{addr}'，YARP 不支持，已跳过该节点。");
                    continue;
                }
                var weight = ParseKeyValue(s.Args, "weight", out var w) && int.TryParse(w, out var wi) && wi > 0 ? wi : 1;
                var down = s.Args.Any(a => a.Equals("down", StringComparison.OrdinalIgnoreCase));
                if (down)
                {
                    notes.Add($"upstream '{name}' 的节点 '{addr}' 标记为 down，已跳过。");
                    continue;
                }
                servers.Add(new UpstreamServer(NormalizeAddress(addr), weight));
            }
            if (servers.Count > 0) upstreams[name] = servers;
        }

        var routes = new Dictionary<string, object?>();
        var clusters = new Dictionary<string, object?>();

        // 先为每个 upstream 建集群
        foreach (var (name, servers) in upstreams)
        {
            var destinations = new Dictionary<string, object?>();
            var anyWeight = servers.Any(s => s.Weight != 1);
            for (var i = 0; i < servers.Count; i++)
            {
                var s = servers[i];
                var dest = new Dictionary<string, object?> { ["Address"] = s.Address };
                if (anyWeight) dest["Metadata"] = new Dictionary<string, object?> { ["Weight"] = s.Weight.ToString(CultureInfo.InvariantCulture) };
                destinations["d" + (i + 1)] = dest;
            }
            var cluster = new Dictionary<string, object?>
            {
                ["LoadBalancingPolicy"] = anyWeight ? "Weighted" : "RoundRobin",
                ["Destinations"] = destinations
            };
            clusters[name] = cluster;
        }

        var routeSeq = 0;
        var inlineSeq = 0;
        var usedRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in FindAll(root, "server"))
        {
            // server_name（取第一条 server_name 指令）
            var hostList = new List<string>();
            var sn = server.Children.FirstOrDefault(c => c.Name.Equals("server_name", StringComparison.OrdinalIgnoreCase));
            if (sn is not null)
            {
                foreach (var h in sn.Args)
                {
                    if (h == "_" || h.Length == 0) continue;
                    hostList.Add(h);
                }
            }

            foreach (var loc in server.Children.Where(c => c.Name.Equals("location", StringComparison.OrdinalIgnoreCase)))
            {
                if (loc.Args.Count == 0) continue;
                var rawPath = loc.Args[^1];
                var modifiers = loc.Args.Take(loc.Args.Count - 1).ToArray();
                var isExact = modifiers.Contains("=");
                var isRegex = modifiers.Any(m => m is "~" or "~*");
                var isNamed = rawPath.StartsWith('@');

                if (isNamed)
                {
                    notes.Add($"命名 location '{rawPath}' 属于内部跳转，YARP 无直接等价，已跳过。");
                    continue;
                }
                if (isRegex)
                {
                    notes.Add($"正则 location '{rawPath}' 无法自动等价为 YARP 路由，已跳过，请人工改写。");
                    continue;
                }

                var proxyPass = loc.Children.FirstOrDefault(c => c.Name.Equals("proxy_pass", StringComparison.OrdinalIgnoreCase));
                if (proxyPass is null || proxyPass.Args.Count == 0)
                {
                    notes.Add($"location '{rawPath}' 无 proxy_pass（可能是静态 root 或 return），未生成路由，请人工处理。");
                    continue;
                }

                var target = proxyPass.Args[0];
                if (target.Contains('$'))
                {
                    notes.Add($"location '{rawPath}' 的 proxy_pass 使用了变量 '{target}'，无法静态解析，已跳过。");
                    continue;
                }

                var clusterId = ResolveCluster(target, upstreams, clusters, ref inlineSeq, notes, rawPath, out var rewriteUri);
                if (clusterId is null) continue;

                // 路由匹配路径
                var trimmed = rawPath.TrimEnd('/');
                string pattern = isExact
                    ? (trimmed.Length == 0 ? "/" : trimmed)
                    : (trimmed.Length == 0 ? "/{**catch-all}" : trimmed + "/{**catch-all}");
                if (isExact)
                    notes.Add($"location = '{rawPath}' 为精确匹配，YARP 仅支持前缀模式，已按近似路径 '{pattern}' 生成，请复核。");

                var match = new Dictionary<string, object?> { ["Path"] = pattern };
                if (hostList.Count > 0) match["Hosts"] = hostList.ToArray();

                var transforms = new List<object?>();
                var locationPrefix = trimmed;
                if (!string.IsNullOrEmpty(rewriteUri) && rewriteUri != "/")
                {
                    if (!string.IsNullOrEmpty(locationPrefix))
                        transforms.Add(new Dictionary<string, object?> { ["PathRemovePrefix"] = locationPrefix });
                    transforms.Add(new Dictionary<string, object?> { ["PathPrefix"] = rewriteUri });
                    notes.Add($"location '{rawPath}' 的 proxy_pass 带 URI '{rewriteUri}'，已生成 PathRemovePrefix/PathPrefix 变换，请复核路径拼接。");
                }

                foreach (var sh in loc.Children.Where(c => c.Name.Equals("proxy_set_header", StringComparison.OrdinalIgnoreCase)))
                {
                    if (sh.Args.Count < 2) continue;
                    var header = sh.Args[0];
                    var value = sh.Args[1];
                    if (value.Contains('$'))
                    {
                        notes.Add($"location '{rawPath}' 的 proxy_set_header {header} 含 nginx 变量 '{value}'，无法自动映射，已跳过，请人工转换。");
                        continue;
                    }
                    // YARP 2.3 格式：{ "RequestHeader": "<头名>", "Set": "<值>" }（两个 key 同级）
                    transforms.Add(new Dictionary<string, object?>
                    {
                        ["RequestHeader"] = header,
                        ["Set"] = value
                    });
                }

                var baseId = string.IsNullOrEmpty(trimmed) || trimmed == "/"
                    ? "root"
                    : SanitizeId(trimmed.TrimStart('/'));
                var routeId = baseId;
                while (!usedRouteIds.Add(routeId)) routeId = baseId + "-" + (++routeSeq);

                var route = new Dictionary<string, object?>
                {
                    ["ClusterId"] = clusterId,
                    ["Match"] = match
                };
                if (transforms.Count > 0) route["Transforms"] = transforms;
                routes[routeId] = route;
                routeSeq++;
            }
        }

        if (routes.Count == 0)
            notes.Add("未识别到任何可转换的 location/proxy_pass，请检查输入是否为完整 nginx.conf。");
        notes.Add("TLS 证书、静态文件 root、rewrite、限流等指令未自动转换；TLS 建议在 IIS 层终止，静态文件可用网关内置 FileServer。");

        var json = BuildPayload(routes, clusters, notes);
        return new NginxConversionResult(json, notes, routes.Count, clusters.Count);
    }

    private sealed record UpstreamServer(string Address, int Weight);

    private static string BuildPayload(Dictionary<string, object?> routes,
        Dictionary<string, object?> clusters, IReadOnlyList<string> notes)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ReverseProxy"] = new Dictionary<string, object?>
            {
                ["Routes"] = routes,
                ["Clusters"] = clusters
            },
            ["GatewayMigrationHints"] = new Dictionary<string, object?>
            {
                ["Note"] = "将 ReverseProxy 节点合并进 appsettings.json；GatewayMigrationHints 仅供人工复核，不参与配置。",
                ["Count"] = notes.Count,
                ["Items"] = notes.ToArray()
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string? ResolveCluster(string target, Dictionary<string, List<UpstreamServer>> upstreams,
        Dictionary<string, object?> clusters, ref int inlineSeq, List<string> notes,
        string locationPath, out string? rewriteUri)
    {
        rewriteUri = null;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var authorityHost = uri.Host;
            // 指向已知 upstream 且无具体 URI 路径 → 直接复用集群
            if (upstreams.ContainsKey(authorityHost) &&
                (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"))
            {
                return authorityHost;
            }

            var path = uri.AbsolutePath;
            if (!string.IsNullOrEmpty(path) && path != "/")
                rewriteUri = path.TrimEnd('/');

            var id = "inline-" + SanitizeId(uri.Authority) + "-" + (++inlineSeq);
            var address = uri.Scheme + "://" + uri.Authority + "/";
            clusters[id] = new Dictionary<string, object?>
            {
                ["Destinations"] = new Dictionary<string, object?>
                {
                    ["d1"] = new Dictionary<string, object?> { ["Address"] = address }
                }
            };
            notes.Add($"location '{locationPath}' 的 proxy_pass '{target}' 已生成内联集群 '{id}'（{address}）。");
            return id;
        }

        // 形如 proxy_pass backend; 直接引用 upstream 名
        if (upstreams.ContainsKey(target)) return target;

        notes.Add($"无法解析 proxy_pass '{target}'，已跳过 location '{locationPath}'。");
        return null;
    }

    private static string NormalizeAddress(string raw)
    {
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return raw.TrimEnd('/') + "/";
        return "http://" + raw.TrimEnd('/') + "/";
    }

    private static bool ParseKeyValue(IEnumerable<string> args, string key, out string value)
    {
        foreach (var a in args)
        {
            var idx = a.IndexOf('=');
            if (idx > 0 && a[..idx].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = a[(idx + 1)..];
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static string SanitizeId(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else if (c == '/' || c == '.' || c == ':' || c == ' ') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "cluster" : s;
    }

    private static IEnumerable<Directive> FindAll(IEnumerable<Directive> nodes, string name)
    {
        foreach (var n in nodes)
        {
            if (n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) yield return n;
            foreach (var child in FindAll(n.Children, name)) yield return child;
        }
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}' or ';') { list.Add(new Token(TokenKind.Symbol, c.ToString())); i++; continue; }
            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != quote)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        var next = s[i + 1];
                        // nginx 引号内只把 \\ 与 \" / \' 当转义；其余反斜杠（如 Windows 路径
                        // C:\new\site）必须原样保留，否则会被错误地删掉字符。
                        if (next == '\\' || next == quote) { sb.Append(next); i += 2; }
                        else { sb.Append('\\'); i++; }
                    }
                    else sb.Append(s[i++]);
                }
                if (i < s.Length) i++;
                list.Add(new Token(TokenKind.Quoted, sb.ToString()));
                continue;
            }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '#' &&
                   s[i] != '{' && s[i] != '}' && s[i] != ';' && s[i] != '"' && s[i] != '\'') i++;
            if (i > start) list.Add(new Token(TokenKind.Word, s[start..i]));
            else i++; // 保险：避免无法前进
        }
        return list;
    }

    private static List<Directive> ParseBlock(List<Token> tokens, ref int pos, bool stopAtBrace)
    {
        var result = new List<Directive>();
        while (pos < tokens.Count)
        {
            var t = tokens[pos];
            if (t.Kind == TokenKind.Symbol && t.Value == "}")
            {
                pos++;
                if (stopAtBrace) return result;
                continue;
            }
            if (t.Kind == TokenKind.Symbol && t.Value == ";") { pos++; continue; }

            var d = new Directive { Name = t.Value };
            pos++;
            while (pos < tokens.Count)
            {
                var a = tokens[pos];
                if (a.Kind == TokenKind.Symbol && (a.Value == ";" || a.Value == "{" || a.Value == "}")) break;
                d.Args.Add(a.Value);
                pos++;
            }
            if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Symbol && tokens[pos].Value == "{")
            {
                pos++;
                d.Children = ParseBlock(tokens, ref pos, stopAtBrace: true);
            }
            else if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Symbol && tokens[pos].Value == ";")
            {
                pos++;
            }
            result.Add(d);
        }
        return result;
    }
}
