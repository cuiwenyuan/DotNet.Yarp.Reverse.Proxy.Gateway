using System.Net;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// IP 黑白名单共享状态（单例）：预解析 CIDR 规则并支持运行时热更新配置。
/// 中间件每次请求经 IsAllowed 判断；管理端点热重载时调用 ApplyOptions 重建规则集。
/// 纯 BCL 实现。
/// </summary>
public sealed class IpFilterState
{
    private readonly object _gate = new();
    private IpFilterOptions _options;
    private IpAddressUtil.CidrRule[] _allow = Array.Empty<IpAddressUtil.CidrRule>();
    private IpAddressUtil.CidrRule[] _deny = Array.Empty<IpAddressUtil.CidrRule>();

    public IpFilterState(IpFilterOptions options)
    {
        _options = options;
        RebuildRules(options);
    }

    /// <summary>拦截时返回的状态码。</summary>
    public int StatusCode => _options.StatusCode;

    /// <summary>是否启用 IP 过滤。</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// 判断来源 IP 是否被拦截（黑名单命中或不在白名单内）。
    /// 规则为空或未启用时返回 false（不拦截）。
    /// </summary>
    public bool IsBlocked(string? ip)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(ip)) return false;
        if (!IPAddress.TryParse(ip, out var address)) return false;

        var allow = _allow;
        var deny = _deny;
        var denied = IpAddressUtil.MatchesAny(address, deny);
        var allowed = allow.Length == 0 || IpAddressUtil.MatchesAny(address, allow);
        return denied || !allowed;
    }

    /// <summary>热重载：替换配置并重建规则集。</summary>
    public void ApplyOptions(IpFilterOptions options)
    {
        lock (_gate)
        {
            _options = options;
            RebuildRules(options);
        }
    }

    private void RebuildRules(IpFilterOptions options)
    {
        _allow = IpAddressUtil.ParseList(options.Allow);
        _deny = IpAddressUtil.ParseList(options.Deny);
    }
}

/// <summary>
/// IP 黑白名单拦截中间件（对应 nginx 的 allow / deny 指令）。
/// 匹配优先级：黑名单命中即拒绝；白名单非空时仅放行名单内来源。
/// 客户端 IP 取自 Connection.RemoteIpAddress（已由 UseForwardedHeaders 还原真实 IP）。
/// 规则经 IpFilterState 预解析与热更新，纯 BCL 实现。
/// </summary>
public class IpFilterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IpFilterState _state;

    public IpFilterMiddleware(RequestDelegate next, IpFilterState state)
    {
        _next = next;
        _state = state;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_state.IsBlocked(context.Connection.RemoteIpAddress?.ToString()))
        {
            GatewayLogger.Warn($"IP 过滤拦截：{context.Connection.RemoteIpAddress} -> {context.Request.Path}");
            context.Response.StatusCode = _state.StatusCode;
            await context.Response.WriteAsync("Forbidden");
            return;
        }

        await _next(context);
    }
}
