using System.Text;
using DotNet.Yarp.Reverse.Proxy.Gateway.Core;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Hosting;

/// <summary>
/// 宿主无关的命令行处理：配置预检（--test-config）、配置差异预览（--diff-config）、
/// nginx 配置转换（--convert-nginx），以及 ContentRoot 解析。
///
/// 两个宿主（Web / Service）在启动监听之前都调用 <see cref="TryHandle"/>：
/// 返回 true 表示本次进程只做了一次工具性操作（已设置退出码），宿主不得继续启动。
/// </summary>
public static class GatewayCommandLine
{
    /// <summary>处理工具型命令行模式。返回 true 表示已处理完毕，调用方应立即退出。</summary>
    public static bool TryHandle(string[] args)
    {
        // (0) 配置预检模式（对应 nginx -t / GoReverseProxy --test-config）：
        //     dotnet <host>.dll --test-config
        //     校验 appsettings.json（治理配置绑定、路由/集群唯一性、后端地址合法性），通过输出 OK 并退出 0。
        if (args.Contains("--test-config") || args.Contains("-t"))
        {
            // 预检必须校验运维指定的同一份配置；原先固定读 BaseDirectory，
            // 导致 "--contentRoot <dir> --test-config" 出现"校验通过但实际配置非法"的假阴性。
            var configPath = ResolveConfigPath(args);
            var ok = ConfigValidator.Validate(configPath, out var configError);
            Console.WriteLine(ok ? "Configuration OK" : $"Configuration error: {configError}");
            // 校验失败必须以非零退出码返回，否则 CI 预检步骤（--test-config）形同虚设。
            if (!ok) Environment.ExitCode = 1;
            return true;
        }

        // (0.2) 配置差异预览：把候选配置与当前生效配置对比，先看清"会新增/删除/改动什么"再决定是否热重载。
        //   dotnet <host>.dll --diff-config <candidate.json> [--configPath <current.json> | --contentRoot <dir>]
        //   退出码：0 = 等价；1 = 有差异；2 = 参数或解析错误。
        var diffIdx = Array.FindIndex(args, a =>
            a.Equals("--diff-config", StringComparison.OrdinalIgnoreCase) || a.Equals("--diff", StringComparison.OrdinalIgnoreCase));
        if (diffIdx >= 0)
        {
            var candidatePath = diffIdx + 1 < args.Length && !args[diffIdx + 1].StartsWith('-') ? args[diffIdx + 1] : null;
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
            {
                Console.Error.WriteLine("Usage: --diff-config <candidate.json> [--configPath <current.json>] [--contentRoot <dir>]");
                Console.Error.WriteLine($"Candidate file not found: {candidatePath}");
                Environment.ExitCode = 2;
                return true;
            }

            var currentPath = ResolveConfigPath(args);
            if (!File.Exists(currentPath))
            {
                Console.Error.WriteLine($"Current config not found: {currentPath}");
                Environment.ExitCode = 2;
                return true;
            }

            try
            {
                var diff = GatewayConfigDiff.Compare(File.ReadAllText(currentPath), File.ReadAllText(candidatePath));
                if (diff.Error is not null)
                {
                    Console.Error.WriteLine("Config diff failed: " + diff.Error);
                    Environment.ExitCode = 2;
                    return true;
                }

                Console.WriteLine($"Comparing {currentPath} -> {candidatePath}");
                if (!diff.HasChanges)
                {
                    Console.WriteLine("No differences (ReverseProxy routes/clusters and Gateway options are identical).");
                    return true;
                }

                foreach (var line in diff.Lines) Console.WriteLine(line);
                Console.WriteLine();
                Console.WriteLine($"{diff.Lines.Count} change(s): " +
                                  $"+{diff.Lines.Count(l => l.StartsWith("+ "))} " +
                                  $"-{diff.Lines.Count(l => l.StartsWith("- "))} " +
                                  $"~{diff.Lines.Count(l => l.StartsWith("~ "))}");

                // 与 diff(1) 一致：有差异时返回 1，便于脚本判断"要不要真的应用这份配置"。
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Config diff failed: " + ex.Message);
                Environment.ExitCode = 2;
            }
            return true;
        }

        // (0.5) nginx.conf 转换模式（等价 nginx 配置迁移工具）：
        //   dotnet <host>.dll --convert-nginx <nginx.conf> [-o <output.json>]
        //   默认输出到 stdout；指定 -o 则写入文件。转换结果含 ReverseProxy 节点与人工复核提示。
        var convertIdx = Array.FindIndex(args, a => a.Equals("--convert-nginx", StringComparison.OrdinalIgnoreCase));
        if (convertIdx >= 0)
        {
            // 兼容 "--convert-nginx -o out.json file.conf" 这类选项前置写法：跳过选项及其值再取位置参数。
            string? inputPath = null;
            for (var i = convertIdx + 1; i < args.Length; i++)
            {
                if (args[i].Equals("-o", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
                if (args[i].StartsWith('-')) continue;
                inputPath = args[i];
                break;
            }
            var outIdx = Array.FindIndex(args, a => a.Equals("-o", StringComparison.OrdinalIgnoreCase));
            var outputPath = outIdx >= 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : null;
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                Console.Error.WriteLine("Usage: --convert-nginx <nginx.conf> [-o <output.json>]");
                Console.Error.WriteLine($"Input file not found: {inputPath}");
                Environment.ExitCode = 2;
                return true;
            }
            try
            {
                // 从文件转换（而不是传入文本）：这样 include 能按各自文件所在目录递归展开。
                var result = GatewayExtensions.ConvertNginxConfigFile(inputPath!);
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Console.WriteLine(result.Json);
                }
                else
                {
                    File.WriteAllText(outputPath, result.Json, new UTF8Encoding(false));
                    Console.WriteLine($"Converted {result.RouteCount} route(s), {result.ClusterCount} cluster(s) -> {outputPath}");
                }
                Console.Error.WriteLine($"Migration notes ({result.Notes.Count}):");
                foreach (var note in result.Notes) Console.Error.WriteLine("  - " + note);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Conversion failed: " + ex.Message);
                Environment.ExitCode = 1;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析 ContentRoot。顺序：<c>--contentRoot</c> → 环境变量 <c>ASPNETCORE_CONTENTROOT</c> → 程序目录。
    ///
    /// 为什么不能依赖默认值：Windows 服务的当前工作目录是 <c>C:\Windows\System32</c>，
    /// 而 ASP.NET Core 默认以工作目录作为 ContentRoot——读不到 appsettings.json 就等于
    /// ReverseProxy 节点为空、所有路由 404。IIS/ANCM 托管时站点物理目录恰好等于程序目录，
    /// 所以固定用程序目录对两种宿主都正确。
    /// </summary>
    public static string ResolveContentRoot(string[] args)
    {
        var idx = Array.FindIndex(args, a => a.Equals("--contentRoot", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[idx + 1]))
            return Path.GetFullPath(args[idx + 1]);

        var fromEnv = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 解析"当前生效配置"的路径：<c>--configPath</c> 优先，其次 ContentRoot/appsettings.json。
    /// 预检（--test-config）与差异比对（--diff-config）必须读同一份文件，
    /// 避免"校验通过但比对了另一份"。
    /// </summary>
    public static string ResolveConfigPath(string[] args)
    {
        var explicitPathIdx = Array.FindIndex(args, a => a.Equals("--configPath", StringComparison.OrdinalIgnoreCase));
        if (explicitPathIdx >= 0 && explicitPathIdx + 1 < args.Length) return args[explicitPathIdx + 1];

        return Path.Combine(ResolveContentRoot(args), "appsettings.json");
    }
}
