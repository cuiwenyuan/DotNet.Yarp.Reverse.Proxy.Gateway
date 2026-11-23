# Windows 服务部署技术评估：NewLife.Agent 方案 vs IIS / ANCM

> 评估对象：把 `DotNet.Yarp.Reverse.Proxy.Gateway` 以 Windows 服务形式部署（NewLife.Agent 托管），
> 与当前 IIS + ANCM 部署方式做全面对比。
>
> 评估日期：2026-11-23 · 状态：**已批准，待触发实施** · 结论：**可行；已决定引入 `NewLife.Agent`**

> **决策更新（2026-11-23）**：已批准引入第 3 个 NuGet 包 `NewLife.Agent`，并确定采用**独立宿主项目**形态——
> 新增 `DotNet.Yarp.Reverse.Proxy.Gateway.Service`（Windows 服务宿主）与
> `DotNet.Yarp.Reverse.Proxy.Gateway.Hosting`（共享管线组装），`Gateway.Core` 的依赖约束不变。
> 因此第 3 节方案 A 中"给 `Web` 项目加包 + 入口三分流"的写法**不再采用**，改为"A'：独立宿主 + 共享 Hosting"。
> 实施时机、任务拆解与验收标准见 **[Roadmap](Roadmap.zh-CN.md)**。
> 下文第 3/5/6 节保留评估当时的原始分析（含当时的形态假设），供回溯对照；结论与坑位清单仍然有效。

---

## 0. 结论摘要

| 问题 | 结论 |
| --- | --- |
| 技术上可行吗？ | **可行**。已用 PoC 跑通：`ServiceBase.StartWork` 内启动 `WebApplication`，Kestrel 正常监听，三条路径（命令、服务、直跑）互不干扰。 |
| 代码复用程度？ | **约 96%**。`Core` 项目 30 个文件、5097 行**一行不改**；改动集中在 `Program.cs` 的宿主组装段（约 110 行机械重构）+ 新增宿主壳（约 150 行）。 |
| 实现难度？ | **中低**。宿主集成半天可通；真正的难点不在 Agent，而在**部署拓扑变了**（自己终止 TLS、自己占 80、服务账户权限、停机时连接排空）。 |
| 相比 IIS 的优势？ | 少一跳接入层、进程级健康自愈（内存/线程/句柄阈值 + 定时重启 + 看门狗）、原生安装/卸载/启停命令、无 IIS 依赖、无 `web.config`/应用池回收概念、gRPC-WebSocket 直通更可控。 |
| 相比 IIS 的劣势？ | **必须自己管 TLS 证书**（IIS 免费送 SNI/CCS/续期友好）、与 IIS 抢 80 端口（**互斥**）、默认服务账户是 `LocalSystem`（权限过高，需改）、缺图形化运维入口、缺 IIS 的失败请求跟踪/W3C 日志生态。 |
| 最大阻碍？ | **NuGet 包从 2 个变成 3 个**，打破项目「只允许 Yarp.ReverseProxy + NewLife.Core」的铁律。这是唯一的实质性决策点，其余都是工程细节。**→ 2026-11-23 已决策：允许（见顶部决策更新）** |

**一句话建议**：把 Windows 服务做成**第二种可选托管形态**（与 IIS 并存，而不是替换 IIS）。技术上没有拦路虎，包数量已获批准；实施按 [Roadmap](Roadmap.zh-CN.md) 的 S1→S3 推进，S1 完成即可交付可用版本。

---

## 1. 评估范围与方法

本次评估只回答四个问题：**能不能做、好不好做、能复用多少、比 IIS 好在哪差在哪**。不包含代码落地。

评估方法分三层，避免「纸上架构」：

1. **读包**：下载 `NewLife.Agent` 10.18.2026.1024 的 `.nupkg`，解析 `nuspec` 依赖图与 API 文档（`NewLife.Agent.xml`），拿到真实的类型、成员、配置项。
2. **实测**：在独立临时目录搭建 PoC（`Sdk=Microsoft.NET.Sdk.Web` + `NewLife.Agent`，三目标框架），把真实的 `WebApplication` 挂到 `ServiceBase` 上，逐条验证命令、端口、工作目录、退出码行为。
3. **对照现状**：以当前 `src/` 下真实代码（`Program.cs`、`Core/*`）为基准，逐文件判断「改 / 不改」，据此量化复用度。

> **验证边界**：评估机当前为普通用户（非管理员）且未安装 IIS，因此「服务安装 → SCM 启动 → 服务账户工作目录」这一段**未能实机验证**，报告中已逐条标注 `[待实机验证]`。其余结论均有实测输出支撑。

