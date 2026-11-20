# 竞品分析（中文）

> 数据采集时间：**2026-11-18**。星级 / 版本来自 GitHub、NuGet、项目官方文档；
> 第三方聚合站的数据仅作旁证。项目变化快，决策前请复核链接。

## 0. 结论速览（TL;DR）

**有，而且数量不少——2026 年 .NET 网关赛道明显升温。** 但一个关键事实对我们有利：

> 新竞品几乎全部聚焦 **Dashboard 动态配置、K8s / 微服务服务发现、Linux 容器部署**；
> 几乎没有一款把"**架在 IIS 后面、ANCM 托管、替代 Windows Server 上的 nginx**"当成一等目标。

这正是本项目 `DotNet.Yarp.Reverse.Proxy.Gateway` 的生态位——我们不跟它们比功能数量，
而是守住"Windows/IIS 原生、开箱即用、低依赖可审计"这条差异化的线。

最值得盯的四个对象：

| 竞品 | 为什么值得盯 |
| --- | --- |
| **Ocelot 25.x** | .NET 生态最成熟的成品网关，8.7k★，2026-10 仍在活跃发版，且已内置熔断（不再依赖 Polly） |
| **Aneiang.Yarp** | 功能最全的 YARP 增强版（Dashboard / WAF / AI 助手 / 服务发现 / 配置回滚），国产、MIT，最直接的竞品 |
| **FastGateway** | 2026 年新品，.NET 10 原生 + ACME 自动证书，增长中，代表"新玩家用 .NET 10 打差异"的路线 |
| **Stella Ops Router** | 2026 年新范式：服务反向连接网关、无需入站端口，架构思路最不一样，且有商业授权模式 |

---

## 1. 竞品全景

### 1.1 第一层：底层库 / 工具箱（我们的依赖，也是"竞品的起点"）

| 项目 | 状态 | 许可 | 说明 |
| --- | --- | --- | --- |
| **YARP 2.3.0**（微软） | 最后稳定版 2025-02-27；NuGet 下载超 4000 万 | MIT | 只提供路由 / 负载均衡 / 健康检查 / 转发；限流、熔断、CORS、认证、日志等治理能力需自建。**这正是本项目存在的理由**。github.com/dotnet/yarp |
| **YARP 3.0** | **preview 阶段**（主线 VersionPrefix 3.0.0 / preview.1），仓库仍活跃 | MIT | 尚未正式发版。值得关注：若 3.0 内置更多治理能力，会侵蚀我们的价值主张 |
| **Ocelot 25.0.2** | 2026-10-15，8.7k★，1.7k fork，月度发版对齐 .NET SDK 补丁 | MIT | .NET 8 / 9 / 10；内置 "express" QoS 熔断（无需 Polly）、WebSockets 中间件与安全选项、多 `ocelot.*.json` 配置合并、动态路由。ocelot.readthedocs.io |

### 1.2 第二层：YARP 之上的"成品网关"（最直接的竞品，也是新玩家扎堆区）

| 项目 | 诞生/活跃 | 许可 | 目标框架 | 核心卖点 | 部署倾向 |
| --- | --- | --- | --- | --- | --- |
| **Aneiang.Yarp** | v2.3.0.30 / 2026-09 | MIT | .NET 8 / 9（**无 .NET 10**） | 11 个内置插件、Dashboard、WAF（SQLi/XSS/路径穿越）、AI 助手（40 工具）、Consul/Nacos/Eureka/K8s 服务发现、配置快照回滚与审计、一行代码服务注册 | 微服务 / Dashboard 为主 |
| **FastGateway** | 2026 新项目，~173★（2026-11） | Apache-2.0 | **.NET 10 原生** | 动态路由/集群零重启、Let's Encrypt ACME 自动证书（HTTP-01/DNS-01）、SNI 多证书、Dashboard、限流黑白名单；后端 FreeSql | Docker Compose / systemd，Linux 优先 |
| **Switchboard** | v5.2.3 / 2026-10，8★（很早期） | MIT | .NET 8 / 10 | **可嵌入 .NET 应用**（NuGet 库）或独立运行；6 种负载均衡、限流、回调式认证、REST 管理 API + React Dashboard、OpenTelemetry、SQLite/MySQL/Postgres/SQLServer | 嵌入式 / Docker；无 IIS 支持说明 |
| **Carp.Gateway** | 最后提交 2025-12 | MIT | .NET 8 / 9 | K8s Ingress Controller、Consul 服务发现、IP 黑白名单、重试、gRPC/WebSocket | Kubernetes 为主，更新放缓 |
| **SKYNET Reversify** | 2026-02 | 未标明 | .NET 8 | Kestrel 80/443、SNI 多证书、JSON 配置热重载、DDoS 检测模块 | 小型自托管 |

