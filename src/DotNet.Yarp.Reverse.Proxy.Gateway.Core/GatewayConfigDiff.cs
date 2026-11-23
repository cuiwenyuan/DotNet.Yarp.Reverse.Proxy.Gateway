using System.Text.Json;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>配置差异比对结果。<see cref="Lines"/> 每条形如 <c>+ path</c> / <c>- path</c> / <c>~ path  old -&gt; new</c>。</summary>
public sealed record ConfigDiffResult(bool HasChanges, IReadOnlyList<string> Lines, string? Error)
{
    public static ConfigDiffResult Fail(string error) => new(false, Array.Empty<string>(), error);
}

/// <summary>
/// 两份网关配置的差异比对（纯 BCL）。用于 <c>--diff-config</c>：在把新配置投入热重载前，
/// 先看清"会新增/删除/改动哪些路由、集群、治理参数"，避免盲改生产配置。
/// <para>只比对 <c>ReverseProxy:R*</c>（Routes / Clusters 及其它节点）与 <c>Gateway</c>；
/// 日志框架、宿主等无关节点一律忽略。</para>
/// </summary>
public static class GatewayConfigDiff
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>比对两份 JSON 文本（通常是 appsettings.json 的旧版与新版）。</summary>
    public static ConfigDiffResult Compare(string currentJson, string candidateJson)
    {
        JsonDocument current, candidate;
        try
        {
            current = JsonDocument.Parse(currentJson, DocOptions);
        }
        catch (JsonException ex)
        {
            return ConfigDiffResult.Fail("current config is not valid JSON: " + ex.Message);
        }
        try
        {
            candidate = JsonDocument.Parse(candidateJson, DocOptions);
        }
        catch (JsonException ex)
        {
            current.Dispose();
            return ConfigDiffResult.Fail("candidate config is not valid JSON: " + ex.Message);
        }

        using (current)
        using (candidate)
        {
            var lines = new List<string>();

            var curRoot = current.RootElement;
            var newRoot = candidate.RootElement;

            var curRp = Child(curRoot, "ReverseProxy");
            var newRp = Child(newRoot, "ReverseProxy");

            // 1) 路由、集群：按 Id 粒度给出 + / -，同 Id 内部再给叶子级 ~
            CompareMap(curRp, newRp, "Routes", lines);
            CompareMap(curRp, newRp, "Clusters", lines);

            // 2) ReverseProxy 下其它节点（如 HttpClient 超时）
            CompareLeaves(curRp, newRp, "ReverseProxy", lines, skip: new[] { "Routes", "Clusters" });

            // 3) 治理配置
            CompareLeaves(Child(curRoot, "Gateway"), Child(newRoot, "Gateway"), "Gateway", lines, skip: null);

            lines.Sort(StringComparer.Ordinal);
            return new ConfigDiffResult(lines.Count > 0, lines, null);
        }
    }

    private static JsonElement? Child(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in element.EnumerateObject())
        {
            if (p.NameEquals(name)) return p.Value;
        }
        return null;
    }

    private static void CompareMap(JsonElement? current, JsonElement? candidate, string section, List<string> lines)
    {
        var cur = Child(current ?? default, section);
        var neu = Child(candidate ?? default, section);
        if (cur is null && neu is null) return;

        var basePath = "ReverseProxy:" + section;
        var curKeys = Keys(cur);
        var newKeys = Keys(neu);

        foreach (var key in newKeys)
        {
            if (!curKeys.Contains(key)) lines.Add("+ " + basePath + ":" + key);
        }
        foreach (var key in curKeys)
        {
            if (!newKeys.Contains(key)) lines.Add("- " + basePath + ":" + key);
        }
        foreach (var key in newKeys.Where(curKeys.Contains).OrderBy(k => k, StringComparer.Ordinal))
        {
            CompareLeaves(cur!.Value.GetProperty(key), neu!.Value.GetProperty(key),
                basePath + ":" + key, lines, skip: null);
        }
    }

    private static HashSet<string> Keys(JsonElement? element)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return set;
        foreach (var p in element.Value.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    private static void CompareLeaves(JsonElement? current, JsonElement? candidate, string path,
        List<string> lines, string[]? skip)
    {
        var cur = new Dictionary<string, string>(StringComparer.Ordinal);
        var neu = new Dictionary<string, string>(StringComparer.Ordinal);
        if (current is not null) Flatten(current.Value, path, cur, skip);
        if (candidate is not null) Flatten(candidate.Value, path, neu, skip);

        foreach (var kv in neu)
        {
            if (!cur.TryGetValue(kv.Key, out var oldValue))
                lines.Add("+ " + kv.Key + "  = " + kv.Value);
            else if (!string.Equals(oldValue, kv.Value, StringComparison.Ordinal))
                lines.Add("~ " + kv.Key + "  " + oldValue + " -> " + kv.Value);
        }
        foreach (var kv in cur)
        {
            if (!neu.ContainsKey(kv.Key)) lines.Add("- " + kv.Key);
        }
    }

    private static void Flatten(JsonElement element, string path,
        Dictionary<string, string> sink, string[]? skip)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    if (skip is not null && Array.Exists(skip, s => s.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    Flatten(p.Value, path + ":" + p.Name, sink, skip: null);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, path + "[" + i + "]", sink, skip: null);
                    i++;
                }
                if (i == 0) sink[path] = "[]";
                break;

            default:
                sink[path] = element.GetRawText();
                break;
        }
    }
}
