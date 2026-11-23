# DotNet.Yarp.Reverse.Proxy.Gateway

基于 .NET 6 / 8 / 10 的轻量级反向代理网关，用于在 Windows Server 上替代 nginx 的核心转发能力
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
| 全局并发上限 | 限制同时在途请求数（可排队），后端承压的最后一道保险 | `limit_conn` |
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
| 监控指标 | `/metrics`（Prometheus 文本：请求量/耗时/状态码 + 限流/并发/熔断/缓存/重试治理指标）+ `/health` 存活探针 | `stub_status` |
| 管理状态端点 | `/admin/status`（JSON 摘要）+ `POST /admin/reload`（localhost 热重载治理配置） | nginx plus API |
| 配置预检 | `--test-config` 启动前校验配置（唯一性/地址合法性/transforms 由 YARP 自身校验），失败返回非零退出码 | `nginx -t` |
| 请求链路 ID | 生成/透传 `X-Request-ID`，贯穿 proxy log、access log 与 XTrace，响应头回写 | `$request_id` / `X-Request-ID` |
| 上游超时控制 | 连接超时 + 集群 ActivityTimeout | `proxy_connect_timeout` / `proxy_read_timeout` |
| 加权负载均衡 | 按 Metadata.Weight 加权随机（可配权重） | upstream `weight` |
| 灰度发布（canary） | YARP 路由按 header 匹配分流到不同集群 | 分流/灰度配置 |
| JSON 结构化日志 | 代理日志单行 JSON（便于 ELK/Loki） | `log_format json` |
| 重定向 Location 改写 | 将后端绝对 `Location` 改写成公网入口 | `proxy_redirect` |
| 请求 / 响应详情日志 | 记录方法 / URL / IP / 耗时 / 状态码 / 请求体 / 响应体（支持按路径+状态码规则与 JSON 输出） | `access_log` + `log_subrequest` |
| 静态文件托管 | `wwwroot` 目录静态资源 | `location { root }` |
| WebSocket / gRPC 透传 | YARP 原生支持 | `proxy_http_version 1.1` + Upgrade |
| 转发头还原 | 还原真实客户端 IP / 协议 | `set_real_ip` / `X-Forwarded-*` |
| **nginx.conf 迁移转换** | `--convert-nginx` 把 nginx 配置自动转成 YARP 路由/集群（含 upstream 权重、Host、路径改写） | 配置迁移工具 |
| **nginx 兼容访问日志** | 可选输出 combined/common 标准格式、按大小轮转，复用既有 GoAccess/ELK 体系 | `access_log` / `log_format` |

## 3. 技术栈与依赖约束

**硬约束：整个方案只引用两个 NuGet 包**，其余能力全部用 .NET BCL（标准类库）或 ASP.NET Core 框架自带能力实现。

| 包 / 引用 | 作用 | 类型 |
| --- | --- | --- |
| `Yarp.ReverseProxy` 2.3.0 | 微软官方反向代理内核 | 第三方 NuGet（允许） |
| `NewLife.Core` 12.0.2026.1024 | 日志（XTrace）、配置、缓存基础设施 | 第三方 NuGet（允许，唯一额外第三方） |
| `Microsoft.AspNetCore.App` | ASP.NET Core 共享框架（HttpContext / 中间件等） | FrameworkReference（**非** NuGet） |
| 其余全部 | 限流、缓存、认证、CORS、扩展名拦截、日志等 | 纯 C# BCL 自研 |

> 说明：`Microsoft.AspNetCore.App` 是编写 ASP.NET Core 应用/中间件的**标准框架引用**，不属于第三方包，
> 不会违反"只引两个 NuGet"的约束。限流特意**没有**使用 `System.Threading.RateLimiting`
> （.NET 7+ 专属），而是自研 `SimpleRateLimiter` 令牌桶以兼容 .NET 6 / 8 / 10 三目标框架。

## 4. 环境要求

- **构建端**：安装 .NET 10 SDK（推荐，能同时编译三个 TFM）；若只需 net6.0 / net8.0 产物，
  装 .NET 6 或 .NET 8 SDK 亦可。