---

## 2. 事实基线（来自包本身与实测）

### 2.1 包与依赖

| 项 | 事实 | 对本项目的意义 |
| --- | --- | --- |
| 包 ID / 版本 | `NewLife.Agent` **10.18.2026.1024** | 与项目 `VersionPrefix 1.1` 的发版节奏同源（年.MDD） |
| 依赖 | **仅** `NewLife.Core` **12.0.2026.1024** | **与项目当前使用的 NewLife.Core 版本完全一致** → 升级/降级零漂移、零冲突 |
| 目标框架 | `net40;net45;net461;netstandard2.0;2.1;net6.0;net7.0;net8.0;net9.0;net10.0` | 三目标 `net6.0;net8.0;net10.0` **全覆盖**，无需条件编译 |
| 许可 | MIT | 商用无约束 |
| 传递依赖数 | 0（除 NewLife.Core） | 不引入额外供应链风险 |

实测依赖解析（PoC，`net10.0`）：

```
顶级包      NewLife.Agent  10.18.2026.1024
可传递的包   NewLife.Core   12.0.2026.1024     ← 与主项目同版本
```

**关键结论**：这是「加包摩擦最小」的一种情况——**只是多了一个 1MB 的托管程序集，不会动你已经调好的 NewLife.Core 版本**。如果把 `PackageReference` 只加在 `Web` 项目，`Core` 项目仍保持「YARP + NewLife.Core」两个包，治理内核的可移植性不受影响。

### 2.2 内置命令（与现有 CLI 是否冲突）

| Agent 命令 | 作用 | 与网关现有参数冲突 |
| --- | --- | --- |
| `-i` / `-install` | 安装 / 安装并启动 | 无 |
| `-u` / `-uninstall` | 卸载 / 停止并卸载 | 无 |
| `-reinstall` | 重装（停止→卸载→安装→启动） | 无 |
| `-start` / `-stop` / `-restart` / `-status` | 生命周期与状态 | 无 |
| `-run` | 前台模拟运行（调试） | 无 |
| `-s` | 由 SCM 调用（内部） | 无 |
| `-watch` | 立即执行一次看门狗检查 | 无 |

网关现有参数：`--test-config` / `-t`、`--diff-config`、`--convert-nginx`、`-o`、`--contentRoot`、`--configPath`。
**交集为空**——`-t`/`-o` 不在 Agent 命令表中，`-i`/`-s`/`-u` 不在网关参数表中。但要注意：**这要求入口处必须先短路 CLI 模式，再交给 `ServiceBase.Main`**，否则 `-t` 这种单字母参数有被命令工厂误吞的可能（见 7.5）。

### 2.3 默认配置（实测生成，必须知情）

首次运行会自动在程序目录下创建 `Config/` 并写入 **3 个文件**：`Agent.config`、`Core.config`、`Socket.config`。`Agent.config` 默认值中对网关最要紧的三条：

```xml
<EnableWebPanel>true</EnableWebPanel>   <!-- 默认开启 Web 管理面板 -->
<WebPort>5580</WebPort>                 <!-- 默认占 5580 端口 -->
<WebPassword>admin</WebPassword>        <!-- 默认口令 admin -->
<WebBind>127.0.0.1</WebBind>            <!-- 默认仅本机 -->
```

**这三条默认值不能原样上生产**（见 7.3）。

---

## 3. 三套候选方案

### 方案 A：`NewLife.Agent` + 手写宿主壳（推荐）

`Web` 项目加一个 `PackageReference`，新增 `GatewayService : ServiceBase`，在 `StartWork/StopWork` 里包住现有的 `WebApplication`：

```csharp
public override void StartWork(String reason)
{
    var app = GatewayHost.Build(Array.Empty<String>()); // 复用现有管线
    _app = app;
    _ = app.RunAsync();          // 不阻塞，让 Agent 的运行循环继续做健康检查
}

public override void StopWork(String reason)
{
    _app?.StopAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    _app = null;
}
```

- **新增包**：1 个
- **新增代码**：约 150 行（含配置读取、异常兜底、优雅停机）
- **能力**：安装/卸载/启停/重装/状态、健康监控（`MaxMemory`/`MaxThread`/`MaxHandle`）、定时重启（`AutoRestart` + `RestartTimeRange` 时段窗口）、看门狗（守护其他服务）、Web 面板、多实例（不同 `ServiceName`）
- **不依赖** `NewLife.Extensions.Hosting.AgentService` —— PoC 已证明 60 行封装即可达到同样效果，无需第 4 个包

