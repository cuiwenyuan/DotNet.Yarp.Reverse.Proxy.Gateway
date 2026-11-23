using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using NewLife.Configuration;
using NewLife.Log;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 治理配置热重载器（基于 NewLife.Core 的 JsonConfigProvider + Bind/MapTo 基础设施）。
///
/// 机制：
/// - 启动时用 JsonConfigProvider.LoadAppSettings 加载 appsettings.json；
/// - 通过哨兵对象 Bind(sentinel, autoUpdate:true) 启动 FileConfigProvider 的文件监控
///   （FileSystemWatcher 事件驱动 + 定时轮询兜底，≤1s 感知变更）；
/// - 订阅 IConfigProvider.Changed，变更时先跑校验闸门（注入的校验委托，等价 nginx -t），
///   校验通过才把新配置发布给各派生状态；校验失败保留旧配置、记录告警，服务不中断。
///
/// 一致性保障：
/// - 校验前后比对文件指纹（最后写入时间 + 长度），指纹变化则放弃本次应用，杜绝"校验 A、应用 B"；
/// - 同一指纹只应用一次，避免 watcher 与轮询触发的重复 apply 清空令牌桶；
/// - 状态字段打包为不可变快照一次原子发布，/admin/status 读到的是完整一代。
///
/// 硬约束：仅使用 NewLife.Core + BCL，不引入任何新 NuGet。
/// </summary>
public sealed class GatewayHotReload : IDisposable
{
    private readonly object _gate = new();
    private readonly JsonConfigProvider _provider;
    private readonly GatewayOptions _shared;              // DI 中的真实单例（保留原地映射，兼容直接注入裸 options 的中间件）
    private readonly RateLimitState _rateLimitState;
    private readonly IpFilterState _ipFilterState;
    private readonly ConcurrencyLimitState _concurrencyState;
    private readonly CircuitBreakerRegistry _circuitRegistry;
    private readonly Func<string, (bool ok, string? error)> _validate;
    private readonly string _configPath;
    private readonly object _sentinel;                    // 仅用于触发 watcher，值不用

    private int _applying;                                 // 并发进入短路（watcher / 轮询 / 手动）
    private int _reloadCount;
    private ReloadStatus _status;

    private sealed class ReloadStatus
    {
        public readonly DateTime? LastReloadAtUtc;
        public readonly DateTime? LastAttemptAtUtc;
        public readonly string? LastError;
        public readonly string AppliedVersion;

        public ReloadStatus(DateTime? lastReloadAtUtc, DateTime? lastAttemptAtUtc,
            string? lastError, string appliedVersion)
        {
            LastReloadAtUtc = lastReloadAtUtc;
            LastAttemptAtUtc = lastAttemptAtUtc;
            LastError = lastError;
            AppliedVersion = appliedVersion;
        }
    }

    /// <summary>成功热重载次数。</summary>
    public int ReloadCount => Volatile.Read(ref _reloadCount);

    /// <summary>最近一次成功重载时间（UTC）。</summary>
    public DateTime? LastReloadAtUtc => Volatile.Read(ref _status).LastReloadAtUtc;

    /// <summary>最近一次尝试时间（UTC，含失败）。</summary>
    public DateTime? LastAttemptAtUtc => Volatile.Read(ref _status).LastAttemptAtUtc;

    /// <summary>最近一次失败原因；null 表示当前无错误。</summary>
    public string? LastError => Volatile.Read(ref _status).LastError;

    public GatewayHotReload(
        string configPath,
        GatewayOptions shared,
        RateLimitState rateLimitState,
        IpFilterState ipFilterState,
        ConcurrencyLimitState concurrencyState,
        CircuitBreakerRegistry circuitRegistry,
        Func<string, (bool ok, string? error)> validate)
    {
        _configPath = configPath;
        _shared = shared;
        _rateLimitState = rateLimitState;
        _ipFilterState = ipFilterState;
        _concurrencyState = concurrencyState;
        _circuitRegistry = circuitRegistry;
        _validate = validate;

        _provider = JsonConfigProvider.LoadAppSettings(configPath);

        // 初始填充真实单例（autoUpdate=false：不自动订阅，避免绕过校验闸门）
        _provider.Bind(_shared, false, "Gateway");

        // 哨兵绑定以启动文件监控；真正的更新全部走 Changed 回调 + 校验闸门。
        _sentinel = new GatewayOptions();
        _provider.Bind(_sentinel, true, "Gateway");

        _status = new ReloadStatus(null, null, null, Fingerprint());

        if (_provider is ConfigProvider cp)
        {
            cp.Changed += OnConfigChanged;
        }

        GatewayLogger.Info($"配置热重载已启用：{configPath}（校验闸门 + 原子发布，变更 ≤1s 感知）");
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // 校验 + 应用统一走 ReloadCore，串行化避免并发回调交错
        ReloadCore(fromFileWatch: true);
    }

