# DotNet.Yarp.Reverse.Proxy.Gateway

A lightweight reverse-proxy gateway built on .NET 6 / .NET 8, designed to replace nginx's core
forwarding capabilities on Windows Server (reverse proxy, load balancing, health checks, rate
limiting, CORS, edge security, Location rewriting, and request/response logging).

> The working directory is named `DotNet.Nginx`, while the repository / solution name is
> `DotNet.Yarp.Reverse.Proxy.Gateway`. They point to the same code — only the naming layer differs
> (the working directory was kept unchanged to avoid move-side effects).

---

## 1. Why this project

Running nginx on Windows Server usually means: maintaining a native binary, a split from the .NET
ecosystem, scattered configuration, and difficulty integrating with CI/CD and logging. This project
implements a .NET-native reverse-proxy entry point that can be hosted inside IIS, consolidating
"forwarding + governance" into a single process, one codebase, and one configuration file — and it
supports **hot reload** after editing `appsettings.json` (equivalent to `nginx -s reload`).

### Common problems running nginx on Windows (official facts)

> Compiled from the [nginx for Windows docs](https://nginx.org/en/docs/windows.html) (the official
> docs state the Windows build is **beta quality**) and statements from nginx core developers — not
> community hearsay.

**A. Official positioning & performance limits**

| Problem | Details |
| --- | --- |
| Performance not expected | The Windows build uses only `select()`/`poll()`; official wording: "high performance and scalability should not be expected" |
| Multiple workers are useless | You can configure several workers, but only one actually handles connections — no multi-core benefit |
| Per-worker connection cap | `select()` had a hard 1024-connection limit; `poll()` (1.15.9+) removes it but has WSAPoll defects |
| No sendfile | Static files go through user-mode copies; lower throughput on large files / high concurrency than Linux |
| Not recommended for production | Core developer: "Running nginx on Windows in production setups might not be a good idea" |

**B. Missing functionality (Windows build)**

| Problem | Details |
| --- | --- |
| No caching | Modules that need shared memory (`proxy_cache` etc.) do not work on Windows (ASLR) |
| No UDP / QUIC | No UDP proxying in the Windows build |
| Missing modules | XSLT filter, image filter, GeoIP, embedded Perl, etc. |

**C. Deployment & operations pain points**

| Problem | Details |
| --- | --- |
| Not a Windows service | Runs as a console app: no auto-start on boot, no auto-restart on crash; needs WinSW / NSSM or similar to register a service |
| Fragile configuration | C-style syntax, UNIX-style forward-slash paths, relative paths based on the run directory; a bad edit breaks the service on `reload` |
| Port 443/80 already in use | Often taken by the IIS default site, Skype, other web servers, WSL2 / Docker; nginx fails silently (only an error.log line), debugging means `netstat` + PID hunting, and conflicts with IIS on the same port are frequent |
| Native binary upkeep | Manual download/replace for upgrades; some antivirus products flag nginx.exe |
| No built-in monitoring | `stub_status` must be compiled in (absent from the official Windows build); plain-text `access_log` only, disconnected from centralized logging |
| No management API | No dynamic config / admin endpoints in the open-source build; canary, rate-limit and other changes require editing files and reloading |

**D. Ecosystem split (.NET team perspective)**

nginx config is disconnected from .NET code, CI/CD and secret management — a higher learning curve
and hard to fold into one unified operations system.

### How this gateway addresses each problem

| nginx pain point | This gateway |
| --- | --- |
| Performance / scalability limits | .NET async model + YARP, hosted by IIS / ANCM, native multi-core use |
| No sendfile | Kestrel / IIS kernel-level transfer (ANCM inprocess) |
| No caching | Self-built `ResponseCacheMiddleware` in-memory cache (proxy_cache equivalent) |
| Not a service | IIS app-pool hosting: auto-start on boot, auto-recycle on crash |
| Port 443 taken / conflicts with IIS | The gateway IS an IIS-hosted app — there is no nginx-vs-IIS port conflict; the 443 binding is managed and visualized by IIS |
| Fragile configuration | `--test-config` precheck (like `nginx -t`) + `POST /admin/reload` online hot reload |
| No monitoring | `/metrics` (Prometheus) + `/health` + `/admin/status` |
| No management API | `/admin/status` + `/admin/reload` admin endpoints |
| Ecosystem split | Pure .NET: same ecosystem as CI/CD, secret stores, and logging |