### 方案 B：`NewLife.Extensions.Hosting.AgentService`（不推荐）

在 `HostBuilder` 上 `UseAgentService()`，由框架完成 `IHostLifetime` 桥接。

- **新增包**：**2 个**（`NewLife.Agent` + `NewLife.Extensions.Hosting.AgentService`）
- **新增代码**：约 80 行（比自己写略少）
- **不推荐理由**：本项目用的是 `WebApplication`（不是裸 `HostBuilder`），该扩展包的价值主要在「生命周期桥接」，而这一点方案 A 用 40 行就解决了。多一个包、多一层版本牵连，收益不划算。

### 方案 C：自研 SCM 外壳（守住两包铁律）

用 P/Invoke 直接对接 Windows 服务控制管理器：`StartServiceCtrlDispatcherW` / `RegisterServiceCtrlHandlerExW` / `SetServiceStatus`，安装卸载交给系统自带 `sc.exe`。

- **新增包**：0
- **新增代码**：约 300–400 行 + 测试（纯 BCL，`DllImport("advapi32.dll")`）
- **优点**：铁律不破，与 `Core` 的零依赖风格一致；原理透明，无第三方行为不确定性
- **缺点**：要自己处理 `SERVICE_STATUS` 状态机（`START_PENDING`/`RUNNING`/`STOP_PENDING`/`STOPPED`）、`Stop`/`Shutdown`/`PowerEvent`/`SessionChange` 控制码、超时应答（SCM 默认 30s 会判定启动失败）；无看门狗、无健康监控、无 Web 面板；**并发调试成本高于方案 A**（服务模式不能附加调试器，只能靠日志）

### 三方案横向对比

| 维度 | A（NewLife.Agent） | B（+Hosting 扩展） | C（自研 P/Invoke） |
| --- | --- | --- | --- |
| 新增 NuGet 包 | 1 | 2 | **0** |
| 新增代码量 | ~150 行 | ~80 行 | ~300–400 行 |
| 业务代码改动 | 仅宿主层 | 仅宿主层 | 仅宿主层 |
| 安装/卸载命令 | 内置 `-i/-u/-reinstall` | 同 A | 靠 `sc.exe` 手工 |
| 健康自愈 | **内置**（内存/线程/句柄/定时/看门狗） | 同 A | 需自研 |
| 进程级管理面板 | 内置（默认 127.0.0.1:5580） | 同 A | 无 |
| 跨平台 | Window/Linux/macOS 一套代码 | 同 A | 仅 Windows |
| 供应链面积 | +1 托管程序集（MIT，依赖同版本 Core） | +2 | 0 |
| 综合推荐度 | ★★★★☆（待包决策） | ★★☆☆☆ | ★★★☆☆（守铁律时选它） |

---

## 4. 与 IIS / ANCM 部署的对比

### 4.1 全景矩阵

