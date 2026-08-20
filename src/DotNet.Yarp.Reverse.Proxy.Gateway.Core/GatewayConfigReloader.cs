using Microsoft.Extensions.Configuration;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 治理配置热重载器（对应 GoReverseProxy 的管理重载端点）。
/// 从 IConfiguration 的 "Gateway" 节点重新绑定可热载的配置并应用到共享状态：
///   - RateLimit（PerMinute / Burst / DelayEnabled / MaxDelayMs）
///   - IpFilter（Allow / Deny / StatusCode）
/// 其余配置（认证、缓存、安全头等）仍为启动时绑定。
/// 由管理端点 POST /admin/reload（仅 localhost）触发。
/// </summary>
public sealed class GatewayConfigReloader
{
    private readonly IConfiguration _configuration;
    private readonly RateLimitState _rateLimitState;
    private readonly IpFilterState _ipFilterState;

    public GatewayConfigReloader(
        IConfiguration configuration,
        RateLimitState rateLimitState,
        IpFilterState ipFilterState)
    {
        _configuration = configuration;
        _rateLimitState = rateLimitState;
        _ipFilterState = ipFilterState;
    }

    /// <summary>最近一次成功重载时间（UTC），供管理状态端点展示。</summary>
    public DateTime? LastReloadAtUtc { get; private set; }

    /// <summary>从配置重新绑定可热载项并应用；成功返回 true。</summary>
    public bool Reload(out string? error)
    {
        try
        {
            var gateway = _configuration.GetSection("Gateway");

            var rateLimit = gateway.GetSection("RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions();
            _rateLimitState.ApplyOptions(rateLimit);

            var ipFilter = gateway.GetSection("IpFilter").Get<IpFilterOptions>() ?? new IpFilterOptions();
            _ipFilterState.ApplyOptions(ipFilter);

            LastReloadAtUtc = DateTime.UtcNow;
            GatewayLogger.Info($"治理配置已热重载：RateLimit(PerMinute={rateLimit.PerMinute},Burst={rateLimit.Burst},Delay={rateLimit.DelayEnabled}) " +
                               $"IpFilter(Enabled={ipFilter.Enabled},Allow={ipFilter.Allow.Length},Deny={ipFilter.Deny.Length})");
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
