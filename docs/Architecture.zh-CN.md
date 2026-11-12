# 系统架构（中文）

本文从架构视角说明 `DotNet.Yarp.Reverse.Proxy.Gateway` 的设计：部署形态、分层、请求管线、
核心组件、配置体系与容错设计。阅读对象为接手维护/二次开发的工程师。

> 配套文档：[README（中文）](../README.md) · [IIS 部署指南（中文）](IIS-Deployment.zh-CN.md) ·
> [nginx → 网关迁移对照](Nginx-to-Gateway-Migration.md) ·
> **English: [System Architecture](Architecture.en.md)**

---

## 1. 系统形态

一个**单进程、单配置文件**的反向代理网关，用 .NET 在 Windows Server 上替代 nginx 的核心
"转发 + 入口治理"能力：

- **部署形态**：默认架在 IIS 之后（ASP.NET Core Module，inprocess），也可 Kestrel 自托管。
- **转发内核**：微软官方 YARP（Yet Another Reverse Proxy）——负责路由匹配、集群选择、负载均衡、
  健康检查、连接池与协议透传（HTTP/1.1、HTTP/2、WebSocket、gRPC）。
- **治理能力**：全部由 `DotNet.Yarp.Reverse.Proxy.Gateway.Core` 类库（下文简称 Gateway.Core）中的
  自研中间件承担（限流、认证、熔断、重试、IP 过滤、缓存、安全头、日志、指标等），不依赖第三方
  网关组件。
- **配置**：单一 `appsettings.json`，内含 `ReverseProxy`（YARP 契约）与 `Gateway`（自研治理）两个
  顶级节点；支持热加载（见第 7 节）。

### 1.1 总体架构图

```
                    ┌──────────────────────────────────────────────────────┐
                    │                 Windows Server                        │
                    │                                                        │
  公网客户端 ──HTTPS──▶  IIS (ANCM v2, inprocess)                            │
                    │   │  w3wp.exe 内运行网关进程                          │
                    │   ▼                                                  │
                    │  ┌──────────────────────────────────────────────┐    │
                    │  │     DotNet.Yarp.Reverse.Proxy.Gateway.Web    │    │
                    │  │  ──────────────────────────────────────────  │    │
                    │  │   Program.cs：管线组装 / 配置预检              │    │
                    │  │   ConfigValidator（--test-config）           │    │
                    │  │   WeightedLoadBalancingPolicy（加权策略）      │    │
                    │  ├──────────────────────────────────────────────┤    │
                    │  │       Gateway.Core（自研治理组件库）            │    │
                    │  │  ┌────────────────────────────────────────┐  │    │
                    │  │  │ 中间件管线（见第 4 节，由外到内）          │  │    │
                    │  │  │  Metrics → Compression → ProxyLogging   │  │    │
                    │  │  │  → Retry → 治理组（拦截/限流/熔断/认证）   │  │    │
                    │  │  │  → ResponseCache → LocationRewrite      │  │    │
                    │  │  └────────────────────────────────────────┘  │    │
                    │  │  共享状态：RateLimitState / IpFilterState     │    │
                    │  │  （支持 POST /admin/reload 在线热重载）        │    │
                    │  └──────────────────────────────────────────────┘    │
                    │               │  YARP 转发（HTTP 客户端连接池）        │
                    │               ▼                                      │
                    │  后端集群：api-cluster（5101/5102）  web-cluster（5200） │
                    └──────────────────────────────────────────────────────┘
```

---

## 2. 分层设计

| 层 | 位置 | 职责 | 依赖 |
| --- | --- | --- | --- |
| **宿主层（Web）** | `src/DotNet.Yarp.Reverse.Proxy.Gateway.Web` | 管线组装（`Program.cs`）、配置预检（`ConfigValidator`）、YARP 依赖的宿主侧类型（`WeightedLoadBalancingPolicy`） | `Yarp.ReverseProxy`（NuGet）+ 项目引用 Core |
| **治理层（Core）** | `src/DotNet.Yarp.Reverse.Proxy.Gateway.Core` | 全部自研中间件、配置绑定（`GatewayOptions`）、共享状态与热重载器、日志封装 | `NewLife.Core`（NuGet）+ `Microsoft.AspNetCore.App`（框架引用） |
| **转发内核** | YARP（进程内） | 路由匹配、集群/目的地选择、健康检查、连接管理、协议透传 | —— |