| 维度 | IIS + ANCM（现状） | Windows 服务 + Agent（评估） | 孰优 |
| --- | --- | --- | --- |
| 接入路径 | 客户端 → http.sys（内核）→ ANCM → w3wp.exe | 客户端 → Kestrel（用户态 socket） | 服务少一跳，但失去 http.sys 内核缓存/端口共享 |
| TLS 终止 | **IIS 管**：站点绑定 / SNI / 集中式证书(CCS) / 续期友好 | **自己管**：Kestrel 配 pfx 或证书存储（`Subject`+`Store`，可避开明文口令） | **IIS 明显更省事** |
| 80/443 端口 | IIS 占用 | 服务占用 | **互斥**，二选一 |
| 进程模型 | 每应用池一个 `w3wp.exe` | 每服务一个独立进程 | 平手（隔离性等价） |
| 空闲回收 | 应用池 `idleTimeout` 默认 20 分钟（网关需显式关掉） | 无空闲回收概念，常驻 | 服务更符合「网关必须常驻」的语义 |
| 定期回收 | 应用池定时回收（会断连接） | 无强制回收；Agent 可选 `AutoRestart` + 时段窗口 | 服务更可控 |
| 崩溃自愈 | 应用池 Rapid-Fail Protection（粗暴：连续崩溃则停止） | SCM 恢复策略 + Agent 健康阈值 + 看门狗 | **服务更细** |
| 资源阈值自愈 | 无 | `MaxMemory`/`MaxThread`/`MaxHandle` 超限自动重启 | **服务独有** |
| 安装方式 | 装 IIS 角色 + 建站点/应用池 + ACL | `xxx.exe -i`（一条命令） | **服务更轻** |
| 依赖 | IIS 角色、ANCM v2、Hosting Bundle | 只需 .NET 运行时（连 IIS 都不用装） | **服务更轻** |
| 图形化运维 | IIS 管理器（成熟） | `services.msc` + Agent Web 面板（轻量） | IIS 更成熟 |
| 日志生态 | W3C 日志、失败请求跟踪、ETW | XTrace 文件日志 + 网关自身 `/metrics` | IIS 生态更全；但本项目自有访问日志/指标已覆盖 |
| 配置热重载 | ✅ 现有机制照常 | ✅ 现有机制照常（需服务账户有读权限） | 平手 |
| 升级方式 | 停站点 → 覆盖 → 启站点 | 停服务 → 覆盖 → 启服务（**dll 被占用，必须先停**） | 平手，服务对文件占用更敏感 |
| 多实例 | 多站点（共享应用池或独立） | 多服务（不同 `ServiceName` + 独立目录/端口） | 平手 |
| 服务账户 | 应用池虚拟账户（低权限，默认安全） | 默认 `LocalSystem`（**高权限，必须改**） | **IIS 默认更安全** |
| 真实客户端 IP | 经 ANCM 传递，需 `UseForwardedHeaders`（现有代码已处理） | 直连，`RemoteIpAddress` 即真实 IP，无需转发头 | **服务更简单** |
| WebSocket / gRPC | 需装 IIS WebSocket 功能；gRPC 受 http.sys 限制 | Kestrel 原生全支持（HTTP/1.1、HTTP/2、WebSocket） | **服务更顺** |
| 容器化迁移 | IIS 无法进容器 | 服务代码可直接变 Linux systemd/容器入口 | **服务更有前途** |

### 4.2 逐项说明（只讲有决策价值的）

**① TLS 是最大落差项。** IIS 模式下证书是「站点属性」，SNI、集中式证书、ACME 续期都有成熟路径；服务模式下证书是「应用配置」，必须回答三个问题：证书放哪（本地证书库 / pfx 文件 / 共享目录）、私钥口令怎么管（环境变量 / 服务账户凭据 / 证书存储免口令）、续期后怎么生效（Kestrel 需重启或做证书热替换，IIS 只需 `iisreset` 或直接换文件）。这一项在实施时应单独立项，不能混在「宿主替换」里做。

**② 80 端口互斥是部署拓扑问题，不是技术问题。** 两者都要占 80 时只能二选一。实际可落地的三种拓扑：
- (a) 网关服务独占 80/443，IIS 站点全部迁到其他端口或内网 → 网关真正「替代 nginx」；
- (b) 网关服务跑高位端口（如 8080），前面继续挂 IIS/ARR → 那不如不做（多一跳，还回到 IIS 依赖）；
- (c) 网关服务独占 80（HTTP），443 由前置负载均衡/NLB 终止 → 证书问题外移，推荐给有 F5/SLB 的环境。

**③ 默认 `LocalSystem` 必须改。** 反向代理对公网暴露，以 `LocalSystem` 运行意味着任何 RCE 直接拿系统权限。应改用 `NetworkService` 或专用低权限账户（Agent 的 `ServiceModel.User` 支持指定），并随之处理：配置目录读权限、日志目录写权限、证书私钥读权限（`certlm.msc` 里给账户授私钥权限）。

**④ 优雅停机会从「可选优化」变成「必须项」。** 当前 `StopWork` 若只给 5 秒，正在传输的大文件、SSE、WebSocket 会被硬断。IIS 下应用池回收同样有这问题，但服务模式下「`-restart` / `-stop` 天天用」，暴露面更大。实现时需把停机排空窗口做成配置项（建议 30s 起），并对齐 Kestrel `HostOptions.ShutdownTimeout`。

**⑤ 有几项 IIS 能力会真的失去。** 失败请求跟踪（FREB）、W3C 日志（虽然本项目已有 nginx 风格访问日志可替代）、IIS 的 URL 重写/请求过滤模块（本网关已有等价自研组件）、以及「一个 IIS 管很多站点」的集中视图。对单一网关场景影响不大，对「一台机器托管十几个站点」的场景影响明显。

---

## 5. 实现难度分解

### 5.1 改造清单

