using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 轻量管理端点中间件（对应 nginx plus 的 API / GoReverseProxy 的管理端点）。
/// - GET  /admin/status：运行状态 JSON 摘要（版本、启动时间、运行时长、内存、指标快照、最近重载时间）；
/// - POST /admin/reload：仅限 localhost 调用，热重载可热载的治理配置（RateLimit / IpFilter）。
/// 挂载在最外层（Metrics 之后），绕过治理与限流。纯 BCL 实现。
/// </summary>
public class AdminStatusMiddleware
{
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;
    private static readonly string AssemblyVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private readonly RequestDelegate _next;
    private readonly AdminOptions _options;
    private readonly GatewayMetrics _metrics;
    private readonly GatewayConfigReloader? _reloader;

    public AdminStatusMiddleware(
        RequestDelegate next,
        AdminOptions options,
        GatewayMetrics metrics,
        GatewayConfigReloader? reloader = null)
    {
        _next = next;
        _options = options;
        _metrics = metrics;
        _reloader = reloader;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // POST /admin/reload：配置热重载（仅 localhost）
        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Equals(_options.ReloadPath, StringComparison.OrdinalIgnoreCase))
        {
            await HandleReloadAsync(context);
            return;
        }

        // GET /admin/status：运行状态摘要
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals(_options.Path, StringComparison.OrdinalIgnoreCase))
        {
            await HandleStatusAsync(context);
            return;
        }

        await _next(context);
    }

    private async Task HandleStatusAsync(HttpContext context)
    {
        var snapshot = _metrics.GetSnapshot();
        var payload = new
        {
            app = "DotNet.Yarp.Reverse.Proxy.Gateway",
            version = AssemblyVersion,
            startedAt = StartedAtUtc.ToString("o"),
            uptimeSeconds = (long)(DateTime.UtcNow - StartedAtUtc).TotalSeconds,
            workingSetMB = Math.Round(Environment.WorkingSet / 1024d / 1024d, 1),
            managedMemoryMB = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
            lastConfigReloadAt = _reloader?.LastReloadAtUtc?.ToString("o"),
            metrics = new
            {
                totalRequests = snapshot.TotalRequests,
                inFlight = snapshot.InFlight,
                totalDurationMs = snapshot.TotalDurationMs,
                maxDurationMs = snapshot.MaxDurationMs,
                statusCodes = snapshot.StatusCodes
            }
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private async Task HandleReloadAsync(HttpContext context)
    {
        // 仅允许本机回环地址（127.0.0.1 / ::1）触发重载，防止远程篡改配置
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Access denied: only localhost allowed.");
            return;
        }

        if (_reloader is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"success\":false,\"message\":\"Reloader not registered\"}");
            return;
        }

        var ok = _reloader.Reload(out var error);
        var result = new
        {
            success = ok,
            timestamp = DateTimeOffset.UtcNow,
            message = ok ? "OK" : error,
            reloaded = new[] { "RateLimit", "IpFilter" }
        };

        context.Response.StatusCode = ok ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