**依赖边界铁律**：整个解决方案只允许两个 NuGet 包——`Yarp.ReverseProxy`（Web 层）与
`NewLife.Core`（Core 层）。`Microsoft.AspNetCore.App` 是框架引用（非 NuGet），提供
`HttpContext` / `RequestDelegate` 等中间件基础设施。所有治理逻辑用纯 C# / BCL 实现，
这保证：审计面小、无供应链风险、三框架（net6.0/net8.0/net10.0）可编译。

**分层原则**：Core 不引用 YARP 类型（如 `ILoadBalancingPolicy`、`ClusterState`），凡依赖 YARP
类型的能力（加权负载策略）放在 Web 宿主层，保持 Core 可独立测试与复用。

---

## 3. 请求生命周期（数据流）

一个请求从进入到返回，依次经过以下阶段：

```
① 进入 → IIS/ANCM → Kestrel
② UseForwardedHeaders：还原 X-Forwarded-For/Proto/Host（真实客户端 IP 与协议）
③ MetricsMiddleware：计数/计时（/metrics、/health 短路返回）
④ ResponseCompression：gzip（日志在内层记录原始字节）
⑤ ProxyLogging：记录请求/响应详情（可选 JSON、BodyLogRules 细粒度控制）
⑥ RetryMiddleware：幂等请求失败时重跑下游管线（换节点）
⑦ 治理组（UseGatewayGovernance，由外到内）：
   AspxBlocker(扩展名黑名单) → SecurityHeaders(安全头) → IpFilter(IP黑白名单)
   → RequestSize(体积限制) → CORS → RateLimit(限流) → CircuitBreaker(熔断)
   → ApiKeyAuth → JwtAuth → ResponseCache(缓存) → LocationRewrite(Location改写)
⑧ FileServer：wwwroot 静态文件
⑨ YARP：路由匹配 → 负载均衡（RoundRobin/Weighted/...）→ 转发后端
⑩ 响应原路返回（各层 after-callback 收尾：熔断计次、缓存回填、日志落盘）
```

**关键设计点**：

- **拦截优先**：AspxBlocker / IpFilter / RequestSize 放在治理组最外层，恶意与超限请求在
  到达 CORS/限流之前就被短路，节省计算。
- **认证在转发前**：ApiKey / JWT 在 YARP 之前完成，后端永远只收到已认证的请求；
  JWT 校验通过后把 `sub` 声明以 `X-Jwt-Sub` 头透传（后端无需重复解析）。
- **日志在压缩外层**：ProxyLogging 位于 ResponseCompression 内层，读到的是**压缩前的原始字节**，
  日志内容与后端实际返回一致；响应体用 MemoryStream 接管，落盘后还原。
- **重试在最外层治理之前**：重试失败时会把整个治理 + 转发管线**整体重跑**（等价于客户端重发），
  从而天然换到另一节点；而治理中间件（限流/熔断）本身不参与重试循环，避免重复扣配额。

---

## 4. 核心组件详解

### 4.1 转发内核（YARP）

| 能力 | 配置位置 | 说明 |
| --- | --- | --- |
| 路由匹配 | `ReverseProxy:Routes` | Path / Host / Headers 匹配（含 catch-all、header 灰度分流） |
| 集群与目的地 | `ReverseProxy:Clusters` | `Destinations` 定义后端地址；`LoadBalancingPolicy` 选策略 |
| 负载均衡 | 集群节点 | RoundRobin / PowerOfTwoChoices / **Weighted**（自研，按 `Metadata.Weight` 加权随机） |
| 健康检查 | 集群节点 | Active（周期探测 `/health`，`ConsecutiveFailures` 策略） |
| 会话保持 | 集群节点 | `SessionAffinity`（Cookie 策略），同一客户端持续落同一后端 |
| 超时 | 集群 `HttpRequest.ActivityTimeout` + `Program.cs` 连接超时 | 单请求活动超时 / TCP 连接超时 |
| 协议透传 | 内置 | WebSocket / gRPC / HTTP/2 开箱即用 |

### 4.2 治理中间件（Gateway.Core）

