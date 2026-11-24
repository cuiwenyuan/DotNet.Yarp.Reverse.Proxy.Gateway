# Windows 服务部署指南（NewLife.Agent）

把网关作为 **Windows 服务**运行，不依赖 IIS。适合：目标机不能装 IIS、需要进程级自愈、
需要与 Linux/容器共用同一套宿主逻辑的场景。

> 配套文档：[IIS 安装部署指南](IIS-Deployment.zh-CN.md) · [Windows 服务技术评估](Windows-Service-NewLifeAgent-Assessment.zh-CN.md) ·
> [系统架构](Architecture.zh-CN.md) · **English: [Windows Service Deployment](Windows-Service-Deployment.en.md)**
>
> 适用版本：`net6.0` / `net8.0` / `net10.0`（下文以 `net10.0` 为例）。
>
> **实施状态**：S1（宿主可用）已完成并通过冒烟；S2 中的 TLS 端点、服务账户 ACL 脚本、
> 压测仍待实施，相关章节已标注「待 S2」。

---

## 1. 前置条件

- Windows Server 2012 及以上（与 IIS 方案相同）。
- **只需 ASP.NET Core Runtime**（.NET 10 / 8 / 6），**不需要 IIS、不需要 Hosting Bundle**。
- 安装服务需要**管理员权限**。

```powershell
dotnet --list-runtimes   # 应能看到 Microsoft.AspNetCore.App 10.x
```

---

## 2. 发布

```bash
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Service -c Release -f net10.0 -o "C:\gateway"
```

产物特征（与 IIS 产物刻意区分）：

| 文件 | 说明 |
| --- | --- |
| `DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe` / `.dll` | 服务宿主 |
| `DotNet.Yarp.Reverse.Proxy.Gateway.Hosting.dll` | 共享的应用层（管线、校验、负载策略） |
| `DotNet.Yarp.Reverse.Proxy.Gateway.Core.dll` | 治理内核 |
| `NewLife.Agent.dll` | 服务框架（**只有服务产物带这个**） |
| `appsettings.json` | 与 Web 项目**同一份源文件**（链接生成，不会各自漂移） |
| `Config\Agent.config` | 服务元数据与健康阈值（第二套配置体系） |
| `wwwroot\` | 静态首页 |

> 产物里**没有 `web.config`** —— 这个宿主不经 IIS，不需要它。

---

## 3. 安装与卸载

以**管理员**身份打开 PowerShell，进入发布目录：

```powershell
# 安装并启动（等价 sc create + sc start）
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -install

# 查看状态
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -status

# 停止 / 启动 / 重启
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -stop
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -start
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -restart

# 停止并卸载
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -uninstall
```

服务名：**`YarpGateway`**（在 `Config\Agent.config` 的 `ServiceName` 中修改；同一台机器多实例时
必须改成不同名字，并各自使用独立目录与端口）。

---

## 4. 三种运行方式

| 方式 | 命令 | 用途 |
| --- | --- | --- |
| 系统服务 | 安装后由 SCM 以 `-s` 拉起 | 生产 |
| 前台直跑 | `.\...Service.exe`（**无参数**） | 排查、容器、CI 冒烟 |
| 模拟运行 | `.\...Service.exe -run` | 开发调试（**需交互式终端**） |

> **无参数直跑**是刻意保留的兼容契约：`dotnet <dll>` 就等于启动网关。
> 若不加这层兼容，Agent 会把无参数解释成「进交互菜单」，在无控制台环境直接抛异常。

---

## 5. 监听端口与地址

当前版本沿用 ASP.NET Core 的标准配置方式，按优先级：

1. 命令行：`.\...Service.exe --urls "http://0.0.0.0:8080"`
2. 环境变量：`ASPNETCORE_URLS=http://0.0.0.0:8080`
3. `appsettings.json` 的 `Kestrel:Endpoints` 节点
4. 默认 `http://localhost:5000`

生产建议：

