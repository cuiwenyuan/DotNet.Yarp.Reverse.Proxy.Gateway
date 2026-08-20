using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 网关运行指标采集器（纯 BCL，Interlocked + ConcurrentDictionary 原子计数）。
/// 统计：总请求数、并发数、耗时总和/最大值、按状态码分布、按路径前缀分布。
/// 通过 MetricsMiddleware 的 /metrics 端点以 Prometheus 文本格式暴露。
/// </summary>
public sealed class GatewayMetrics
{
    private long _totalRequests;
    private long _inFlight;
    private long _totalDurationMs;
    private long _maxDurationMs;
    private readonly ConcurrentDictionary<int, long> _statusCodes = new();
    private readonly ConcurrentDictionary<string, long> _pathCounts = new();
    private readonly bool _includePathLabels;

    public GatewayMetrics(MetricsOptions options)
    {
        _includePathLabels = options.IncludePathLabels;
    }

    /// <summary>请求开始：并发计数 +1。应在进入下游管线前调用。</summary>
    public void RecordStart() => Interlocked.Increment(ref _inFlight);

    /// <summary>请求结束：更新总量/耗时/状态码分布。</summary>
    public void RecordEnd(int statusCode, string? path, long elapsedMs)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalDurationMs, elapsedMs);
        Interlocked.Decrement(ref _inFlight);
        UpdateMax(ref _maxDurationMs, elapsedMs);

        _statusCodes.AddOrUpdate(statusCode, 1, (_, c) => c + 1);

        if (_includePathLabels)
        {
            var key = path ?? "/";
            var idx = key.IndexOf('/', 1);
            var prefix = idx > 0 ? key[..idx] : key;
            _pathCounts.AddOrUpdate(prefix, 1, (_, c) => c + 1);
        }
    }

    /// <summary>指标快照（供管理端点 /admin/status 使用）。</summary>
    public MetricsSnapshot GetSnapshot()
    {
        var statusCodes = new Dictionary<int, long>();
        foreach (var kv in _statusCodes)
        {
            _statusCodes.TryGetValue(kv.Key, out var count);
            statusCodes[kv.Key] = count;
        }

        return new MetricsSnapshot(
            Interlocked.Read(ref _totalRequests),
            Interlocked.Read(ref _inFlight),
            Interlocked.Read(ref _totalDurationMs),
            Interlocked.Read(ref _maxDurationMs),
            statusCodes);
    }

    public readonly record struct MetricsSnapshot(
        long TotalRequests, long InFlight, long TotalDurationMs, long MaxDurationMs,
        IReadOnlyDictionary<int, long> StatusCodes);

    /// <summary>渲染 Prometheus 文本格式指标。</summary>
    public string RenderPrometheus()
    {        var sb = new StringBuilder(512);
        sb.Append("# HELP gateway_requests_total Total HTTP requests processed by the gateway.\n");
        sb.Append("# TYPE gateway_requests_total counter\n");
        sb.Append("gateway_requests_total ").Append(Interlocked.Read(ref _totalRequests)).Append('\n');

        sb.Append("# HELP gateway_requests_inflight Currently in-flight requests.\n");
        sb.Append("# TYPE gateway_requests_inflight gauge\n");
        sb.Append("gateway_requests_inflight ").Append(Interlocked.Read(ref _inFlight)).Append('\n');

        sb.Append("# HELP gateway_request_duration_ms_sum Cumulative request duration (ms).\n");
        sb.Append("# TYPE gateway_request_duration_ms_sum counter\n");
        sb.Append("gateway_request_duration_ms_sum ").Append(Interlocked.Read(ref _totalDurationMs)).Append('\n');

        sb.Append("# HELP gateway_request_duration_ms_max Max request duration (ms).\n");
        sb.Append("# TYPE gateway_request_duration_ms_max gauge\n");
        sb.Append("gateway_request_duration_ms_max ").Append(Interlocked.Read(ref _maxDurationMs)).Append('\n');

        sb.Append("# HELP gateway_requests_by_status Requests by HTTP status code.\n");
        sb.Append("# TYPE gateway_requests_by_status counter\n");
        foreach (var kv in _statusCodes.OrderBy(k => k.Key))
        {
            _statusCodes.TryGetValue(kv.Key, out var count);
            sb.Append("gateway_requests_by_status{code=\"").Append(kv.Key)
              .Append("\"} ").Append(count).Append('\n');
        }

        if (_includePathLabels)
        {
            sb.Append("# HELP gateway_requests_by_path Requests by path prefix.\n");
            sb.Append("# TYPE gateway_requests_by_path counter\n");
            foreach (var kv in _pathCounts.OrderBy(k => k.Key))
            {
                _pathCounts.TryGetValue(kv.Key, out var count);
                sb.Append("gateway_requests_by_path{path=\"").Append(kv.Key)
                  .Append("\"} ").Append(count).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static void UpdateMax(ref long field, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref field)))
        {
            Interlocked.CompareExchange(ref field, value, current);
        }
    }
}

/// <summary>
/// 监控指标中间件：最外层挂载。
/// - GET /metrics：返回 Prometheus 文本格式指标；
/// - GET /health：返回 200 ok（存活探针，等价 nginx 的 stub_status / health 端点）；
/// - 其余请求：记录总量/耗时/状态码分布后继续下游。
/// </summary>
public class MetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MetricsOptions _options;
    private readonly GatewayMetrics _metrics;

    public MetricsMiddleware(RequestDelegate next, MetricsOptions options, GatewayMetrics metrics)
    {
        _next = next;
        _options = options;
        _metrics = metrics;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_options.Enabled && HttpMethods.IsGet(context.Request.Method))
        {
            if (context.Request.Path.Equals(_options.MetricsPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain; version=0.0.4";
                await context.Response.WriteAsync(_metrics.RenderPrometheus());
                return;
            }

            if (context.Request.Path.Equals(_options.HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("ok");
                return;
            }
        }

        var sw = Stopwatch.StartNew();
        _metrics.RecordStart();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            _metrics.RecordEnd(context.Response.StatusCode, context.Request.Path.Value, sw.ElapsedMilliseconds);
        }
    }
}
