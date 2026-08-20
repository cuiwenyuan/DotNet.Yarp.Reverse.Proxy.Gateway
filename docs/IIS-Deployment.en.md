# IIS Deployment Guide (English)

This guide explains how to publish and host `DotNet.Yarp.Reverse.Proxy.Gateway` under IIS on Windows
Server, using the **ASP.NET Core Module (ANCM)** in **inprocess** mode.

> Companion docs: [README](../README.en.md) · [System Architecture](Architecture.en.md) ·
> [nginx → Gateway Migration Guide](Nginx-to-Gateway-Migration.en.md) ·
> **中文版： [IIS 安装部署指南](IIS-Deployment.zh-CN.md)**

> Both .NET 6 and .NET 8 dual targets are supported. The steps below use `net8.0`; for `net6.0`,
> replace `-f net8.0` with `-f net6.0` and install the ASP.NET Core Runtime 6 instead.

---

## 1. Prerequisites

### 1.1 Install IIS

Open **PowerShell as Administrator** and run:

```powershell
# Enable Web Server (IIS) core + management console
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -All
Enable-WindowsOptionalFeature -Online -FeatureName IIS-ManagementConsole
```

> If you need WebSocket pass-through (backend uses WebSocket / SignalR), enable it under
> "Windows Features → Internet Information Services → World Wide Web Services → Application Development Features"
> by checking **WebSocket Protocol**, or:
> ```powershell
> Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets
> ```

### 1.2 Install the .NET runtime / Hosting Bundle

The target machine **must** have the **ASP.NET Core Runtime** (or the Hosting Bundle, which also
includes ANCM v2) matching the publish target. Download and install from Microsoft:

- .NET 8: <https://dotnet.microsoft.com/download/dotnet/8.0> → "ASP.NET Core Runtime 8.x" or
  ".NET Core Hosting Bundle"
- .NET 6: <https://dot.net/download/dotnet/6.0> → same (6.x)

After installation, **reboot the server once** so the ANCM module is registered with IIS.

Verify:

```powershell
# Should list the dotnet runtime
dotnet --list-runtimes
# aspnetcorev2.dll (ANCM v2) should exist under C:\Windows\System32\inetsrv\
```

---

## 2. Publish the app

On the **build machine** (with .NET SDK), publish the output to a folder:

```bash
# net8.0 example
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net8.0 -o "C:\inetpub\wwwroot\gateway"
```

After publishing, `C:\inetpub\wwwroot\gateway` should contain:

- `DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll` (main assembly)
- `DotNet.Yarp.Reverse.Proxy.Gateway.Core.dll` (governance library)
- `Yarp.ReverseProxy.dll`, `NewLife.Core.dll`, and other dependencies
- `appsettings.json` (edit for production)
- `web.config` (copied from the project, with ANCM / inprocess config)

> To host outside `wwwroot` (e.g. `D:\sites\gateway`), just point `-o` there and reference it in the
> IIS site later.

### 2.1 Pre-deployment config check (optional, like `nginx -t`)

Before starting the gateway you can validate the configuration (route/cluster ID uniqueness, backend
address validity, and whether the governance config binds correctly) to avoid surfacing config errors
only at runtime:

```bash
# Run inside the publish folder
cd "C:\inetpub\wwwroot\gateway"
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # prints Configuration OK on success
```

> `--test-config` validates and exits without opening a listening port; it can also be used as a gate
> in the CI/CD publish pipeline.

---

## 3. Configure web.config (usually no change needed)

`web.config` is bundled with the project. Key contents (keep defaults for deployment):

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

Notes:

- `hostingModel="inprocess"`: runs inside the IIS worker process (w3wp.exe) — best performance.
- `arguments` points to the published main assembly `DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll`.
- `stdoutLogEnabled`: temporarily set to `true` for troubleshooting; logs go to `.\logs\stdout`
  (the directory needs write permission).
