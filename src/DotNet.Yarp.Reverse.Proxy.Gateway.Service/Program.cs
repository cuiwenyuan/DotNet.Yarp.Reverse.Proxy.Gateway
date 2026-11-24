using DotNet.Yarp.Reverse.Proxy.Gateway.Hosting;
using DotNet.Yarp.Reverse.Proxy.Gateway.Service;

// Windows 服务宿主入口。
//
// 三分流（缺一不可）：
//   A. 工具型 CLI —— --test-config / --diff-config / --convert-nginx，处理完即退出；
//   B. 无参数 —— 直接前台运行网关。保持 "dotnet <dll> 就是启动网关" 的既有契约，
//      也方便容器/排查/CI 冒烟使用（Agent 默认把无参数解释成"进交互菜单"，
//      在无控制台环境会直接抛异常，所以不能把它交给 Agent）；
//   C. Agent 命令 —— -i/-u/-start/-stop/-restart/-status/-run/-s/-watch，交给 ServiceBase 处理。

if (GatewayCommandLine.TryHandle(args)) return;

if (args.Length == 0)
{
    GatewayHostFactory.Build(Array.Empty<String>()).Run();
    return;
}

if (!IsAgentCommand(args[0]))
{
    Console.Error.WriteLine($"未知参数：{args[0]}");
    Console.Error.WriteLine("用法：");
    Console.Error.WriteLine("  <exe> -i | -install      安装并启动服务（需管理员）");
    Console.Error.WriteLine("  <exe> -u | -uninstall    停止并卸载服务（需管理员）");
    Console.Error.WriteLine("  <exe> -start | -stop | -restart | -status   服务生命周期");
    Console.Error.WriteLine("  <exe> -run               前台模拟运行（需交互式终端）");
    Console.Error.WriteLine("  <exe>                    直接前台运行网关（等价 nginx 前台启动）");
    Console.Error.WriteLine("  <exe> --test-config      配置预检（等价 nginx -t）");
    Environment.ExitCode = 2;
    return;
}

// 只把宿主相关参数传给 Web 应用：-s / -run 这类 Agent 参数若进入
// WebApplicationOptions.Args，会被 ASP.NET Core 的配置系统当成未知开关处理。
new GatewayService(SelectHostArgs(args)).Main(args);

static Boolean IsAgentCommand(String arg) => arg.ToLowerInvariant() switch
{
    "-i" or "-install" => true,
    "-u" or "-uninstall" => true,
    "-reinstall" => true,
    "-start" or "-stop" or "-restart" or "-status" => true,
    "-run" or "-s" or "-watch" => true,
    _ => false,
};

static String[] SelectHostArgs(String[] args)
{
    String[] known = { "--contentRoot", "--urls", "--environment" };
    var kept = new List<String>();
    for (var i = 0; i < args.Length; i++)
    {
        if (Array.Exists(known, k => k.Equals(args[i], StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
        {
            kept.Add(args[i]);
            kept.Add(args[++i]);
        }
    }
    return kept.ToArray();
}
