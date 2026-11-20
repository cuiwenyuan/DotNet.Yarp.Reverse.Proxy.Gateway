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
///   校验通过才用 ConfigHelper.MapTo 把新配置"原地映射"到真实单例对象（持有引用的中间件零改动），
///   并重建有派生状态的两个 state；校验失败则保留旧配置、记录告警，服务不中断。
///
/// 硬约束：仅使用 NewLife.Core + BCL，不引入任何新 NuGet。
/// </summary>
public sealed class GatewayHotReload : IDisposable
{
    private readonly object _gate = new();
    private readonly JsonConfigProvider _provider;
    private readonly GatewayOptions _shared;              // DI 中的真实单例（原地更新，引用不变）
    private readonly RateLimitState _rateLimitState;
    private readonly IpFilterState _ipFilterState;
    private readonly Func<string, (bool ok, string? error)> _validate;
    private readonly string _configPath;
    private readonly object _sentinel;                    // 仅用于触发 watcher，值不用

    private int _reloadCount;
    private DateTime? _lastReloadAtUtc;
    private DateTime? _lastAttemptAtUtc;
    private string? _lastError;

    /// <summary>成功热重载次数。</summary>
    public int ReloadCount => Volatile.Read(ref _reloadCount);

    /// <summary>最近一次成功重载时间（UTC）。</summary>
    public DateTime? LastReloadAtUtc => _lastReloadAtUtc;

    /// <summary>最近一次尝试时间（UTC，含失败）。</summary>
    public DateTime? LastAttemptAtUtc => _lastAttemptAtUtc;

    /// <summary>最近一次失败原因；null 表示当前无错误。</summary>
    public string? LastError => _lastError;

    public GatewayHotReload(
        string configPath,
        GatewayOptions shared,
        RateLimitState rateLimitState,
        IpFilterState ipFilterState,
        Func<string, (bool ok, string? error)> validate)
    {
        _configPath = configPath;
        _shared = shared;
        _rateLimitState = rateLimitState;
        _ipFilterState = ipFilterState;
        _validate = validate;

        _provider = JsonConfigProvider.LoadAppSettings(configPath);

        // 初始填充真实单例（autoUpdate=false：不自动订阅，避免绕过校验闸门）
        _provider.Bind(_shared, false, "Gateway");

        // 哨兵绑定以启动文件监控；真正的更新全部走 Changed 回调 + 校验闸门 + MapTo。
        _sentinel = new GatewayOptions();
        _provider.Bind(_sentinel, true, "Gateway");

        if (_provider is ConfigProvider cp)
        {
            cp.Changed += OnConfigChanged;
        }

        GatewayLogger.Info($"配置热重载已启用：{configPath}（校验闸门 + 原地映射，变更 ≤1s 感知）");
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // 校验 + 应用统一走 ReloadCore，串行化避免并发回调交错
        ReloadCore(fromFileWatch: true);
    }

    /// <summary>
    /// 手动兜底重载（POST /admin/reload）。复用同一套校验闸门 + 应用逻辑。
    /// 与文件监听的区别：手动调用时强制重新读取磁盘（provider.LoadAll），不依赖 watcher。
    /// </summary>
    public bool Reload(out string? error) => ReloadCore(fromFileWatch: false, out error);

    private bool ReloadCore(bool fromFileWatch) => ReloadCore(fromFileWatch, out _);

    private bool ReloadCore(bool fromFileWatch, out string? error)
    {
        lock (_gate)
        {
            _lastAttemptAtUtc = DateTime.UtcNow;

            // 手动兜底时强制重新加载磁盘配置树（文件监听场景配置树已是新值）
            if (!fromFileWatch)
            {
                _provider.LoadAll();
            }

            // 1) 校验闸门：复用 --test-config 同款校验，失败则保留旧配置
            var (ok, validateError) = _validate(_configPath);
            if (!ok)
            {
                _lastError = validateError ?? "配置校验失败";
                error = _lastError;
                GatewayLogger.Warn($"配置热重载被校验闸门拦截，已保留旧配置：{_lastError}");
                return false;
            }

            try
            {
                // 2) 原地映射新配置到真实单例（嵌套对象/数组均保持对象引用，仅改属性）
                var section = _provider.GetSection("Gateway");
                if (section == null)
                {
                    _lastError = "配置节点 Gateway 不存在";
                    error = _lastError;
                    GatewayLogger.Warn($"配置热重载失败，已保留旧配置：{_lastError}");
                    return false;
                }
                section.MapTo(_shared, _provider);

                // 3) 重建有派生状态的两个组件
                _rateLimitState.ApplyOptions(_shared.RateLimit);
                _ipFilterState.ApplyOptions(_shared.IpFilter);

                Interlocked.Increment(ref _reloadCount);
                _lastReloadAtUtc = DateTime.UtcNow;
                _lastError = null;
                error = null;
                GatewayLogger.Info($"治理配置已热重载（{(fromFileWatch ? "文件变更" : "手动")}）：" +
                                   $"RateLimit(PerMinute={_shared.RateLimit.PerMinute},PerIp={_shared.RateLimit.PerIp}) " +
                                   $"IpFilter(Enabled={_shared.IpFilter.Enabled})");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                error = _lastError;
                GatewayLogger.Error("治理配置热重载应用失败，已保留旧配置", ex);
                return false;
            }
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