## 2. Features

| Capability | Description | nginx equivalent |
| --- | --- | --- |
| Reverse proxy | YARP routes requests to backend clusters | `proxy_pass` |
| Load balancing | RoundRobin / PowerOfTwo / etc. | `upstream` + algorithm |
| Active / passive health check | Periodic probe of backend `/health` | `health_check` |
| Rate limiting | Self-built token bucket (rate/burst/delay configurable), X-Rate-Limit-* headers | `limit_req` |
| CORS | Origin / method / header allowlist | `add_header 'Access-Control-Allow-*'` |
| Simple API Key auth | Validates `X-Api-Key` header | lightweight `auth_request` |
| Edge security block | Extension blacklist (.aspx/.php/.jsp…) → 403 | `location ~* \.(aspx\|php)$ { return 403 }` |
| IP allow/deny list | CIDR prefix matching, deny hit → 403 | `allow` / `deny` |
| Request body size limit | Content-Length over limit → 413 | `client_max_body_size` |
| Retry on failure | Idempotent requests retry on a different node after 502/504/conn error | `proxy_next_upstream` |
| Circuit breaker | Opens after consecutive failures, half-open probe recovers | Ocelot QoS / breaker plugin |
| Session affinity (sticky) | Native YARP SessionAffinity, cookie keeps same backend | nginx plus / `ip_hash` |
| JWT auth | Self-built BCL validator (HS256/RS256), forwards `sub` to backend | Kong `jwt` / nginx plus |
| gzip compression | Built-in ResponseCompression (HTTP & HTTPS) | `gzip` |
| Response cache | In-memory cache of GET/HEAD 200 responses (configurable TTL) | `proxy_cache` |
| Security headers | X-Frame-Options / nosniff / CSP / Referrer-Policy etc. | SecurityHeaders plugin |
| Metrics endpoint | `/metrics` (Prometheus text) + `/health` liveness probe | `stub_status` |
| Admin endpoints | `/admin/status` (JSON summary) + `POST /admin/reload` (localhost hot reload of governance config) | nginx plus API |
| Config precheck | `--test-config` validates config before startup (uniqueness/address validity) | `nginx -t` |
| Upstream timeouts | Connect timeout + per-cluster ActivityTimeout | `proxy_connect_timeout` / `proxy_read_timeout` |
| Weighted load balancing | Random by `Metadata.Weight` (configurable weights) | upstream `weight` |
| Canary / gray release | YARP routes split by header match into different clusters | traffic-split config |
| JSON structured logging | Single-line JSON proxy logs (ELK/Loki friendly) | `log_format json` |
| Redirect Location rewrite | Rewrite backend absolute `Location` to public entry | `proxy_redirect` |
| Request / response logging | Method / URL / IP / latency / status / body (path+status rules, JSON output) | `access_log` + `log_subrequest` |
| Static file hosting | `wwwroot` static assets | `location { root }` |
| WebSocket / gRPC pass-through | Native YARP support | `proxy_http_version 1.1` + Upgrade |
| Forwarded-header restore | Restore real client IP / scheme | `set_real_ip` / `X-Forwarded-*` |

## 3. Tech stack & dependency constraints

**Hard constraint: the whole solution references only two NuGet packages.** Everything else is
implemented with the .NET BCL (standard libraries) or ASP.NET Core's built-in framework capabilities.

| Package / Reference | Role | Type |
| --- | --- | --- |
| `Yarp.ReverseProxy` 2.3.0 | Microsoft's official reverse-proxy core | 3rd-party NuGet (allowed) |
| `NewLife.Core` 11.18.2026.801 | Logging (XTrace), config, cache infra | 3rd-party NuGet (allowed, only extra 3rd-party) |
| `Microsoft.AspNetCore.App` | ASP.NET Core shared framework (HttpContext / middleware, etc.) | FrameworkReference (**not** NuGet) |
| Everything else | Rate limit, cache, auth, CORS, ext block, logging | Pure C# BCL |

> Note: `Microsoft.AspNetCore.App` is the **standard framework reference** for writing ASP.NET Core
> apps/middleware — not a third-party package, so it does not violate the "two NuGet only" rule.
> Rate limiting deliberately does **not** use `System.Threading.RateLimiting` (.NET 7+ only); instead
> a self-built `SimpleRateLimiter` token bucket keeps .NET 6 / 8 dual-targeting compatible.

## 4. Requirements