```powershell
# 以服务方式运行时，环境变量要在「系统」级别设置，否则 SCM 拉起的进程读不到
[Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:80", "Machine")
```

> `http://0.0.0.0:80` 与 IIS 的 80 端口**互斥**：两者只能有一个占用。
> 详见[技术评估第 4.2 节](Windows-Service-NewLifeAgent-Assessment.zh-CN.md)。

### 5.1 HTTPS（待 S2）

当前版本未内置证书配置。两种过渡方案：

- 前置负载均衡/NLB 终止 TLS（推荐，网关只听 80）；
- 在 `appsettings.json` 增加 `Kestrel:Endpoints:Https`，或改用证书存储
  （`Certificate:Subject` + `Store`，避免在配置里写明文口令）。

> S2 会把「证书存储优先、pfx 兜底」做成标准配置项并补文档。

---

## 6. 配置

两套配置，职责不同，**不要混用**：

| 文件 | 管什么 | 生效方式 |
| --- | --- | --- |
| `appsettings.json` | 路由 / 集群 / 全部治理参数（限流、IP、CORS、熔断、认证、日志…） | **保存即热重载**（≤1s，校验闸门拦截非法配置） |
| `Config\Agent.config` | 服务名、显示名、健康阈值、看门狗、面板开关 | 改后**重启服务** |

热重载行为与 IIS 方案完全一致（同一个 `GatewayHotReload`）：

```powershell
# 验证热重载已生效
Invoke-RestMethod http://localhost/admin/status | Select-Object -ExpandProperty hotReload
# 手动兜底（仅 localhost）
Invoke-RestMethod -Method Post http://localhost:5000/admin/reload
```

### 6.1 关于 Agent 自带的管理面板

默认**已关闭**（`Config\Agent.config` 的 `EnableWebPanel=false`）。

原因：该面板默认口令 `admin/admin`、额外监听 5580。对公网反向代理来说，多一个默认口令的
管理面是纯风险。网关自身已有两个只读端点，日常够用：

- `GET /metrics` —— Prometheus 文本，接监控
- `GET /admin/status` —— 运行状态 JSON（版本、启动时间、内存、热重载、治理快照）

确实需要进程级管理面板时，改 `EnableWebPanel=true` + 立即修改 `WebPassword` + 保持 `WebBind=127.0.0.1`。

### 6.2 进程级自愈（IIS 没有的能力）

`Config\Agent.config`：

```xml
<MaxMemory>0</MaxMemory>      <!-- 工作集超过 N MB 自动重启；0=不限 -->
<MaxThread>1000</MaxThread>   <!-- 线程数超限重启 -->
<MaxHandle>10000</MaxHandle>  <!-- 句柄数超限重启（Windows） -->
<AutoRestart>0</AutoRestart>  <!-- 定时重启间隔（分钟），0=关闭 -->
<RestartTimeRange>02:00-05:00</RestartTimeRange>  <!-- 只允许在该时段重启 -->
<WatchDog></WatchDog>         <!-- 守护其它服务，逗号分隔 -->
```

建议先给宽松阈值，观察一周再收紧；`AutoRestart` 配合低峰时段使用。

---

## 7. 服务账户（待 S2 提供脚本）

Agent 默认以 **`LocalSystem`** 安装。对公网网关权限过高，**生产必须改**：

```powershell
# 改成内置低权限账户（示例：NetworkService）
sc.exe config YarpGateway obj= "NT AUTHORITY\NetworkService" password= ""
```

改账户后要重新授权：

- 发布目录：**读取**（配置、静态文件、程序集）
- 日志目录（`Log\`）：**写入**
- 证书私钥（若用证书存储）：`certlm.msc` → 证书 → 所有任务 → 管理私钥 → 添加该账户「读取」

> S2 会提供 `scripts/install-service.ps1` 把这些一次做完。

---

## 8. 验证部署

```powershell
# 状态
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -status

