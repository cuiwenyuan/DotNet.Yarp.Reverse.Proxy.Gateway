# IIS 安装部署指南（中文）

本文说明如何将 `DotNet.Yarp.Reverse.Proxy.Gateway` 发布并托管到 Windows Server 的 IIS 下，
使用 **ASP.NET Core Module (ANCM)** 以 inprocess 模式运行。

> 配套文档：[README（中文）](../README.md) · [系统架构（中文）](Architecture.zh-CN.md) ·
> [nginx → 网关迁移对照](Nginx-to-Gateway-Migration.md) ·
> **English: [IIS Deployment Guide](IIS-Deployment.en.md)**

> 适用版本：.NET 6 / .NET 8 双目标框架均支持。下文以 `net8.0` 为例；如需 `net6.0`，把命令中的
> `-f net8.0` 换成 `-f net6.0`、运行库换成 ASP.NET Core Runtime 6 即可。

---

## 1. 前置条件

> **支持范围**（依据 .NET 官方支持矩阵）：**Windows Server 2012 / 2012 R2 及以上**——
> 对应 IIS 8.0 / 8.5 / 10，`net6.0` 与 `net8.0` 发布包均支持。
> - Windows Server 2012 / 2012 R2 已结束官方支持（2023-10，可用 ESU 续期），
>   生产环境建议 **2016+**；如确需部署在 2012 / 2012 R2，请额外安装
>   **Microsoft Visual C++ 2015-2019 Redistributable**（否则可能报
>   `api-ms-win-crt-runtime-l1-1-0.dll` 缺失）。

### 1.1 安装 IIS

以管理员身份打开 **PowerShell**，执行：

```powershell
# 启用 Web 服务器（IIS）核心 + 管理控制台
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -All
Enable-WindowsOptionalFeature -Online -FeatureName IIS-ManagementConsole
```

> 如需 WebSocket 透传（后端用了 WebSocket / SignalR），请在"Windows 功能 → Internet Information
> Services → 万维网服务 → 应用程序开发功能"中勾选 **WebSocket 协议**，或：
> ```powershell
> Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets
> ```

### 1.2 安装 .NET 运行时 / Hosting Bundle

部署机**必须**安装与发布目标匹配的 **ASP.NET Core Runtime**（或 Hosting Bundle，它同时含 ANCM v2）。
到微软官方下载并安装：

- .NET 8：<https://dotnet.microsoft.com/download/dotnet/8.0> → "ASP.NET Core Runtime 8.x" 或
  ".NET Core Hosting Bundle"
- .NET 6：<https://dot.net/download/dotnet/6.0> → 同上（6.x）

安装完成后，**建议重启一次服务器**，确保 ANCM 模块被正确注册到 IIS。

验证：

```powershell
# 应能看到 dotnet 运行时
dotnet --list-runtimes
# 应在 C:\Windows\System32\inetsrv\ 下存在 aspnetcorev2.dll（ANCM v2）
```

---

## 2. 发布应用

在**构建机**（已装 .NET SDK）上执行发布，把产物输出到一个目录：

```bash
# 以 net8.0 为例
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net8.0 -o "C:\inetpub\wwwroot\gateway"
```

发布完成后，`C:\inetpub\wwwroot\gateway` 目录应包含：

- `DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll`（主程序集）
- `DotNet.Yarp.Reverse.Proxy.Gateway.Core.dll`（治理组件库）
- `Yarp.ReverseProxy.dll`、`NewLife.Core.dll` 等依赖
- `appsettings.json`（可按生产环境修改）
- `web.config`（已由项目拷贝，含 ANCM / inprocess 配置）

> 若要把站点放在非 `wwwroot` 目录（如 `D:\sites\gateway`），只要把 `-o` 指向该目录并在后续 IIS
> 站点里指向它即可。

### 2.1 部署前配置预检（可选，等价 `nginx -t`）

启动网关前可先校验配置（路由/集群 ID 唯一性、后端地址合法性、治理配置能否正确绑定），
避免运行期才暴露配置错误：

```bash
# 在发布目录内执行
cd "C:\inetpub\wwwroot\gateway"
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # 输出 Configuration OK 即通过
```

> `--test-config` 只校验配置后退出，不会启动监听端口；也可以放在 CI/CD 发布流水线里作为门禁。

---

## 3. 配置 web.config（通常无需改动）

`web.config` 已随项目内置，关键内容如下（部署时一般保持默认）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll"
                  stdoutLogEnabled="false"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