- For `outofprocess`, keep `processPath="dotnet"` and let Kestrel listen on a port (configure the
  `Kestrel` section in `appsettings.json` or the `ASPNETCORE_URLS` environment variable).

---

## 4. Create the site in IIS

### 4.1 Create an app pool (No Managed Code)

1. Open **Internet Information Services (IIS) Manager**.
2. "Application Pools" → "Add Application Pool".
   - Name: `DotNetGatewayAppPool`
   - **.NET CLR version: No Managed Code**
   - Managed pipeline mode: **Integrated**
3. OK.

> This is the standard setup for ASP.NET Core apps: the code runs on the .NET runtime and IIS only
> forwards, so the CLR version is "No Managed Code".

### 4.2 Create the site

1. "Sites" → "Add Website".
   - Site name: `DotNetGateway`
   - Application pool: select `DotNetGatewayAppPool`
   - Physical path: `C:\inetpub\wwwroot\gateway`
   - Port: e.g. `80` (or any free port, e.g. `8080`)
   - Hostname: leave blank (bind all host headers) or set a public domain like `gw.example.com`
2. OK.

### 4.3 Directory permissions

Make sure the IIS app-pool identity has read / write (logs) permission on the site folder:

```powershell
# Grant the app-pool identity (example: IIS AppPool\DotNetGatewayAppPool)
$acl = Get-Acl "C:\inetpub\wwwroot\gateway"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "IIS AppPool\DotNetGatewayAppPool", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($rule)
Set-Acl "C:\inetpub\wwwroot\gateway" $acl
```

> With the default `ApplicationPoolIdentity`, you can also grant the `IUSR` and `IIS_IUSRS` groups.

---

## 5. Bind HTTPS (recommended)

Terminate TLS at IIS in production. IIS offers two ways to deploy SSL certificates: **per-site
certificate binding** (each site explicitly picks a certificate) and the **Centralized Certificate
Store (CCS)** (certificates are auto-matched by hostname). The differences are described below.

The app does not need to care which one you choose: in both cases it receives `X-Forwarded-Proto:
https`, and `UseForwardedHeaders` in `Program.cs` restores the real scheme (equivalent to nginx's
`X-Forwarded-*`) — the app itself never handles TLS.

### 5.1 Option A — Per-site certificate binding

Best for: few sites, certificates managed individually, or when specific sites need specific
certificates (e.g. mixing wildcard and single-name certs).

Steps:

1. Import the certificate (.pfx / .cer) into the server's local machine store:
   - `certlm.msc` (Local Computer → Personal → Certificates) → right-click "Import", or PowerShell:
     ```powershell
     $cert = Import-PfxCertificate -FilePath "C:\certs\gw.example.com.pfx" `
         -CertStoreLocation Cert:\LocalMachine\My -Password (ConvertTo-SecureString "password" -AsPlainText -Force)
     ```
2. In IIS Manager → site "Bindings" → add `https`:
   - IP address: `*` (all) or a specific IP; Port `443`;
   - Host name: the public domain (with **SNI** enabled, multiple certificates can share IP:443);
   - SSL certificate: pick the imported certificate from the dropdown.
3. OK — the certificate is now active for that site.

> Multiple certs on one port: tick "Require Server Name Indication (SNI)" to bind different
> certificates for different domains on the same IP:443 — functionally equivalent to CCS hostname
> matching, but certificates still have to be imported and bound one by one.

### 5.2 Option B — Centralized Certificate Store (CCS)

Best for: hosting many sites, frequently auto-renewed certificates (Let's Encrypt / ACME), or sharing
one certificate library across multiple IIS nodes / a web farm. Requires **IIS 8.0+ / Windows Server
2012+**.

How it works: certificates are stored as `.pfx` files in one shared directory; **the file name equals
the certificate's friendly name (hostname)**. IIS automatically looks up the matching `.pfx` based on
the request's hostname (SNI), so no per-site binding is needed.

Enable and configure:

1. Enable the feature (Server Manager or PowerShell):
   ```powershell
   Install-WindowsFeature Web-CentralizedSSLSupport
   # or GUI: Add Roles and Features → Web Server (IIS) → Security → Centralized SSL Certificates
   ```
2. Prepare the certificate store directory (preferably a share, e.g. `\\file-srv\certs`) and set
   permissions:
   - **Read** access only, for `IIS_IUSRS` / the computer accounts of each IIS node; private keys are
     protected by the store password.
3. Centralized Certificates config (IIS Manager → server-level node → Centralized Certificates):
   - Certificate store path: `\\file-srv\certs`
   - Password: the shared import password for all `.pfx` files (use a strong one and keep it safe)
   - User name / Password: credentials for accessing the share (if the share requires auth)
4. Put `.pfx` files in the store directory, named after the hostname:
   `gw.example.com.pfx`, `api.example.com.pfx`, `*.example.com.pfx` (wildcard)
5. Site binding: add an `https` binding and **leave the SSL certificate field empty** so CCS matches
   by hostname automatically (an explicitly selected certificate takes precedence if provided).

Management notes:

- **Renewal = replace the file**: after ACME / Let's Encrypt auto-renewal, just overwrite the same
  `.pfx` file — no per-site binding changes. After overwriting, refresh to invalidate the cache
  (restart the WWW service or run `iisreset`; CCS caches certificates by default).
- **One share, many nodes**: multiple IIS nodes can share the same directory — a single source of
  truth for web farms / NLB.
- **Security**: keep directory permissions minimal (read-only), never put the password in plaintext
  docs; use a dedicated account and audit access.

### 5.3 Comparison of the two approaches

| Aspect | Per-site binding (Option A) | Centralized Certificate Store (Option B) |
| --- | --- | --- |
| IIS version | All IIS versions | IIS 8.0+ / Windows Server 2012+ |
| Certificate storage | Local machine store (Local Machine\My) | `.pfx` files in a shared directory (named by hostname) |
| Binding model | Pick a certificate per site manually | Binding has no certificate; auto-matched by SNI hostname |
| Few sites | Simple and intuitive | More setup steps, little benefit |
| Many sites / frequent renewal | Import + bind one by one, error-prone | Renewal = replace file, automation-friendly (ACME) |
| Multi-node web farm | Import on every node | One shared library, naturally consistent |
| Multiple certs on one port | Per-site binding + SNI | Auto-distinguished by hostname |
| Management UI | Site "Bindings" dialog / MMC | Server-level "Centralized Certificates" page |
| Main risk | Scattered certs, easy to miss renewals | Mis-managed directory ACL / password = private-key exposure |

### 5.4 Which to choose — and how it relates to this project

- **One or a few sites**: use Option A (per-site binding) — fewer steps, more intuitive.
- **Many sites / domains, automated certificate renewal, or a web farm**: use Option B (CCS) —
  treat certificates like files.
- **This project**: the gateway only cares about the `X-Forwarded-Proto` it receives; it is
  independent of how the certificate is deployed. Both options work as-is.

Other recommendations:

- The certificate private key must be exportable (include private key), otherwise it cannot be bound
  in IIS or exported as `.pfx` for CCS.
- Use self-signed certificates for testing only; always use a trusted CA certificate in production.
- Optional: HTTP → HTTPS redirect (URL Rewrite module) to avoid a plaintext entry point.

---

## 6. Production configuration

Edit the published `appsettings.json`. Changes take effect via two different mechanisms:

| Config node | How it takes effect |
| --- | --- |
| `ReverseProxy` (routes/clusters/health checks/session affinity) | **YARP hot-reloads automatically** — no restart (equiv `nginx -s reload`) |
| `Gateway:RateLimit` / `Gateway:IpFilter` | **`POST /admin/reload` hot reload** — call once after editing, no restart (see 6.1) |
| Other `Gateway` nodes (CORS/Auth/breaker/logging, etc.) | Bound at startup; **recycle the app pool** to apply |

> Note: `RateLimit` / `IpFilter` use a shared-state design that supports online updates at runtime;
> the rest of the governance config remains startup-bound singletons. The two mechanisms are meant to
> be combined: hot-reload what you can, then recycle the pool for the rest on a change window.

### 6.1 Hot-reload governance config (RateLimit / IpFilter)

1. Edit `appsettings.json` in the publish folder — change `Gateway:RateLimit` or `Gateway:IpFilter`
   (e.g. temporarily block an IP).
2. Call the admin endpoint (**localhost only**, to prevent remote triggering):

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost/admin/reload"
# returns {"success":true,"reloaded":["RateLimit","IpFilter"],...}
```