- **Build machine**: .NET 6 SDK or .NET 8 SDK (either is enough; dual-targeting produces both
  `net6.0` and `net8.0` artifacts).
- **Runtime**: the target machine needs the corresponding **ASP.NET Core Runtime 6 / 8** (or the
  **.NET Core Hosting Bundle** for IIS).
- **OS** (per the official .NET support matrix): **Windows Server 2012 / 2012 R2 and later**
  (IIS 8.0 / 8.5 / 10 — both the `net6.0` and `net8.0` packages are supported); Kestrel
  self-hosting also works on Windows 10 / 11 and Linux / macOS.
  - On the minimum **Windows Server 2012 / 2012 R2**, install the
    **Microsoft Visual C++ 2015-2019 Redistributable** as well, or you may hit a missing
    `api-ms-win-crt-runtime-l1-1-0.dll`.
  - Note: Windows Server 2012 / 2012 R2 reached end of official support (Oct 2023; ESU can
    extend it). For production environments that need security patches, prefer **2016+**.
- **Local dev & testing on Windows 10 / 11**: both .NET 6 and .NET 8 are supported; three ways to
  run —
  1. **Kestrel direct (recommended)**: `dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web`
     (or extract a Release package and run `dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll`).
     The SDK ships the runtime; works on every Windows 10/11 edition; default
     `http://localhost:5000`. Use this for everyday development and quick checks.
  2. **IIS Express**: used automatically by Visual Studio (F5) — handy for debugging
     `web.config` (ANCM) behavior.
  3. **Local IIS**: enable the "Internet Information Services" feature to get the closest
     production match (app pools / bindings / certs). Note: **the IIS feature is only available on
     Windows 10/11 Pro / Enterprise / Education** — Home edition has no IIS; use option 1 or 2.
  > Tip: Kestrel direct run bypasses IIS, so the ANCM settings in `web.config` don't apply, but YARP
  > and all governance features behave identically. Before going live, verify with `--test-config`
  > plus option 3 (or the target server's IIS).

## 5. Quick start

### Option A — Deploy from a Release (recommended, no .NET SDK needed)

No source code or compilation required — download and run:

1. Open the Releases page: <https://github.com/cuiwenyuan/DotNet.Yarp.Reverse.Proxy.Gateway/releases>
2. Download the latest `gateway-net8.0-vX.Y.Z.zip` (for Windows Server 2019+ with the .NET 8
   runtime installed; pick `gateway-net6.0-vX.Y.Z.zip` if the target machine only has .NET 6).
3. Extract it anywhere (e.g. `C:\gateway`) — the zip is a complete publish folder (dll /
   appsettings.json / web.config / wwwroot).
4. (Optional) Config precheck, equivalent to `nginx -t`:

   ```bash
   cd C:\gateway
   dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # prints Configuration OK
   ```

5. Start the gateway (Kestrel listens on http://localhost:5000 by default):

   ```bash
   cd C:\gateway
   dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll
   ```

6. Open `http://localhost:5000/` — it should return the static home page
   (`wwwroot/index.html`, 200).

> Release packages are **framework-dependent**: the target machine needs the matching ASP.NET Core
> Runtime (or the IIS Hosting Bundle); for production deployment under IIS see
> [9. Publish to IIS].

### Option B — Build from source (requires the .NET SDK)

```bash
# 1) Enter the repo root (contains DotNet.Yarp.Reverse.Proxy.Gateway.slnx)
cd DotNet.Nginx

# 2) Restore and build (dual-targeting, Release)
dotnet build -c Release

# 2.1) Config precheck (optional, like `nginx -t`): validates route/cluster uniqueness and backend addresses
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net8.0 -o publish
dotnet ./publish/DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --test-config   # prints Configuration OK

# 3) Run the gateway (Kestrel listens on http://localhost:5000 by default)
dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web
```

Open `http://localhost:5000/` — it should return the static home page (`wwwroot/index.html`, 200).

### Smoke test with the bundled script (works with either option)

The repo ships a pure-PowerShell verification script (no 3rd-party deps) plus two Python smoke
backends. Users deploying from a Release should clone the repo (or download just the `scripts/`
folder) to get these files:

```bash
# Terminal A: start two smoke backends
python scripts/echo_backend.py      # listens on 5200 (web-cluster)
python scripts/redir_backend.py     # listens on 5101 (one api-cluster node, for Location-rewrite test)

# Terminal B: start the gateway (Option A: run the dll from the extracted folder; Option B: dotnet run ...)
dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web

# Terminal C: run verification (first make sure $Base in the script points to the gateway URL)
pwsh scripts/verify.ps1
```

## 6. Project structure

```
DotNet.Nginx/
├── DotNet.Yarp.Reverse.Proxy.Gateway.slnx   # Solution
├── src/
│   ├── DotNet.Yarp.Reverse.Proxy.Gateway.Core/   # Class library: self-built governance (BCL + NewLife.Core)
│   │   ├── GatewayOptions.cs             # Config binding (maps to "Gateway" in appsettings.json)
│   │   ├── GatewayExtensions.cs          # Registration & pipeline (AddGateway / UseGatewayGovernance)
│   │   ├── SimpleRateLimiter.cs          # Self-built token bucket (rate/burst/delay, remaining query)
│   │   ├── RateLimitState.cs             # Rate-limit shared state + middleware (X-Rate-Limit-*, 429/delay)
│   │   ├── CorsMiddleware.cs             # CORS middleware
│   │   ├── ApiKeyAuthMiddleware.cs        # Simple API Key auth (401 on failure)
│   │   ├── AspxBlockerMiddleware.cs      # Extension blacklist block (403 on match)
│   │   ├── IpAddressUtil.cs              # IP/CIDR matching (IPv4/IPv6)
│   │   ├── IpFilterState.cs              # IP allow/deny shared state + middleware (hot-reloadable)
│   │   ├── RequestSizeLimitMiddleware.cs # Request body size limit (413 over limit)
│   │   ├── RetryMiddleware.cs            # Retry-on-failure middleware (node failover)
│   │   ├── CircuitBreaker.cs             # Circuit-breaker state machine (Closed/Open/HalfOpen)
│   │   ├── CircuitBreakerMiddleware.cs   # Circuit-breaker middleware (503 when open)
│   │   ├── JwtAuthMiddleware.cs          # JWT Bearer auth (pure BCL HS256/RS256)
│   │   ├── ResponseCacheMiddleware.cs    # Response cache middleware (proxy_cache)
│   │   ├── SecurityHeadersMiddleware.cs  # Security response headers middleware
│   │   ├── GatewayMetrics.cs             # Metrics collector + /metrics /health middleware
│   │   ├── AdminStatusMiddleware.cs      # Admin endpoints (/admin/status + POST /admin/reload)
│   │   ├── GatewayConfigReloader.cs      # Governance config hot reloader (RateLimit/IpFilter)
│   │   ├── LocationRewriteMiddleware.cs   # Redirect Location rewrite
│   │   ├── ProxyLoggingMiddleware.cs      # Request/response detail logging
│   │   ├── SimpleMemoryCache.cs          # Built-in in-memory cache (extension point)
│   │   └── GatewayLogger.cs              # NewLife.Core XTrace logging wrapper
│   └── DotNet.Yarp.Reverse.Proxy.Gateway.Web/    # ASP.NET Core host (YARP + project ref only)
│       ├── Program.cs                    # Pipeline assembly + --test-config precheck
│       ├── ConfigValidator.cs            # Config precheck tool (--test-config)
│       ├── WeightedLoadBalancingPolicy.cs # Weighted load-balancing policy (Metadata.Weight)
│       ├── appsettings.json              # Routes / clusters / governance config
│       ├── web.config                    # IIS (ANCM) hosting config
│       └── wwwroot/index.html            # Static home page
├── scripts/
│   ├── verify.ps1                        # Verification script
│   ├── echo_backend.py                   # Smoke backend (web cluster)
│   ├── smoke_api_5101.py                 # Smoke backend (api cluster, retry-chain test)
│   └── redir_backend.py                  # Smoke backend (Location-rewrite test)
└── docs/
    ├── Architecture.zh-CN.md             # 系统架构（中文）
    ├── Architecture.en.md                # System Architecture (English)
    ├── Nginx-to-Gateway-Migration.md     # nginx → gateway migration mapping (sanitized)
    ├── Nginx-to-Gateway-Migration.en.md  # nginx → Gateway Migration Guide (English)
    ├── IIS-Deployment.zh-CN.md           # IIS deployment guide (Chinese)
    └── IIS-Deployment.en.md              # IIS Deployment Guide (English)
```

## 7. Request pipeline (outer → inner)

```
Request → UseForwardedHeaders (restore X-Forwarded-*)
        → MetricsMiddleware (outermost: short-circuit /metrics & /health, count all requests)
        → UseResponseCompression (gzip; logging/retry inside record original bytes)
        → ProxyLoggingMiddleware (log request/response details)
        → RetryMiddleware (retry idempotent requests on failure)
        → UseGatewayGovernance:
             AspxBlocker (extension blacklist)
             → SecurityHeaders (security response headers)
             → IpFilter (IP allow/deny list)
             → RequestSize (request body size limit)
             → CORS
             → Rate limit
             → CircuitBreaker (circuit breaker / degradation)
             → API Key auth
             → JWT auth (HS256/RS256)
             → ResponseCache (response cache)
             → LocationRewrite (redirect Location rewrite)
        → UseFileServer (static files)
        → MapReverseProxy (YARP forwards to backend clusters, incl. SessionAffinity)
```

## 8. Configuration reference (appsettings.json)

### 8.1 Reverse proxy (`ReverseProxy`)

Routes and clusters follow the YARP config contract and support hot reload via `LoadFromConfig`.
Example:

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
      }
    }
  }
}
```

> Edits to the `ReverseProxy` node are hot-reloaded by YARP without a restart.
>
> Session affinity (sticky): native YARP `SessionAffinity`, enabled per cluster (Cookie policy) —
> the same client keeps hitting the same backend node (equiv nginx plus `sticky` / open-source `ip_hash`).
>
> Weighted load balancing: set cluster `LoadBalancingPolicy` to `Weighted`; each destination declares a
> weight via `Metadata: { "Weight": "3" }` (default 1) and traffic is distributed proportionally
> (equiv upstream `weight`).
>
> Canary / gray release: YARP routes support header-based split natively via `Match.Headers`, e.g.:
> ```json
> { "canary-route": { "ClusterId": "canary-cluster",
>     "Match": { "Path": "/api/{**catch-all}", "Headers": [ { "Name": "X-Canary", "Values": [ "1" ] } ] } } }
> ```
> Requests with `X-Canary: 1` go to the canary cluster, everything else follows the default route —
> pure config, hot-reloaded, no code.

### 8.2 Edge governance (`Gateway`)

| Setting | Default | Description |
| --- | --- | --- |
| `RateLimit:PerMinute` | `600` | Requests/min per client (token refill rate); `429` when exceeded |
| `RateLimit:Burst` | `0` (=PerMinute) | Bucket capacity (allows bursts, equiv nginx limit_req burst) |
| `RateLimit:DelayEnabled` | `false` | Smooth mode: over-quota requests wait up to MaxDelayMs instead of instant 429 (equiv nginx delay) |
| `RateLimit:MaxDelayMs` | `1000` | Max wait (ms) in smooth mode; reject after timeout |
| `RateLimit:PerIp` | `true` | Per-client-IP buckets (equiv nginx limit_req keyed by remote_addr) |
| `RateLimit:MaxIpBuckets` | `10000` | Max IP buckets; falls back to the global bucket when exceeded |
| `RateLimit:BucketIdleMinutes` | `30` | Idle IP-bucket eviction (minutes) |
| `Cors:AllowedOrigins` | `[]` (allow all) | Allowed origins; non-empty restricts to the list |
| `Cors:AllowedMethods` | `GET,POST,PUT,DELETE,OPTIONS` | Allowed methods |
| `Cors:AllowedHeaders` | `Content-Type,Authorization,X-Api-Key` | Allowed request headers |
| `Auth:Required` | `false` | Enable `X-Api-Key` validation (`401` on failure) |
| `Auth:Keys` | `["demo-key-1"]` | Valid keys (use a secret store in prod, don't hardcode) |
| `Auth:Jwt:Enabled` | `false` | Enable JWT Bearer validation (`401` on failure) |
| `Auth:Jwt:Secret` | `""` | HS256 symmetric key (UTF-8 or Base64) |
| `Auth:Jwt:PublicKeyPem` | `""` | RS256 public key PEM (or raw Base64 SPKI) |
| `Auth:Jwt:Issuer` / `Audience` | `""` | Validate `iss` / `aud` claims when set |
| `Auth:Jwt:ClockSkewSeconds` | `60` | Clock skew tolerance (seconds) |
| `Auth:Jwt:RequireExpiry` | `true` | Require the `exp` claim |
| `Auth:Jwt:ClaimsHeader` | `X-Jwt-Sub` | Header used to forward `sub` to the backend |
| `Cache:Enabled` | `true` | Enable response cache (GET/HEAD 200; skips Authorization/Cookie requests) |
| `Cache:DefaultTtlSeconds` | `30` | Default cache TTL (seconds); prefers response Cache-Control max-age |
| `Cache:MaxCacheEntries` | `10000` | Max cache entries (growth guard) |
| `Security:Enabled` | `true` | Enable extension blacklist block |
| `Security:BlockedExtensions` | `aspx,asp,php,jsp,asa,asax,ashx,axd,config` | Blocked extensions |
| `Security:StatusCode` | `403` | Status code returned on block |
| `IpFilter:Enabled` | `false` | Enable IP allow/deny list |
| `IpFilter:Allow` / `Deny` | `[]` | Allow/deny entries (CIDR or single IP, e.g. `10.0.0.0/8`); deny wins |
| `IpFilter:StatusCode` | `403` | Status code returned on block |
| `RequestSize:Enabled` | `true` | Enable request body size limit |
| `RequestSize:MaxBytes` | `10485760` | Max request body (bytes, default 10 MB) |
| `RequestSize:StatusCode` | `413` | Status code when over limit |
| `Retry:Enabled` | `true` | Enable retry on failure (idempotent methods only: GET/HEAD/OPTIONS/TRACE) |
| `Retry:MaxRetries` | `1` | Max retries (storm-guard, immediate retry, no wait) |
| `Retry:RetryableStatusCodes` | `[502,504]` | Retryable status codes (503 = breaker/overload, not retried) |
| `Retry:RetryOnConnectionError` | `true` | Retry on connection failure / timeout |
| `Retry:RetryOnHttp500` | `false` | Retry on 500 |
| `CircuitBreaker:Enabled` | `true` | Enable circuit breaker |
| `CircuitBreaker:FailureThreshold` | `5` | Consecutive failures before the breaker opens |
| `CircuitBreaker:OpenSeconds` | `30` | Open duration (seconds), then half-open probe |
| `CircuitBreaker:HalfOpenMaxRequests` | `1` | Probe requests allowed in half-open state |
| `CircuitBreaker:FailureStatusCodes` | `[500,502,503,504]` | Response codes treated as failure |
| `CircuitBreaker:StatusCode` | `503` | Status code when open (with Retry-After) |
| `SecurityHeaders:Enabled` | `true` | Enable security response headers |
| `SecurityHeaders:XFrameOptions` | `SAMEORIGIN` | X-Frame-Options (empty = not added) |
| `SecurityHeaders:XContentTypeOptions` | `nosniff` | X-Content-Type-Options |
| `SecurityHeaders:ReferrerPolicy` | `strict-origin-when-cross-origin` | Referrer-Policy |
| `SecurityHeaders:ContentSecurityPolicy` | `""` | CSP (empty = not added, configure as needed) |
| `Metrics:Enabled` | `true` | Enable metrics endpoints |
| `Metrics:MetricsPath` | `/metrics` | Prometheus text metrics path |
| `Metrics:HealthPath` | `/health` | Liveness probe path |
| `Metrics:IncludePathLabels` | `false` | Add path-prefix labels (high cardinality, off by default) |
| `HttpTimeout:Enabled` | `true` | Enable upstream connect timeout |
| `HttpTimeout:ConnectSeconds` | `10` | TCP connect timeout to backend (s); per-request activity timeout via cluster `HttpRequest.ActivityTimeout` |
| `Admin:Enabled` | `true` | Enable admin endpoints |
| `Admin:Path` | `/admin/status` | Runtime status JSON summary path |
| `Admin:ReloadEnabled` | `true` | Enable config hot-reload endpoint (localhost only) |
| `Admin:ReloadPath` | `/admin/reload` | Reload endpoint (POST; reloads RateLimit / IpFilter) |
| `LocationRewrite:Enabled` | `true` | Rewrite redirect Location |
| `LocationRewrite:UseRequestHostAsPublic` | `true` | Replace backend absolute Location with request `scheme://host` (equiv `proxy_redirect default`) |
| `LocationRewrite:Rules` | `[]` | Explicit prefix rewrite rules (Match → Replace) |
| `ProxyLogging:Enabled` | `true` | Log proxy detail |
| `ProxyLogging:MaxBodyBytes` | `65536` | Max body read size (bytes); larger bodies are truncated |
| `ProxyLogging:LogRequestHeaders` | `false` | Log request headers |
| `ProxyLogging:LogRequestBody` | `true` | Log request body |
| `ProxyLogging:LogResponseBody` | `true` | Log response body (uses MemoryStream to take over Response.Body; disable for large responses) |
| `ProxyLogging:IgnoreStatusCodes` | `[]` | Skip logging for these status codes (e.g. `304`) |
| `ProxyLogging:Json` | `false` | Emit single-line JSON proxy logs (ELK/Loki friendly) |
| `ProxyLogging:BodyLogRules` | `[]` | Fine-grained body-log rules: `[{PathContains:"/api/login",StatusCodes:[200]}]`; when non-empty only matching requests log the body |

