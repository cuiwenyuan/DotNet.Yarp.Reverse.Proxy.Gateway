using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// nginx 兼容访问日志配置（对应 appsettings.json 的 "Gateway:NginxAccessLog" 节点）。
/// 输出 nginx combined / common 标准格式，便于复用 GoAccess / ELK 等既有日志分析体系，
/// 降低从 nginx 迁移的观测成本。纯 BCL 实现，零第三方 NuGet。
/// </summary>
public class NginxAccessLogOptions
{
    /// <summary>是否启用访问日志。默认关闭，避免与现有 ProxyLogging 重复。</summary>
    public bool Enabled { get; set; }

    /// <summary>日志格式：combined（默认，含 referer/user-agent）或 common。</summary>
    public string Format { get; set; } = "combined";

    /// <summary>
    /// 日志文件路径。相对路径基于 AppContext.BaseDirectory；为空时回退到 "logs/access.log"。
    /// </summary>
    public string Path { get; set; } = "logs/access.log";

    /// <summary>单文件轮转阈值（字节），默认 10MB；超过后滚动为 .1/.2 ...。0 表示不轮转。</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>保留的历史文件数量，默认 5。</summary>
    public int RetainedFiles { get; set; } = 5;

    /// <summary>命中这些状态码的请求不写访问日志（如 /health 的 200 可按需过滤，默认不过滤）。</summary>
    public int[] IgnoreStatusCodes { get; set; } = Array.Empty<int>();

    /// <summary>
    /// 是否在标准 nginx 字段之后追加链路 ID 作为末段（形如 "reqid"）。
    /// 默认关闭以保持严格的 nginx combined/common 格式，便于 GoAccess 等工具直接解析。
    /// 开启后仍不影响前面标准字段的位置，只是行尾多一个引号字段。
    /// </summary>
    public bool IncludeCorrelationId { get; set; }
}

/// <summary>
/// nginx 访问日志写入器：线程安全、按大小轮转。作为单例注入，避免每条日志开关文件句柄。
/// 纯 BCL（StreamWriter + lock），不依赖任何日志框架。
/// </summary>
public sealed class NginxAccessLogWriter : IDisposable
{
    // 批量落盘阈值：避免每行一次 Flush 把高 QPS 请求串行化在磁盘 I/O 上。
    private const long FlushThresholdBytes = 64 * 1024;

    private readonly NginxAccessLogOptions _options;
    private readonly object _gate = new();
    private readonly string _fullPath;
    private StreamWriter? _writer;
    private long _currentBytes;

    public NginxAccessLogWriter(NginxAccessLogOptions options)
    {
        _options = options;
        var path = string.IsNullOrWhiteSpace(options.Path) ? "logs/access.log" : options.Path;
        _fullPath = System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(AppContext.BaseDirectory, path);
    }