| # | 改造点 | 涉及文件 | 难度 | 风险 | 工作量 |
| --- | --- | --- | --- | --- | --- |
| 1 | 抽出 `GatewayHost.Build(args)`：把 `Program.cs` 第 146–256 行的管线组装搬进静态方法，`args` 传空数组 | `Program.cs` | 低 | 低（机械重构） | 0.5h |
| 2 | 新增 `GatewayService : ServiceBase`（`ServiceName`/`DisplayName` 写死在代码里 + `StartWork/StopWork` + 异常兜底） | 新文件 `GatewayService.cs` | 低 | 低 | 2h |
| 3 | 入口分流：CLI 模式（`--test-config`/`--diff-config`/`--convert-nginx`）→ 原逻辑；Agent 命令（`-i/-s/-run/...`）→ `ServiceBase.Main`；**无参数 → 直跑 Web（保持既有契约）** | `Program.cs` | 中 | **中**（见 7.2） | 2h |
| 4 | `ContentRootPath` 显式指向 `AppContext.BaseDirectory` | `Program.cs` | 低 | **高**（见 7.1） | 0.5h |
| 5 | 关闭 Agent Web 面板（`EnableWebPanel=false`）或强制改口令 + 保持回环绑定 | `Config/Agent.config` | 低 | **高**（见 7.3） | 0.5h |
| 6 | Kestrel 端点与 TLS 配置（`Kestrel:Endpoints`，支持证书存储免口令） | `appsettings.json` + 文档 | 中 | 中 | 4h |
| 7 | 优雅停机：可配置排空窗口 + `ShutdownTimeout` 对齐 | `GatewayService.cs` + `GatewayOptions` | 中 | 中 | 3h |
| 8 | 服务账户与目录 ACL（`NetworkService` + 配置/日志/证书权限） | 部署脚本 | 中 | 中 | 3h |
| 9 | 发布产物与安装脚本（`publish` → `-i` → `-start` → 冒烟） | `scripts/` + CI | 低 | 低 | 3h |
| 10 | 文档：`docs/Windows-Service-Deployment.zh-CN.md` + `.en.md`，与 IIS 文档并列 | `docs/` + README | 低 | 低 | 4h |
| 11 | 回归：确认 IIS 部署方式**未被破坏**（双宿主并存） | 测试 + 冒烟 | 中 | 中 | 3h |

**合计约 2.5 人日**（不含 TLS 证书流程与压测）。

### 5.2 难度结论

- **宿主集成本身是「低难度」**：PoC 从零到跑通约 20 分钟，含一次编译错误修正（`StartWork/StopWork` 是 `public` 不是 `protected`，容易踩）。
- **难度真正落在「部署语义」上**：TLS、端口、账户、排空四项，都是 IIS 帮你做了、现在要自己做。这部分不是写代码难，是**容易漏**。
- **没有架构级阻碍**：不需要动 `Core`、不需要动中间件管线的顺序、不需要动 YARP 配置模型。

---

## 6. 代码复用度（量化）

### 6.1 现状代码规模

| 项目 | 文件数 | 行数 | 服务化改动 |
| --- | --- | --- | --- |
| `Gateway.Core`（30 个文件：中间件、限流、熔断、缓存、重试、热重载、指标、nginx 转换、diff…） | 30 | **5097** | **0 行** |
| `Gateway.Web/ConfigValidator.cs` | 1 | 230 | **0 行** |
| `Gateway.Web/WeightedLoadBalancingPolicy.cs` | 1 | 71 | **0 行** |
| `Gateway.Web/Program.cs` — CLI 模式段（`--test-config` / `--diff-config` / `--convert-nginx`） | 1 | 约 135 | **0 行**（仅需前置分流） |
| `Gateway.Web/Program.cs` — 管线组装段 | 1 | 约 111 | **机械重构**（搬进方法，逻辑不变） |
| 新增宿主壳 | 1–2 | 约 150 | 新增 |
| **合计** | **34–35** | **约 5654** | **改动 ≈ 200 行** |

**复用率 ≈ 96.5%**（5654 行中约 200 行需新增或改写）。

### 6.2 为什么复用度这么高

服务化只改变「谁调用 `app.Run()`」，不改变「`app` 里装了什么」。下面这些**全部原样生效**，已在 PoC 中确认管线可正常构建与监听：

- 中间件全集（`ProxyLogging → Retry → AspxBlocker → IpFilter → RequestSize → CORS → RateLimit → ConcurrencyLimit → CircuitBreaker → ApiKeyAuth/JWT → Cache → LocationRewrite → FileServer → YARP`）
- 配置热重载（`GatewayHotReload`：NewLife.Core watcher + 40s 轮询兜底 + SHA-256 指纹去重 + 校验闸门）
- `/metrics`（Prometheus 文本）与 `/admin/status`（治理快照）
- `--test-config` / `--diff-config` / `--convert-nginx` 三条 CLI 通路
- 三目标框架 `net6.0;net8.0;net10.0`

