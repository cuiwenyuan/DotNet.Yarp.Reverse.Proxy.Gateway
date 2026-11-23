using NewLife.Log;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 日志封装：仅依赖 NewLife.Core 的 XLog（XTrace），满足"只允许 NewLife.Core"的约束。
/// 若后续要切换日志后端，只需改这里，不动业务中间件。
/// 请求上下文内自动附带链路 ID（见 CorrelationContext），把同一次请求的日志串起来。
/// </summary>
public static class GatewayLogger
{
    // 有链路 ID 时加 "[req=xxxx] " 前缀；非请求上下文（启动/后台）不加，保持原样。
    private static string Tag()
    {
        var id = CorrelationContext.RequestId;
        return string.IsNullOrEmpty(id) ? string.Empty : "[req=" + id + "] ";
    }

    public static void Info(string message) => XTrace.WriteLine(Tag() + message);

    public static void Warn(string message) => XTrace.WriteLine(Tag() + "WARN " + message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
            XTrace.WriteLine(Tag() + "ERROR " + message);
        else
            XTrace.WriteException(ex);
    }
}
