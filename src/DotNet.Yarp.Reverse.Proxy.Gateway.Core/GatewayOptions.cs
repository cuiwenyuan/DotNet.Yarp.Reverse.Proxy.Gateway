using Microsoft.AspNetCore.Http;

namespace DotNet.Yarp.Reverse.Proxy.Gateway.Core;

/// <summary>
/// 网关自研治理组件的配置绑定（对应 appsettings.json 的 "Gateway" 节点）。
/// 仅使用 .NET 配置绑定（Microsoft.Extensions.Configuration），属于 BCL/框架自带能力。
/// </summary>
public class GatewayOptions
{
    public RateLimitOptions RateLimit { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public LocationRewriteOptions LocationRewrite { get; set; } = new();
    public ProxyLoggingOptions ProxyLogging { get; set; } = new();
    public IpFilterOptions IpFilter { get; set; } = new();
    public RequestSizeOptions RequestSize { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public SecurityHeadersOptions SecurityHeaders { get; set; } = new();
    public MetricsOptions Metrics { get; set; } = new();
    public HttpTimeoutOptions HttpTimeout { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
}

public class RateLimitOptions
{
    /// <summary>每分钟允许的请求数（令牌补充速率）。对应 nginx 的 limit_req rate。</summary>
    public int PerMinute { get; set; } = 600;

    /// <summary>
    /// 桶容量（允许的瞬时突发积压令牌数，对应 nginx limit_req burst）。
    /// 0 或小于 PerMinute 时取 PerMinute（无额外突发，等价原行为）；
    /// 大于 PerMinute 时允许瞬时突发（如 PerMinute=60、Burst=120 允许瞬时 120 后按 1/s 平滑）。
    /// </summary>
    public int Burst { get; set; }

    /// <summary>
    /// 平滑模式（对应 nginx limit_req delay / nodelay）。
    /// 为 true 时，超额请求最多等待 MaxDelayMs 毫秒放行（平滑排队）而非立即拒绝；
    /// 等待仍超限才返回 429。默认 false：超限立即 429。
    /// </summary>
    public bool DelayEnabled { get; set; }

    /// <summary>平滑模式下允许的最大等待时长（毫秒），默认 1000。超过则拒绝。</summary>
    public int MaxDelayMs { get; set; } = 1000;

    /// <summary>
    /// 是否按客户端 IP 分桶限流（对应 nginx limit_req 默认按 remote_addr 计 key）。
    /// 开启后每个 IP 独立配额，防止单客户端打满全局额度；关闭则退回全局共享令牌桶。
    /// </summary>
    public bool PerIp { get; set; } = true;

    /// <summary>按 IP 分桶的桶数量上限，防止内存无限增长（超出后回退到全局桶）。</summary>
    public int MaxIpBuckets { get; set; } = 10000;

    /// <summary>IP 桶空闲回收时间（分钟）。超过该时长无访问的 IP 桶将被惰性清理。</summary>
    public int BucketIdleMinutes { get; set; } = 30;
}

public class CorsOptions
{
    /// <summary>允许的来源列表；为空表示放行任意来源（*）。</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    public string[] AllowedMethods { get; set; } = { "GET", "POST", "PUT", "DELETE", "OPTIONS" };

    public string[] AllowedHeaders { get; set; } = { "Content-Type", "Authorization", "X-Api-Key" };
}

public class AuthOptions
{
    /// <summary>是否开启简易 API Key 校验（等价于 nginx 的 auth_request 轻量版）。</summary>
    public bool Required { get; set; }

    /// <summary>合法的 Key 集合（生产环境建议从配置中心/密钥库读取，勿硬编码）。</summary>
    public string[] Keys { get; set; } = Array.Empty<string>();

    /// <summary>JWT Bearer 认证配置（自研纯 BCL 校验，支持 HS256 / RS256）。</summary>
    public JwtOptions Jwt { get; set; } = new();
}

/// <summary>JWT Bearer 认证配置（对应 Kong jwt 插件 / nginx plus JWT 校验）。</summary>
public class JwtOptions
{
    /// <summary>是否启用 JWT 校验。启用后请求必须携带合法的 `Authorization: Bearer &lt;token&gt;`。</summary>
    public bool Enabled { get; set; }

    /// <summary>HS256 对称密钥（UTF-8 或 Base64）。启用 HS256 时必填。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>RS256 公钥 PEM（含 -----BEGIN PUBLIC KEY----- 头尾，或裸 Base64 SubjectPublicKeyInfo）。</summary>
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>可选：配置后校验 token 的 iss 声明。</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>可选：配置后校验 token 的 aud 声明。</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>时间偏移容差（秒），默认 60。</summary>
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>是否强制要求 exp 声明（默认 true，无 exp 视为无效）。</summary>
    public bool RequireExpiry { get; set; } = true;

    /// <summary>校验通过后透传给后端的用户标识头（默认取 sub 声明，头名 X-Jwt-Sub）。</summary>
    public string ClaimsHeader { get; set; } = "X-Jwt-Sub";
}

public class CacheOptions
{
    /// <summary>是否启用响应缓存（对应 nginx proxy_cache）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>响应缓存默认 TTL（秒）。</summary>
    public int DefaultTtlSeconds { get; set; } = 30;

    /// <summary>缓存条目上限（防止缓存投毒导致内存膨胀，超出后不再写入新缓存）。</summary>
    public int MaxCacheEntries { get; set; } = 10000;

    /// <summary>可缓存的状态码（默认仅 200）。</summary>
    public int[] CacheableStatusCodes { get; set; } = { StatusCodes.Status200OK };
}

/// <summary>扩展名黑名单拦截配置（对应 Gitee 的 SecurityConfig / AspxBlocker）。</summary>
public class SecurityOptions
{
    /// <summary>是否启用扩展名黑名单拦截。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>被拦截的扩展名列表（可带或不带点，如 ".aspx" 或 "aspx"）。</summary>
    public string[] BlockedExtensions { get; set; } =
        { "aspx", "asp", "php", "jsp", "asa", "asax", "ashx", "axd", "config" };

    /// <summary>拦截时返回的 HTTP 状态码（默认 403 Forbidden）。</summary>
    public int StatusCode { get; set; } = StatusCodes.Status403Forbidden;
}

/// <summary>Location 改写单条规则（Match 前缀 -> Replace 前缀）。</summary>
public class LocationRewriteRule
{
    /// <summary>要匹配的后端 origin 前缀，如 "http://localhost:5101"。</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>替换为的公网入口前缀，如 "https://gw.example.com"。</summary>
    public string Replace { get; set; } = string.Empty;
}

/// <summary>重定向 Location 改写配置（对应 Gitee 的 LocationRewrite / nginx proxy_redirect）。</summary>
public class LocationRewriteOptions
{
    /// <summary>是否启用 Location 改写。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 为 true 时，将后端绝对 Location 的 origin 直接替换为请求自身的 scheme://host
    /// （等价 nginx `proxy_redirect default`）。相对路径 Location 不会受影响。
    /// </summary>
    public bool UseRequestHostAsPublic { get; set; } = true;

    /// <summary>显式前缀替换规则（Match -> Replace），首个匹配生效。UseRequestHostAsPublic 为 false 或需固定映射时使用。</summary>
    public LocationRewriteRule[] Rules { get; set; } = Array.Empty<LocationRewriteRule>();
}

/// <summary>代理请求/响应详情日志配置（对应 Gitee 的 ProxyLoggingMiddleware）。</summary>
public class ProxyLoggingOptions
{
    /// <summary>是否启用代理日志。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>请求体/响应体读取上限（字节）。超过此大小的文本体将被截断，二进制体不读取。默认 64KB。</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>是否记录请求头（默认关闭，避免日志过噪）。</summary>
    public bool LogRequestHeaders { get; set; }

    /// <summary>是否记录请求体（默认开启）。</summary>
    public bool LogRequestBody { get; set; } = true;

    /// <summary>是否记录响应体（默认开启）。注意：会使用 MemoryStream 接管 Response.Body，大响应会占用内存，生产可按需关闭。</summary>
    public bool LogResponseBody { get; set; } = true;

    /// <summary>忽略规则：命中这些状态码的请求不写日志（减少噪声，例如填入 304 可忽略缓存命中）。</summary>
    public int[] IgnoreStatusCodes { get; set; } = Array.Empty<int>();

    /// <summary>
    /// 是否以单行 JSON 格式输出代理日志（便于 ELK / Loki 采集）。
    /// 默认 false：保持人类可读的多行文本格式。
    /// </summary>
    public bool Json { get; set; }

    /// <summary>
    /// 细粒度请求体日志规则（对应 GoReverseProxy 的 BodyLogRules）。
    /// 非空时仅"路径包含 PathContains 且状态码命中 StatusCodes"的请求记录请求体；
    /// 为空时按全局 LogRequestBody 开关记录。PathContains 忽略大小写，StatusCodes 为空表示任意状态码。
    /// </summary>
    public BodyLogRule[] BodyLogRules { get; set; } = Array.Empty<BodyLogRule>();
}

/// <summary>请求体日志规则：路径包含 + 状态码组合。</summary>
public class BodyLogRule
{
    /// <summary>路径需包含的子串（忽略大小写），如 "/api/login"。</summary>
    public string PathContains { get; set; } = string.Empty;

    /// <summary>命中的状态码列表；为空表示任意状态码。</summary>
    public int[] StatusCodes { get; set; } = Array.Empty<int>();
}

/// <summary>IP 黑白名单拦截配置（对应 nginx 的 allow / deny 指令）。</summary>
public class IpFilterOptions
{
    /// <summary>是否启用 IP 过滤。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 白名单（CIDR 或单 IP，如 "10.0.0.0/8"、"192.168.1.1"、"::1"）。
    /// 非空时仅放行名单内来源；空表示不启用白名单。
    /// </summary>
    public string[] Allow { get; set; } = Array.Empty<string>();

    /// <summary>黑名单（CIDR 或单 IP）。命中即拒绝；黑名单优先于白名单。</summary>
    public string[] Deny { get; set; } = Array.Empty<string>();

    /// <summary>拦截时返回的 HTTP 状态码（默认 403 Forbidden）。</summary>
    public int StatusCode { get; set; } = StatusCodes.Status403Forbidden;
}

/// <summary>请求体大小限制配置（对应 nginx 的 client_max_body_size）。</summary>
public class RequestSizeOptions
{
    /// <summary>是否启用请求体大小限制。</summary>
    public bool Enabled { get; set; }

    /// <summary>允许的最大请求体大小（字节）。默认 10MB。</summary>
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>超限时返回的 HTTP 状态码（默认 413 Payload Too Large）。</summary>
    public int StatusCode { get; set; } = StatusCodes.Status413PayloadTooLarge;
}

/// <summary>失败重试配置（对应 nginx 的 proxy_next_upstream）。</summary>
public class RetryOptions
{
    /// <summary>是否启用失败重试。</summary>
    public bool Enabled { get; set; }

    /// <summary>最多重试次数（不含首次请求），默认 1 次，防风暴。</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>可重试的响应状态码（默认 502/504：网关连接失败、上游超时类）。</summary>
    public int[] RetryableStatusCodes { get; set; } = { StatusCodes.Status502BadGateway, StatusCodes.Status504GatewayTimeout };

    /// <summary>是否在转发异常（连接失败/超时抛错）时重试。默认开启。</summary>
    public bool RetryOnConnectionError { get; set; } = true;

    /// <summary>是否对 500 内部错误也重试（默认关闭，500 一般代表后端业务异常，重试意义有限）。</summary>
    public bool RetryOnHttp500 { get; set; }
}

/// <summary>熔断/降级配置（对应 Ocelot QoS、Kong 熔断插件、nginx plus 被动健康检查升级版）。</summary>
public class CircuitBreakerOptions
{
    /// <summary>是否启用熔断。</summary>
    public bool Enabled { get; set; }

    /// <summary>连续失败达到该次数后熔断打开（Closed -> Open）。</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>熔断打开持续时间（秒），到期后进入半开状态放行探测请求。</summary>
    public int OpenSeconds { get; set; } = 30;

    /// <summary>半开状态允许同时放行的探测请求数。</summary>
    public int HalfOpenMaxRequests { get; set; } = 1;

    /// <summary>视为"失败"的响应状态码（默认 5xx）。</summary>
    public int[] FailureStatusCodes { get; set; } =
        { StatusCodes.Status500InternalServerError, StatusCodes.Status502BadGateway,
          StatusCodes.Status503ServiceUnavailable, StatusCodes.Status504GatewayTimeout };

    /// <summary>熔断打开时返回的 HTTP 状态码（默认 503 Service Unavailable）。</summary>
    public int StatusCode { get; set; } = StatusCodes.Status503ServiceUnavailable;
}

/// <summary>安全响应头配置（对应各网关的安全头插件，如 SecurityHeaders）。</summary>
public class SecurityHeadersOptions
{
    /// <summary>是否启用安全响应头。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>X-Frame-Options 值（空字符串 = 不添加）。</summary>
    public string XFrameOptions { get; set; } = "SAMEORIGIN";

    /// <summary>X-Content-Type-Options 值（空字符串 = 不添加）。</summary>
    public string XContentTypeOptions { get; set; } = "nosniff";

    /// <summary>Referrer-Policy 值（空字符串 = 不添加）。</summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>Content-Security-Policy 值（空字符串 = 不添加；按需配置，如 "default-src 'self'"）。</summary>
    public string ContentSecurityPolicy { get; set; } = string.Empty;

    /// <summary>X-Permitted-Cross-Domain-Policies 值（空字符串 = 不添加）。</summary>
    public string XPermittedCrossDomainPolicies { get; set; } = "none";
}

/// <summary>监控指标配置（自研 /metrics 与 /health 端点，Prometheus 文本格式）。</summary>
public class MetricsOptions
{
    /// <summary>是否启用监控指标端点。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Prometheus 指标端点路径，默认 /metrics。</summary>
    public string MetricsPath { get; set; } = "/metrics";

    /// <summary>存活探针路径，默认 /health。</summary>
    public string HealthPath { get; set; } = "/health";

    /// <summary>是否按路径前缀打标（会引入高基数标签，默认关闭）。</summary>
    public bool IncludePathLabels { get; set; }
}

/// <summary>上游超时控制配置（对应 nginx 的 proxy_connect_timeout / proxy_read_timeout）。</summary>
public class HttpTimeoutOptions
{
    /// <summary>是否启用超时控制。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>到后端的 TCP 连接超时（秒），默认 10。通过 SocketsHttpHandler.ConnectTimeout 生效。</summary>
    public int ConnectSeconds { get; set; } = 10;
}

/// <summary>管理状态端点配置（轻量管理 API）。</summary>
public class AdminOptions
{
    /// <summary>是否启用管理端点。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>运行状态 JSON 摘要路径，默认 /admin/status。</summary>
    public string Path { get; set; } = "/admin/status";

    /// <summary>是否启用配置热重载端点（POST，仅 localhost 可调用）。</summary>
    public bool ReloadEnabled { get; set; } = true;

    /// <summary>配置热重载端点路径，默认 /admin/reload。</summary>
    public string ReloadPath { get; set; } = "/admin/reload";
}
