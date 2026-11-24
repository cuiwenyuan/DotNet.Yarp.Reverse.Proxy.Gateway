using DotNet.Yarp.Reverse.Proxy.Gateway.Hosting;
using Microsoft.AspNetCore.Builder;
using NewLife.Agent;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Service;

/// <summary>
/// Windows 服务宿主：把 Gateway.Hosting 组装出来的 Web 应用挂到 NewLife.Agent 的服务外壳上。
///
/// 生命周期契约：
///   StartWork —— 构建并启动 Kestrel，**不阻塞**（必须立刻返回，否则 Agent 的健康检查循环无法运行）；
///   StopWork  —— 在排空窗口内停止应用，让在途请求与长连接有机会自然结束。
///
/// 注意：基类的 StartWork/StopWork 是 public，写成 protected 会编译报 CS0507。
/// </summary>
public sealed class GatewayService : ServiceBase
{
    /// <summary>停机排空窗口。反向代理常有长连接/SSE/WebSocket，5 秒会硬断（S2 会改为可配置项）。</summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly String[] _hostArgs;
    private WebApplication? _app;

    public GatewayService(String[] hostArgs)
    {
        _hostArgs = hostArgs;

        ServiceName = "YarpGateway";
        DisplayName = "YARP Reverse Proxy Gateway";
        Description = ".NET reverse-proxy gateway (YARP) hosted as a Windows service";

        // 关闭 Agent 自带的 Web 管理面板：它默认开启、默认口令 admin/admin、额外监听 5580。
        // 网关自身已有 /admin/status 与 /metrics，多一个默认口令的管理面是纯风险，不是功能。
        // 配置文件 Config/Agent.config 里也写了 false，这里再设一次作为兜底（配置缺失时仍然安全）。
        try
        {
            Setting.Current.EnableWebPanel = false;
        }
        catch
        {
            // 配置系统尚未就绪时忽略：Agent 在 Init 阶段还会按 Config/Agent.config 再读一次。
        }
    }

    public override void StartWork(String reason)
    {
        base.StartWork(reason);

        WriteLog("正在启动网关，ContentRoot={0}，CurrentDirectory={1}",
            AppContext.BaseDirectory, Environment.CurrentDirectory);

        WebApplication app;
        try
        {
            app = GatewayHostFactory.Build(_hostArgs);
        }
        catch (Exception ex)
        {
            // 构建失败（例如 appsettings.json 缺失）必须让 SCM 看到启动失败，
            // 否则服务会显示"正在运行"却什么都不监听。
            WriteLog("网关构建失败：{0}", ex);
            throw;
        }

        _app = app;

        // 不 await：RunAsync 会一直挂到停机信号，阻塞在这里会让 Agent 的运行循环停摆。
        // 异常交给下面的延续处理并写日志，避免"静默死掉但服务状态还是 Running"。
        _ = app.RunAsync().ContinueWith(t =>
        {
            if (t.Exception is not null) WriteLog("网关运行异常：{0}", t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);

        WriteLog("网关已启动（{0}）", reason);
    }

    public override void StopWork(String reason)
    {
        base.StopWork(reason);

        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;

        WriteLog("正在停止网关，排空窗口 {0} 秒（{1}）", ShutdownDrainTimeout.TotalSeconds, reason);
        try
        {
            // 先停监听再排空：StopAsync 会触发 IHostApplicationLifetime.Stopping，
            // Kestrel 停止接受新连接，在窗口内等待在途请求完成。
            app.StopAsync(ShutdownDrainTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteLog("停止网关时出错：{0}", ex);
        }
        finally
        {
            (app as IDisposable)?.Dispose();
            WriteLog("网关已停止");
        }
    }
}
