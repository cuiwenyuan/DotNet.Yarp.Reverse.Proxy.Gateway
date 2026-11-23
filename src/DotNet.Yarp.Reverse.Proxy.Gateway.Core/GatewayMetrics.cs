using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 网关运行指标采集器（纯 BCL，Interlocked + ConcurrentDictionary 原子计数）。
/// 基础指标：总请求数、并发数、耗时总和/最大值、按状态码分布、按路径前缀分布。
/// 治理指标：限流允许/拒绝/延迟、并发占用/拒绝/等待、熔断拒绝/成败/分桶状态、
/// 缓存命中/未命中/写入/绕过、重试请求/次数/耗尽/失败。
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

    private long _rateLimitAllowed;
    private long _rateLimitRejected;
    private long _rateLimitDelayed;

    private long _concurrencyAcquired;
    private long _concurrencyRejected;
    private long _concurrencyWaitMsTotal;
    private long _concurrencyWaitTimeouts;

    private long _circuitRejected;
    private long _circuitFailures;
    private long _circuitSuccesses;

    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheStores;
    private long _cacheBypassed;

    private long _retryRequests;
    private long _retryAttempts;
    private long _retryExhausted;
    private long _retryFailures;

    private readonly ConcurrencyLimitState? _concurrencyState;
    private readonly CircuitBreakerRegistry? _circuitRegistry;

    public GatewayMetrics(
        MetricsOptions options,
        ConcurrencyLimitState? concurrencyState = null,
        CircuitBreakerRegistry? circuitRegistry = null)
    {
        _includePathLabels = options.IncludePathLabels;
        _concurrencyState = concurrencyState;
        _circuitRegistry = circuitRegistry;
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
            var prefix = GetPathPrefix(path);
            _pathCounts.AddOrUpdate(prefix, 1, (_, c) => c + 1);
        }
    }

    public void RecordRateLimitAllowed() => Interlocked.Increment(ref _rateLimitAllowed);

    public void RecordRateLimitRejected() => Interlocked.Increment(ref _rateLimitRejected);

    public void RecordRateLimitDelayed() => Interlocked.Increment(ref _rateLimitDelayed);

    public void RecordConcurrencyAcquired() => Interlocked.Increment(ref _concurrencyAcquired);

    public void RecordConcurrencyRejected() => Interlocked.Increment(ref _concurrencyRejected);

    public void RecordConcurrencyWait(long waitMs)
    {
        if (waitMs > 0) Interlocked.Add(ref _concurrencyWaitMsTotal, waitMs);
    }

    public void RecordConcurrencyWaitTimeout() => Interlocked.Increment(ref _concurrencyWaitTimeouts);

    public void RecordCircuitRejected() => Interlocked.Increment(ref _circuitRejected);

    public void RecordCircuitFailure() => Interlocked.Increment(ref _circuitFailures);

    public void RecordCircuitSuccess() => Interlocked.Increment(ref _circuitSuccesses);

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);

    public void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);

    public void RecordCacheStore() => Interlocked.Increment(ref _cacheStores);

    public void RecordCacheBypass() => Interlocked.Increment(ref _cacheBypassed);

    public void RecordRetryRequest() => Interlocked.Increment(ref _retryRequests);

    /// <summary>记录一次额外尝试；attempt 为本次请求的第几次重试（从 1 开始）。</summary>
    public void RecordRetryAttempt(int attempt)
    {
        if (attempt <= 0) return;
        Interlocked.Increment(ref _retryAttempts);
    }

    public void RecordRetryExhausted() => Interlocked.Increment(ref _retryExhausted);

    public void RecordRetryFailure() => Interlocked.Increment(ref _retryFailures);

    /// <summary>基础指标快照（供管理端点 /admin/status 使用）。</summary>
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

    /// <summary>治理指标快照（供管理端点 /admin/status 与排障使用）。</summary>
    public GovernanceMetricsSnapshot GetGovernanceSnapshot() => new(
        Interlocked.Read(ref _rateLimitAllowed),
        Interlocked.Read(ref _rateLimitRejected),
        Interlocked.Read(ref _rateLimitDelayed),
        Interlocked.Read(ref _concurrencyAcquired),
        Interlocked.Read(ref _concurrencyRejected),
        Interlocked.Read(ref _concurrencyWaitMsTotal),
        Interlocked.Read(ref _concurrencyWaitTimeouts),
        Interlocked.Read(ref _circuitRejected),
        Interlocked.Read(ref _circuitFailures),
        Interlocked.Read(ref _circuitSuccesses),
        Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _cacheMisses),
        Interlocked.Read(ref _cacheStores),
        Interlocked.Read(ref _cacheBypassed),
        Interlocked.Read(ref _retryRequests),
        Interlocked.Read(ref _retryAttempts),
        Interlocked.Read(ref _retryExhausted),
        Interlocked.Read(ref _retryFailures));

    public readonly record struct MetricsSnapshot(
        long TotalRequests, long InFlight, long TotalDurationMs, long MaxDurationMs,
        IReadOnlyDictionary<int, long> StatusCodes);

    public readonly record struct GovernanceMetricsSnapshot(
        long RateLimitAllowed,
        long RateLimitRejected,
        long RateLimitDelayed,
        long ConcurrencyAcquired,
        long ConcurrencyRejected,
        long ConcurrencyWaitMsTotal,
        long ConcurrencyWaitTimeouts,
        long CircuitRejected,
        long CircuitFailures,
        long CircuitSuccesses,
        long CacheHits,
        long CacheMisses,
        long CacheStores,
        long CacheBypassed,
        long RetryRequests,
        long RetryAttempts,
        long RetryExhausted,
        long RetryFailures);

    /// <summary>渲染 Prometheus 文本格式指标。</summary>
    public string RenderPrometheus()
    {
        var sb = new StringBuilder(1024);

        // ---- 基础流量指标 ----
        AppendCounter(sb, "gateway_requests_total", "Total HTTP requests processed by the gateway.",
            Interlocked.Read(ref _totalRequests));
        AppendGauge(sb, "gateway_requests_inflight", "Currently in-flight requests.",
            Interlocked.Read(ref _inFlight));
        AppendCounter(sb, "gateway_request_duration_ms_sum", "Cumulative request duration in milliseconds.",
            Interlocked.Read(ref _totalDurationMs));
        AppendGauge(sb, "gateway_request_duration_ms_max", "Maximum request duration in milliseconds.",
            Interlocked.Read(ref _maxDurationMs));

        sb.Append("# HELP gateway_requests_by_status Requests by HTTP status code.\n");
        sb.Append("# TYPE gateway_requests_by_status counter\n");
        foreach (var kv in _statusCodes.OrderBy(k => k.Key))
        {
            _statusCodes.TryGetValue(kv.Key, out var count);
            sb.Append("gateway_requests_by_status{code=\"").Append(kv.Key).Append("\"} ").Append(count).Append('\n');
        }

        if (_includePathLabels)
        {
            sb.Append("# HELP gateway_requests_by_path Requests by path prefix.\n");
            sb.Append("# TYPE gateway_requests_by_path counter\n");
            foreach (var kv in _pathCounts.OrderBy(k => k.Key))
            {
                _pathCounts.TryGetValue(kv.Key, out var count);
                sb.Append("gateway_requests_by_path{path=\"").Append(EscapeLabelValue(kv.Key))
                  .Append("\"} ").Append(count).Append('\n');
            }
        }

        // ---- 限流（nginx limit_req）----
        AppendCounter(sb, "gateway_ratelimit_allowed_total", "Requests allowed by the rate limiter.",
            Interlocked.Read(ref _rateLimitAllowed));
        AppendCounter(sb, "gateway_ratelimit_rejected_total", "Requests rejected by the rate limiter (HTTP 429).",
            Interlocked.Read(ref _rateLimitRejected));
        AppendCounter(sb, "gateway_ratelimit_delayed_total", "Requests delayed by the rate limiter before being released.",
            Interlocked.Read(ref _rateLimitDelayed));

        // ---- 全局并发上限（nginx limit_conn）----
        AppendCounter(sb, "gateway_concurrency_acquired_total", "Requests that acquired a concurrency slot.",
            Interlocked.Read(ref _concurrencyAcquired));
        AppendCounter(sb, "gateway_concurrency_rejected_total", "Requests rejected because the concurrency limit was reached.",
            Interlocked.Read(ref _concurrencyRejected));
        AppendCounter(sb, "gateway_concurrency_wait_ms_total", "Total milliseconds spent waiting for a concurrency slot.",
            Interlocked.Read(ref _concurrencyWaitMsTotal));
        AppendCounter(sb, "gateway_concurrency_wait_timeouts_total", "Requests that timed out while waiting for a concurrency slot.",
            Interlocked.Read(ref _concurrencyWaitTimeouts));
        if (_concurrencyState is not null)
        {
            AppendGauge(sb, "gateway_concurrency_inflight", "Current in-flight requests holding a concurrency slot.",
                _concurrencyState.InFlight);
            AppendGauge(sb, "gateway_concurrency_peak_inflight", "Peak in-flight concurrency slots observed.",
                _concurrencyState.PeakInFlight);
            AppendGauge(sb, "gateway_concurrency_limit_max", "Configured maximum concurrency slots.",
                Math.Max(1, _concurrencyState.Options.MaxConcurrent));
        }

        // ---- 熔断（Ocelot QoS / Kong circuit-breaker）----
        AppendCounter(sb, "gateway_circuit_rejected_total", "Requests fast-failed while a circuit breaker was open or saturated.",
            Interlocked.Read(ref _circuitRejected));
        AppendCounter(sb, "gateway_circuit_failures_total", "Downstream failures recorded by circuit breakers.",
            Interlocked.Read(ref _circuitFailures));
        AppendCounter(sb, "gateway_circuit_successes_total", "Downstream successes recorded by circuit breakers.",
            Interlocked.Read(ref _circuitSuccesses));
        sb.Append("# HELP gateway_circuit_breaker_state Circuit breaker state by path bucket (0=closed, 1=open, 2=half-open).\n");
        sb.Append("# TYPE gateway_circuit_breaker_state gauge\n");
        if (_circuitRegistry is not null)
        {
            foreach (var kv in _circuitRegistry.Snapshot().OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append("gateway_circuit_breaker_state{bucket=\"").Append(EscapeLabelValue(kv.Key))
                  .Append("\"} ").Append((int)kv.Value).Append('\n');
            }
        }

        // ---- 响应缓存（nginx proxy_cache）----
        AppendCounter(sb, "gateway_cache_hits_total", "Requests served from the response cache.",
            Interlocked.Read(ref _cacheHits));
        AppendCounter(sb, "gateway_cache_misses_total", "Cacheable requests that missed the response cache.",
            Interlocked.Read(ref _cacheMisses));
        AppendCounter(sb, "gateway_cache_stored_total", "Responses written to the response cache.",
            Interlocked.Read(ref _cacheStores));
        AppendCounter(sb, "gateway_cache_bypassed_total", "Requests bypassed caching by policy (non-idempotent or private headers).",
            Interlocked.Read(ref _cacheBypassed));

        // ---- 失败重试（nginx proxy_next_upstream）----
        AppendCounter(sb, "gateway_retry_requests_total", "Requests eligible for retry middleware.",
            Interlocked.Read(ref _retryRequests));
        AppendCounter(sb, "gateway_retry_attempts_total", "Additional upstream attempts triggered by retries.",
            Interlocked.Read(ref _retryAttempts));
        AppendCounter(sb, "gateway_retry_exhausted_total", "Requests whose retry budget was exhausted.",
            Interlocked.Read(ref _retryExhausted));
        AppendCounter(sb, "gateway_retry_failed_total", "Requests that finally failed after retries.",
            Interlocked.Read(ref _retryFailures));

        return sb.ToString();
    }

    private static void AppendCounter(StringBuilder sb, string name, string help, long value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        sb.Append(name).Append(' ').Append(value).Append('\n');
    }

    private static void AppendGauge(StringBuilder sb, string name, string help, long value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        sb.Append(name).Append(' ').Append(value).Append('\n');
    }

    private static string GetPathPrefix(string? path)
    {
        var key = path ?? "/";
        var idx = key.IndexOf('/', 1);
        return idx > 0 ? key[..idx] : key;
    }

    /// <summary>Prometheus 标签值转义：反斜杠、双引号与换行必须转义，避免破坏文本格式。</summary>
    internal static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
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
                context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
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