要点：

- `hostingModel="inprocess"`：在 IIS 工作进程（w3wp.exe）内运行，性能最佳。
- `arguments` 指向发布后的主程序集名 `DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll`。
- `stdoutLogEnabled`：排障时临时改为 `true`，日志写入 `.\logs\stdout`（目录需有写权限）。
- 若改为 `outofprocess`，则 `processPath` 仍可为 `dotnet`，由 Kestrel 监听端口（此时需配合
  `appsettings.json` 的 `Kestrel` 端口或环境变量 `ASPNETCORE_URLS`）。

---

## 4. 在 IIS 中创建站点

### 4.1 创建应用池（无托管代码）

1. 打开 **Internet Information Services (IIS) 管理器**。
2. 左侧"应用程序池" → 右侧"添加应用程序池"。
   - 名称：`DotNetGatewayAppPool`
   - **.NET CLR 版本：无托管代码**（No Managed Code）
   - 托管管道模式：**集成**（Integrated）
3. 确定。

> 这是 ASP.NET Core 应用的标准做法：代码由 .NET 运行时执行，IIS 仅做转发，因此 CLR 版本选"无托管代码"。

### 4.2 创建站点

1. 左侧"网站" → 右侧"添加网站"。
   - 站点名称：`DotNetGateway`
   - 应用程序池：选择刚建的 `DotNetGatewayAppPool`
   - 物理路径：`C:\inetpub\wwwroot\gateway`
   - 端口：如 `80`（或任意空闲端口，例如 `8080`）
   - 主机名：可留空（绑定所有主机头），或填写公网域名如 `gw.example.com`
2. 确定。

### 4.3 目录权限

确保 IIS 应用池标识对站点目录有读 / 写（日志）权限：

```powershell
# 给应用池标识授权（示例：IIS AppPool\DotNetGatewayAppPool）
$acl = Get-Acl "C:\inetpub\wwwroot\gateway"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "IIS AppPool\DotNetGatewayAppPool", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($rule)
Set-Acl "C:\inetpub\wwwroot\gateway" $acl
```

> 若使用 `ApplicationPoolIdentity` 默认标识，也可用 `IUSR` 与 `IIS_IUSRS` 组授权。

---

## 5. 绑定 HTTPS（推荐）

生产环境应在 IIS 处终止 TLS。IIS 提供两种 SSL 证书部署方式：**站点证书绑定**（每站点单独指定证书）
与**集中式证书存储（Centralized Certificate Store, CCS）**（按主机名自动匹配证书）。两者差异如下。

应用侧无需感知差异：无论哪种方式，应用都会收到 `X-Forwarded-Proto: https`，由 `Program.cs` 中的
`UseForwardedHeaders` 还原真实协议（对应 nginx 的 `X-Forwarded-*`），应用本身不处理 TLS。

### 5.1 方式一：站点证书绑定（每站点独立证书）

适合：站点数量少、证书各自管理、或需要给特定站点指定特定证书（含通配符/单域名混用）的场景。

步骤：

