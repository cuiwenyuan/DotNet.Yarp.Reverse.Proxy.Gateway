# 计划与待办（Roadmap）

> 本文是**待触发清单**，不是排期承诺。每条都有明确的触发条件——没触发就不做，避免为了"看起来在推进"而提前引入复杂度。
>
> 状态标记：`已批准`（决策已定，等触发） · `待评估`（有想法，未定） · `不做`（已否决，保留理由）

---

## 1. 已实施 S1：Windows 服务宿主（NewLife.Agent）

### 1.1 决策记录

| 项 | 内容 |
| --- | --- |
| 决策日期 | 2026-11-23 |
| 决策 | **允许引入第 3 个 NuGet 包 `NewLife.Agent`**，用于 Windows 服务部署 |
| 形态 | **独立宿主项目**，不改造现有 `Gateway.Web`；`Gateway.Core` 的依赖约束不变 |
| 实施时机 | **2026-11-24 已启动并完成 S1**（宿主可用、双宿主冒烟通过）；S2/S3 见 1.4 |
| 依据 | [Windows 服务部署技术评估](Windows-Service-NewLifeAgent-Assessment.zh-CN.md)（含 PoC 实测） |
| 部署文档 | [Windows 服务部署指南](Windows-Service-Deployment.zh-CN.md) |

### 1.2 目标形态

关键取舍：**Windows 服务宿主单独一个项目，而不是给 `Gateway.Web` 加分支**。理由是让两个宿主互不污染——IIS 部署产物里不应出现 `NewLife.Agent.dll`，服务部署产物里也不应带 `web.config`。

```
src/
├── DotNet.Yarp.Reverse.Proxy.Gateway.Core        # 治理中间件（仅 2 包：NewLife.Core）
│                                                  不依赖 YARP，保持可移植
├── DotNet.Yarp.Reverse.Proxy.Gateway.Hosting     # [已实现] 管线组装 + ConfigValidator + WeightedLoadBalancingPolicy
│                                                  依赖 Core + Yarp.ReverseProxy（包总数不变）
├── DotNet.Yarp.Reverse.Proxy.Gateway.Web         # IIS/ANCM 宿主（引 Hosting，已改为薄壳）
└── DotNet.Yarp.Reverse.Proxy.Gateway.Service     # [已实现] Windows 服务宿主
                                                   引 Hosting + NewLife.Agent（唯一的第 3 个包）
```

**为什么多一层 `Hosting`**：现在管线顺序写在 `Web/Program.cs` 里，`ConfigValidator` 和 `WeightedLoadBalancingPolicy` 也在 `Web` 下——这三样本质是"应用层"而非"宿主层"。两个宿主都要用，抽出来才能"一套管线、两种托管"，否则迟早漂移成两份不一样的中间件顺序。

代价：`Gateway.Hosting` 会依赖 YARP，`Core` 仍然不依赖——**包总数仍是 2 个**（`Yarp.ReverseProxy` + `NewLife.Core`），只是在项目间重新分布；第 3 个包只出现在 `Service` 项目里。铁律的实质（不引第三方治理库）没有被削弱。

### 1.3 触发条件（满足任意一条再做）

> **已触发（2026-11-24）**：决定提前落地宿主形态——先把"S1 可用"做出来，真实部署需求出现时直接启用，
> 不必再等一次评估。下列条件留给后续判断 S2/S3 的优先级。

1. 出现**不能安装 IIS** 的部署环境（最小化系统、受管控服务器、容器/云主机）。
2. 需要**进程级自愈**：内存/线程/句柄阈值重启、定时重启窗口、看门狗——IIS 应用池只能粗暴地"连续崩溃就停"。
3. 需要把网关**移植到 Linux systemd** 或做成容器入口（同一套 `ServiceBase` 代码可跨平台）。
4. IIS 站点与网关**抢 80 端口**的运维摩擦不可接受，决定让网关独占入口。
5. 需要 `WebSocket`/`gRPC` 但环境不允许装 IIS WebSocket 功能。

> 反过来说：如果部署环境本来就有 IIS、且只需要一个反代站点，**继续用 IIS 更省事**（TLS/证书/日志/图形化运维都是白送的）。不要为了"架构更干净"去做。

### 1.4 分阶段任务