## 9. Publish to IIS

See **[System Architecture (English)](docs/Architecture.en.md)** /
**[系统架构（中文）](docs/Architecture.zh-CN.md)** for the design,
**[nginx → Gateway Migration Guide (English)](docs/Nginx-to-Gateway-Migration.en.md)** /
**[nginx → 网关迁移对照（中文）](docs/Nginx-to-Gateway-Migration.md)** for migration from nginx, and
**[IIS Deployment Guide (English)](docs/IIS-Deployment.en.md)** /
**[IIS 安装部署指南（中文）](docs/IIS-Deployment.zh-CN.md)** for the deployment steps.

Quick steps:

```bash
# Publish (net8.0 shown; net6.0 also supported)
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net8.0 -o "C:\inetpub\wwwroot\gateway"
```

Then create an IIS site pointing to that folder, set the app pool to **No Managed Code** (Integrated
pipeline). `web.config` is copied on publish and configured for the inprocess hosting model.

## 10. Build artifacts

- Dual-targeting: `net6.0` and `net8.0`; `dotnet build` emits both TFM assemblies.
- To target a single runtime, specify `-f` on publish (e.g. `-f net8.0`).
- Log directory: XTrace logs are written under `publish/Log` (or `bin/Release/<tfm>/Log`).

## 11. Versioning & automated releases