### 6.3 与 IIS 方案的复用关系

不是「替换」，而是「叠加」：同一份发布产物里，`web.config` 让 IIS 能托管，`-i` 让它能装成服务。**两种部署方式可共存于同一份二进制**，由部署方二选一。这意味着一件事——**做这个改造不会让现有 IIS 用户迁移**，风险可控。

---

## 7. 实测发现（PoC 结果与坑）

PoC 环境：`net10.0`（三框架均编译通过，0 错误，仅 2 条 `net6.0` EOL 预期警告），临时目录，未改动主仓库。

### 7.1 【高危】服务模式下 `CurrentDirectory` 不是程序目录

Windows 服务的默认工作目录是 `C:\Windows\System32`。ASP.NET Core 默认以 `CurrentDirectory` 作为 ContentRoot → **`appsettings.json` 读不到 → `ReverseProxy` 节点为空 → 所有路由 404**。这与项目早前踩过的坑同源（当时是直接 `dotnet xxx.dll` 启动时 ContentRoot 取当前目录）。

修法（一行）：

```csharp
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<String>(),
    ContentRootPath = AppContext.BaseDirectory,   // 必须显式指定
});
```

`[待实机验证]` 服务模式下的实际取值需在管理员环境确认；但「显式指定 BaseDirectory」是无论哪种情况的正确做法。

### 7.2 【高危】无参数启动会进交互菜单，破坏既有契约

实测输出（`dotnet PoC.dll`，无参数）：

```
请选择操作项
 1、显示状态   -status
 2、安装服务   -i
 5、模拟运行   -run
 0. 退出
请输入操作编号：
```

且在**标准输入被重定向 / 无控制台**（CI、任务计划、`> log.txt`）时直接抛未处理异常退出：

```
System.InvalidOperationException: Cannot read keys when either application does not have a console
or when console input has been redirected.
   at NewLife.Agent.ServiceBase.ReadMenuKey()
   at NewLife.Agent.ServiceBase.ProcessMenu(String[] args)
```

现行契约是「`dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll` = 启动网关」——`--test-config` 预检、运维现场排查、冒烟脚本都依赖这种直接启动方式。若直接套 Agent，**无参数会静默变成「等人按键」**，是重大的行为破坏。

修法（PoC 已验证有效）：入口三分流。

```csharp
if (args.Length == 0 || !args[0].StartsWith('-'))
{
    GatewayHost.Run();            // 兼容路径：直接前台运行，不进菜单
    return;
}
new GatewayService().Main(args); // Agent 命令：-i/-u/-start/-stop/-status/-run/-s
```

实测兼容路径输出：`Now listening on: http://127.0.0.1:18080`，`/probe` 返回 `contentRoot` 指向程序目录，符合预期。

### 7.3 【高危】Web 管理面板默认开启、默认口令 admin、默认占 5580

实测启动日志：

```
Http 开始监听 tcp://127.0.0.1:5580
[WebPanel]Web管理面板已启动端口：5580
```

三重影响：
1. **安全**：默认 `admin/admin`，虽然首次启动会强制在回环地址改密，但「默认口令存在」本身就是审计红线；
2. **端口**：多一个 5580 监听，防火墙策略、端口扫描、安全基线都要同步；
3. **管理面重复**：网关自身已有 `/admin/status`、`/metrics`，再加一套面板会让「哪个界面说了算」变得含糊。

建议：**默认 `EnableWebPanel=false`**，作为可选能力在文档里说明；如确需开启，强制改口令 + 保持 `WebBind=127.0.0.1`。

### 7.4 【中】`-run` 需要交互式终端，不能用于自动化冒烟

`-run` 启动成功后会 `Console.ReadKey()` 等待按键退出，在重定向环境抛异常（同 7.2 的根因）。因此：**CI 冒烟不要用 `-run`**，用兼容路径（无参数直跑）+ `curl` 探测，或安装成服务后用 `-status`。

### 7.5 【低】CLI 参数可能被命令工厂误吞

Agent 的命令工厂用反射扫描并分发命令。现有 `-t`（`--test-config` 短写）、`-o`（转换输出）虽与内置命令不冲突，但**必须在进入 `ServiceBase.Main` 之前把 CLI 模式短路掉**（`Program.cs` 现有顺序恰好满足：三个 CLI 分支都在 `CreateBuilder` 之前 `return`），改造时要保持这个顺序，不能让「参数解析」先于「分流」。

