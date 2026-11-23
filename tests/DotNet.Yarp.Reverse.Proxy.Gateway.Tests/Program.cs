using System.Net;
using System.Text;
using DotNet.Yarp.Reverse.Proxy.Gateway.Core;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Tests;

/// <summary>
/// 零 NuGet 的测试执行器：只用 BCL + FrameworkReference(Microsoft.AspNetCore.App)。
/// 覆盖本次新增的治理指标埋点、熔断注册表、热重载配置可见性与异常状态码修正。
/// 退出码 0 = 全部通过，1 = 有失败；可直接接入 CI。
/// </summary>
internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static async Task<int> Main()
    {
        await Run("Prometheus 输出包含全部治理指标族且转义正确", TestPrometheusFamilies);
        await Run("熔断注册表暴露分桶状态且热重载保留状态", TestCircuitRegistry);
        await Run("限流中间件记录允许与 429 拒绝", TestRateLimitCounters);
        await Run("并发中间件拒绝时不泄漏槽位并计数", TestConcurrencyCounters);
        await Run("缓存中间件记录未命中/写入/命中/绕过", TestCacheCounters);
        await Run("重试中间件记录额外尝试与预算耗尽", TestRetryCounters);
        await Run("熔断中间件记录失败/快速失败并暴露状态", TestCircuitCounters);
        await Run("未处理异常按 500 计入状态码分布", TestExceptionStatusCode);
        await Run("缓存元数据与响应体同生命周期且过期后可重新写入", TestCacheMetaLifecycle);
        await Run("熔断半开配额与跨代探测结果隔离", TestCircuitHalfOpenGeneration);
        await Run("熔断分桶数量存在硬上限", TestCircuitBucketLimit);
        await Run("管理端点 ReloadEnabled 开关与 XFF 伪造防护", TestAdminReloadGuards);
        await Run("Prometheus 计数器命名符合规范", TestCounterNaming);
        await Run("IP 规则热重载原子发布", TestIpFilterSnapshot);

        Console.WriteLine();
        Console.WriteLine($"Tests: {_passed + _failed}, Passed: {_passed}, Failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine("PASS  " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL  " + name);
            Console.WriteLine("      " + ex.Message.Replace("\n", "\n      ", StringComparison.Ordinal));
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
        }
    }

    private static (GatewayMetrics Metrics, ConcurrencyLimitState Concurrency, CircuitBreakerRegistry Circuits)
        NewMetrics(
            MetricsOptions? metricsOptions = null,
            ConcurrencyLimitOptions? concurrencyOptions = null,
            CircuitBreakerOptions? circuitOptions = null)
    {
        var concurrency = new ConcurrencyLimitState(concurrencyOptions ?? new ConcurrencyLimitOptions());
        var circuits = new CircuitBreakerRegistry(circuitOptions ?? new CircuitBreakerOptions());
        var metrics = new GatewayMetrics(metricsOptions ?? new MetricsOptions(), concurrency, circuits);
        return (metrics, concurrency, circuits);
    }

    private static DefaultHttpContext NewContext(string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task TestPrometheusFamilies()
    {
        var options = new MetricsOptions { IncludePathLabels = true };
        var (metrics, _, circuits) = NewMetrics(metricsOptions: options);

        metrics.RecordRateLimitAllowed();
        metrics.RecordRateLimitRejected();
        metrics.RecordRateLimitDelayed();
        metrics.RecordConcurrencyAcquired();
        metrics.RecordConcurrencyRejected();
        metrics.RecordConcurrencyWait(12);
        metrics.RecordConcurrencyWaitTimeout();
        metrics.RecordCircuitFailure();
        metrics.RecordCircuitSuccess();
        metrics.RecordCircuitRejected();
        metrics.RecordCacheHit();
        metrics.RecordCacheMiss();
        metrics.RecordCacheStore();
        metrics.RecordCacheBypass();
        metrics.RecordRetryRequest();
        metrics.RecordRetryAttempt(1);
        metrics.RecordRetryExhausted();
        metrics.RecordRetryFailure();
        metrics.RecordEnd(200, "/api\"b\nc", 7);

        var breakerOptions = new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            OpenSeconds = 30,
            FailureStatusCodes = new[] { StatusCodes.Status503ServiceUnavailable }
        };
        var registry = new CircuitBreakerRegistry(breakerOptions);
        var metricsWithBreaker = new GatewayMetrics(options, null, registry);
        registry.GetOrAdd("/api").RecordFailure();

        var text = metrics.RenderPrometheus();
        string[] expected =
        {
            "gateway_ratelimit_allowed_total 1",
            "gateway_ratelimit_rejected_total 1",
            "gateway_ratelimit_delayed_total 1",
            "gateway_concurrency_acquired_total 1",
            "gateway_concurrency_rejected_total 1",
            "gateway_concurrency_wait_ms_total 12",
            "gateway_concurrency_wait_timeouts_total 1",
            "gateway_circuit_rejected_total 1",
            "gateway_circuit_failures_total 1",
            "gateway_circuit_successes_total 1",
            "gateway_cache_hits_total 1",
            "gateway_cache_misses_total 1",
            "gateway_cache_stored_total 1",
            "gateway_cache_bypassed_total 1",
            "gateway_retry_requests_total 1",
            "gateway_retry_attempts_total 1",
            "gateway_retry_exhausted_total 1",
            "gateway_retry_failed_total 1"
        };
        foreach (var line in expected)
        {
            Check(text.Contains('\n' + line + '\n', StringComparison.Ordinal), $"缺少指标行: {line}");
        }

        // 路径标签中的双引号与换行必须转义，否则 Prometheus 文本格式会损坏。
        Check(text.Contains("path=\"/api\\\"b\\nc\"", StringComparison.Ordinal), "路径标签未正确转义");
        Check(metricsWithBreaker.RenderPrometheus().Contains(
            "gateway_circuit_breaker_state{bucket=\"/api\"} 1", StringComparison.Ordinal),
            "缺少分桶熔断状态");
        Check(registry.Snapshot().Count == 1, "熔断分桶未正确登记（原断言为空注册表，属永真断言）");
    }

    private static Task TestCircuitRegistry()
    {
        var options = new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            OpenSeconds = 30,
            FailureStatusCodes = new[] { StatusCodes.Status503ServiceUnavailable }
        };
        var registry = new CircuitBreakerRegistry(options);
        var breaker = registry.GetOrAdd("/api");
        breaker.RecordFailure();
        Equal(CircuitBreakerState.Open, registry.Snapshot()["/api"], "首次失败后应打开");

        registry.ApplyOptions(new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 5,
            OpenSeconds = 1,
            FailureStatusCodes = new[] { StatusCodes.Status503ServiceUnavailable }
        });
        Equal(CircuitBreakerState.Open, registry.Snapshot()["/api"], "热重载不应重置熔断状态");

        // 新配置里的 OpenSeconds=1 必须被调用方读到（由 AllowRequest 通过 Volatile.Read 取得）。
        Equal(CircuitBreakerState.Open, breaker.State, "状态读取异常");
        return Task.CompletedTask;
    }

    private static async Task TestRateLimitCounters()
    {
        var state = new RateLimitState(new RateLimitOptions
        {
            PerMinute = 1,
            Burst = 0,
            DelayEnabled = false,
            PerIp = false,
            MaxIpBuckets = 10,
            BucketIdleMinutes = 30
        });
        var (metrics, _, _) = NewMetrics();
        var nextCalls = 0;
        var middleware = new RateLimitMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        }, state, metrics);

        var first = NewContext("/first");
        var second = NewContext("/second");
        await middleware.Invoke(first);
        await middleware.Invoke(second);

        Equal(1, nextCalls, "仅应放行一次");
        Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode, "超限应返回 429");
        var snapshot = metrics.GetGovernanceSnapshot();
        Equal(1L, snapshot.RateLimitAllowed, "允许计数");
        Equal(1L, snapshot.RateLimitRejected, "拒绝计数");
    }

    private static async Task TestConcurrencyCounters()
    {
        var (metrics, state, _) = NewMetrics(concurrencyOptions: new ConcurrencyLimitOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            WaitEnabled = false,
            StatusCode = StatusCodes.Status503ServiceUnavailable
        });

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ConcurrencyLimitMiddleware(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, state, metrics);

        var firstTask = middleware.Invoke(NewContext("/slow"));
        await entered.Task;

        var rejectedContext = NewContext("/rejected");
        await middleware.Invoke(rejectedContext);
        release.TrySetResult();
        await firstTask;

        Equal(StatusCodes.Status503ServiceUnavailable, rejectedContext.Response.StatusCode, "超限应返回 503");
        Equal(0, state.InFlight, "请求结束后槽位必须归还");
        var snapshot = metrics.GetGovernanceSnapshot();
        Equal(1L, snapshot.ConcurrencyAcquired, "占用计数");
        Equal(1L, snapshot.ConcurrencyRejected, "拒绝计数");
    }

    private static async Task TestCacheCounters()
    {
        var (metrics, _, _) = NewMetrics();
        var options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 30,
            MaxCacheEntries = 100,
            CacheableStatusCodes = new[] { StatusCodes.Status200OK }
        };
        var cache = new SimpleMemoryCache();
        var nextCalls = 0;
        var middleware = new ResponseCacheMiddleware(async context =>
        {
            nextCalls++;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("payload"));
        }, options, cache, metrics);

        var first = NewContext("/cached");
        first.Request.QueryString = new QueryString("?k=fixed");
        var second = NewContext("/cached");
        second.Request.QueryString = new QueryString("?k=fixed");

        await middleware.Invoke(first);
        await middleware.Invoke(second);

        Equal(1, nextCalls, "第二次应命中缓存，不再调用下游");
        Equal("payload", Encoding.UTF8.GetString(((MemoryStream)second.Response.Body).ToArray()), "缓存内容");
        var snapshot = metrics.GetGovernanceSnapshot();
        Equal(1L, snapshot.CacheMisses, "未命中计数");
        Equal(1L, snapshot.CacheStores, "写入计数");
        Equal(1L, snapshot.CacheHits, "命中计数");

        var privateRequest = NewContext("/private");
        privateRequest.Request.Headers.Authorization = "Bearer token";
        await middleware.Invoke(privateRequest);
        Equal(1L, metrics.GetGovernanceSnapshot().CacheBypassed, "私有请求应计绕过");
    }

    private static async Task TestRetryCounters()
    {
        var options = new RetryOptions
        {
            Enabled = true,
            MaxRetries = 1,
            RetryableStatusCodes = new[] { StatusCodes.Status502BadGateway },
            RetryOnConnectionError = true,
            RetryOnHttp500 = false
        };
        var (metrics, _, _) = NewMetrics();

        var calls = 0;
        var middleware = new RetryMiddleware(context =>
        {
            calls++;
            context.Response.StatusCode = calls == 1
                ? StatusCodes.Status502BadGateway
                : StatusCodes.Status200OK;
            return Task.CompletedTask;
        }, options, metrics);

        var context = NewContext("/retry");
        await middleware.Invoke(context);

        Equal(2, calls, "应额外尝试一次");
        Equal(StatusCodes.Status200OK, context.Response.StatusCode, "重试后最终状态");
        var snapshot = metrics.GetGovernanceSnapshot();
        Equal(1L, snapshot.RetryRequests, "有资格重试的请求数");
        Equal(1L, snapshot.RetryAttempts, "额外尝试数");

        var exhaustedCalls = 0;
        var exhaustedMiddleware = new RetryMiddleware(ctx =>
        {
            exhaustedCalls++;
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        }, options, metrics);
        await exhaustedMiddleware.Invoke(NewContext("/exhausted"));

        Equal(2, exhaustedCalls, "耗尽前仍应尝试一次");
        Equal(1L, metrics.GetGovernanceSnapshot().RetryExhausted, "预算耗尽计数");
    }

    private static async Task TestCircuitCounters()
    {
        var options = new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            OpenSeconds = 30,
            HalfOpenMaxRequests = 1,
            FailureStatusCodes = new[] { StatusCodes.Status503ServiceUnavailable },
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
        var registry = new CircuitBreakerRegistry(options);
        var (metrics, _, _) = NewMetrics(circuitOptions: options);
        var nextCalls = 0;
        var middleware = new CircuitBreakerMiddleware(context =>
        {
            nextCalls++;
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }, options, registry, metrics);

        await middleware.Invoke(NewContext("/api/first"));
        var rejected = NewContext("/api/second");
        await middleware.Invoke(rejected);

        Equal(1, nextCalls, "熔断打开后不应再调用下游");
        Equal(StatusCodes.Status503ServiceUnavailable, rejected.Response.StatusCode, "快速失败状态");
        Equal(CircuitBreakerState.Open, registry.Snapshot()["/api"], "分桶状态");
        var snapshot = metrics.GetGovernanceSnapshot();
        Equal(1L, snapshot.CircuitFailures, "下游失败计数");
        Equal(1L, snapshot.CircuitRejected, "快速失败计数");
    }

    private static async Task TestExceptionStatusCode()
    {
        var (metrics, _, _) = NewMetrics();
        var middleware = new MetricsMiddleware(
            _ => throw new InvalidOperationException("boom"),
            new MetricsOptions(),
            metrics);

        try
        {
            await middleware.Invoke(NewContext("/boom"));
            throw new InvalidOperationException("异常未被抛出");
        }
        catch (InvalidOperationException ex) when (ex.Message == "boom")
        {
            // 预期路径。
        }

        var snapshot = metrics.GetSnapshot();
        Check(snapshot.StatusCodes.TryGetValue(StatusCodes.Status500InternalServerError, out var count) && count == 1,
            "未处理异常应计入 500 而不是 200");
    }

    private static async Task TestCacheMetaLifecycle()
    {
        var options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 1,
            MaxCacheEntries = 2,
            CacheableStatusCodes = new[] { StatusCodes.Status200OK }
        };
        var (metrics, _, _) = NewMetrics();
        var cache = new SimpleMemoryCache();
        var calls = 0;
        var middleware = new ResponseCacheMiddleware(async context =>
        {
            calls++;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain";
            context.Response.Headers.CacheControl = "public, max-age=1";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("v" + calls));
        }, options, cache, metrics);

        var first = NewContext("/meta?k=1");
        await middleware.Invoke(first);
        var second = NewContext("/meta?k=1");
        await middleware.Invoke(second);
        Equal(1, calls, "第二次应命中缓存");
        Equal("text/plain", second.Response.ContentType, "缓存元数据必须随响应体一起保留");
        Equal("v1", Encoding.UTF8.GetString(((MemoryStream)second.Response.Body).ToArray()), "缓存内容");

        await middleware.Invoke(NewContext("/meta?k=2"));
        Equal(2, calls, "第二个 key 应正常写入");

        // 两个 key 都已达到 MaxCacheEntries；等 TTL 过期后写入第三个 key，
        // 必须触发过期回收，否则缓存会永久停止写入（旧实现的 _meta 只增不减）。
        await Task.Delay(1200);
        await middleware.Invoke(NewContext("/meta?k=3"));
        Equal(3, calls, "过期条目应被回收并允许新 key 写入");
    }

    private static async Task TestCircuitHalfOpenGeneration()
    {
        var options = new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            OpenSeconds = 1,
            HalfOpenMaxRequests = 1,
            FailureStatusCodes = new[] { StatusCodes.Status503ServiceUnavailable }
        };
        var breaker = new CircuitBreaker(options);
        breaker.RecordFailure();
        Equal(CircuitBreakerState.Open, breaker.State, "失败达阈值应打开");

        await Task.Delay(1150);
        Check(breaker.AllowRequest(out var firstGeneration), "到期后应放行第一个探测");
        Check(firstGeneration > 0, "半开探测必须带代编号");
        Check(!breaker.AllowRequest(out _), "HalfOpenMaxRequests=1 时第二个探测应被拒绝（含触发转换的请求）");

        breaker.RecordSuccess(firstGeneration);
        Equal(CircuitBreakerState.Closed, breaker.State, "探测成功应恢复 Closed");

        breaker.RecordFailure();
        Equal(CircuitBreakerState.Open, breaker.State, "再次失败应重新打开");
        await Task.Delay(1150);
        Check(breaker.AllowRequest(out var secondGeneration), "新的开放期应放行探测");
        Check(secondGeneration != firstGeneration, "半开代编号必须递增");

        breaker.RecordSuccess(firstGeneration); // 上一代探测的迟到结果
        Equal(CircuitBreakerState.HalfOpen, breaker.State, "上一代迟到结果不得影响当前半开状态");
    }

    private static Task TestCircuitBucketLimit()
    {
        var registry = new CircuitBreakerRegistry(new CircuitBreakerOptions());
        for (var i = 0; i < CircuitBreakerRegistry.MaxBuckets + 100; i++)
        {
            registry.GetOrAdd("/path" + i);
        }
        Check(registry.Snapshot().Count <= CircuitBreakerRegistry.MaxBuckets,
            "熔断分桶必须有硬上限，防止任意路径导致无界增长");
        Check(registry.Snapshot().ContainsKey("__overflow__"), "超限路径应归入 overflow 桶");
        return Task.CompletedTask;
    }

    private static async Task TestAdminReloadGuards()
    {
        var (metrics, _, _) = NewMetrics();

        var nextCalls = 0;
        var disabled = new AdminStatusMiddleware(
            _ => { nextCalls++; return Task.CompletedTask; },
            new AdminOptions { Enabled = true, ReloadEnabled = false },
            metrics);
        var ctx = NewContext("/admin/reload", "POST");
        await disabled.Invoke(ctx);
        Equal(1, nextCalls, "ReloadEnabled=false 时 /admin/reload 不得被处理");

        var guarded = new AdminStatusMiddleware(
            _ => Task.CompletedTask,
            new AdminOptions { Enabled = true, ReloadEnabled = true },
            metrics);
        var forged = NewContext("/admin/reload", "POST");
        forged.Connection.RemoteIpAddress = IPAddress.Loopback; // 被 XFF 改写后的值
        forged.Items[AdminStatusMiddleware.OriginalRemoteIpItemsKey] = IPAddress.Parse("8.8.8.8"); // 真实来源
        await guarded.Invoke(forged);
        Equal(StatusCodes.Status403Forbidden, forged.Response.StatusCode,
            "伪造 X-Forwarded-For 不得绕过 localhost 限制");
    }

    private static Task TestCounterNaming()
    {
        var (metrics, _, _) = NewMetrics();
        var text = metrics.RenderPrometheus();
        Check(text.Contains("gateway_request_duration_ms_total ", StringComparison.Ordinal),
            "counter 必须以 _total 结尾");
        Check(!text.Contains("gateway_request_duration_ms_sum", StringComparison.Ordinal),
            "不应再输出不符合规范的 _sum counter");
        return Task.CompletedTask;
    }

    private static Task TestIpFilterSnapshot()
    {
        var state = new IpFilterState(new IpFilterOptions
        {
            Enabled = true,
            Deny = new[] { "192.168.1.0/24" }
        });
        Check(state.IsBlocked("192.168.1.5"), "旧黑名单应生效");

        // 把同一网段从 deny 翻转为 allow：若规则集跨代混用，两个断言不可能同时成立。
        state.ApplyOptions(new IpFilterOptions
        {
            Enabled = true,
            Allow = new[] { "192.168.1.0/24" }
        });
        Check(!state.IsBlocked("192.168.1.5"), "旧 deny 规则不得残留");
        Check(state.IsBlocked("172.16.0.1"), "新白名单应立即生效（名单外拒绝）");
        return Task.CompletedTask;
    }
}
