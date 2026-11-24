# Windows Service Deployment Guide (NewLife.Agent)

Run the gateway as a **Windows service**, with no IIS dependency. Best for: machines where IIS
cannot be installed, workloads needing process-level self-healing, or environments that want the
same hosting logic on Windows, Linux and containers.

> Companion docs: [IIS Deployment Guide](IIS-Deployment.en.md) · [Windows Service Assessment](Windows-Service-NewLifeAgent-Assessment.en.md) ·
> [System Architecture](Architecture.en.md) · **中文版：[Windows 服务部署指南](Windows-Service-Deployment.zh-CN.md)**
>
> Targets `net6.0` / `net8.0` / `net10.0` (examples use `net10.0`).
>
> **Status**: S1 (working host) is complete and smoke-tested. TLS endpoints, the service-account ACL
> script and load testing from S2/S3 are still pending; those sections are marked "S2".

---

## 1. Prerequisites

- Windows Server 2012 or later.
- **Only the ASP.NET Core Runtime** is required — **no IIS, no Hosting Bundle**.
- Installing the service requires **administrator** rights.

```powershell
dotnet --list-runtimes   # expect Microsoft.AspNetCore.App 10.x
```

---

## 2. Publish

```bash
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Service -c Release -f net10.0 -o "C:\gateway"
```

The artifact is deliberately different from the IIS one:

| File | Purpose |
| --- | --- |
| `DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe` / `.dll` | service host |
| `DotNet.Yarp.Reverse.Proxy.Gateway.Hosting.dll` | shared application layer (pipeline, validation, LB policy) |
| `DotNet.Yarp.Reverse.Proxy.Gateway.Core.dll` | governance kernel |
| `NewLife.Agent.dll` | service framework (**service artifact only**) |
| `appsettings.json` | **the same source file** as the Web project (linked, never drifts) |
| `Config\Agent.config` | service metadata and health thresholds (second config system) |
| `wwwroot\` | static landing page |

> There is **no `web.config`** — this host does not go through IIS.

---

## 3. Install and uninstall

In an **administrator** PowerShell, from the publish directory:

```powershell
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -install    # install + start
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -status
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -stop
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -start
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -restart
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -uninstall  # stop + uninstall
```

Service name: **`YarpGateway`** (change `ServiceName` in `Config\Agent.config`). For multiple
instances on one machine, give each a distinct name, directory and port.

---

## 4. Three ways to run

| Mode | Command | Use |
| --- | --- | --- |
| System service | installed, then started by SCM with `-s` | production |
| Foreground | `.\...Service.exe` (**no arguments**) | troubleshooting, containers, CI smoke |
| Simulated run | `.\...Service.exe -run` | development (**needs an interactive terminal**) |

> The no-argument path is an intentional compatibility contract: `dotnet <dll>` starts the gateway.
> Without it, Agent would interpret "no arguments" as "open the interactive menu" and throw in a
> console-less environment.

---

## 5. Listening address

Standard ASP.NET Core precedence:

1. command line: `.\...Service.exe --urls "http://0.0.0.0:8080"`
2. environment variable: `ASPNETCORE_URLS=http://0.0.0.0:8080`
3. `Kestrel:Endpoints` in `appsettings.json`
4. default `http://localhost:5000`

For production under SCM, set the variable at machine scope so the service process sees it:

```powershell
[Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:80", "Machine")
```

> `http://0.0.0.0:80` and IIS port 80 are **mutually exclusive**. See
> [assessment section 4.2](Windows-Service-NewLifeAgent-Assessment.en.md).

### 5.1 HTTPS (S2)

No built-in certificate configuration yet. Interim options: terminate TLS on an upstream
load balancer (recommended, gateway listens on 80), or add `Kestrel:Endpoints:Https` /
a certificate-store lookup (`Certificate:Subject` + `Store`) yourself.

---

## 6. Configuration

Two files, two responsibilities — do not mix them:

| File | Scope | Activation |
| --- | --- | --- |
| `appsettings.json` | routes / clusters / every governance option | **hot reload on save** (≤1 s, invalid config rejected by the validation gate) |
| `Config\Agent.config` | service name, health thresholds, watchdog, panel | requires a service restart |

Hot reload behaves exactly as under IIS (same `GatewayHotReload`):

```powershell
Invoke-RestMethod http://localhost/admin/status | Select-Object -ExpandProperty hotReload
Invoke-RestMethod -Method Post http://localhost:5000/admin/reload   # manual fallback, localhost only
```

