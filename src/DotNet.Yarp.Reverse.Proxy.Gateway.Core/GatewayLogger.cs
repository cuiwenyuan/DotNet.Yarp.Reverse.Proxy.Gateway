using NewLife.Log;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 日志封装：仅依赖 NewLife.Core 的 XLog（XTrace），满足"只允许 NewLife.Core"的约束。
/// 若后续要切换日志后端，只需改这里，不动业务中间件。
/// </summary>
public static class GatewayLogger
{
    public static void Info(string message) => XTrace.WriteLine(message);

    public static void Warn(string message) => XTrace.WriteLine("WARN " + message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
            XTrace.WriteLine("ERROR " + message);
        else
            XTrace.WriteException(ex);
    }
}
