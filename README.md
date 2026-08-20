# DotNet.Yarp.Reverse.Proxy.Gateway

基于 .NET 6 / .NET 8 的轻量级反向代理网关，用于在 Windows Server 上替代 nginx 的核心转发能力
（反向代理、负载均衡、健康检查、限流、跨域、入口安全、Location 改写、请求/响应日志）。

> 项目工作目录名为 `DotNet.Nginx`，仓库 / 解决方案名称为 `DotNet.Yarp.Reverse.Proxy.Gateway`。
> 二者指向同一份代码，仅命名层级不同（工作目录未改名以避免移动风险）。

---

## 1. 为什么做这个项目

在 Windows Server 上部署 nginx 常常伴随：原生二进制维护、与 .NET 生态割裂、配置分散、难以纳入
CI/CD 与日志体系。本项目用**纯 .NET** 实现一套可托管在 IIS 下的反向代理入口，把"转发 + 治理"收拢到
同一个进程、同一套代码与配置里，并可在修改 `appsettings.json` 后**热加载**（等价 `nginx -s reload`）。

### Windows 上部署 nginx 的常见问题（官方事实）

> 依据 [nginx for Windows 官方文档](https://nginx.org/en/docs/windows.html)（官方明确 Windows 版为
> **beta 版本**）与 nginx 官方核心开发者邮件整理，非社区传言。

**A. 官方定位与性能限制**

| 问题 | 说明 |
| --- | --- |
| 高性能不可期待 | Windows 版仅用 `select()`/`poll()` 事件模型，官方原文 "high performance and scalability should not be expected" |
| 多 worker 形同虚设 | 可配置多个 worker，但只有一个实际处理连接，无法利用多核 |
| 单 worker 连接受限 | `select()` 模型下 1024 连接硬上限；`poll()`（1.15.9+）无硬上限但存在 WSAPoll 缺陷 |
| 不支持 sendfile | 静态文件走用户态拷贝，大文件 / 高并发吞吐低于 Linux |
| 官方不建议生产 | 核心开发者原文：Running nginx on Windows in production setups might not be a good idea |

**B. 功能缺失（Windows 版）**

| 问题 | 说明 |
| --- | --- |
| 缓存不可用 | `proxy_cache` 等依赖共享内存的模块在 Windows（ASLR）上不工作 |
| 不支持 UDP / QUIC | Windows 版无 UDP 代理能力 |
| 缺少部分模块 | XSLT 过滤、图片过滤、GeoIP、内嵌 Perl 等缺失 |

**C. 部署与运维痛点**

| 问题 | 说明 |
| --- | --- |
| 非 Windows 服务 | 以控制台程序运行：开机不自启、崩溃不自动拉起，需 WinSW / NSSM 等第三方工具注册服务 |
| 配置易错难查 | C 风格语法、路径须 UNIX 正斜杠、相对路径以运行目录为前缀；改错配置 reload 即中断服务 |
| 443/80 端口被占用 | 常被 IIS 默认站点、Skype、其他 Web 服务、WSL2 / Docker 抢占；nginx 启动失败只写 error.log，排障依赖 `netstat` 查 PID，且与 IIS 同端口冲突频繁 |
| 二进制维护 | 升级需手动下载替换；部分杀毒软件对 nginx.exe 误报 |
| 无内置监控 | `stub_status` 需编译进模块（官方 Windows 版不含）；日志为纯文本 access_log，与集中日志体系割裂 |
| 无管理 API | 开源版无动态配置 / 管理端点，灰度、限流调整都需改文件 reload |

**D. 生态割裂（.NET 团队视角）**

配置与 .NET 代码 / CI/CD / 密钥管理割裂，团队学习成本高，难以纳入统一运维体系。

### 本系统如何对应解决

| nginx 痛点 | 本系统方案 |
| --- | --- |
| 性能 / 扩展性受限 | .NET 异步模型 + YARP，IIS / ANCM 托管，多核原生利用 |
| 不支持 sendfile | Kestrel / IIS 原生内核传输（ANCM inprocess） |
| 缓存不可用 | 自研 `ResponseCacheMiddleware` 内存缓存（`proxy_cache` 等价） |
| 非服务化 | IIS 应用池托管：开机自启、崩溃自动回收重启 |
| 端口被占用 / 与 IIS 抢 443 | 网关即 IIS 托管，不存在 nginx 与 IIS 抢端口的场景；443 绑定由 IIS 统一管理、冲突可视化 |
| 配置易错 | `--test-config` 预检（等价 `nginx -t`）+ `POST /admin/reload` 在线热重载 |
| 无内置监控 | `/metrics`（Prometheus）+ `/health` + `/admin/status` |
| 无管理 API | `/admin/status` + `/admin/reload` 管理端点 |
| 生态割裂 | 纯 .NET：与 CI/CD、密钥库、日志体系同生态 |

## 2. 功能特性

| 能力 | 说明 | 等价 nginx |
| --- | --- | --- |
| 反向代理转发 | YARP 按路由转发到后端集群 | `proxy_pass` |
| 负载均衡 | RoundRobin / PowerOfTwo / 等策略 | `upstream` + 算法 |
| 主动 / 被动健康检查 | 周期探测后端 `/health` | `health_check` |
| 限流 | 自研令牌桶（rate/burst/delay 可配），输出 X-Rate-Limit-* 头 | `limit_req` |
| 跨域 CORS | 来源 / 方法 / 头白名单 | `add_header 'Access-Control-Allow-*'` |
| 简易 API Key 认证 | 校验 `X-Api-Key` 头 | `auth_request` 轻量版 |
| 入口安全拦截 | 扩展名黑名单（.aspx/.php/.jsp…）直接 403 | `location ~* \.(aspx\|php)$ { return 403 }` |
| IP 黑白名单 | CIDR 前缀匹配 allow/deny，黑名单命中即 403 | `allow` / `deny` |
| 请求体大小限制 | Content-Length 超限直接 413 | `client_max_body_size` |
| 失败重试 | 幂等请求连接失败/502/504 时换节点重试 | `proxy_next_upstream` |
| 熔断/降级 | 连续失败自动熔断，半开探测自动恢复 | Ocelot QoS / 熔断插件 |
| 会话保持（sticky） | YARP 原生 SessionAffinity，Cookie 保持同一后端 | nginx plus / `ip_hash` |
| JWT 认证 | 自研纯 BCL 校验 HS256/RS256，Bearer 透传 sub 给后端 | Kong `jwt` / nginx plus |
| gzip 压缩 | 框架内置 ResponseCompression（HTTP 与 HTTPS 均生效） | `gzip` |
| 响应缓存 | 内存缓存 GET/HEAD 200 响应（TTL 可配） | `proxy_cache` |
| 安全响应头 | X-Frame-Options / nosniff / CSP / Referrer-Policy 等 | SecurityHeaders 插件 |
| 监控指标 | `/metrics`（Prometheus 文本）+ `/health` 存活探针 | `stub_status` |
| 管理状态端点 | `/admin/status`（JSON 摘要）+ `POST /admin/reload`（localhost 热重载治理配置） | nginx plus API |
| 配置预检 | `--test-config` 启动前校验配置（唯一性/地址合法性） | `nginx -t` |
| 上游超时控制 | 连接超时 + 集群 ActivityTimeout | `proxy_connect_timeout` / `proxy_read_timeout` |
| 加权负载均衡 | 按 Metadata.Weight 加权随机（可配权重） | upstream `weight` |
| 灰度发布（canary） | YARP 路由按 header 匹配分流到不同集群 | 分流/灰度配置 |
| JSON 结构化日志 | 代理日志单行 JSON（便于 ELK/Loki） | `log_format json` |
| 重定向 Location 改写 | 将后端绝对 `Location` 改写成公网入口 | `proxy_redirect` |
| 请求 / 响应详情日志 | 记录方法 / URL / IP / 耗时 / 状态码 / 请求体 / 响应体（支持按路径+状态码规则与 JSON 输出） | `access_log` + `log_subrequest` |
| 静态文件托管 | `wwwroot` 目录静态资源 | `location { root }` |
| WebSocket / gRPC 透传 | YARP 原生支持 | `proxy_http_version 1.1` + Upgrade |
| 转发头还原 | 还原真实客户端 IP / 协议 | `set_real_ip` / `X-Forwarded-*` |

## 3. 技术栈与依赖约束

**硬约束：整个方案只引用两个 NuGet 包**，其余能力全部用 .NET BCL（标准类库）或 ASP.NET Core 框架自带能力实现。

| 包 / 引用 | 作用 | 类型 |
| --- | --- | --- |
| `Yarp.ReverseProxy` 2.3.0 | 微软官方反向代理内核 | 第三方 NuGet（允许） |
| `NewLife.Core` 11.18.2026.801 | 日志（XTrace）、配置、缓存基础设施 | 第三方 NuGet（允许，唯一额外第三方） |
| `Microsoft.AspNetCore.App` | ASP.NET Core 共享框架（HttpContext / 中间件等） | FrameworkReference（**非** NuGet） |
| 其余全部 | 限流、缓存、认证、CORS、扩展名拦截、日志等 | 纯 C# BCL 自研 |

> 说明：`Microsoft.AspNetCore.App` 是编写 ASP.NET Core 应用/中间件的**标准框架引用**，不属于第三方包，
> 不会违反"只引两个 NuGet"的约束。限流特意**没有**使用 `System.Threading.RateLimiting`
> （.NET 7+ 专属），而是自研 `SimpleRateLimiter` 令牌桶以兼容 .NET 6 / 8 双目标框架。

## 4. 环境要求

- **构建端**：安装 .NET 6 SDK 或 .NET 8 SDK（任一即可；双目标框架会同时产出 `net6.0` 与 `net8.0` 两个产物）。
- **运行端**：部署机安装对应的 **ASP.NET Core Runtime 6 / 8**（或 IIS 用的 **.NET Core Hosting Bundle**）。
- **操作系统**：Windows Server 2016+（IIS 托管）；也可在 Linux / macOS 以 Kestrel 自托管。

## 5. 快速开始

```bash
# 1) 克隆 / 进入仓库根目录（含 DotNet.Yarp.Reverse.Proxy.Gateway.slnx）
cd DotNet.Nginx

# 2) 还原并构建（双目标框架，Release）
dotnet build -c Release

# 2.1) 配置预检（可选，等价 nginx -t）：校验路由/集群唯一性与后端地址合法性
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net8.0 -o publish
./publish/DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # 输出 Configuration OK

# 3) 启动网关（Kestrel 默认监听 http://localhost:5000）
dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web
```

浏览器访问 `http://localhost:5000/` 应返回静态首页（`wwwroot/index.html`，200）。

### 用脚本做冒烟验收

仓库内置纯 PowerShell 验收脚本（无第三方依赖），并附带两个 Python 冒烟后端：

```bash
# 终端 A：启动两个冒烟后端
python scripts/echo_backend.py      # 监听 5200（web-cluster）
python scripts/redir_backend.py     # 监听 5101（api-cluster 之一，用于 Location 改写验证）

# 终端 B：启动网关
dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web

# 终端 C：运行验收
pwsh scripts/verify.ps1
```

## 6. 项目结构

```
DotNet.Nginx/
├── DotNet.Yarp.Reverse.Proxy.Gateway.slnx   # 解决方案
├── src/
│   ├── DotNet.Yarp.Reverse.Proxy.Gateway.Core/   # 类库：自研治理组件（纯 BCL + NewLife.Core）
│   │   ├── GatewayOptions.cs             # 配置绑定（对应 appsettings.json 的 "Gateway"）
│   │   ├── GatewayExtensions.cs          # 注册与管线挂载（AddGateway / UseGatewayGovernance）
│   │   ├── SimpleRateLimiter.cs          # 自研令牌桶（rate/burst/delay，余量查询）
│   │   ├── RateLimitState.cs             # 限流共享状态 + 中间件（X-Rate-Limit-* 头、429/延迟）
│   │   ├── CorsMiddleware.cs             # 跨域中间件
│   │   ├── ApiKeyAuthMiddleware.cs        # 简易 API Key 认证（失败返回 401）
│   │   ├── AspxBlockerMiddleware.cs      # 扩展名黑名单拦截（命中返回 403）
│   │   ├── IpAddressUtil.cs              # IP/CIDR 匹配工具（IPv4/IPv6）
│   │   ├── IpFilterState.cs              # IP 黑白名单共享状态 + 中间件（可热重载）
│   │   ├── RequestSizeLimitMiddleware.cs # 请求体大小限制中间件（超限返回 413）
│   │   ├── RetryMiddleware.cs            # 失败重试中间件（换节点重试）
│   │   ├── CircuitBreaker.cs             # 熔断状态机（Closed/Open/HalfOpen）
│   │   ├── CircuitBreakerMiddleware.cs   # 熔断中间件（打开时返回 503）
│   │   ├── JwtAuthMiddleware.cs          # JWT Bearer 认证（纯 BCL HS256/RS256）
│   │   ├── ResponseCacheMiddleware.cs    # 响应缓存中间件（proxy_cache）
│   │   ├── SecurityHeadersMiddleware.cs  # 安全响应头中间件
│   │   ├── GatewayMetrics.cs             # 监控指标采集器 + /metrics /health 中间件
│   │   ├── AdminStatusMiddleware.cs      # 管理端点（/admin/status + POST /admin/reload）
│   │   ├── GatewayConfigReloader.cs      # 治理配置热重载器（RateLimit/IpFilter）
│   │   ├── LocationRewriteMiddleware.cs   # 重定向 Location 改写
│   │   ├── ProxyLoggingMiddleware.cs      # 请求/响应详情日志
│   │   ├── SimpleMemoryCache.cs          # 内置内存缓存组件（扩展点）
│   │   └── GatewayLogger.cs              # NewLife.Core XTrace 日志封装
│   └── DotNet.Yarp.Reverse.Proxy.Gateway.Web/    # ASP.NET Core 宿主（仅引 YARP + 项目引用）
│       ├── Program.cs                    # 管线组装 + --test-config 预检
│       ├── ConfigValidator.cs            # 配置预检工具（--test-config）
│       ├── WeightedLoadBalancingPolicy.cs # 加权负载策略（Metadata.Weight）
│       ├── appsettings.json              # 路由 / 集群 / 治理配置
│       ├── web.config                    # IIS（ANCM）托管配置
│       └── wwwroot/index.html            # 静态首页
├── scripts/
│   ├── verify.ps1                        # 验收脚本
│   ├── echo_backend.py                   # 冒烟后端（web 集群）
│   ├── smoke_api_5101.py                 # 冒烟后端（api 集群，重试链路验证）
│   └── redir_backend.py                  # 冒烟后端（Location 改写验证）
└── docs/
    ├── Architecture.zh-CN.md             # 系统架构（中文）
    ├── Architecture.en.md                # System Architecture (English)
    ├── Nginx-to-Gateway-Migration.md     # nginx → 网关迁移对照（脱敏示例）
    ├── Nginx-to-Gateway-Migration.en.md  # nginx → Gateway Migration Guide (English)
    ├── IIS-Deployment.zh-CN.md           # IIS 安装部署指南（中文）
    └── IIS-Deployment.en.md              # IIS Deployment Guide（English）
```

## 7. 请求处理管线（由外到内）

```
请求 → UseForwardedHeaders（还原 X-Forwarded-*）
     → MetricsMiddleware（最外层：/metrics 与 /health 短路，统计全部请求）
     → UseResponseCompression（gzip 压缩，日志/重试在内层记录原始字节）
     → ProxyLoggingMiddleware（记录请求/响应详情）
     → RetryMiddleware（失败重试：幂等请求失败时换节点重试）
     → UseGatewayGovernance：
          AspxBlocker（扩展名黑名单）
          → SecurityHeaders（安全响应头）
          → IpFilter（IP 黑白名单）
          → RequestSize（请求体大小限制）
          → CORS
          → 限流
          → CircuitBreaker（熔断/降级）
          → API Key 认证
          → JWT 认证（HS256/RS256）
          → ResponseCache（响应缓存）
          → LocationRewrite（重定向 Location 改写）
     → UseFileServer（静态文件）
     → MapReverseProxy（YARP 转发到后端集群，含 SessionAffinity 会话保持）
```

## 8. 配置参考（appsettings.json）

### 8.1 反向代理（`ReverseProxy`）

路由与集群完全遵循 YARP 配置契约，支持 `LoadFromConfig` 热加载。示例：

```json
"ReverseProxy": {
  "Routes": {
    "api-route": {
      "ClusterId": "api-cluster",
      "Match": { "Path": "/api/{**catch-all}" },
      "Transforms": [ { "PathRemovePrefix": "/api" } ]
    }
  },
  "Clusters": {
    "api-cluster": {
      "LoadBalancingPolicy": "RoundRobin",
      "Destinations": {
        "api1": { "Address": "http://localhost:5101/" },
        "api2": { "Address": "http://localhost:5102/" }
      },
      "HealthCheck": {
        "Active": { "Enabled": true, "Interval": "00:00:10", "Timeout": "00:00:02",
                    "Policy": "ConsecutiveFailures", "Path": "/health" }
      },
      "SessionAffinity": { "Enabled": true, "Policy": "Cookie", "AffinityKeyName": ".YarpGatewayAffinity" }
    }
  }
}
```

> 会话保持（sticky）：YARP 原生 `SessionAffinity`，在集群节点上配置即启用（Cookie 策略），
> 同一客户端持续落到同一后端节点（等价 nginx plus 的 sticky / 开源版 `ip_hash`）。
>
> 加权负载均衡：集群 `LoadBalancingPolicy` 填 `Weighted`，各 destination 通过
> `Metadata: { "Weight": "3" }` 声明权重（默认 1），按权重比例随机分发（等价 upstream `weight`）。
>
> 灰度发布（canary）：YARP 路由 `Match.Headers` 原生支持按请求头分流，例如：
> ```json
> { "canary-route": { "ClusterId": "canary-cluster",
>     "Match": { "Path": "/api/{**catch-all}", "Headers": [ { "Name": "X-Canary", "Values": [ "1" ] } ] } } }
> ```
> 带 `X-Canary: 1` 的请求进入 canary 集群，其余走默认路由——无需代码，纯配置热加载。

> 修改 `ReverseProxy` 节点后无需重启，YARP 会自动热加载。

### 8.2 入口治理（`Gateway`）

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `RateLimit:PerMinute` | `600` | 每个客户端每分钟请求数（令牌补充速率），超限返回 `429` |
| `RateLimit:Burst` | `0`（=PerMinute） | 桶容量（允许瞬时突发，对应 nginx limit_req burst） |
| `RateLimit:DelayEnabled` | `false` | 平滑模式：超额请求最多等待 MaxDelayMs 放行而非立即 429（对应 nginx delay） |
| `RateLimit:MaxDelayMs` | `1000` | 平滑模式最大等待时长（毫秒），超时仍拒绝 |
| `RateLimit:PerIp` | `true` | 按客户端 IP 分桶限流（等价 nginx limit_req 按 remote_addr 计 key） |
| `RateLimit:MaxIpBuckets` | `10000` | IP 桶数量上限，超出回退全局桶 |
| `RateLimit:BucketIdleMinutes` | `30` | IP 桶空闲回收时间（分钟） |
| `Cors:AllowedOrigins` | `[]`（放行所有） | 允许的来源；非空则仅放行列表内 |
| `Cors:AllowedMethods` | `GET,POST,PUT,DELETE,OPTIONS` | 允许的方法 |
| `Cors:AllowedHeaders` | `Content-Type,Authorization,X-Api-Key` | 允许的请求头 |
| `Auth:Required` | `false` | 是否开启 `X-Api-Key` 校验（开启后失败返回 `401`） |
| `Auth:Keys` | `["demo-key-1"]` | 合法 Key 集合（生产建议从密钥库读取，勿硬编码） |
| `Auth:Jwt:Enabled` | `false` | 是否启用 JWT Bearer 校验（失败返回 `401`） |
| `Auth:Jwt:Secret` | `""` | HS256 对称密钥（UTF-8 或 Base64） |
| `Auth:Jwt:PublicKeyPem` | `""` | RS256 公钥 PEM（或裸 Base64 SPKI） |
| `Auth:Jwt:Issuer` / `Audience` | `""` | 配置后校验 iss / aud 声明 |
| `Auth:Jwt:ClockSkewSeconds` | `60` | 时间偏移容差（秒） |
| `Auth:Jwt:RequireExpiry` | `true` | 强制要求 exp 声明 |
| `Auth:Jwt:ClaimsHeader` | `X-Jwt-Sub` | 校验通过后透传给后端的 sub 头名 |
| `Cache:Enabled` | `true` | 是否启用响应缓存（GET/HEAD 200，带 Authorization/Cookie 不缓存） |
| `Cache:DefaultTtlSeconds` | `30` | 响应缓存默认 TTL（秒），优先取响应 Cache-Control max-age |
| `Cache:MaxCacheEntries` | `10000` | 缓存条目上限（防膨胀） |
| `Security:Enabled` | `true` | 是否启用扩展名黑名单拦截 |
| `Security:BlockedExtensions` | `aspx,asp,php,jsp,asa,asax,ashx,axd,config` | 被拦截扩展名 |
| `Security:StatusCode` | `403` | 拦截返回状态码 |
| `IpFilter:Enabled` | `false` | 是否启用 IP 黑白名单 |
| `IpFilter:Allow` / `Deny` | `[]` | 白/黑名单（CIDR 或单 IP，如 `10.0.0.0/8`）；黑名单优先 |
| `IpFilter:StatusCode` | `403` | 拦截返回状态码 |
| `RequestSize:Enabled` | `true` | 是否启用请求体大小限制 |
| `RequestSize:MaxBytes` | `10485760` | 允许的最大请求体（字节，默认 10MB） |
| `RequestSize:StatusCode` | `413` | 超限返回状态码 |
| `Retry:Enabled` | `true` | 是否启用失败重试（仅幂等方法 GET/HEAD/OPTIONS/TRACE） |
| `Retry:MaxRetries` | `1` | 最多重试次数（防风暴，立即重试无等待） |
| `Retry:RetryableStatusCodes` | `[502,504]` | 可重试状态码（503 熔断/过载默认不重试） |
| `Retry:RetryOnConnectionError` | `true` | 连接失败/超时是否重试 |
| `Retry:RetryOnHttp500` | `false` | 是否重试 500 |
| `CircuitBreaker:Enabled` | `true` | 是否启用熔断 |
| `CircuitBreaker:FailureThreshold` | `5` | 连续失败阈值（后触发熔断） |
| `CircuitBreaker:OpenSeconds` | `30` | 熔断持续时间（秒），到期转半开探测 |
| `CircuitBreaker:HalfOpenMaxRequests` | `1` | 半开状态放行探测请求数 |
| `CircuitBreaker:FailureStatusCodes` | `[500,502,503,504]` | 视为失败的响应状态码 |
| `CircuitBreaker:StatusCode` | `503` | 熔断打开时返回状态码（含 Retry-After） |
| `SecurityHeaders:Enabled` | `true` | 是否启用安全响应头 |
| `SecurityHeaders:XFrameOptions` | `SAMEORIGIN` | X-Frame-Options（空=不添加） |
| `SecurityHeaders:XContentTypeOptions` | `nosniff` | X-Content-Type-Options |
| `SecurityHeaders:ReferrerPolicy` | `strict-origin-when-cross-origin` | Referrer-Policy |
| `SecurityHeaders:ContentSecurityPolicy` | `""` | CSP（空=不添加，按需配置） |
| `Metrics:Enabled` | `true` | 是否启用监控端点 |
| `Metrics:MetricsPath` | `/metrics` | Prometheus 文本指标路径 |
| `Metrics:HealthPath` | `/health` | 存活探针路径 |
| `Metrics:IncludePathLabels` | `false` | 是否按路径前缀打标（高基数，默认关） |
| `HttpTimeout:Enabled` | `true` | 是否启用上游连接超时 |
| `HttpTimeout:ConnectSeconds` | `10` | 到后端 TCP 连接超时（秒）；单请求活动超时用集群 `HttpRequest.ActivityTimeout` |
| `Admin:Enabled` | `true` | 是否启用管理端点 |
| `Admin:Path` | `/admin/status` | 运行状态 JSON 摘要路径 |
| `Admin:ReloadEnabled` | `true` | 是否启用配置热重载端点（仅 localhost） |
| `Admin:ReloadPath` | `/admin/reload` | 热重载端点路径（POST，重载 RateLimit / IpFilter） |
| `LocationRewrite:Enabled` | `true` | 是否改写重定向 Location |
| `LocationRewrite:UseRequestHostAsPublic` | `true` | 用请求自身 `scheme://host` 替换后端绝对 Location（等价 `proxy_redirect default`） |
| `LocationRewrite:Rules` | `[]` | 显式前缀替换规则（Match → Replace） |
| `ProxyLogging:Enabled` | `true` | 是否记录代理详情日志 |
| `ProxyLogging:MaxBodyBytes` | `65536` | 请求/响应体读取上限（字节），超出截断 |
| `ProxyLogging:LogRequestHeaders` | `false` | 是否记录请求头 |
| `ProxyLogging:LogRequestBody` | `true` | 是否记录请求体 |
| `ProxyLogging:LogResponseBody` | `true` | 是否记录响应体（会用 MemoryStream 接管 Response.Body，大响应按需关闭） |
| `ProxyLogging:IgnoreStatusCodes` | `[]` | 命中这些状态码的请求不写日志（如填 `304`） |
| `ProxyLogging:Json` | `false` | 代理日志是否输出单行 JSON（ELK/Loki 友好） |
| `ProxyLogging:BodyLogRules` | `[]` | 细粒度请求体日志规则：`[{PathContains:"/api/login",StatusCodes:[200]}]`；非空时仅命中（路径包含+状态码）才记录请求体 |

## 9. 发布到 IIS

架构设计见 **[系统架构（中文）](docs/Architecture.zh-CN.md)** /
**[System Architecture (English)](docs/Architecture.en.md)**；
nginx 迁移对照见 **[nginx → 网关迁移对照（中文）](docs/Nginx-to-Gateway-Migration.md)** /
**[nginx → Gateway Migration Guide (English)](docs/Nginx-to-Gateway-Migration.en.md)**；
部署步骤详见 **[IIS 安装部署指南（中文）](docs/IIS-Deployment.zh-CN.md)** /
**[IIS Deployment Guide (English)](docs/IIS-Deployment.en.md)**。

简要步骤：

```bash
# 发布（以 net8.0 为例；也可选 net6.0）
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net8.0 -o "C:\inetpub\wwwroot\gateway"
```

之后在 IIS 中创建站点指向该目录，应用池设为"**无托管代码**"（集成管道），
`web.config` 已随发布一同拷贝并配置为 inprocess 托管模型。

## 10. 构建产物说明

- 双目标框架：`net6.0` 与 `net8.0`，`dotnet build` 会分别产出两个 TFM 的程序集。
- 若仅需其中一个运行时，发布时指定 `-f` 即可（如 `-f net8.0`）。
- 日志目录：运行时在 `publish/Log`（或 `bin/Release/<tfm>/Log`）下生成 XTrace 日志。

## 11. 版本与自动发版

### 版本号（SemVer，单一来源）

版本号由 **Git tag 驱动**：日常开发版本写在根目录
[`Directory.Build.props`](Directory.Build.props) 的 `VersionPrefix`（当前 `1.0.0`）；发版时由
GitHub Actions 用 `-p:Version=<tag 版本>` 注入覆盖，本地无需额外维护，杜绝两处不同步。

- 程序集版本：`AssemblyVersion / FileVersion` = 版本号。
- 信息版本：`InformationalVersion` = 版本号 + Git commit（如 `1.0.0+6a44d70`），
  `/admin/status` 的 `version` 字段直接可见，便于线上对账。

### 发版流程（GitHub Actions 自动）

```bash
# 提交代码后，打 tag 并推送（版本号 = tag 去掉 v 前缀）
git tag v1.0.0
git push origin v1.0.0
```

推送 `v*` tag 后，[`release.yml`](.github/workflows/release.yml) 自动执行：

1. **构建门禁**：net6.0 + net8.0 双框架 `dotnet build -c Release`，0 错误；
2. **配置预检门禁**：`--test-config`（等价 `nginx -t`），失败即中止；
3. **发布打包**：publish net6.0 / net8.0（框架依赖）→
   `gateway-net8.0-v1.0.0.zip`、`gateway-net6.0-v1.0.0.zip`；
4. **创建 Release**：上传两个 zip + 自动生成 release notes（tag 间 commits）。

### 部署产物

zip 内为完整发布目录（dll / appsettings.json / web.config / wwwroot），**解压到 IIS 站点目录即
完成部署**；部署机需安装对应 ASP.NET Core Runtime / Hosting Bundle（框架依赖）。

> 手动触发（不推 tag）：GitHub Actions 页面 → Release workflow → **Run workflow**，
> 仅执行构建 + 预检门禁，用于日常验证。

## 12. 许可证与免责声明

本项目采用 **[MIT License](LICENSE)**（详见仓库根目录 `LICENSE` 文件），可自由使用、修改与分发。
生产环境请务必：

- 将 API Key / 密钥从配置中移出，改由密钥库或配置中心注入；
- 关闭 `ProxyLogging:LogResponseBody` 或限制 `MaxBodyBytes` 以避免大响应占用内存；
- 在 IIS 前置 TLS（HTTPS 绑定），并依赖 `UseForwardedHeaders` 还原真实协议。

---

如需在 Linux 以 systemd + Kestrel 自托管，或接入 Prometheus / 健康检查面板，可在 Issue 中反馈。