### 7.6 【低】发布目录会多出 `Config/` 三个文件

`Agent.config`（服务元数据 + 健康阈值 + 面板）、`Core.config`（NewLife.Core 通用配置）、`Socket.config`（面板监听）。它们与 `appsettings.json` **是两套配置体系**，运维要知道「服务行为改 `Config/Agent.config`，网关行为改 `appsettings.json`」。为避免混乱，建议把 `ServiceName`/`DisplayName` 直接写死在代码里（Agent 首次运行会以代码值为准生成配置，实测已把 `ServiceName=YarpGatewayPoC` 写入 `Agent.config`），只在需要调健康阈值时才动这个文件。

### 7.7 【低】`-status` 输出中文在非 UTF-8 控制台乱码

实测在管道输出场景出现乱码（GBK/UTF-8 解释不一致）。纯显示问题，不影响功能；文档中提示 `chcp 65001` 或直接看日志文件。

### 7.8 【低】重写 `StartWork/StopWork` 必须 `public`

基类成员是 `public`，写成 `protected override` 编译报 `CS0507`。这一点连官方 README 的示例都没写清楚，容易卡住第一次集成。

---

## 8. 性能说明（不做无根据的承诺）

服务模式与 IIS 模式跑的是**同一份 ASP.NET Core 代码、同一套中间件、同一个 YARP**，差异只在最外层接入：

| 路径 | 结构 |
| --- | --- |
| IIS inprocess | 客户端 → http.sys（内核）→ ANCM → w3wp（同进程内 ASP.NET Core） |
| 服务模式 | 客户端 → Kestrel（用户态 socket）→ ASP.NET Core |

理论上服务模式少一次内核→用户态的转交与 IIS 模块管道遍历，**但增益幅度受请求大小、连接复用、TLS 终止位置影响很大，不能凭架构图下结论**。

因此本次评估**不给性能数字**，只给验收方法：

1. 目标机上用同一份 `appsettings.json`（同样的路由/集群/限流阈值），分别以 IIS 与服务两种方式各跑一次；
2. 压测工具建议 `bombardier`（Windows 原生，单文件）或 `wrk`（WSL）；
3. 指标取 `RPS`、`p50/p99 延迟`、`CPU%`、`工作集`，四组各跑 3 分钟、重复 3 次取中位；
4. **重点观察两个场景**：小包高频（4KB 以内，看接入层开销）、大文件传输（10MB+，看零拷贝与背压行为）。

> 这一项属于原有「边界外待办」里的「真实环境 soak / 压测」，服务化实施时应一并进行，因为接入层变了，之前的压测结论不能直接复用。

---

## 9. 风险与缓解

| # | 风险 | 级别 | 缓解 |
| --- | --- | --- | --- |
| 1 | 打破「只允许两个 NuGet 包」铁律 | **高（决策级）** | 见第 11 节；可选方案 C 完全规避 |
| 2 | 服务模式 ContentRoot 错误导致全 404 | 高 | 显式 `ContentRootPath=AppContext.BaseDirectory`（7.1）+ 启动自检日志打印生效配置路径 |
| 3 | 无参数进菜单，破坏既有运维脚本 | 高 | 入口三分流（7.2）+ 回归测试覆盖「无参数 → 监听」 |
| 4 | 默认面板 `admin/admin` + 5580 暴露 | 高 | 默认关闭面板（7.3）；开启时强制口令 + 回环 |
| 5 | 默认 `LocalSystem` 权限过高 | 高 | 安装时指定低权限账户；ACL 脚本随发布（5.1 #8） |
| 6 | 与 IIS 抢 80 端口 | 中 | 部署前拓扑决策（4.2 ②）；安装脚本检测 80 占用并明确报错 |
| 7 | TLS 证书续期后不生效 | 中 | 优先用证书存储（`Subject`+`Store`）而非 pfx；文档写明重启策略 |
| 8 | 停机硬断长连接 | 中 | 可配置排空窗口（5.1 #7），默认 30s |
| 9 | `-run` 在 CI 崩溃 | 低 | 冒烟改用兼容路径（7.4） |
| 10 | 两套配置体系（`Config/*.config` vs `appsettings.json`）混淆 | 低 | 元数据写死在代码；文档明确分工（7.6） |
| 11 | 破坏 IIS 现有部署 | 中 | 双宿主并存（6.3）+ 回归验证（5.1 #11） |

---

## 10. 建议路线

**若决定做**，建议按下面四步推进，每步都可独立交付、独立回滚：