- **运行端**：部署机安装对应的 **ASP.NET Core Runtime 6 / 8 / 10**（或 IIS 用的 **.NET Core Hosting Bundle**）。
- **操作系统与 IIS**（依据 [.NET 官方支持矩阵](https://learn.microsoft.com/dotnet/core/install/windows)）：
  **Windows Server 2012 / 2012 R2 及以上**，也可在 Windows 10 / 11 或 Linux / macOS 以 Kestrel 自托管。
  三个 TFM 的 Windows Server / IIS 兼容范围一致，差别只在 .NET 运行时自身的官方支持期：

  | 目标框架 | 支持的 Windows Server 版本 | 对应 IIS 版本 | .NET 官方支持状态 |
  | --- | --- | --- | --- |
  | `net10.0` | 2012 / 2012 R2 / 2016 / 2019 / 2022 / 23H2 / 2025（含 Server Core；Nano Server 2019 / 2022 / 2025） | IIS 8.0 / 8.5 / 10.0 | ✅ LTS，支持至 **2028-11**，**推荐** |
  | `net8.0` | 2012 / 2012 R2 / 2016 / 2019 / 2022 / 23H2 / 2025（含 Server Core） | IIS 8.0 / 8.5 / 10.0 | ⚠️ 支持已于 **2026-11-10** 结束，仅供存量环境 |
  | `net6.0` | 2012 / 2012 R2 / 2016 / 2019 / 2022（含 Server Core） | IIS 8.0 / 8.5 / 10.0 | ❌ 支持已于 **2024-11-12** 结束，仅供存量环境 |

  - 三个 TFM 最低都是 **Windows Server 2012（IIS 8.0）**；生产环境请优先选
    `gateway-net10.0-*.zip` + **ASP.NET Core Runtime 10 / .NET Hosting Bundle 10**。
  - 最低版本 **Windows Server 2012 / 2012 R2**（IIS 8.0 / 8.5）需额外安装
    **Microsoft Visual C++ 2015-2019 Redistributable**，否则可能报
    `api-ms-win-crt-runtime-l1-1-0.dll` 缺失。
  - 提醒：Windows Server 2012 / 2012 R2 官方支持已结束（2023-10，可用 ESU 续期），
    有安全补丁诉求的生产环境建议 **2016+（IIS 10.0）**。
  - 客户端 Windows：.NET 10 官方支持 **Windows 11** 各现行版本，以及 **Windows 10 的
    LTSC（1607 / 1809 / 21H2）与 Enterprise 版**；Win10 消费版（Home / Pro 22H2）已过支持期，
    开发测试建议 Windows 11 或 Win10 LTSC / Enterprise。
- **开发本机测试（Windows 10 / 11）**：.NET 6 / 8 / 10 均可运行，三种跑法——
  1. **Kestrel 直跑（推荐）**：`dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web`
     （或解压 Release 包后 `dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll`），SDK 自带 runtime，
     Win10/11 全版本可用，默认 `http://localhost:5000`；日常开发/快速验证用这个即可。
  2. **IIS Express**：Visual Studio 按 F5 自动使用，适合调试 `web.config`（ANCM）相关行为。
  3. **本机 IIS**：控制面板启用"Internet Information Services"功能，最接近生产
     （应用池/绑定/证书）；注意 **IIS 功能仅 Win10/11 Pro / Enterprise / Education 版提供**，
     Home 版无 IIS，请用方式 1 或 2。
  > 提示：Kestrel 直跑不走 IIS，`web.config` 的 ANCM 配置不生效，但 YARP 与全部治理功能一致；
  > 上线前用 `--test-config` 预检 + 方式 3（或目标服务器 IIS）做最终验证。

## 5. 快速开始

### 方式一：从 Release 部署（推荐，无需 .NET SDK）

无需源码与编译，直接从 GitHub Releases 下载即用：

1. 打开 Releases 页：<https://github.com/cuiwenyuan/DotNet.Yarp.Reverse.Proxy.Gateway/releases>
2. 下载最新版 `gateway-net10.0-v<版本>.zip`（版本号形如 `1.1.2026.1120`，部署机已装 .NET 10
   运行时，推荐 LTS 长期支持）；若目标机是 .NET 8 环境选 `gateway-net8.0-v<版本>.zip`，
   仅有 .NET 6 则选 `gateway-net6.0-v<版本>.zip`。
3. 解压到任意目录（如 `C:\gateway`）——包内为完整发布目录（dll / appsettings.json / web.config / wwwroot）。
4. （可选）配置预检，等价 `nginx -t`：

   ```bash
   cd C:\gateway
   dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # 输出 Configuration OK
   ```

5. 启动网关（Kestrel 默认监听 http://localhost:5000）：

   ```bash
   cd C:\gateway
   dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll
   ```

6. 浏览器访问 `http://localhost:5000/` 应返回静态首页（`wwwroot/index.html`，200）。

> Release 包为**框架依赖**，部署机需安装对应的 ASP.NET Core Runtime（或 IIS 用的 Hosting Bundle）；
> 正式部署到 IIS 见 [9. 发布到 IIS]。

### 方式二：从源码编译（需要 .NET SDK）

```bash
# 1) 克隆 / 进入仓库根目录（目录名 = 仓库名，含 DotNet.Yarp.Reverse.Proxy.Gateway.slnx）
cd DotNet.Yarp.Reverse.Proxy.Gateway

# 2) 还原并构建（三目标框架，Release）
dotnet build -c Release

# 2.0) 治理指标单元测试（零 NuGet 测试程序，8 项断言，退出码 0 = 通过）
dotnet run --project tests/DotNet.Yarp.Reverse.Proxy.Gateway.Tests -c Release --no-build

# 2.1) 配置预检（可选，等价 nginx -t）：校验路由/集群唯一性与后端地址合法性
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net10.0 -o publish
dotnet ./publish/DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # 输出 Configuration OK

# 3) 启动网关（Kestrel 默认监听 http://localhost:5000）
dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web
```

浏览器访问 `http://localhost:5000/` 应返回静态首页（`wwwroot/index.html`，200）。

### 用脚本做冒烟验收（两种方式均可）

仓库内置纯 PowerShell 验收脚本（无第三方依赖），并附带两个 Python 冒烟后端。
从 Release 部署的用户，先克隆仓库（或仅下载 `scripts/` 目录）即可获得这些脚本：

```bash
# 终端 A：启动两个冒烟后端
python scripts/echo_backend.py      # 监听 5200（web-cluster）
python scripts/redir_backend.py     # 监听 5101（api-cluster 之一，用于 Location 改写验证）

# 终端 B：启动网关（方式一：在解压目录运行 dll；方式二：dotnet run ...）
dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web

# 终端 C：运行验收（先确认脚本中 $Base 指向网关地址）
pwsh scripts/verify.ps1
```

## 6. 项目结构

```
DotNet.Yarp.Reverse.Proxy.Gateway/   # 仓库根（本地工作目录名可能为 DotNet.Nginx，二者同代码）
├── DotNet.Yarp.Reverse.Proxy.Gateway.slnx   # 解决方案
├── src/
│   ├── DotNet.Yarp.Reverse.Proxy.Gateway.Core/   # 类库：自研治理组件（纯 BCL + NewLife.Core）
│   │   ├── GatewayOptions.cs             # 配置绑定（对应 appsettings.json 的 "Gateway"）
│   │   ├── GatewayExtensions.cs          # 注册与管线挂载（AddGateway / UseGatewayGovernance）
│   │   ├── SimpleRateLimiter.cs          # 自研令牌桶（rate/burst/delay，余量查询）
│   │   ├── RateLimitState.cs             # 限流共享状态 + 中间件（X-Rate-Limit-* 头、429/延迟）
│   │   ├── ConcurrencyLimitMiddleware.cs # 并发上限状态 + 中间件（Interlocked CAS，等价 limit_conn）
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
    ├── Competitive-Analysis.zh-CN.md     # 竞品分析（中文，含 2026 新竞品）
    ├── Competitive-Analysis.en.md        # Competitive Analysis (English)
    ├── IIS-Deployment.zh-CN.md           # IIS 安装部署指南（中文）
    └── IIS-Deployment.en.md              # IIS Deployment Guide（English）
```

## 7. 请求处理管线（由外到内）

```
请求 → UseForwardedHeaders（还原 X-Forwarded-*）
     → NginxAccessLogMiddleware（可选：nginx combined/common 访问日志，统计压缩后字节）
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
          → ConcurrencyLimit（全局并发上限，可排队）
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
| `NginxAccessLog:Enabled` | `false` | 是否启用 nginx 兼容访问日志（默认关，避免与 ProxyLogging 重复） |
| `NginxAccessLog:Format` | `combined` | `combined`（含 referer/user-agent）或 `common` |
| `NginxAccessLog:Path` | `logs/access.log` | 日志文件路径，相对路径基于发布目录 |
| `NginxAccessLog:MaxFileBytes` | `10485760` | 单文件轮转阈值（字节），0=不轮转；超限滚动为 `.1`/`.2` |
| `NginxAccessLog:RetainedFiles` | `5` | 保留的历史轮转文件数 |
| `NginxAccessLog:IgnoreStatusCodes` | `[]` | 命中这些状态码的请求不写访问日志 |
| `NginxAccessLog:IncludeCorrelationId` | `false` | 是否在标准字段之后追加链路 ID 末段（默认关以保持严格 nginx 格式） |
| `CorrelationId:Enabled` | `true` | 是否启用请求链路 ID |
| `CorrelationId:HeaderName` | `X-Request-ID` | 请求头 / 响应头名称 |
| `CorrelationId:EchoResponseHeader` | `true` | 是否把 ID 回写响应头供客户端追踪 |
| `CorrelationId:TrustIncoming` | `true` | 是否信任并透传客户端传入的合法 ID（非法 ID 一律重新生成） |
| `ConcurrencyLimit:Enabled` | `false` | 是否启用全局并发上限（默认关，不影响既有部署） |
| `ConcurrencyLimit:MaxConcurrent` | `100` | 允许的最大在途请求数（等价 nginx `limit_conn` 的 zone 并发上限） |
| `ConcurrencyLimit:WaitEnabled` | `false` | 超额时是否排队等待空位（平滑模式）；默认立即拒绝 |
| `ConcurrencyLimit:MaxWaitMs` | `1000` | 排队模式最长等待毫秒数，超时返回 `StatusCode` |
| `ConcurrencyLimit:StatusCode` | `503` | 并发超限时的 HTTP 状态码（对齐 nginx `limit_conn` 默认行为），并回写 `Retry-After: 1` 与 `X-Concurrency-Limit` 头 |

#### 治理指标（`/metrics`）

除基础流量指标外，`/metrics` 以 Prometheus 文本格式暴露以下治理指标（零第三方 NuGet，纯 `Interlocked` 原子计数）：

| 指标族 | 关键指标 | 含义 |
| --- | --- | --- |
| 限流 | `gateway_ratelimit_allowed_total` / `gateway_ratelimit_rejected_total` / `gateway_ratelimit_delayed_total` | 令牌桶放行、返回 429、平滑延迟后放行次数（对应 `limit_req`） |
| 并发上限 | `gateway_concurrency_acquired_total` / `gateway_concurrency_rejected_total` / `gateway_concurrency_wait_ms_total` / `gateway_concurrency_wait_timeouts_total` / `gateway_concurrency_inflight` / `gateway_concurrency_peak_inflight` / `gateway_concurrency_limit_max` | 占用槽位、拒绝、排队等待、超时以及当前/峰值/上限（对应 `limit_conn`） |
| 熔断 | `gateway_circuit_rejected_total` / `gateway_circuit_failures_total` / `gateway_circuit_successes_total` / `gateway_circuit_breaker_state{bucket="..."}` | 快速失败、下游成败计数；分桶状态 `0=Closed / 1=Open / 2=HalfOpen` |
| 缓存 | `gateway_cache_hits_total` / `gateway_cache_misses_total` / `gateway_cache_stored_total` / `gateway_cache_bypassed_total` | 命中、未命中、写入、策略绕过次数（对应 `proxy_cache`） |
| 重试 | `gateway_retry_requests_total` / `gateway_retry_attempts_total` / `gateway_retry_exhausted_total` / `gateway_retry_failed_total` | 有资格重试的请求、额外尝试次数、预算耗尽、最终失败 |
| 路径分布 | `gateway_requests_by_path{path="..."}` | 仅在 `Metrics:IncludePathLabels=true` 时输出，按路径首段聚合，注意基数 |

`/admin/status` 同步增加 `governance` 节点（限流/并发/熔断/缓存/重试计数与各熔断分桶状态），便于不接 Prometheus 时快速排障。

### 8.3 配置热重载（等价 nginx -s reload）

修改 `appsettings.json` 并保存后，**全部 `Gateway` 治理配置自动生效，无需重启、无需手动 reload**。基于已引用的 NewLife.Core 实现，不引入任何新依赖。

| 环节 | 机制 |
| --- | --- |
| 变更感知 | `JsonConfigProvider`（FileSystemWatcher 事件驱动，≤1s）+ 定时轮询兜底（~40s，覆盖原子替换 / 网络盘 / watcher 失效） |
| 原地映射 | `ConfigHelper.MapTo` 把新配置原地写入已注入的 Options 单例（含嵌套对象与数组），持有引用的中间件零改动即见新值 |
| 派生状态重建 | 限流令牌桶、IP 黑白名单 CIDR 规则集经 `Changed` 回调调 `ApplyOptions` 重建；并发上限只换配置引用、**不清零在途计数**（避免重载瞬间放宽配额）；熔断注册表同步新配置但保留当前状态 |
| 校验闸门 | 应用前先跑与 `--test-config` 相同的 `ConfigValidator`（含 YARP 路由 transforms 校验）；非法配置被拦截、保留上一份有效配置、记录 `lastError`，服务不中断 |

- **路由/集群**（`ReverseProxy`）仍由 YARP 的 `IConfiguration` reloadOnChange 热加载，行为不变。
- **手动兜底**：`POST /admin/reload`（仅 localhost）强制从磁盘重载，走同一套校验闸门；文件监听失效时可用。
- **可观测**：`GET /admin/status` 的 `hotReload` 节点返回 `reloadCount` / `lastReloadAt` / `lastAttemptAt` / `lastError`。
- **注意**：用"原子替换"方式写文件（如 `sed -i`、部分工具的"先删后写"）会破坏 watcher 句柄，此时依赖 ~40s 定时轮询兜底；用编辑器常规保存（原地写入）可 ≤1s 生效。

### 8.4 nginx 迁移套件

从 nginx 迁移时两个高频痛点——**配置改写**与**日志格式断裂**——由两个纯 BCL 工具解决，不引入任何新依赖。

**（1）nginx.conf 自动转换（`--convert-nginx`）**

把现有 `nginx.conf` 转成 YARP 的 `ReverseProxy` JSON 片段，大幅降低改写工作量：

```bash
# 输出到 stdout（查看）
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --convert-nginx C:\nginx\nginx.conf

# 输出到文件，再把其中 ReverseProxy 节点合并进 appsettings.json
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --convert-nginx C:\nginx\nginx.conf -o converted.json
```

自动映射规则：

| nginx | 转换结果 |
| --- | --- |
| `upstream` 的多个 `server` + `weight=N` | 集群 Destinations + `Metadata.Weight` + `LoadBalancingPolicy: Weighted` |
| `server_name` | 路由 `Match.Hosts` |
| `location /api/` + `proxy_pass http://pool` | 路由 `Match.Path: /api/{**catch-all}` + `ClusterId` |
| `proxy_pass http://host:port/v1/`（带 URI） | `PathRemovePrefix` + `PathPrefix` 变换 |
| 静态值的 `proxy_set_header X Y` | YARP `RequestHeader` / `Set` 变换 |
| `down` 节点 / `unix:` socket | 自动跳过并提示 |

> 不能静态等价的指令（正则 location、`$变量`、TLS 证书、`root`、`rewrite`、限流等）会写入输出 JSON 的
> `GatewayMigrationHints` 节点和 stderr，供人工复核；该节点不参与运行配置，合并时忽略即可。
> 转换后务必用 `--test-config` 校验，并按 [迁移对照文档](docs/Nginx-to-Gateway-Migration.md) 逐项确认。

**（2）nginx 兼容访问日志（`Gateway:NginxAccessLog`）**

开启后输出标准 combined 格式，可直接复用现有 GoAccess / ELK 报表，迁移前后观测口径一致：

```
127.0.0.1 - - [21/Nov/2026:21:30:01 +0800] "GET /api/test?x=1 HTTP/1.1" 200 342 "http://ref.test/" "curl/8.22.0"
```

- 挂在压缩外层，`$body_bytes_sent` 统计的是发给客户端的实际字节；升级请求（WebSocket/gRPC）记为 0。
- 按 `MaxFileBytes` 大小轮转，保留 `RetainedFiles` 份；路径默认发布目录下 `logs/access.log`。
- 与 `ProxyLogging`（详情/请求体日志）互补：前者给通用日志分析，后者给排障详情，可同时开启。
- 变更开关/格式/忽略码可随 8.3 热重载即时生效；**修改 `Path` 需重启**以重建写入器。

**（3）图形界面工具（`tools/NginxConfigConverter.UI`）**

不想敲命令行时，可用随仓库提供的 WinForms 桌面工具（独立项目，已加入解决方案）。它复用与命令行**同一份转换源码**（源码链接，零第三方 NuGet），支持：

- 文件选择 / 直接粘贴配置双模式；
- 一键复制 JSON、另存为文件；
- 迁移提示清单（逐条列出需人工复核的指令）；
- 「备份并合并到 appsettings.json」：自动备份原文件（`.bak.<时间戳>`）后替换 `ReverseProxy` 节点，其他节点保持不变。

**获取方式（推荐）**：每个 Release 都附带一个独立附件 `nginx-config-converter-gui-win-x64-v<版本>.zip`。
它是 **self-contained 单文件**程序，解压后双击 `NginxConfigConverter.UI.exe` 即可运行，**无需在机器上预装 .NET / Desktop Runtime**，也不依赖网关本体。

自行构建与运行（可选，需 Windows + .NET 10 SDK）：

```bash
dotnet build tools/NginxConfigConverter.UI -c Release
# 运行：进入 bin/Release/net10.0-windows 双击 NginxConfigConverter.UI.exe，或：
dotnet run --project tools/NginxConfigConverter.UI
```

> 该工具仅在 Windows 上可编译（WinForms），不影响网关三框架的 Linux/Windows 构建；它作为**独立附件**发布，不在网关本体 zip 内。
> 合并后请用 `--test-config` 预检并重启服务。

## 9. 发布到 IIS

架构设计见 **[系统架构（中文）](docs/Architecture.zh-CN.md)** /
**[System Architecture (English)](docs/Architecture.en.md)**；
nginx 迁移对照见 **[nginx → 网关迁移对照（中文）](docs/Nginx-to-Gateway-Migration.md)** /
**[nginx → Gateway Migration Guide (English)](docs/Nginx-to-Gateway-Migration.en.md)**；
竞品分析见 **[竞品分析（中文）](docs/Competitive-Analysis.zh-CN.md)** /
**[Competitive Analysis (English)](docs/Competitive-Analysis.en.md)**；
部署步骤详见 **[IIS 安装部署指南（中文）](docs/IIS-Deployment.zh-CN.md)** /
**[IIS Deployment Guide (English)](docs/IIS-Deployment.en.md)**。

简要步骤：

```bash
# 发布（以 net10.0 为例；也可选 net8.0 / net6.0）
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net10.0 -o "C:\inetpub\wwwroot\gateway"
```

之后在 IIS 中创建站点指向该目录，应用池设为"**无托管代码**"（集成管道），
`web.config` 已随发布一同拷贝并配置为 inprocess 托管模型。

## 10. 构建产物说明

- 三目标框架：`net6.0`、`net8.0` 与 `net10.0`，`dotnet build` 会分别产出三个 TFM 的程序集。
- 若仅需其中一个运行时，发布时指定 `-f` 即可（如 `-f net10.0`）。
- 日志目录：运行时在 `publish/Log`（或 `bin/Release/<tfm>/Log`）下生成 XTrace 日志。

## 11. 版本与自动发版

### 版本号（NewLife 规则：主.次.年.MDD）

与 [NewLife.EFBM](https://github.com/cuiwenyuan/NewLife.EFBM) 的自动发布保持一致，本项目采用
**NewLife 风格四段版本号 `主.次.年.MDD`**（月**不补零**），例如 `1.1.2026.1120` 表示 1.1 基号、
2026 年 11 月 20 日发布。

- **基号**（主.次）写在根目录 [`Directory.Build.props`](Directory.Build.props) 的 `VersionPrefix`
  （当前 `1.1`），只手工维护主版本与次版本。
- `.年.MDD` 段在发版时自动生成：tag 触发取 tag 去掉 `v`，手动触发可填或按当天日期拼出。
- 校验闸门：不符合 `主.次.年.MDD` 规则的版本号直接中止发版，不会产出 Release。
- 程序集版本：`AssemblyVersion / FileVersion` = 完整四段版本号；信息版本由 SDK 自动追加 Git
  commit（如 `1.1.2026.1120+6a44d70`），`/admin/status` 的 `version` 字段可见，便于线上对账。

### 发版流程（GitHub Actions 自动）

```bash
# 提交代码后，打 tag 并推送（版本号 = tag 去掉 v 前缀，须符合 主.次.年.MDD）
git tag v1.1.2026.1120
git push origin v1.1.2026.1120
```

推送 `v*` tag 后，[`release.yml`](.github/workflows/release.yml) 自动执行：

1. **构建门禁**：net6.0 + net8.0 + net10.0 三框架 `dotnet build -c Release`，0 错误；
2. **单测门禁**：`tests/DotNet.Yarp.Reverse.Proxy.Gateway.Tests` 纯 BCL 测试程序，覆盖治理指标/熔断/热重载状态，失败即中止；
3. **配置预检门禁**：`--test-config`（等价 `nginx -t`，以 net10.0 产物执行），失败即中止；
4. **确定版本**：从 tag 提取版本号并校验 NewLife 规则，失败即中止；
5. **发布打包**：publish net10.0 / net8.0 / net6.0（框架依赖）→
   `gateway-net10.0-v1.1.2026.1120.zip`、`gateway-net8.0-v1.1.2026.1120.zip`、
   `gateway-net6.0-v1.1.2026.1120.zip`；
6. **创建 Release**：标题 `Gateway v1.1.2026.1120`，上传三个 zip + GUI 工具 zip + 自动生成 release notes，
   并标记为最新（latest）。

### 部署产物

zip 内为完整发布目录（dll / appsettings.json / web.config / wwwroot），**解压到 IIS 站点目录即
完成部署**；部署机需安装对应 ASP.NET Core Runtime / Hosting Bundle（框架依赖）。

> 手动发版（不推 tag）：GitHub Actions 页面 → Release workflow → **Run workflow**，可填版本号
> （如 `1.1.2026.1120`）；留空则按基号 + 当天日期自动生成。手动触发同样会创建 Release 与 tag，
> 且需通过上面全部门禁。

## 12. 许可证与免责声明

本项目采用 **[MIT License](LICENSE)**（详见仓库根目录 `LICENSE` 文件），可自由使用、修改与分发。
生产环境请务必：

- 将 API Key / 密钥从配置中移出，改由密钥库或配置中心注入；
- 关闭 `ProxyLogging:LogResponseBody` 或限制 `MaxBodyBytes` 以避免大响应占用内存；
- 在 IIS 前置 TLS（HTTPS 绑定），并依赖 `UseForwardedHeaders` 还原真实协议。

---

如需在 Linux 以 systemd + Kestrel 自托管，或接入 Prometheus / 健康检查面板，可在 Issue 中反馈。