### Version number (SemVer, single source of truth)

The version is **driven by Git tags**: the development version lives in
[`Directory.Build.props`](Directory.Build.props) at the repo root under `VersionPrefix`
(currently `1.0.0`); on release, GitHub Actions overrides it via `-p:Version=<tag version>`, so
nothing has to be maintained locally and the two can never drift apart.

- Assembly version: `AssemblyVersion / FileVersion` = the version number.
- Informational version: `InformationalVersion` = version + Git commit (e.g. `1.0.0+6a44d70`),
  visible in the `version` field of `/admin/status` for production reconciliation.

### Release flow (automatic via GitHub Actions)

```bash
# After committing, create a tag and push (version = tag without the leading "v")
git tag v1.0.0
git push origin v1.0.0
```

Pushing a `v*` tag triggers [`release.yml`](.github/workflows/release.yml) automatically:

1. **Build gate**: dual-TFM `dotnet build -c Release` (net6.0 + net8.0), zero errors;
2. **Config precheck gate**: `--test-config` (equivalent to `nginx -t`), abort on failure;
3. **Publish & package**: publish net6.0 / net8.0 (framework-dependent) →
   `gateway-net8.0-v1.0.0.zip`, `gateway-net6.0-v1.0.0.zip`;
4. **Create Release**: upload both zips + auto-generated release notes (commits since last tag).

### Deployment artifact

Each zip contains the complete publish folder (dll / appsettings.json / web.config / wwwroot) —
**extract it into the IIS site folder to deploy**. The target machine needs the matching ASP.NET
Core Runtime / Hosting Bundle (framework-dependent).

> Manual trigger (no tag): GitHub Actions → Release workflow → **Run workflow** — runs only the
> build + precheck gates for everyday validation.

## 12. License & disclaimer

This project is licensed under the **[MIT License](LICENSE)** (see the `LICENSE` file at the repo
root) — free to use, modify, and redistribute. For production, please:

- Move API keys / secrets out of config into a secret store or config center;
- Disable `ProxyLogging:LogResponseBody` or limit `MaxBodyBytes` to avoid memory pressure on large responses;
- Terminate TLS (HTTPS binding) in front of IIS and rely on `UseForwardedHeaders` to restore the real scheme.

---

For self-hosting on Linux with systemd + Kestrel, or integrating Prometheus / health dashboards,
feel free to open an Issue.
