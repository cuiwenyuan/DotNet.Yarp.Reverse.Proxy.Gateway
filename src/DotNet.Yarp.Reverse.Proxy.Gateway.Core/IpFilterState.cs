using System.Net;
using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// IP 黑白名单共享状态（单例）：预解析 CIDR 规则并支持运行时热更新配置。
/// 规则集与开关打包为不可变快照，一次 Volatile.Write 发布：请求线程一次 Volatile.Read
/// 拿到同一代配置，杜绝"新 Enabled + 旧规则"这类跨代组合。纯 BCL 实现。
/// </summary>
public sealed class IpFilterState
{
    private readonly object _gate = new();

    /// <summary>一代完整规则集；构造完成后不再修改。</summary>
    private sealed class IpRules
    {
        public readonly bool Enabled;
        public readonly int StatusCode;
        public readonly IpAddressUtil.CidrRule[] Allow;
        public readonly IpAddressUtil.CidrRule[] Deny;

        public IpRules(IpFilterOptions options)
        {
            Enabled = options.Enabled;
            StatusCode = options.StatusCode;
            Allow = IpAddressUtil.ParseList(options.Allow);
            Deny = IpAddressUtil.ParseList(options.Deny);
        }
    }

    private IpRules _rules;

    public IpFilterState(IpFilterOptions options) => _rules = new IpRules(options);

    /// <summary>拦截时返回的状态码。</summary>
    public int StatusCode => Volatile.Read(ref _rules).StatusCode;

    /// <summary>是否启用 IP 过滤。</summary>
    public bool Enabled => Volatile.Read(ref _rules).Enabled;

    /// <summary>
    /// 判断来源 IP 是否被拦截（黑名单命中或不在白名单内）。
    /// 规则为空或未启用时返回 false（不拦截）。
    /// </summary>
    public bool IsBlocked(string? ip)
    {
        // 一次读取整代快照，后续判断只用这一份，避免热重载期间 allow/deny 跨代混用。
        var rules = Volatile.Read(ref _rules);
        if (!rules.Enabled || string.IsNullOrEmpty(ip)) return false;
        if (!IPAddress.TryParse(ip, out var address)) return false;

        var denied = IpAddressUtil.MatchesAny(address, rules.Deny);
        var allowed = rules.Allow.Length == 0 || IpAddressUtil.MatchesAny(address, rules.Allow);
        return denied || !allowed;
    }

    /// <summary>热重载：构造完整新规则集后一次性原子发布。</summary>
    public void ApplyOptions(IpFilterOptions options)
    {
        var next = new IpRules(options); // 解析放在锁外，缩短临界区
        lock (_gate)
        {
            Volatile.Write(ref _rules, next);
        }
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