    /// <summary>
    /// 手动兜底重载（POST /admin/reload）。复用同一套校验闸门 + 应用逻辑。
    /// 与文件监听使用同一套流程：强制重读磁盘 → 校验 → 指纹复核 → 原子发布。
    /// </summary>
    public bool Reload(out string? error) => ReloadCore(fromFileWatch: false, out error);

    private bool ReloadCore(bool fromFileWatch) => ReloadCore(fromFileWatch, out _);

    private bool ReloadCore(bool fromFileWatch, out string? error)
    {
        // watcher 与 NewLife 定时轮询可能针对同一次保存各触发一次；并发进入直接短路。
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
        {
            error = "Another reload is already in progress";
            return false;
        }

        try
        {
            var attemptedAt = DateTime.UtcNow;
            var before = Fingerprint();

            // 两种路径都强制重读，保证 provider 内存树与校验器读到的磁盘快照同源。
            _provider.LoadAll();

            // 1) 校验闸门：复用 --test-config 同款校验，失败则保留旧配置。
            var (ok, validateError) = _validate(_configPath);
            if (!ok)
            {
                var message = validateError ?? "配置校验失败";
                Volatile.Write(ref _status, new ReloadStatus(
                    Volatile.Read(ref _status).LastReloadAtUtc, attemptedAt, message,
                    Volatile.Read(ref _status).AppliedVersion));
                error = message;
                GatewayLogger.Warn($"配置热重载被校验闸门拦截，已保留旧配置：{message}");
                return false;
            }

            // 2) 指纹复核：校验期间文件又变了，则本次结果已过期，放弃并等待下一次 Changed。
            var after = Fingerprint();
            if (after != before)
            {
                error = "Config file changed during validation, skipped";
                Volatile.Write(ref _status, new ReloadStatus(
                    Volatile.Read(ref _status).LastReloadAtUtc, attemptedAt, error,
                    Volatile.Read(ref _status).AppliedVersion));
                GatewayLogger.Warn($"配置热重载跳过：{error}");
                return false;
            }

            // 3) 同一版本只应用一次，避免重复清空令牌桶 / 虚增 reloadCount。
            var previous = Volatile.Read(ref _status);
            if (string.Equals(previous.AppliedVersion, after, StringComparison.Ordinal))
            {
                error = null;
                return true;
            }

            lock (_gate)
            {
                var section = _provider.GetSection("Gateway");
                if (section == null)
                {
                    error = "配置节点 Gateway 不存在";
                    Volatile.Write(ref _status, new ReloadStatus(
                        previous.LastReloadAtUtc, attemptedAt, error, previous.AppliedVersion));
                    GatewayLogger.Warn($"配置热重载失败，已保留旧配置：{error}");
                    return false;
                }

                // fresh 是全新的不可变代，交给有派生状态的组件原子发布；
                // _shared 仍原地映射，兼容尚未改造为快照读取的中间件（CORS/安全头等）。
                var fresh = new GatewayOptions();
                section.MapTo(fresh, _provider);
                section.MapTo(_shared, _provider);

                _rateLimitState.ApplyOptions(fresh.RateLimit);
                _ipFilterState.ApplyOptions(fresh.IpFilter);
                _concurrencyState.ApplyOptions(fresh.ConcurrencyLimit);
                _circuitRegistry.ApplyOptions(fresh.CircuitBreaker);

                Interlocked.Increment(ref _reloadCount);
                Volatile.Write(ref _status, new ReloadStatus(
                    DateTime.UtcNow, attemptedAt, null, after));
                error = null;
                GatewayLogger.Info($"治理配置已热重载（{(fromFileWatch ? "文件变更" : "手动")}）：" +
                                   $"RateLimit(PerMinute={fresh.RateLimit.PerMinute},PerIp={fresh.RateLimit.PerIp}) " +
                                   $"IpFilter(Enabled={fresh.IpFilter.Enabled}) " +
                                   $"ConcurrencyLimit(Enabled={fresh.ConcurrencyLimit.Enabled},Max={fresh.ConcurrencyLimit.MaxConcurrent}) " +
                                   $"CircuitBreaker(Enabled={fresh.CircuitBreaker.Enabled})");
                return true;
            }
        }
        catch (Exception ex)
        {
            var previous = Volatile.Read(ref _status);
            error = ex.Message;
            Volatile.Write(ref _status, new ReloadStatus(
                previous.LastReloadAtUtc, DateTime.UtcNow, error, previous.AppliedVersion));
            GatewayLogger.Error("治理配置热重载应用失败，已保留旧配置", ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }

    /// <summary>文件指纹：配置内容 SHA-256。指纹相同视为同一版本配置。</summary>
    private string Fingerprint()
    {
        try
        {
            using var stream = File.OpenRead(_configPath);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return "unknown";
        }
    }

    public void Dispose()
    {
        if (_provider is ConfigProvider cp)
        {
            cp.Changed -= OnConfigChanged;
        }
        _provider.Dispose();
    }
}
