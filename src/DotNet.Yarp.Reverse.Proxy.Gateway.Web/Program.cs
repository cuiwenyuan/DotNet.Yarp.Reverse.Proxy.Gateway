using DotNet.Yarp.Reverse.Proxy.Gateway.Hosting;

// 网关宿主（IIS / ANCM in-process，也可直接 `dotnet <dll>` 前台运行）。
//
// 本文件刻意保持"薄"：只做两件事——识别工具型命令行、把启动交给 Hosting。
// 所有服务注册与中间件顺序都在 Gateway.Hosting.GatewayHostFactory 里，
// 与 Windows 服务宿主（Gateway.Service）共用同一份，避免两个宿主各写一套管线而悄悄漂移。

// (1) 工具型命令行：--test-config（配置预检，等价 nginx -t）、--diff-config（配置差异预览）、
//     --convert-nginx（nginx 配置转换）。处理完即退出，绝不继续启动监听。
if (GatewayCommandLine.TryHandle(args)) return;

// (2) 构建并运行。ContentRoot 走 --contentRoot → ASPNETCORE_CONTENTROOT → 程序目录，
//     保证"在哪个目录被拉起"都不影响 appsettings.json 的定位。
var app = GatewayHostFactory.Build(args);
app.Run();