| 阶段 | 内容 | 产出 | 阻塞项 |
| --- | --- | --- | --- |
| **S0 决策** | 确认是否允许第 3 个包（`NewLife.Agent`）；若不允许，转方案 C | 决策记录 | —— |
| **S1 宿主可用** | 5.1 的 #1–#5：抽 `GatewayHost`、新增 `GatewayService`、入口三分流、ContentRoot、关面板。**此时 `-i` 装成服务即可跑通现有全部功能** | 可安装的 Windows 服务（HTTP，高位端口） | S0 |
| **S2 生产就绪** | 5.1 的 #6–#10：TLS 端点、优雅停机、服务账户与 ACL、安装脚本、文档；冒烟覆盖「服务模式」与「IIS 模式」双跑 | 可上生产的部署方案 | S1 |
| **S3 验证** | 5.1 的 #11 + 第 8 节压测 + 真实 SCM 生命周期（安装/停止/重启/卸载/断电恢复） | 评估结论升级为「已验证」 | S2 |

**若决定暂不做**（也完全合理），建议保留本文档作为决策依据，并在 `docs/IIS-Deployment.*.md` 里加一句「如需作为 Windows 服务运行，见本评估文档」——避免半年后重新论证。

---

## 11. 待决策问题（需要你拍板）

1. ~~**是否允许第 3 个 NuGet 包？**~~ **✅ 已决策（2026-11-23）：允许**，采用独立宿主项目形态（`Service` + `Hosting` 两个新项目，`Core` 仍只有两个包）。原三选一方案作废：
   - ~~允许 → 走方案 A~~ → 改为走方案 A'（独立宿主 + 共享 Hosting），见 [Roadmap](Roadmap.zh-CN.md)
   - ~~不允许 → 走方案 C~~ → 不再需要（保留记录：若将来推翻此决策，自研 P/Invoke SCM 外壳约 4–5 人日）
   - ~~折中 → 只给 `Web` 项目加包~~ → 已由"独立宿主项目"取代

2. **部署拓扑选哪种？** 网关独占 80/443（真正替代 nginx）／网关高位端口 + 前置 LB／网关独占 80 且 TLS 外移（4.2 ②）。

3. **TLS 证书放哪？** 本地证书库（免口令，推荐）／pfx 文件 + 环境变量口令／共享目录 CCS 风格。

4. **服务账户用哪个？** `NetworkService`（够用、易配）／专用低权限域账户（更严，需运维介入）。

5. **Agent Web 面板要不要？** 建议默认关闭；若需要进程级管理界面，则强制改口令并纳管 5580 端口。

---

## 附录 A：PoC 关键代码（可复现）

```csharp
// PoC.csproj: Sdk=Microsoft.NET.Sdk.Web, TargetFrameworks=net6.0;net8.0;net10.0
// PackageReference: NewLife.Agent 10.18.2026.1024

public static class GatewayHost
{
    public static WebApplication Build(String[] args) => WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,                                // 传空数组，避免 -s/-run 被 ASP.NET Core 配置解析
        ContentRootPath = AppContext.BaseDirectory, // 服务模式下 CurrentDirectory 是 System32
    }).Build();
}

public class GatewayService : ServiceBase
{
    private WebApplication? _app;
    public GatewayService()
    {
        ServiceName = "YarpGateway";
        DisplayName = "YARP Reverse Proxy Gateway";
    }
    public override void StartWork(String reason)      // 注意：必须 public
    {
        _app = GatewayHost.Build(Array.Empty<String>());
        _ = _app.RunAsync();                            // 不阻塞，让 Agent 做健康检查
    }
    public override void StopWork(String reason)
    {
        _app?.StopAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        _app = null;
    }
}

public static class Program
{
    public static void Main(String[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith('-')) { GatewayHost.Build(Array.Empty<String>()).Run(); return; }
        new GatewayService().Main(args);
    }
}
```

## 附录 B：服务运维命令

```powershell
# 安装并启动（管理员）
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -install

# 状态 / 停止 / 启动 / 重启 / 重装 / 卸载
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -status
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -stop
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -start
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -restart
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -reinstall
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -uninstall

# 兼容路径（保持既有契约：无参数即启动网关，可用于自动化冒烟）
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll

# 系统级兜底
sc.exe qc YarpGateway
sc.exe failure YarpGateway reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

---

> 配套文档：[IIS 安装部署指南](IIS-Deployment.zh-CN.md) · [系统架构](Architecture.zh-CN.md) · [nginx → 网关迁移对照](Nginx-to-Gateway-Migration.md)