3. Verify: `lastConfigReloadAt` in `GET http://localhost/admin/status` is updated to the reload time.

### 6.2 Recycle the app pool (for the rest of the Gateway config)

```powershell
Restart-WebAppPool -Name "DotNetGatewayAppPool"
```

---

## 7. Verify the deployment

### 7.1 Local probe

```powershell
# Static home page
Invoke-WebRequest -Uri "http://localhost/" -Method GET -SkipHttpErrorCheck

# Extension blacklist (expect 403)
Invoke-WebRequest -Uri "http://localhost/evil.aspx" -Method GET -SkipHttpErrorCheck

# Reverse proxy (200 if backend reachable; 502/503 if backend is down)
Invoke-WebRequest -Uri "http://localhost/api/health" -Method GET -SkipHttpErrorCheck

# Metrics endpoints (expect 200)
Invoke-WebRequest -Uri "http://localhost/metrics" -Method GET -SkipHttpErrorCheck   # Prometheus text
Invoke-WebRequest -Uri "http://localhost/health"  -Method GET -SkipHttpErrorCheck   # liveness probe

# Admin endpoint (expect 200, JSON summary; localhost only)
Invoke-WebRequest -Uri "http://localhost/admin/status" -Method GET -SkipHttpErrorCheck
```

### 7.2 Verification script

If backends are reachable, run the repo's `scripts/verify.ps1` on the server (change its `Base` to the
site address first).

### 7.3 Logs / troubleshooting

- XTrace runtime logs: `.log` files under `publish/Log` (or `bin/Release/<tfm>/Log`).
- Startup failure: set `web.config`'s `stdoutLogEnabled` to `true`, hit the site, then read
  `.\logs\stdout*.log`.
- Event Viewer: Windows → Application, filter by source `IIS AspNetCore Module` (500.31/500.32 usually
  mean a runtime-version mismatch or a wrong assembly name in `web.config`).

---

## 8. FAQ

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| 500.31 / HTTP Error 500.31 | Missing ASP.NET Core Runtime / Hosting Bundle | Install the matching runtime and reboot |
| 403 from the app | Hit the extension blacklist | Check `Security:BlockedExtensions` |
| 502.5 process exited | App failed to start (config / missing dependency) | Enable stdoutLog, read `.\logs\stdout` |
| Port in use | Site port conflicts with another site | Change port or binding |
| Home page works, proxy 502 | Backend not running or wrong address | Check `ReverseProxy.Clusters.Destinations.Address` |
| Location rewrite not working | Backend returns relative Location, or `UseRequestHostAsPublic=false` with empty `Rules` | See `LocationRewrite` config |

---

## 9. Upgrade & rollback

- **Upgrade**: re-run `dotnet publish -c Release -f net8.0 -o <same folder>`; recycle the app pool
  after overwriting. It's safer to stop the site (`Stop-WebSite`) first to avoid file locks.
- **Rollback**: keep a snapshot of the previous publish folder (or a Git tag), overwrite with the old
  files, and recycle the app pool.

---

> Note: IIS only hosts and forwards. Rate limiting, auth, CORS, security blocking, Location rewriting,
> and request logging are all done inside the gateway app — mapping one-to-one to nginx's edge features.