1. 将证书（.pfx / .cer）导入服务器本机证书库：
   - `certlm.msc`（本地计算机 → 个人 → 证书）→ 右键"导入"，或 PowerShell：
     ```powershell
     $cert = Import-PfxCertificate -FilePath "C:\certs\gw.example.com.pfx" `
         -CertStoreLocation Cert:\LocalMachine\My -Password (ConvertTo-SecureString "密码" -AsPlainText -Force)
     ```
2. 在 IIS 管理器 → 站点"绑定" → 添加 `https`：
   - IP 地址：可填 `*`（所有）或指定 IP；端口 `443`；
   - 主机名：填公网域名（配合 **SNI**，可在同一 IP:443 上绑定多个证书）；
   - SSL 证书：下拉选择刚导入的证书。
3. 确定后证书即对该站点生效。

> 多证书同端口：勾选"需要服务器名称指示（SNI）"，即可在同一 IP:443 上为不同域名绑定不同证书，
> 与"集中式证书"的按主机名匹配效果等价，但证书仍需逐个导入与绑定。

### 5.2 方式二：集中式证书存储（Centralized Certificate Store, CCS）

适合：托管大量站点、证书频繁自动续期（如 Let's Encrypt / ACME）、希望在多个 IIS 节点/Web 场间
共享同一份证书库的场景。要求 **IIS 8.0+ / Windows Server 2012+**。

原理：证书以 `.pfx` 文件形式集中存放在一个共享目录中，**文件名 = 证书的友好名称（主机名）**，
IIS 依据请求的主机名（SNI）自动查找并加载对应 `.pfx`，无需逐站点绑定。

启用与配置：

1. 启用功能（Server Manager 或 PowerShell）：
   ```powershell
   Install-WindowsFeature Web-CentralizedSSLSupport
   # 或图形界面：添加角色和功能 → 服务器角色 → Web 服务器(IIS) → 安全 → 集中式证书
   ```
2. 准备证书存储目录（建议放共享路径，如 `\\file-srv\certs`），并设置目录权限：
   - 仅需 **读取** 权限，用户为 `IIS_IUSRS` / 各 IIS 节点计算机账户；证书私钥受存储密码保护。
3. 集中式证书配置（IIS 管理器 → 服务器级节点 → 集中式证书）：
   - 证书存储路径：`\\file-srv\certs`
   - 密码：所有 `.pfx` 共用的导入密码（推荐用强密码并妥善保管）
   - 用户名/密码：访问共享目录用的凭据（若共享需认证）
4. 将 `.pfx` 文件放入存储目录，文件名与主机名一致：
   `gw.example.com.pfx`、`api.example.com.pfx`、`*.example.com.pfx`（通配符证书）
5. 站点绑定：添加 `https` 绑定，**SSL 证书一栏留空 / 不指定具体证书**，
   由 CCS 按请求主机名自动匹配（若显式指定证书，则优先使用指定证书）。

管理要点：

- **续期 = 换文件**：ACME/Let's Encrypt 自动续期后把新 `.pfx` 覆盖同名文件即可，无需逐个改站点绑定；
  覆盖后建议执行一次"刷新"（重启 WWW 服务或 `iisreset` 使缓存失效，集中式证书默认有缓存）。
- **同一共享目录可被多个 IIS 节点共用**：证书只需维护一份，天然适配 Web 场 / NLB。
- **安全**：目录权限严格最小化（只读），密码不要放在明文文档中；建议独立账户并做审计。

### 5.3 两种方式差异对比

| 对比项 | 站点证书绑定（方式一） | 集中式证书存储 CCS（方式二） |
| --- | --- | --- |
| 适用版本 | 所有 IIS 版本 | IIS 8.0+ / Windows Server 2012+ |
| 证书存放 | 服务器本地证书库（Local Machine\My） | 共享目录中的 `.pfx` 文件（按主机名命名） |
| 绑定方式 | 每站点手动选择证书 | 站点绑定不指定证书，按 SNI 主机名自动匹配 |
| 站点数量少 | 简单直观 | 配置步骤略多，收益不明显 |
| 大量站点/频繁续期 | 逐个导入+绑定，繁琐易漏 | 换文件即续期，自动化友好（ACME） |
| 多节点 Web 场 | 每节点都要导入证书 | 共享一份证书库，天然一致 |
| 同一端口多证书 | 需逐站绑定+启用 SNI | 自动按主机名区分 |
| 管理入口 | 站点"绑定"对话框 / MMC 证书管理 | 服务器级"集中式证书"配置页 |
| 风险点 | 证书分散、续期易遗漏 | 目录权限/密码管理不当 = 私钥泄露面 |

### 5.4 选择建议与本项目的关系

- **单个/少量站点**：用方式一（站点证书绑定），步骤少、直观。
- **多站点、域名多、证书自动续期、或存在 Web 场**：用方式二（CCS），把证书"当文件管理"。
- **本项目**：网关只关心收到的 `X-Forwarded-Proto`，与证书部署方式无关；两种方式均可直接使用。

其他建议：

- 证书私钥必须可导出（含私钥），否则无法用于 IIS 绑定或导出为 `.pfx` 进 CCS。
- 测试可用 IIS 自签证书，生产务必使用受信任 CA 证书。
- 可选：HTTP → HTTPS 重定向（URL 重写模块），避免明文入口。

---

## 6. 修改生产配置

编辑发布目录下的 `appsettings.json`。配置生效分两种机制：

| 配置节点 | 生效方式 |
| --- | --- |
| `ReverseProxy`（路由/集群/健康检查/会话保持） | **YARP 自动热加载**，改完即生效，无需重启（等价 `nginx -s reload`） |
| `Gateway:RateLimit` / `Gateway:IpFilter` | **`POST /admin/reload` 热重载**：改完文件后调用一次即可，无需重启（见 6.1） |
| `Gateway` 其余节点（CORS/Auth/熔断/日志等） | 启动时绑定，需**回收应用池**生效 |

> 说明：`RateLimit` / `IpFilter` 采用共享状态（State）设计，支持运行期在线更新；其余治理配置
> 仍为启动绑定单例。二者混合使用：**先热载能热载的，剩下的再按变更计划回收应用池**。

### 6.1 热重载治理配置（RateLimit / IpFilter）

1. 编辑发布目录 `appsettings.json`，修改 `Gateway:RateLimit` 或 `Gateway:IpFilter`（例如临时封禁一个 IP）。
2. 调用管理端点（**仅限 localhost 访问**，防止远程触发）：

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost/admin/reload"
# 返回 {"success":true,"reloaded":["RateLimit","IpFilter"],...}
```