### 1.3 第三层：架构范式创新（不同赛道，但值得知道）

- **Stella Ops Router**（2026 最新）：服务**主动外连**网关（TCP/TLS/UDP/RabbitMQ/Valkey），
  服务本身不监听入站端口、不需要负载均衡入口；端点在 C# 代码里用特性声明，带 claims 鉴权。
  许可：非生产免费，生产每安装 ≤ 500 个 Endpoint 免费，超出或第三方托管需商业 Order；
  客户端包 Apache-2.0，每个版本发布满 4 年后转 Apache-2.0。有 Windows service 部署方式。
  → 它不是传统反向代理，不会正面抢我们的 nginx 替换场景，但代表了"无入站端口"的新思路。

### 1.4 第四层：我们要替换的传统对象

- **nginx / nginx Plus**：功能强、生态大，但在 Windows 上有服务化、配置、证书、动态改动等痛点
  （详见 README §1），这是本项目的立项依据。
- **IIS ARR 3.0 + URL Rewrite**：微软 IIS 原生负载均衡/反向代理扩展，Works with IIS 7–10，
  但产品本身多年未有实质更新，配置靠 IIS 管理器，无现代 API / 治理能力。我们不是 replaces ARR，
  而是给 IIS 用户一个更现代、可编程的选择。
- **Envoy / Kong / Traefik / HAProxy**：跨平台重量级选手，主场在 Linux / 容器 / 服务网格，
  Windows Server + IIS 不是其目标场景，与本项目不构成直接竞争。

---

## 2. 能力对比矩阵

| 维度 | **本项目** | Ocelot 25 | Aneiang.Yarp | FastGateway | Switchboard | IIS ARR | nginx(Win) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 交付形态 | **可部署成品**（zip 开箱） | NuGet 库 | NuGet 库 + Dashboard | 成品 + 控制台 | 可嵌入库 + 独立服务 | IIS 扩展 | 独立 exe |
| IIS / ANCM 一等支持 | **✅ web.config + inprocess** | ❌ | ❌ | ❌ | ❌ | ✅（自身即扩展） | ⚠️ 能跑但非原生 |
| .NET 10 支持 | **✅（三目标 6/8/10）** | ✅ | ❌ | ✅ | ✅ | N/A | N/A |
| 最低 Windows Server | **2012（IIS 8.0）** | 取决于 .NET | 取决于 .NET | Linux 优先 | 取决于 .NET | 2008+ | 桌面体验差 |
| 开箱治理（限流/熔断/CORS/认证/日志） | **✅ 全自研** | 部分（熔断内置） | ✅✅（插件） | 部分 | 部分 | 少 | 需手写配置 |
| Dashboard / 可视化配置 | ❌（只读 `/admin/status`） | ❌ | ✅ | ✅ | ✅ | IIS 管理器 | ❌ |
| 配置热重载（改文件即生效） | **✅（NewLife.Core + 校验闸门，≤1s）** | ❌ | ✅ | ✅（路由） | 部分 | 需重启 | `nginx -s reload` |
| 自动证书 ACME | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ |
| WAF | 部分（IP 过滤） | ❌ | ✅ | 黑白名单 | ❌ | ❌ | ❌ |
| 服务发现 | ❌ | ✅（多 provider） | ✅（多 provider） | ❌ | ❌ | ❌ | ❌ |
| NuGet 依赖数量 | **2（YARP + NewLife.Core）** | 多 | 多 | 多 | 多 | — | — |
| `nginx -t` 等价预检 + 迁移对照文档 | **✅ 独有** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## 3. 我们的护城河

