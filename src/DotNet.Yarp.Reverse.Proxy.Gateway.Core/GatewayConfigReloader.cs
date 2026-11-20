using Microsoft.Extensions.Configuration;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 治理配置手动重载器（对应 POST /admin/reload 管理端点）。
///
/// 仅 localhost 可触发。优先委托给 GatewayHotReload（走校验闸门 + 原地映射 + 派生状态重建）；
/// 若热重载器未注册（兼容旧行为），则回退到直接从 IConfiguration 重新绑定 RateLimit / IpFilter。
/// 文件监听失效时（如网络盘、Watcher 被禁用），此端点作为手动兜底。
/// </summary>
public sealed class GatewayConfigReloader
{
    private readonly IConfiguration _configuration;
    private readonly RateLimitState _rateLimitState;
    private readonly IpFilterState _ipFilterState;
    private readonly GatewayHotReload? _hotReload;

    public GatewayConfigReloader(
        IConfiguration configuration,
        RateLimitState rateLimitState,
        IpFilterState ipFilterState,
        GatewayHotReload? hotReload = null)
    {
        _configuration = configuration;
        _rateLimitState = rateLimitState;
        _ipFilterState = ipFilterState;
        _hotReload = hotReload;
    }

    /// <summary>最近一次成功重载时间（UTC），供管理状态端点展示。</summary>
    public DateTime? LastReloadAtUtc => _hotReload?.LastReloadAtUtc;

    /// <summary>从配置重新绑定可热载项并应用；成功返回 true。</summary>
    public bool Reload(out string? error)
    {
        // 热重载器已注册：走统一校验闸门 + 原地映射 + state 重建
        if (_hotReload != null)
        {
            return _hotReload.Reload(out error);
        }

        // 回退：无热重载器时维持原有行为
        try
        {
            var gateway = _configuration.GetSection("Gateway");

            var rateLimit = gateway.GetSection("RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions();
            _rateLimitState.ApplyOptions(rateLimit);

            var ipFilter = gateway.GetSection("IpFilter").Get<IpFilterOptions>() ?? new IpFilterOptions();
            _ipFilterState.ApplyOptions(ipFilter);

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            GatewayLogger.Error("治理配置热重载失败", ex);
            return false;
        }
    }
}