# 端点
Invoke-WebRequest http://localhost/ -SkipHttpErrorCheck            # 静态首页
Invoke-WebRequest http://localhost/health -SkipHttpErrorCheck      # ok
Invoke-WebRequest http://localhost/metrics -SkipHttpErrorCheck     # Prometheus 文本
Invoke-WebRequest http://localhost/admin/status -SkipHttpErrorCheck # 状态 JSON（仅本机）

# 代理（后端可达时 200；后端未起时 502/503）
Invoke-WebRequest http://localhost/api/health -SkipHttpErrorCheck
```

---

## 9. 日志与排错

| 位置 | 内容 |
| --- | --- |
| `Log\*.log`（发布目录） | XTrace 运行日志（含「网关已启动 / 正在停止网关」等服务生命周期记录） |
| 事件查看器 → Windows 日志 → 应用程序 | 服务安装/启动失败（来源含 `YarpGateway`） |
| `GET /admin/status` | `hotReload.lastError` 看配置被闸门拒绝的原因 |

**排错顺序**：先看 `Log\` 里有没有「网关已启动」→ 再看 `Content root path` 是否指向发布目录
→ 最后看 `/admin/status` 的 `hotReload` 与降级字段。

> 服务模式下当前工作目录是 `C:\Windows\System32`。宿主已显式把 ContentRoot 指向程序目录，
> 因此 `appsettings.json` 一定能被读到；若你自行改了启动方式，务必确认日志里的
> `Content root path` 不是 `System32`——否则会出现「服务在跑、所有路由 404」的诡异现象。

---

## 10. 与 IIS 部署的关系

两种部署方式**共用同一份代码、同一份 appsettings.json**，只是两个宿主项目：

| 产物 | 宿主 | 依赖 |
| --- | --- | --- |
| `gateway-<tfm>-v<版本>.zip` | `Gateway.Web` | IIS + ANCM + Hosting Bundle |
| `gateway-service-<tfm>-v<版本>.zip` | `Gateway.Service` | 只需 .NET 运行时 |

同一台机器上**二选一**（80 端口互斥）；不同机器可以各用各的。
功能上完全对等：路由、治理、热重载、`/metrics`、`/admin/status` 全部一致——
中间件顺序由 `Gateway.Hosting` 统一编排，两个宿主不允许各自增删。

---

## 11. 升级与回滚

```powershell
# 1) 停服务（必须先停：exe/dll 被进程占用时无法覆盖）
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -stop

# 2) 覆盖发布文件（保留 Config\Agent.config 与 appsettings.json 的生产值）
Copy-Item -Path "<new-publish>\*" -Destination "C:\gateway" -Recurse -Force

# 3) 启动并验证
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -start
Invoke-WebRequest http://localhost/health -SkipHttpErrorCheck
```

回滚：保留上一版目录快照，覆盖回去再启动。配置变更可用 `--diff-config` 先比对：

```powershell
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe --diff-config C:\candidate-appsettings.json
# 退出码 0=等价、1=有差异、2=参数/解析错误
```

---

## 12. 已知限制（S1 现状）

| 项 | 现状 | 计划 |
| --- | --- | --- |
| TLS 证书 | 需前置 LB 终止，或手工配 `Kestrel:Endpoints` | S2 标准化（证书存储优先） |
| 服务账户 | 默认 `LocalSystem`，需手工 `sc config` 改 | S2 提供安装脚本 + ACL |
| 停机排空 | 固定 30 秒 | S2 改为可配置项 |
| 压测 | 未做 | S3（IIS vs Service 同配置对比） |
| 真实 SCM 生命周期 | 已在开发机验证命令与启动路径，未做断电/重启恢复演练 | S3 |

---

> 相关：[Windows 服务技术评估](Windows-Service-NewLifeAgent-Assessment.zh-CN.md)（为什么这样设计、与 IIS 的逐项对比） ·
> [计划与待办](Roadmap.zh-CN.md)（S2/S3 任务清单）