1. **IIS 原生一等公民**：`web.config` + ANCM inprocess + "无托管代码"应用池，运维无需懂 .NET，
   这是所有新竞品（含 Ocelot/Aneiang/FastGateway/Switchboard）都没做的。
2. **替换 nginx 的完整路径**：`--test-config`（等价 `nginx -t`）+ 中英双语的 nginx→网关迁移对照，
   直接降低 Windows 运维的迁移心理门槛——没有第二家提供这套。
3. **极少依赖、可审计**：全解决方案只有 2 个 NuGet 包，其余治理逻辑纯 BCL 自研。
   对政企 / 等保 / 供应链审查场景，这是实打实的加分项，也是"功能多"换不来的。
4. **老服务器覆盖**：net6/8/10 三目标 + Windows Server 2012+，能覆盖存量 Windows 服务器环境。

## 4. 短板与风险（诚实面对）

| 短板 | 影响 | 建议优先级 |
| --- | --- | --- |
| 无可视化 Dashboard | 新竞品多有 Dashboard，纯配置文件的运维体验相对朴素 | 中（可后续做轻量只读状态页；配置热重载已用 NewLife.Core 补齐，见下） |
| 无 WAF / 服务发现 / ACME 自动证书 | 在微服务或公网入口场景会被 Aneiang/FastGateway 比下去 | 中（按用户需求排期，别为堆功能破坏"两包"铁律） |
| 社区规模小 | 口碑与贡献者增长慢 | 中（把这份竞品分析公开本身也是内容营销） |
| YARP 3.0 若内置更多治理能力 | 我们的"自研治理"价值会被稀释 | 持续观察（2.3.0 仍是最新稳定版，短期无虞） |
| Ocelot 25 已自带熔断、月度发版 | 在"纯 .NET 网关"选型中它是最强默认选项 | 我们应强化 IIS 场景而非正面比功能 |

## 5. 行动建议

- **定位话术**：README 首屏继续强调"Windows Server / IIS 上替代 nginx 的成品网关"，
  不要把自己描述成"又一个 .NET API 网关"——那是 Ocelot 和 Aneiang 的战场。
- **短期**：把本分析加入文档体系（已完成）；在 README §1 的痛点论证里补一句
  "现有 .NET 网关多为需要自行集成的库或面向 K8s/容器，缺少 IIS 原生成品"。
- **中期（已完成）**：**配置热重载已落地**——基于已引用的 NewLife.Core（`JsonConfigProvider`
  + `MapTo` 原地映射 + 校验闸门），保存 `appsettings.json` 即全量 `Gateway` 配置 ≤1s 生效，
  零新依赖、不破坏"两包"铁律；详见 README §8.3。剩余中期项是只读 Dashboard / 状态页。
- **长期**：跟踪 YARP 3.0 正式版与 Stella Router 范式；若 YARP 官方开始内置治理中间件，
  重新评估本项目定位。

---

## 6. 数据来源

- YARP：github.com/dotnet/yarp（releases、活跃度）、www.nuget.org/packages/Yarp.ReverseProxy
- Ocelot：github.com/ThreeMammals/Ocelot、ocelot.readthedocs.io/en/25.0/releasenotes.html
- Aneiang.Yarp：github.com/AneiangSoft/Aneiang.Yarp、yarp.aneiang.com
- FastGateway：GitHub 仓库画像（reporank / 开源榜快照，2026-11）
- Switchboard：github.com/jchristn/Switchboard、NuGet `SwitchboardApplicationProxy`
- Stella Ops Router：stella-ops.org/docs/modules/router/guides（含 2026-11-08 对比页、许可页）
- IIS ARR：iis.net/downloads/microsoft/application-request-routing
- .NET 支持矩阵：learn.microsoft.com/dotnet/core/install/windows