### 6.1 The built-in Agent panel is off

`EnableWebPanel=false` ships as the default because the panel uses `admin/admin` and an extra
port 5580 — a default-credentialed control plane is pure risk on a public reverse proxy. The
gateway already exposes `GET /metrics` and `GET /admin/status`. If you really need process-level
management, set `EnableWebPanel=true`, change `WebPassword` immediately, and keep `WebBind=127.0.0.1`.

### 6.2 Process-level self-healing (something IIS cannot do)

```xml
<MaxMemory>0</MaxMemory>      <!-- restart when working set exceeds N MB; 0 = unlimited -->
<MaxThread>1000</MaxThread>
<MaxHandle>10000</MaxHandle>
<AutoRestart>0</AutoRestart>  <!-- scheduled restart in minutes; 0 = off -->
<RestartTimeRange>02:00-05:00</RestartTimeRange>
<WatchDog></WatchDog>         <!-- comma-separated services to guard -->
```

Start with generous thresholds, observe for a week, then tighten.

---

## 7. Service account (S2 will ship a script)

Agent installs as **`LocalSystem`** by default — too privileged for a public gateway. Change it:

```powershell
sc.exe config YarpGateway obj= "NT AUTHORITY\NetworkService" password= ""
```

Then re-grant: **read** on the publish directory, **write** on `Log\`, and **read** on the
certificate private key (if using the certificate store).

---

## 8. Verify

```powershell
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -status

Invoke-WebRequest http://localhost/ -SkipHttpErrorCheck
Invoke-WebRequest http://localhost/health -SkipHttpErrorCheck
Invoke-WebRequest http://localhost/metrics -SkipHttpErrorCheck
Invoke-WebRequest http://localhost/admin/status -SkipHttpErrorCheck   # localhost only
Invoke-WebRequest http://localhost/api/health -SkipHttpErrorCheck     # proxy path
```

---

## 9. Logs and troubleshooting

| Location | Content |
| --- | --- |
| `Log\*.log` | XTrace runtime log, including "gateway started / stopping" lifecycle lines |
| Event Viewer → Windows Logs → Application | install/start failures |
| `GET /admin/status` | `hotReload.lastError` explains rejected config |

Order: check `Log\` for "gateway started" → check the logged `Content root path` points at the
publish directory → check `hotReload` on `/admin/status`.

> Under a service the working directory is `C:\Windows\System32`. The host pins ContentRoot to the
> application directory, so `appsettings.json` is always found. If you change the startup path,
> verify the logged `Content root path` is not `System32` — otherwise you get "service running but
> every route 404s".

---

## 10. Relationship to the IIS deployment

Same code, same `appsettings.json`, two host projects:

| Artifact | Host | Prerequisites |
| --- | --- | --- |
| `gateway-<tfm>-v<version>.zip` | `Gateway.Web` | IIS + ANCM + Hosting Bundle |
| `gateway-service-<tfm>-v<version>.zip` | `Gateway.Service` | .NET runtime only |

Pick one per machine (port 80 is exclusive); different machines may use different modes.
Functionally identical — middleware order is owned by `Gateway.Hosting`, and neither host is
allowed to add or remove middleware on its own.

---

## 11. Upgrade and rollback

```powershell
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -stop
Copy-Item -Path "<new-publish>\*" -Destination "C:\gateway" -Recurse -Force
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe -start
Invoke-WebRequest http://localhost/health -SkipHttpErrorCheck
```

Stop the service first — the `.exe`/`.dll` are locked while running. To preview config changes:

```powershell
.\DotNet.Yarp.Reverse.Proxy.Gateway.Service.exe --diff-config C:\candidate-appsettings.json
# exit 0 = identical, 1 = differences, 2 = bad arguments/JSON
```

---

## 12. Known limitations (S1)

| Item | Status | Plan |
| --- | --- | --- |
| TLS certificates | upstream LB termination, or manual `Kestrel:Endpoints` | S2 (certificate-store first) |
| Service account | defaults to `LocalSystem`, manual `sc config` | S2 install script + ACLs |
| Shutdown drain | fixed 30 s | S2 configurable |
| Load testing | not done | S3 (IIS vs Service, same config) |
| Real SCM lifecycle | commands and startup path verified; no power-loss/reboot drill | S3 |

---

> See also: [Windows Service Assessment](Windows-Service-NewLifeAgent-Assessment.en.md) ·
> [Roadmap and Backlog](Roadmap.en.md)