3. 验证：`GET http://localhost/admin/status` 返回体中的 `lastConfigReloadAt` 会更新为本次时间。

### 6.2 回收应用池（其余 Gateway 配置生效）

```powershell
Restart-WebAppPool -Name "DotNetGatewayAppPool"
```

---

## 7. 验证部署

### 7.1 本地探测

```powershell
# 静态首页
Invoke-WebRequest -Uri "http://localhost/" -Method GET -SkipHttpErrorCheck

# 扩展名黑名单（应 403）
Invoke-WebRequest -Uri "http://localhost/evil.aspx" -Method GET -SkipHttpErrorCheck

# 反向代理（后端可达时 200；后端未起时 502/503）
Invoke-WebRequest -Uri "http://localhost/api/health" -Method GET -SkipHttpErrorCheck

# 监控端点（应 200）
Invoke-WebRequest -Uri "http://localhost/metrics" -Method GET -SkipHttpErrorCheck   # Prometheus 文本
Invoke-WebRequest -Uri "http://localhost/health"  -Method GET -SkipHttpErrorCheck   # 存活探针

# 管理端点（应 200，JSON 摘要；仅本机可访问）
Invoke-WebRequest -Uri "http://localhost/admin/status" -Method GET -SkipHttpErrorCheck
```

### 7.2 用验收脚本

若后端服务可达，可在服务器上运行仓库的 `scripts/verify.ps1`（需先把 `Base` 改为站点地址）。

### 7.3 查看日志 / 排错

- XTrace 运行日志：`publish/Log`（或 `bin/Release/<tfm>/Log`）下的 `.log` 文件。
- 启动失败：把 `web.config` 的 `stdoutLogEnabled` 改为 `true`，访问站点后查看 `.\logs\stdout*.log`。
- 事件查看器：Windows → 应用程序，筛选来源 `IIS AspNetCore Module`（常见 500.31/500.32 多为
  运行库版本不匹配或 `web.config` 程序集名错误）。

---

## 8. 常见问题（FAQ）

| 现象 | 可能原因 | 处理 |
| --- | --- | --- |
| 500.31 / HTTP Error 500.31 | 未安装对应 ASP.NET Core Runtime / Hosting Bundle | 安装匹配的运行库并重启服务器 |
| 403.0 ×××（来自应用） | 命中扩展名黑名单 | 检查 `Security:BlockedExtensions` |
| 502.5 进程退出 | 应用启动异常（配置 / 缺依赖） | 开启 stdoutLog 看 `.\logs\stdout` |
| 端口被占用 | 站点端口与其他站点冲突 | 换端口或修改绑定 |
| 静态首页能开、代理 502 | 后端服务未启动或地址错误 | 核对 `ReverseProxy.Clusters.Destinations.Address` |
| Location 改写不生效 | 后端返回相对 Location 或 `UseRequestHostAsPublic=false` 且 `Rules` 为空 | 见 `LocationRewrite` 配置 |

---

## 9. 升级与回滚

- **升级**：重新 `dotnet publish -c Release -f net8.0 -o <同目录>`，覆盖文件后回收应用池即可。
  建议先停站点（`Stop-WebSite`）再覆盖，避免文件占用。
- **回滚**：保留上一版发布目录快照（或 Git 标签），覆盖回旧文件并回收应用池。

---

> 提示：IIS 仅负责托管与转发；限流、认证、跨域、安全拦截、Location 改写、请求日志等均由本网关在
> 应用内完成，与 nginx 的入口治理能力一一对应。