| 中间件 | 状态码 | 核心机制 | 热重载 |
| --- | --- | --- | --- |
| `AspxBlockerMiddleware` | 403 | 扩展名黑名单（.aspx/.php/.jsp/...） | 否（启动绑定） |
| `SecurityHeadersMiddleware` | —— | X-Frame-Options / nosniff / Referrer-Policy / CSP，已有头不覆盖 | 否 |
| `IpFilterMiddleware` | 403 | CIDR 匹配 allow/deny（IPv4/IPv6），黑名单优先 | **是**（`IpFilterState`） |
| `RequestSizeLimitMiddleware` | 413 | Content-Length 超限即拒 | 否 |
| `CorsMiddleware` | —— | 来源/方法/头白名单 + 预检 | 否 |
| `RateLimitMiddleware` | 429（或延迟） | **令牌桶**：rate（补充速率）/ burst（容量）/ delay（平滑等待），按客户端 IP 分桶；输出 `X-Rate-Limit-Limit/Remaining/Reset` | **是**（`RateLimitState`） |
| `CircuitBreakerMiddleware` | 503 | 按路径前缀分桶的熔断状态机（Closed/Open/HalfOpen），连续失败触发，半开探测自动恢复 | 否 |
| `ApiKeyAuthMiddleware` | 401 | `X-Api-Key` 头校验（恒定时间比较） | 否 |
| `JwtAuthMiddleware` | 401 | 纯 BCL 校验 HS256/RS256（exp/nbf/iss/aud），`sub` 透传 | 否 |
| `ResponseCacheMiddleware` | —— | 内存缓存 GET/HEAD 200，带 Authorization/Cookie 不缓存，优先 Cache-Control max-age | 否 |
| `LocationRewriteMiddleware` | —— | 3xx 响应的 Location 改写（请求主机替换或显式规则） | 否 |
| `RetryMiddleware` | —— | 幂等方法 + 502/504/连接错误时重跑下游（限 1 次防风暴） | 否 |
| `ProxyLoggingMiddleware` | —— | 方法/URL/IP/耗时/状态码/请求体/响应体；JSON 输出；BodyLogRules 细粒度 | 否 |
| `MetricsMiddleware` | —— | `/metrics` Prometheus 文本 + `/health` 探针（Interlocked 计数） | 否 |
| `AdminStatusMiddleware` | —— | `/admin/status` JSON 摘要 + `POST /admin/reload`（仅 localhost） | 否 |

### 4.3 共享状态与热重载

为实现 `RateLimit` / `IpFilter` 的**在线更新**，这两个中间件不直接注入启动时绑定的 options
单例，而是注入**共享状态对象**：

```
appsettings.json ──(编辑)──▶ POST /admin/reload
                                  │
                                  ▼
                     GatewayConfigReloader.Reload()
                                  │  重新绑定配置 → ApplyOptions()
                                  ▼
              RateLimitState（ConcurrentDictionary<IP, 桶>）
              IpFilterState  （CIDR 列表，原子替换）
```

- `RateLimitState`：持有每个客户端 IP 的令牌桶；`ApplyOptions` 更新速率/容量/模式，存量 IP 桶
  保留（不中断在途限流）。
- `IpFilterState`：`ApplyOptions` 原子替换 CIDR 列表，下一次请求即用新规则。
- 其余治理配置仍为启动绑定（单例），改后需回收应用池（IIS）或重启进程。

---

## 5. 配置体系

单一 `appsettings.json`，两级配置 + 三种生效机制：

```
appsettings.json
├── ReverseProxy            ← YARP 契约（路由/集群/健康检查/会话保持）
│                             生效：YARP 自动热加载（reloadOnChange）
└── Gateway                 ← 自研治理（GatewayOptions 绑定）
     ├── RateLimit / IpFilter  生效：POST /admin/reload 热重载
     └── 其余节点              生效：回收应用池 / 重启
```

**设计意图**：把"转发配置"（可能频繁调整：后端扩容、灰度、健康检查）与"治理配置"（安全策略，
变更频率低）分开；且把**最需要在线调整的限流与 IP 过滤**做成可热重载，兼顾安全与可用性。
`--test-config` 可在启动/发布前校验两级配置（ID 唯一性、地址合法性、治理绑定）。