| 阶段 | 任务 | 验收标准 |
| --- | --- | --- |
| **S1 宿主可用** ✅ | ~~①新建 `Gateway.Hosting`…②`Gateway.Web` 改为调用 `Hosting`…③新建 `Gateway.Service`…~~ **全部完成（2026-11-24）**：`Hosting` 承载管线/校验/负载策略/CLI；`Web/Program.cs` 已缩为 3 行薄壳；`Service` 含 `ServiceBase` 宿主、入口三分流、`ContentRootPath=AppContext.BaseDirectory`、面板关闭 | **已验证**：三框架 0 错误；单测 22/22；两宿主 `--test-config` 均 OK；Web 前台直跑 `/`、`/health`、`/admin/status`、`/metrics` 正常；Service 无参数直跑同样正常；`-run` 路径下 `StartWork` 接通 Kestrel（18091 监听成功）；代理链路 `/api/probe` 经 rewrite、LB、健康检查切换后返回 200 |
| **S2 生产就绪** | ①Kestrel 端点与 TLS（优先证书存储 `Subject`+`Store`，避免明文口令）②优雅停机排空窗口可配置（默认 30s，对齐 `HostOptions.ShutdownTimeout`）③服务账户改低权限 + 目录 ACL 脚本 ④安装/卸载脚本、发布产物 `gateway-service-<tfm>-v<版本>.zip` ⑤文档 `docs/Windows-Service-Deployment.{zh-CN,en}.md` + README 同步 | 在目标机完成一次"安装→代理→热重载→停止→卸载"全流程；`sc.exe failure` 恢复策略配置生效 |
| **S3 验证** | ①真实 SCM 生命周期（含断电/重启后自恢复）②同配置下 IIS vs Service 压测（小包高频 + 大文件，RPS/p50/p99/CPU/工作集）③长连接排空实测 | 压测结论写入评估报告；评估状态从"已评估"升级为"已验证" |

**进度**：S1 已完成（实际约 1 人日，含 `Hosting` 抽取与双宿主冒烟）；S2 约 2 人日（TLS、账户 ACL、可配置排空、安装脚本、文档已先行）；S3 视压测环境而定。

### 1.5 实施时必读的四个坑

来自 PoC 实测，详见[评估报告第 7 节](Windows-Service-NewLifeAgent-Assessment.zh-CN.md)：

1. 服务模式 `CurrentDirectory` 是 `C:\Windows\System32` → 必须显式 `ContentRootPath=AppContext.BaseDirectory`，否则全 404。
2. 无参数启动进入 Agent 交互菜单（无 TTY 直接抛异常）→ 入口必须三分流，保住"`dotnet xxx.dll` 即启动"的契约。
3. Agent Web 面板默认开启 + `admin/admin` + 占 5580 → 生产必须 `EnableWebPanel=false`。
4. `StartWork/StopWork` 覆写必须 `public`（写 `protected` 报 CS0507）。

### 1.6 明确不做（本次范围外）

- 不改 `Core` 的中间件逻辑、不改 YARP 配置模型、不改现有 CLI 参数语义。
- 不做服务模式下的配置界面/图形化安装器。
- 不替换 IIS 部署方式——两者并存，由部署方二选一。

---

## 2. 待评估（有想法，未定）

| 项 | 价值 | 前置条件 |
| --- | --- | --- |
| Windows 事件日志输出 | 运维习惯：错误进事件查看器，不必翻文件 | 需要额外包（`Microsoft.Extensions.Logging.EventLog`）或用 `EventLog.WriteEntry` P/Invoke；都与"少包"取向冲突，需权衡 |
| 轻量 WAF（规则集） | 补 SQL 注入/XSS/路径穿越的基础拦截 | 规则来源与误报率需先验证；纯自研成本高 |
| 只读 Dashboard 状态页 | 把 `/admin/status` + `/metrics` 变成人看得懂的页面 | 与 Agent 面板职责重叠，先想清楚"给谁看" |
| gRPC / WebSocket 专项验证 | 现在只是"透传应该没问题"，没有用例证明 | 需要后端 stub + 客户端压测脚本 |

## 3. 已完成（归档，便于回溯）

| 项 | 完成时间 | 说明 |
| --- | --- | --- |
| 入口治理中间件全集 | 2026-08 | 限流/跨域/认证/缓存/熔断/重试/IP/JWT 等 |
| 配置热重载 + 校验闸门 | 2026-11-20 | NewLife.Core watcher + 指纹去重 + 非法配置回滚 |
| 治理指标进 `/metrics` | 2026-11-23 | Prometheus 文本 + `governance` 节点 |
| 测试门禁（22 项） | 2026-11-23 | 零 NuGet 控制台测试器，进 CI |
| nginx 转换器增强（第一梯队） | 2026-11-23 | include/map/rewrite/多 server + `--diff-config` |
| 架构图（PNG + Mermaid 折叠） | 2026-11-23 | 不依赖 Mermaid 渲染器的静态图 |
| Windows 服务部署技术评估 | 2026-11-23 | 见本文第 1 节 |

---

> 配套文档：[Windows 服务部署技术评估](Windows-Service-NewLifeAgent-Assessment.zh-CN.md) · [系统架构](Architecture.zh-CN.md) · [IIS 安装部署指南](IIS-Deployment.zh-CN.md)