    /// <summary>追加一行日志；达到阈值时轮转文件。写入异常不会中断请求。</summary>
    public void WriteLine(string line)
    {
        lock (_gate)
        {
            try
            {
                EnsureWriter();
                if (_writer is null) return;
                _writer.WriteLine(line);
                _currentBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_currentBytes >= FlushThresholdBytes)
                {
                    _writer.Flush();
                }
                if (_options.MaxFileBytes > 0 && _currentBytes >= _options.MaxFileBytes)
                    Rotate();
            }
            catch (Exception ex)
            {
                GatewayLogger.Warn("NginxAccessLog 写入失败: " + ex.Message);
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null) return;
        var dir = System.IO.Path.GetDirectoryName(_fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(_fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false
        };
        var fi = new FileInfo(_fullPath);
        _currentBytes = fi.Exists ? fi.Length : 0;
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        _currentBytes = 0;
        var keep = Math.Max(0, _options.RetainedFiles);
        if (keep == 0)
        {
            // "不留历史"必须删掉当前文件再新建，否则 Append 重开会导致文件无限增长。
            try { File.Delete(_fullPath); }
            catch (Exception ex) { GatewayLogger.Warn($"NginxAccessLog 删除轮转文件失败: {ex.Message}"); }
            return;
        }
        // 删除最旧，然后依次后移：access.log.2 <- .1 <- access.log
        for (var i = keep; i >= 1; i--)
        {
            var src = i == 1 ? _fullPath : _fullPath + "." + (i - 1);
            var dst = _fullPath + "." + i;
            if (!File.Exists(src)) continue;
            try
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            catch (Exception ex)
            {
                GatewayLogger.Warn($"NginxAccessLog 轮转 {src}->{dst} 失败: " + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // 进程退出前把未满阈值的数据刷盘，避免丢失最后一段访问日志。
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// nginx 兼容访问日志中间件。输出 combined/common 格式：
///   $remote_addr - $remote_user [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent"
/// 字段说明：
///   - remote_addr 取 X-Forwarded-For 还原后的客户端 IP（UseForwardedHeaders 需在其之前挂载）；
///   - request 为 "METHOD path?query HTTP/x.y"；
///   - body_bytes_sent 通过接管响应流计数，兼容分块传输；
///   - 升级请求（WebSocket/gRPC）不接管响应体，字节数记为 0。
/// 应挂载在治理/压缩之外层、ForwardedHeaders 之后，以记录全部请求并拿到真实 IP。
/// </summary>
public class NginxAccessLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly NginxAccessLogOptions _options;
    private readonly NginxAccessLogWriter _writer;

    public NginxAccessLogMiddleware(RequestDelegate next, NginxAccessLogOptions options, NginxAccessLogWriter writer)
    {
        _next = next;
        _options = options;
        _writer = writer;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var isUpgrade = context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Upgrade");

        // 非升级请求：包装响应流以统计实际写入字节数（含分块）。
        CountingStream? counting = null;
        var originalBody = context.Response.Body;
        if (!isUpgrade)
        {
            counting = new CountingStream(originalBody);
            context.Response.Body = counting;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            var status = context.Response.StatusCode;
            if (counting is not null) context.Response.Body = originalBody;
            if (!_options.IgnoreStatusCodes.Contains(status))
            {
                var bytesSent = counting?.BytesWritten ?? 0;
                var line = FormatLine(context, status, bytesSent);
                _writer.WriteLine(line);
            }
        }
    }

    private string FormatLine(HttpContext context, int status, long bytesSent)
    {
        // $remote_addr：优先取 X-Forwarded-For 首段（若前置代理已设置），否则连接对端 IP。
        var remoteAddr = ResolveClientIp(context);
        // $time_local：[21/Nov/2026:21:20:48 +0800]（时区无冒号）
        var localNow = DateTime.Now;
        var timeLocal = localNow.ToString("dd/MMM/yyyy:HH:mm:ss", CultureInfo.InvariantCulture)
                    + " " + FormatTimeZone(localNow);

        var protocol = context.Request.Protocol;
        if (string.IsNullOrEmpty(protocol)) protocol = "HTTP/1.1";
        var request = context.Request.Method + " " + context.Request.PathBase.Value +
                      context.Request.Path.Value + context.Request.QueryString.Value +
                      " " + protocol;

        var referer = context.Request.Headers.Referer.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var common = string.Concat(
            Escape(remoteAddr), " - - [", timeLocal, "] \"", Escape(request), "\" ",
            status.ToString(CultureInfo.InvariantCulture), " ",
            bytesSent.ToString(CultureInfo.InvariantCulture));

        var isCombined = !_options.Format.Equals("common", StringComparison.OrdinalIgnoreCase);
        var line = isCombined
            ? string.Concat(common, " \"", Escape(referer), "\" \"", Escape(userAgent), "\"")
            : common;

        // 可选：追加链路 ID 末段，把 access log 与 proxy log / XTrace 按同一 ID 串联。
        if (_options.IncludeCorrelationId)
        {
            var requestId = CorrelationContext.FromHttpContext(context);
            if (!string.IsNullOrEmpty(requestId))
                line = string.Concat(line, " \"", Escape(requestId), "\"");
        }
        return line;
    }

    private static string ResolveClientIp(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
        {
            var first = xff.ToString().Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "-";
    }

    private static string FormatTimeZone(DateTime dt)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return sign.ToString() + ((int)abs.TotalHours).ToString("00", CultureInfo.InvariantCulture)
               + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>轻量计数流：仅统计写入字节，其余操作透传到底层流，dispose 不关闭底层。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesWritten { get; private set; }

        public CountingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _inner.WriteAsync(buffer, offset, count, ct);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _inner.WriteAsync(buffer, ct);
            BytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            // 不关闭底层响应流，由 ASP.NET Core 自行管理。
        }
    }
}