---

## 6. 容错与可靠性设计

| 故障场景 | 防护机制 | 行为 |
| --- | --- | --- |
| 单个后端节点故障 | 主动健康检查摘除 + 被动探测 | 故障节点退出可用集合 |
| 后端 502/504 / 连接失败 | `RetryMiddleware` | 幂等请求重跑管线换节点（限 1 次） |
| 后端持续故障 | `CircuitBreaker` | 连续失败 → 打开 → **503 快速失败**（带 Retry-After），避免请求堆积；半开探测恢复 |
| 客户端洪峰 | `RateLimit`（令牌桶） | 超限 429；`delay` 模式平滑排队 |
| 恶意/超限请求 | AspxBlocker / IpFilter / RequestSize | 在转发前短路（403/413） |
| 后端响应慢 | 集群 `ActivityTimeout` + 连接超时 | 单请求超时中断，不拖垮连接池 |
| 配置写坏 | `--test-config` 预检 | 启动/发布前拦错，失败即退出 |
| 进程/部署异常 | IIS 应用池自动回收 + XTrace 日志 | 500.31/502.5 快速定位 |

**限流/熔断/重试三者关系**（避免互相干扰）：

```
限流（入口流量控制）→ 熔断（下游健康状态）→ 重试（单次请求容错）
   429/延迟              503 快速失败            502/504 换节点
```

- 熔断打开返回的 503 **不在**重试的可重试状态码中（防熔断风暴）。
- 重试在治理组外层，重跑整个管线时**不会重复扣限流令牌**（限流在重试内层）。

---

## 7. 扩展点

| 扩展点 | 机制 | 示例 |
| --- | --- | --- |
| 负载策略 | 实现 `ILoadBalancingPolicy`，注册 DI | `WeightedLoadBalancingPolicy`（Metadata.Weight） |
| 路由分流 | YARP `Match.Headers` 纯配置 | 灰度 canary（`X-Canary: 1` 头分流） |
| 动态配置源 | 实现 `IProxyConfigProvider` | 预留：Consul / k8s / 管理 API 写操作 |
| 响应缓存存储 | `SimpleMemoryCache`（可替换） | 预留：分布式缓存 |
| 日志格式 | `ProxyLogging:Json` + `BodyLogRules` | ELK/Loki 采集 |
| 指标消费 | `/metrics` Prometheus 文本 | Prometheus / Grafana |

---

## 8. 目录结构（架构视角）

```
src/
├── DotNet.Yarp.Reverse.Proxy.Gateway.Core/   # 治理层（纯 BCL + NewLife.Core）
│   ├── GatewayOptions.cs                     # 配置绑定模型（appsettings "Gateway"）
│   ├── GatewayExtensions.cs                  # DI 注册 + 管线挂载（AddGateway/UseGatewayGovernance）
│   ├── GatewayConfigReloader.cs              # 热重载器（RateLimit/IpFilter）
│   ├── RateLimitState.cs                     # 限流共享状态 + 中间件（含 SimpleRateLimiter 令牌桶）
│   ├── IpFilterState.cs                      # IP 黑白名单共享状态 + 中间件
│   ├── *Middleware.cs                        # 其余治理/观测中间件
│   ├── CircuitBreaker.cs                     # 熔断状态机（与中间件分离，便于单测）
│   ├── IpAddressUtil.cs                      # CIDR 匹配工具
│   ├── SimpleMemoryCache.cs                  # 内存缓存（扩展点）
│   └── GatewayLogger.cs                      # NewLife.Core XTrace 封装
└── DotNet.Yarp.Reverse.Proxy.Gateway.Web/    # 宿主层（YARP + 项目引用）
    ├── Program.cs                            # 管线组装 / --test-config / 连接超时
    ├── ConfigValidator.cs                    # 配置预检
    ├── WeightedLoadBalancingPolicy.cs        # 加权负载策略（依赖 YARP 类型）
    ├── appsettings.json                      # 两级配置
    └── web.config                            # IIS/ANCM 托管
```

**分层理由**：Core 不依赖 YARP 类型 → 可独立编译/测试/复用；Web 薄而专注 → 替换宿主
（Kestrel/IIS/Windows 服务）不影响治理能力。
