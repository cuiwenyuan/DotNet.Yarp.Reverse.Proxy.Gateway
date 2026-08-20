using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 代理请求/响应详情日志中间件（对应 Gitee GoReverseProxy 的 ProxyLoggingMiddleware）。
/// 记录每一次经网关的代理详情：方法、完整 URL、客户端 IP、耗时、状态码，并可选择性记录：
///   - 请求头（LogRequestHeaders）
///   - 请求体（LogRequestBody，支持 MaxBodyBytes 截断，仅对文本类 Content-Type 有效）
///   - 响应体（LogResponseBody，同上）
/// 通过 Gateway.ProxyLogging.IgnoreStatusCodes 可配置"忽略这些状态码的日志"（减少噪声）。
///
/// 实现要点（纯 BCL + NewLife.Core，不引入第三方 NuGet）：
///   1) 请求体：EnableBuffering 后读取并复位流位置，下游（YARP/认证）仍可正常读取。
///   2) 响应体：用 MemoryStream 接管 Response.Body，下游写入后读取日志，再拷回原始流。
///   3) WebSocket/gRPC 升级请求不接管响应体，避免破坏双向流。
/// 该中间件注册在治理管线最外层、YARP 之前，因此能完整包裹"请求 -> 后端代理 -> 响应"。
/// </summary>
public class ProxyLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ProxyLoggingOptions _options;

    public ProxyLoggingMiddleware(RequestDelegate next, ProxyLoggingOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // 升级类请求（WebSocket/gRPC）不接管响应体，否则会破坏双向流。
        var isUpgrade = context.WebSockets.IsWebSocketRequest ||
                        context.Request.Headers.ContainsKey("Upgrade");

        // ---- 读取请求体（如有）----
        // 预读条件：全局开关 + 规则允许（无规则=全部；有规则=路径命中才缓冲，状态码在拿到响应后判断）。
        var reqBody = string.Empty;
        if (ShouldBufferRequestBody(context) && context.Request.ContentLength > 0)
        {
            try
            {
                if (IsLoggableBody(context.Request.ContentType) &&
                    context.Request.ContentLength <= _options.MaxBodyBytes)
                {
                    context.Request.EnableBuffering();
                    reqBody = await ReadBodyAsync(context.Request.Body, _options.MaxBodyBytes);
                    context.Request.Body.Position = 0; // 复位，供下游（YARP/认证）读取
                }
            }
            catch (Exception ex)
            {
                GatewayLogger.Warn("ProxyLogging 读取请求体失败: " + ex.Message);
            }
        }

        // ---- 读取请求头（如有）----
        var reqHeaders = string.Empty;
        if (_options.LogRequestHeaders)
        {
            reqHeaders = string.Join("; ", context.Request.Headers
                .Select(h => h.Key + "=" + h.Value.ToString()));
        }

        // ---- 是否接管响应体 ----
        var wrapResponse = _options.LogResponseBody && !isUpgrade;
        var respBody = string.Empty;
        var originalBody = context.Response.Body;

        if (wrapResponse)
        {
            using var mem = new MemoryStream();
            context.Response.Body = mem;
            try
            {
                await _next(context);
            }
            finally
            {
                // 无论下游是否异常，都先把响应流还原，保证上层错误处理可用。
                context.Response.Body = originalBody;
            }

            var status = context.Response.StatusCode;
            stopwatch.Stop();

            // 仅当响应体为文本类且不超过上限时才读取日志（大响应/二进制不读）。
            if (IsLoggableBody(context.Response.ContentType) && mem.Length <= _options.MaxBodyBytes)
            {
                mem.Position = 0;
                respBody = await ReadBodyAsync(mem, _options.MaxBodyBytes);
            }

            // 把真实响应拷回客户端。
            mem.Position = 0;
            await mem.CopyToAsync(originalBody);

            var effectiveReqBody = ShouldLogRequestBody(context, status) ? reqBody : string.Empty;
            EmitLog(context, status, stopwatch.ElapsedMilliseconds, reqHeaders, effectiveReqBody, respBody);
        }
        else
        {
            await _next(context);
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            var effectiveReqBody = ShouldLogRequestBody(context, status) ? reqBody : string.Empty;
            EmitLog(context, status, stopwatch.ElapsedMilliseconds,
                reqHeaders, effectiveReqBody, respBody);
        }
    }

    private void EmitLog(HttpContext context, int status, long elapsedMs,
        string reqHeaders, string reqBody, string respBody)
    {
        // 忽略规则：命中配置中的状态码则不写日志（减少噪声）。
        if (_options.IgnoreStatusCodes.Contains(status)) return;

        var url = context.Request.Scheme + "://" + context.Request.Host + context.Request.Path;
        if (context.Request.QueryString.HasValue) url += context.Request.QueryString;

        if (_options.Json)
        {
            // 单行 JSON，便于 ELK / Loki 结构化采集
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                type = "proxy",
                method = context.Request.Method,
                url,
                clientIp = context.Connection.RemoteIpAddress?.ToString(),
                status,
                elapsedMs,
                reqHeaders = _options.LogRequestHeaders ? reqHeaders : null,
                reqBody = _options.LogRequestBody ? reqBody : null,
                respBody = _options.LogResponseBody ? respBody : null
            }, JsonOptions);
            GatewayLogger.Info(line);
            return;
        }

        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] PROXY ");
        sb.Append(context.Request.Method).Append(' ');
        sb.Append(url);
        sb.Append(" <- ").Append(context.Connection.RemoteIpAddress);
        sb.Append(" => ").Append(status).Append(' ').Append(elapsedMs).Append("ms");

        if (_options.LogRequestHeaders && reqHeaders.Length > 0)
            sb.Append("\n  ReqHeaders: ").Append(reqHeaders);
        if (_options.LogRequestBody && reqBody.Length > 0)
            sb.Append("\n  ReqBody: ").Append(reqBody);
        if (_options.LogResponseBody && respBody.Length > 0)
            sb.Append("\n  RespBody: ").Append(respBody);

        GatewayLogger.Info(sb.ToString());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static bool IsLoggableBody(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var ct = contentType.ToLowerInvariant();
        return ct.Contains("json") || ct.Contains("xml") || ct.Contains("text") ||
               ct.Contains("x-www-form-urlencoded") || ct.Contains("javascript") ||
               ct.Contains("html");
    }

    /// <summary>是否预缓冲请求体（无规则=全部；有规则=仅路径命中 PathContains 才缓冲）。</summary>
    private bool ShouldBufferRequestBody(HttpContext context)
    {
        if (!_options.LogRequestBody) return false;
        if (_options.BodyLogRules.Length == 0) return true;

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var rule in _options.BodyLogRules)
        {
            if (!string.IsNullOrEmpty(rule.PathContains)
                && path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>是否在日志中包含请求体（有规则时需同时命中路径与状态码）。</summary>
    private bool ShouldLogRequestBody(HttpContext context, int status)
    {
        if (!_options.LogRequestBody) return false;
        if (_options.BodyLogRules.Length == 0) return true;

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var rule in _options.BodyLogRules)
        {
            if (string.IsNullOrEmpty(rule.PathContains)) continue;
            if (!path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.StatusCodes.Length == 0 || Array.IndexOf(rule.StatusCodes, status) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>从流读取至多 maxBytes 字节并解码为 UTF-8 字符串（用于记录请求/响应体）。</summary>
    private static async Task<string> ReadBodyAsync(Stream stream, int maxBytes)
    {
        if (stream.CanSeek) stream.Position = 0;
        var buffer = new byte[maxBytes];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(buffer, total, maxBytes - total)) > 0)
        {
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
