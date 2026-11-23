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
/// <para>
/// 支持：<c>include</c> 递归展开（含通配符与循环保护）、<c>upstream</c>（weight / down / unix socket）、
/// 多个 <c>server</c> 块（server_name → Hosts）、<c>location</c>（前缀 / 精确 / 命名 / 正则）、
/// <c>proxy_pass</c>（upstream 名、绝对 URL、带 URI 路径）、<c>proxy_set_header</c>、
/// <c>map $host 变量 → proxy_pass $变量</c> 的按 Host 展开、字面前缀 <c>rewrite</c>。
/// </para>
/// 输出可直接合并进 appsettings.json 的 ReverseProxy 节点；无法静态等价的指令写入 Notes 供人工复核。
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

    private sealed record UpstreamServer(string Address, int Weight);

    private sealed record NginxMap(string Source, string? Default, List<KeyValuePair<string, string>> Entries);

    /// <summary>include 嵌套上限，防止病态配置把转换器拖死。</summary>
    private const int MaxIncludeDepth = 8;

    /// <summary>提示条数上限，避免大配置产生上千条 note 淹没终端输出。</summary>
    private const int MaxNotes = 200;

    /// <summary>把 nginx.conf 文本转换为 YARP 配置 JSON（不展开 include，遇到时给出提示）。</summary>
    public static NginxConversionResult Convert(string text) => Convert(text, null);

    /// <summary>
    /// 把 nginx.conf 文本转换为 YARP 配置 JSON。<paramref name="baseDirectory"/> 非空时按其解析
    /// <c>include</c> 的相对路径（递归，相对各自所在文件目录）。
    /// </summary>
    public static NginxConversionResult Convert(string text, string? baseDirectory)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            Note(notes, "输入为空，未生成任何路由。");
            return new NginxConversionResult(BuildPayload(new Dictionary<string, object?>(),
                new Dictionary<string, object?>(), notes), notes, 0, 0);
        }

        if (!string.IsNullOrEmpty(baseDirectory))
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            text = ExpandIncludes(text, Path.GetFullPath(baseDirectory!), notes, visited, 0);
        }
        else if (ContainsInclude(text))
        {
            Note(notes, "检测到 include 指令但未提供基准目录，未展开；请用 --convert-nginx <文件>（或 GUI 打开文件）转换，" +
                        "include 会按各自文件所在目录递归展开。");
        }

        return ConvertCore(text, notes);
    }

    /// <summary>从文件转换，并按文件所在目录递归展开 include（支持 conf.d/*.conf 这类通配符）。</summary>
    public static NginxConversionResult ConvertFile(string path)
    {
        var full = Path.GetFullPath(path);
        var text = File.ReadAllText(full);
        var notes = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full };
        text = ExpandIncludes(text, Path.GetDirectoryName(full) ?? ".", notes, visited, 0);
        return ConvertCore(text, notes);
    }

    // ------------------------------------------------------------------ 主体
    private static NginxConversionResult ConvertCore(string text, List<string> notes)
    {
        var tokens = Tokenize(text);
        var pos = 0;
        var root = ParseBlock(tokens, ref pos, stopAtBrace: false);

        // 收集所有 upstream（http 块内外均可）
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
                    Note(notes, $"upstream '{name}' 包含 unix socket '{addr}'，YARP 不支持，已跳过该节点。");
                    continue;
                }
                var weight = ParseKeyValue(s.Args, "weight", out var w) && int.TryParse(w, out var wi) && wi > 0 ? wi : 1;
                var down = s.Args.Any(a => a.Equals("down", StringComparison.OrdinalIgnoreCase));
                if (down)
                {
                    Note(notes, $"upstream '{name}' 的节点 '{addr}' 标记为 down，已跳过。");
                    continue;
                }
                servers.Add(new UpstreamServer(NormalizeAddress(addr), weight));
            }
            if (servers.Count > 0) upstreams[name] = servers;
        }

        var routes = new Dictionary<string, object?>();
        var clusters = new Dictionary<string, object?>();
        var maps = ParseMaps(root);
        var routeSeq = 0;
        var inlineSeq = 0;
        var usedRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先为每个 upstream 建集群
        foreach (var (name, servers) in upstreams)
        {
            var destinations = new Dictionary<string, object?>();
            var anyWeight = servers.Any(s => s.Weight != 1);
            for (var i = 0; i < servers.Count; i++)
            {
                var s = servers[i];
                var dest = new Dictionary<string, object?> { ["Address"] = s.Address };
                if (anyWeight)
                    dest["Metadata"] = new Dictionary<string, object?> { ["Weight"] = s.Weight.ToString(CultureInfo.InvariantCulture) };
                destinations["d" + (i + 1)] = dest;
            }
            clusters[name] = new Dictionary<string, object?>
            {
                ["LoadBalancingPolicy"] = anyWeight ? "Weighted" : "RoundRobin",
                ["Destinations"] = destinations
            };
        }

        // upstream 里的 "server 127.0.0.1:5101;" 也是 server 指令，必须排除，
        // 否则会被当成虚拟主机计入 server 块统计（实测会把 2 个 server 块数成 6 个）。
        var upstreamNodes = new HashSet<Directive>(
            FindAll(root, "upstream").SelectMany(u => u.Children.Where(c => c.Name.Equals("server", StringComparison.OrdinalIgnoreCase))));
        var serverBlocks = FindAll(root, "server").Where(s => !upstreamNodes.Contains(s)).ToList();
        var multipleServers = serverBlocks.Count > 1;

        for (var si = 0; si < serverBlocks.Count; si++)
        {
            var server = serverBlocks[si];

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

            // server 级整站指令：YARP 无等价能力，只做提示
            var srvReturn = server.Children.FirstOrDefault(c => c.Name.Equals("return", StringComparison.OrdinalIgnoreCase));
            if (srvReturn is not null)
                Note(notes, $"server '{HostsText(hostList)}' 使用 return {string.Join(' ', srvReturn.Args)}（整站跳转/拒绝），" +
                            "YARP 无等价能力，建议在 IIS URL Rewrite 或后端实现。");
            if (server.Children.Any(c => c.Name.StartsWith("ssl_", StringComparison.OrdinalIgnoreCase)))
                Note(notes, "检测到 ssl_certificate/ssl_* 配置：TLS 建议在 IIS 层终止，网关内不做证书绑定。");

            var before = routes.Count;
            var locations = server.Children.Where(c => c.Name.Equals("location", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var loc in locations)
                ProcessLocation(loc, hostList);

            if (multipleServers)
                Note(notes, $"server #{si + 1}（server_name: {HostsText(hostList)}）解析出 {routes.Count - before} 条路由。");
        }

        ReportBodySize(root, notes);

        if (routes.Count == 0)
            Note(notes, "未识别到任何可转换的 location/proxy_pass，请检查输入是否为完整 nginx.conf。");
        Note(notes, "TLS 证书、静态文件 root、正则 rewrite、限流等指令未自动转换；TLS 建议在 IIS 层终止，静态文件可用网关内置 FileServer。");

        var json = BuildPayload(routes, clusters, notes);
        return new NginxConversionResult(json, notes, routes.Count, clusters.Count);

        // -------------------------------------------------------------- 局部函数
        void ProcessLocation(Directive loc, List<string> hosts)
        {
            if (loc.Args.Count == 0) return;
            var rawPath = loc.Args[^1];
            var modifiers = loc.Args.Take(loc.Args.Count - 1).ToArray();
            var isExact = modifiers.Contains("=");
            var isRegex = modifiers.Any(m => m is "~" or "~*");
            var isNamed = rawPath.StartsWith('@');

            if (isNamed)
            {
                Note(notes, $"命名 location '{rawPath}' 属于内部跳转，YARP 无直接等价，已跳过。");
                return;
            }
            if (isRegex)
            {
                Note(notes, $"正则 location '{rawPath}' 无法自动等价为 YARP 路由，已跳过，请人工改写。");
                return;
            }

            var proxyPass = loc.Children.FirstOrDefault(c => c.Name.Equals("proxy_pass", StringComparison.OrdinalIgnoreCase));
            if (proxyPass is null || proxyPass.Args.Count == 0)
            {
                var rewriteOnly = loc.Children.FirstOrDefault(c => c.Name.Equals("rewrite", StringComparison.OrdinalIgnoreCase));
                if (rewriteOnly is not null)
                {
                    Note(notes, $"location '{rawPath}' 只有 rewrite（'{string.Join(' ', rewriteOnly.Args)}'）而没有 proxy_pass：" +
                                "路径重写/跳转需在 IIS URL Rewrite 或后端实现。");
                }
                else
                {
                    Note(notes, $"location '{rawPath}' 无 proxy_pass（可能是静态 root 或 return），未生成路由，请人工处理。");
                }
                return;
            }

            // 路由匹配路径
            var trimmed = rawPath.TrimEnd('/');
            string pattern = isExact
                ? (trimmed.Length == 0 ? "/" : trimmed)
                : (trimmed.Length == 0 ? "/{**catch-all}" : trimmed + "/{**catch-all}");
            if (isExact)
                Note(notes, $"location = '{rawPath}' 为精确匹配，YARP 仅支持前缀模式，已按近似路径 '{pattern}' 生成，请复核。");

            var transforms = new List<object?>();
            var rewritten = ApplyRewrites(loc, rawPath, transforms);

            foreach (var sh in loc.Children.Where(c => c.Name.Equals("proxy_set_header", StringComparison.OrdinalIgnoreCase)))
            {
                if (sh.Args.Count < 2) continue;
                var header = sh.Args[0];
                var value = sh.Args[1];
                if (value.Contains('$'))
                {
                    Note(notes, $"location '{rawPath}' 的 proxy_set_header {header} 含 nginx 变量 '{value}'，无法自动映射，已跳过，请人工转换。");
                    continue;
                }
                // YARP 2.3 格式：{ "RequestHeader": "<头名>", "Set": "<值>" }（两个 key 同级）
                transforms.Add(new Dictionary<string, object?>
                {
                    ["RequestHeader"] = header,
                    ["Set"] = value
                });
            }

            var target = proxyPass.Args[0];

            // proxy_pass $var + map $host → 按 Host 展开成多条路由（nginx 常见的多域名分流写法）
            if (target.Contains('$') && TryExpandMappedTarget(target, hosts, rawPath, pattern, transforms))
                return;

            if (target.Contains('$'))
            {
                Note(notes, $"location '{rawPath}' 的 proxy_pass 使用了变量 '{target}'，无法静态解析，已跳过。");
                return;
            }

            var clusterId = ResolveCluster(target, upstreams, clusters, ref inlineSeq, notes, rawPath, out var rewriteUri);
            if (clusterId is null) return;

            if (!rewritten && !string.IsNullOrEmpty(rewriteUri) && rewriteUri != "/")
            {
                if (!string.IsNullOrEmpty(trimmed))
                    transforms.Add(new Dictionary<string, object?> { ["PathRemovePrefix"] = trimmed });
                transforms.Add(new Dictionary<string, object?> { ["PathPrefix"] = rewriteUri });
                Note(notes, $"location '{rawPath}' 的 proxy_pass 带 URI '{rewriteUri}'，已生成 PathRemovePrefix/PathPrefix 变换，请复核路径拼接。");
            }

            EmitRoute(hosts, clusterId, pattern, null, transforms);
        }

        bool TryExpandMappedTarget(string target, List<string> hosts, string rawPath, string pattern, List<object?> transforms)
        {
            var varName = target.TrimStart('$').Trim('{', '}');
            if (!maps.TryGetValue(varName, out var map)) return false;

            if (!IsHostVariable(map.Source))
            {
                Note(notes, $"map '{varName}'（源 {map.Source}）不是按 Host 取值，YARP 无等价 map 能力，已跳过 location '{rawPath}'。");
                return true;
            }

            var emitted = 0;
            foreach (var kv in map.Entries)
            {
                if (kv.Key.Length == 0)
                {
                    Note(notes, $"map '{varName}' 的空键（''）表示空 Host，YARP 无法表达，已跳过。");
                    continue;
                }
                if (kv.Key.StartsWith('~'))
                {
                    Note(notes, $"map '{varName}' 的正则键 '{kv.Key}' 无法静态展开，已跳过，请人工改写为具体的 Hosts。");
                    continue;
                }

                var cid = ResolveCluster(kv.Value, upstreams, clusters, ref inlineSeq, notes, rawPath, out var uri);
                if (cid is null) continue;
                var t = new List<object?>(transforms);
                if (!string.IsNullOrEmpty(uri) && uri != "/")
                {
                    if (!string.IsNullOrEmpty(rawPath.TrimEnd('/')))
                        t.Add(new Dictionary<string, object?> { ["PathRemovePrefix"] = rawPath.TrimEnd('/') });
                    t.Add(new Dictionary<string, object?> { ["PathPrefix"] = uri });
                }
                // 保留原有 Hosts（server_name）与新键的交集语义：map 键即为该条路由的 Host
                EmitRoute(new List<string> { kv.Key }, cid, pattern, SanitizeId(kv.Key), t);
                emitted++;
            }

            if (map.Default is not null)
            {
                // 兜底路由只覆盖"server_name 里有、但 map 里没有"的 Host。
                // 若直接拿整个 server_name 生成兜底路由，它会和上面的具体 Host 路由同时命中同一条请求，
                // 变成路由歧义（实测会把 b.example.com 抢到 default 集群）。
                var mappedKeys = new HashSet<string>(
                    map.Entries.Select(e => e.Key).Where(k => k.Length > 0 && !k.StartsWith('~')),
                    StringComparer.OrdinalIgnoreCase);
                var uncovered = hosts.Count == 0 ? null : hosts.Where(h => !mappedKeys.Contains(h)).ToList();

                if (hosts.Count == 0 || (uncovered is { Count: > 0 }))
                {
                    var cid = ResolveCluster(map.Default, upstreams, clusters, ref inlineSeq, notes, rawPath, out _);
                    if (cid is not null)
                    {
                        EmitRoute(uncovered ?? new List<string>(), cid, pattern, "default", new List<object?>(transforms));
                        emitted++;
                    }
                }
            }

            Note(notes, $"location '{rawPath}' 的 proxy_pass '{target}' 已按 map '{varName}'（源 {map.Source}）展开为 {emitted} 条按 Host 匹配的路由。");
            return true;
        }

        bool ApplyRewrites(Directive loc, string rawPath, List<object?> transforms)
        {
            var applied = false;
            foreach (var rw in loc.Children.Where(c => c.Name.Equals("rewrite", StringComparison.OrdinalIgnoreCase)))
            {
                if (rw.Args.Count < 2) continue;
                var pattern = rw.Args[0];
                var replacement = rw.Args[1];
                var flag = rw.Args.Count > 2 ? rw.Args[2] : null;

                if (flag is "permanent" or "redirect" or "301" or "302")
                {
                    Note(notes, $"location '{rawPath}' 的 rewrite '{pattern} {replacement} {flag}' 属于重定向，" +
                                "YARP 无等价能力，建议在 IIS URL Rewrite 层实现。");
                    continue;
                }
                if (replacement.Contains('$'))
                {
                    Note(notes, $"location '{rawPath}' 的 rewrite '{pattern} {replacement}' 含捕获组变量，" +
                                "YARP 不支持正则重写，请人工改写。");
                    continue;
                }
                if (!TryLiteralPrefix(pattern, out var prefix))
                {
                    Note(notes, $"location '{rawPath}' 的 rewrite '{pattern}' 含正则语法，YARP 不支持正则重写，请人工改写。");
                    continue;
                }
                if (!replacement.StartsWith('/'))
                {
                    Note(notes, $"location '{rawPath}' 的 rewrite 目标 '{replacement}' 不是绝对路径，已跳过。");
                    continue;
                }

                transforms.Add(new Dictionary<string, object?> { ["PathRemovePrefix"] = prefix });
                var target = replacement.TrimEnd('/');
                if (target.Length > 0)
                    transforms.Add(new Dictionary<string, object?> { ["PathPrefix"] = target });
                applied = true;
                Note(notes, $"location '{rawPath}' 的 rewrite '{pattern} {replacement}' 已近似转换为 " +
                            $"PathRemovePrefix('{prefix}')" + (target.Length > 0 ? $" + PathPrefix('{target}')" : "") + "，请复核。");
            }
            return applied;
        }

        void EmitRoute(List<string> hosts, string clusterId, string pattern, string? suffix, List<object?> transforms)
        {
            var match = new Dictionary<string, object?> { ["Path"] = pattern };
            if (hosts.Count > 0) match["Hosts"] = hosts.ToArray();

            var baseId = pattern == "/{**catch-all}"
                ? "root"
                : SanitizeId(pattern.Replace("{**catch-all}", string.Empty).Trim('/'));
            if (!string.IsNullOrEmpty(suffix)) baseId = baseId + "-" + suffix;

            var routeId = baseId;
            while (!usedRouteIds.Add(routeId)) routeId = baseId + "-" + (++routeSeq);

            var route = new Dictionary<string, object?>
            {
                ["ClusterId"] = clusterId,
                ["Match"] = match
            };
            if (transforms.Count > 0) route["Transforms"] = new List<object?>(transforms);
            routes[routeId] = route;
            routeSeq++;
        }
    }

    // ------------------------------------------------------------------ include
    private static bool ContainsInclude(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("include", StringComparison.OrdinalIgnoreCase)
                && t.Length > 7 && char.IsWhiteSpace(t[7])) return true;
        }
        return false;
    }

    /// <summary>
    /// 把 <c>include</c> 指令就地替换为被包含文件的文本（递归）。相对路径按当前文件所在目录解析，
    /// 与运维把 conf.d 放在主配置旁边的常见布局一致；绝对路径原样使用。
    /// </summary>
    private static string ExpandIncludes(string text, string baseDirectory, List<string> notes,
        HashSet<string> visited, int depth)
    {
        if (depth >= MaxIncludeDepth)
        {
            Note(notes, $"include 嵌套超过 {MaxIncludeDepth} 层，已停止展开（可能存在环）。");
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var semi = trimmed.IndexOf(';');
            if (!trimmed.StartsWith("include", StringComparison.OrdinalIgnoreCase)
                || trimmed.Length <= 7 || !char.IsWhiteSpace(trimmed[7]) || semi < 0)
            {
                sb.Append(line).Append('\n');
                continue;
            }

            var spec = trimmed[7..semi].Trim().Trim('"', '\'');
            if (spec.Length == 0)
            {
                sb.Append(line).Append('\n');
                continue;
            }

            foreach (var file in ResolveIncludeFiles(spec, baseDirectory, notes))
            {
                var full = Path.GetFullPath(file);
                if (!visited.Add(full))
                {
                    Note(notes, $"include 重复引用 '{full}'，已跳过以避免重复生成路由。");
                    continue;
                }
                try
                {
                    var nested = File.ReadAllText(full);
                    sb.Append("# >>> include ").Append(full).Append('\n');
                    sb.Append(ExpandIncludes(nested, Path.GetDirectoryName(full) ?? baseDirectory, notes, visited, depth + 1));
                }
                catch (Exception ex)
                {
                    Note(notes, $"读取 include 文件 '{full}' 失败：{ex.Message}");
                }
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> ResolveIncludeFiles(string spec, string baseDirectory, List<string> notes)
    {
        var pattern = spec.Replace('\\', '/');
        var firstWildcard = pattern.IndexOfAny(new[] { '*', '?' });
        try
        {
            if (firstWildcard >= 0)
            {
                var slash = pattern.LastIndexOf('/', firstWildcard);
                var dirPart = slash >= 0 ? pattern[..slash] : ".";
                var filePart = slash >= 0 ? pattern[(slash + 1)..] : pattern;
                var dir = Path.IsPathRooted(dirPart) ? dirPart : Path.Combine(baseDirectory, dirPart);
                if (!Directory.Exists(dir))
                {
                    Note(notes, $"include '{spec}' 的目录不存在：{dir}，已跳过。");
                    return Array.Empty<string>();
                }
                var files = Directory.GetFiles(dir, filePart);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                if (files.Length == 0) Note(notes, $"include '{spec}' 未匹配到任何文件，已跳过。");
                return files;
            }

            var path = Path.IsPathRooted(pattern) ? pattern : Path.Combine(baseDirectory, pattern);
            if (File.Exists(path)) return new[] { path };
            Note(notes, $"include '{spec}' 指向的文件不存在（解析为 {path}），已跳过。");
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Note(notes, $"include '{spec}' 解析失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------ map
    private static Dictionary<string, NginxMap> ParseMaps(IEnumerable<Directive> nodes)
    {
        var maps = new Dictionary<string, NginxMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in FindAll(nodes, "map"))
        {
            if (m.Args.Count < 2 || !m.Args[1].StartsWith('$')) continue;
            var name = m.Args[1].TrimStart('$').Trim('{', '}');
            string? def = null;
            var entries = new List<KeyValuePair<string, string>>();
            foreach (var e in m.Children)
            {
                if (e.Args.Count == 0) continue;
                if (e.Name.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    def = e.Args[0];
                    continue;
                }
                entries.Add(new KeyValuePair<string, string>(e.Name, e.Args[0]));
            }
            if (maps.ContainsKey(name)) continue;
            maps[name] = new NginxMap(m.Args[0], def, entries);
        }
        return maps;
    }

    private static bool IsHostVariable(string source)
    {
        var s = source.Trim().TrimStart('$').Trim('{', '}');
        return s.Equals("host", StringComparison.OrdinalIgnoreCase)
               || s.Equals("http_host", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ 其它指令
    private static void ReportBodySize(IEnumerable<Directive> root, List<string> notes)
    {
        var cms = FindAll(root, "client_max_body_size").FirstOrDefault();
        if (cms is null || cms.Args.Count == 0) return;
        var raw = cms.Args[0];
        if (!TryParseSize(raw, out var bytes))
        {
            Note(notes, $"client_max_body_size '{raw}' 无法解析，请人工设置 Gateway:RequestSize:MaxBytes。");
            return;
        }
        Note(notes, $"client_max_body_size {raw} 未自动写入配置：请设置 Gateway:RequestSize:Enabled=true、MaxBytes={bytes}。");
    }

    private static bool TryParseSize(string raw, out long bytes)
    {
        bytes = 0;
        var s = raw.Trim().ToLowerInvariant();
        long multiplier = 1;
        if (s.EndsWith('k')) { multiplier = 1024; s = s[..^1]; }
        else if (s.EndsWith('m')) { multiplier = 1024 * 1024; s = s[..^1]; }
        else if (s.EndsWith('g')) { multiplier = 1024L * 1024 * 1024; s = s[..^1]; }
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
        bytes = n * multiplier;
        return true;
    }

    /// <summary>
    /// 判断 rewrite 的正则是否等价于"字面前缀"：<c>^/old/</c>、<c>^/old$</c> 可近似映射为
    /// PathRemovePrefix；含 <c>()[]|+*?\</c> 等元字符的一律交回人工。
    /// </summary>
    private static bool TryLiteralPrefix(string regex, out string prefix)
    {
        prefix = string.Empty;
        if (!regex.StartsWith('^')) return false;
        var body = regex[1..];
        if (body.EndsWith('$')) body = body[..^1];
        if (!body.StartsWith('/')) return false;
        foreach (var c in body)
        {
            if ("[](){}|*+?\\.^".IndexOf(c) >= 0) return false;
        }
        prefix = body.TrimEnd('/');
        return prefix.Length > 0;
    }

    // ------------------------------------------------------------------ 输出
    private static string HostsText(List<string> hosts) => hosts.Count == 0 ? "(未指定 server_name)" : string.Join(", ", hosts);

    private static void Note(List<string> notes, string message)
    {
        if (notes.Count >= MaxNotes) return;
        if (notes.Contains(message)) return;
        notes.Add(message);
    }

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
            Note(notes, $"location '{locationPath}' 的 proxy_pass '{target}' 已生成内联集群 '{id}'（{address}）。");
            return id;
        }

        // 形如 proxy_pass backend; 直接引用 upstream 名
        if (upstreams.ContainsKey(target)) return target;

        Note(notes, $"无法解析 proxy_pass '{target}'，已跳过 location '{locationPath}'。");
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

    // ------------------------------------------------------------------ 词法 / 语法
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
