using System.Net;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// IP/CIDR 匹配工具（纯 BCL，兼容 IPv4 / IPv6 / IPv4-mapped-IPv6）。
/// 对应 nginx 的 allow / deny 指令中的地址与 CIDR 写法。
/// </summary>
public static class IpAddressUtil
{
    /// <summary>预解析后的单条规则：网络地址 + 前缀长度（位）。</summary>
    public readonly struct CidrRule
    {
        public IPAddress Network { get; }
        public int PrefixLength { get; }

        public CidrRule(IPAddress network, int prefixLength)
        {
            Network = network;
            PrefixLength = prefixLength;
        }
    }

    /// <summary>把配置字符串列表（"1.2.3.4"、"10.0.0.0/8"、"fe80::/10"）预解析为规则数组。</summary>
    public static CidrRule[] ParseList(string[] entries)
    {
        var list = new List<CidrRule>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var s = entry.Trim();
            var slash = s.IndexOf('/');
            if (slash < 0)
            {
                if (IPAddress.TryParse(s, out var ip))
                    list.Add(new CidrRule(ip, ip.GetAddressBytes().Length * 8));
            }
            else if (IPAddress.TryParse(s[..slash], out var net) && int.TryParse(s[(slash + 1)..], out var prefix))
            {
                list.Add(new CidrRule(net, prefix));
            }
        }
        return list.ToArray();
    }

    /// <summary>判断 IP 是否命中任一规则。</summary>
    public static bool MatchesAny(IPAddress? ip, CidrRule[] rules)
    {
        if (ip is null) return false;
        foreach (var rule in rules)
        {
            if (IsMatch(ip, rule)) return true;
        }
        return false;
    }

    /// <summary>判断 IP 是否命中单条规则（CIDR 前缀匹配）。</summary>
    public static bool IsMatch(IPAddress ip, CidrRule rule)
    {
        var ipBytes = ip.GetAddressBytes();
        var netBytes = rule.Network.GetAddressBytes();

        // 统一地址族：IPv4-mapped-IPv6（::ffff:x.x.x.x）与纯 IPv4 互相归一
        if (ipBytes.Length != netBytes.Length)
        {
            if (ipBytes.Length == 16 && netBytes.Length == 4 && ip.IsIPv4MappedToIPv6)
                ipBytes = ip.MapToIPv4().GetAddressBytes();
            else if (ipBytes.Length == 4 && netBytes.Length == 16 && rule.Network.IsIPv4MappedToIPv6)
                netBytes = rule.Network.MapToIPv4().GetAddressBytes();
            else
                return false;
        }

        var bitLength = ipBytes.Length * 8;
        var prefix = Math.Min(rule.PrefixLength, bitLength);
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (ipBytes[i] != netBytes[i]) return false;
        }

        if (remainingBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }

        return true;
    }
}
