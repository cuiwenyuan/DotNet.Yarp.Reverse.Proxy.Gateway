# DotNet.Yarp.Reverse.Proxy.Gateway

A lightweight reverse-proxy gateway built on .NET 6 / 8 / 10, designed to replace nginx's core
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
| Global concurrency cap | Limits concurrent in-flight requests (optional queueing), last line of defense for backend pressure | `limit_conn` |
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
| Config precheck | `--test-config` validates config before startup (uniqueness / address validity / transforms via YARP's own validator), exits non-zero on failure | `nginx -t` |
| Request correlation ID | Generate / pass through `X-Request-ID` across proxy logs, access logs and XTrace, echoed in response | `$request_id` / `X-Request-ID` |
| Upstream timeouts | Connect timeout + per-cluster ActivityTimeout | `proxy_connect_timeout` / `proxy_read_timeout` |
| Weighted load balancing | Random by `Metadata.Weight` (configurable weights) | upstream `weight` |
| Canary / gray release | YARP routes split by header match into different clusters | traffic-split config |
| JSON structured logging | Single-line JSON proxy logs (ELK/Loki friendly) | `log_format json` |
| Redirect Location rewrite | Rewrite backend absolute `Location` to public entry | `proxy_redirect` |
| Request / response logging | Method / URL / IP / latency / status / body (path+status rules, JSON output) | `access_log` + `log_subrequest` |
| Static file hosting | `wwwroot` static assets | `location { root }` |
| WebSocket / gRPC pass-through | Native YARP support | `proxy_http_version 1.1` + Upgrade |
| Forwarded-header restore | Restore real client IP / scheme | `set_real_ip` / `X-Forwarded-*` |
| **nginx.conf migration converter** | `--convert-nginx` turns nginx config into YARP routes/clusters (upstream weights, Hosts, path rewrites) | config migration tool |
| **nginx-compatible access log** | Optional combined/common format with size-based rotation; reuse existing GoAccess/ELK stacks | `access_log` / `log_format` |

## 3. Tech stack & dependency constraints

**Hard constraint: the whole solution references only two NuGet packages.** Everything else is
implemented with the .NET BCL (standard libraries) or ASP.NET Core's built-in framework capabilities.

| Package / Reference | Role | Type |
| --- | --- | --- |
| `Yarp.ReverseProxy` 2.3.0 | Microsoft's official reverse-proxy core | 3rd-party NuGet (allowed) |
| `NewLife.Core` 12.0.2026.1024 | Logging (XTrace), config, cache infra | 3rd-party NuGet (allowed, only extra 3rd-party) |
| `Microsoft.AspNetCore.App` | ASP.NET Core shared framework (HttpContext / middleware, etc.) | FrameworkReference (**not** NuGet) |
| Everything else | Rate limit, cache, auth, CORS, ext block, logging | Pure C# BCL |

> Note: `Microsoft.AspNetCore.App` is the **standard framework reference** for writing ASP.NET Core
> apps/middleware — not a third-party package, so it does not violate the "two NuGet only" rule.
> Rate limiting deliberately does **not** use `System.Threading.RateLimiting` (.NET 7+ only); instead
> a self-built `SimpleRateLimiter` token bucket keeps .NET 6 / 8 / 10 multi-targeting compatible.

## 4. Requirements

- **Build machine**: .NET 10 SDK (recommended — it can compile all three TFMs); if you only need the
  `net6.0` / `net8.0` artifacts, the .NET 6 or .NET 8 SDK is enough as well.
- **Runtime**: the target machine needs the corresponding **ASP.NET Core Runtime 6 / 8 / 10** (or the
  **.NET Core Hosting Bundle** for IIS).
- **OS & IIS** (per the [official .NET support matrix](https://learn.microsoft.com/en-us/dotnet/core/install/windows)):
  **Windows Server 2012 / 2012 R2 and later**; Kestrel self-hosting also works on Windows 10 / 11
  and Linux / macOS. All three TFMs share the same Windows Server / IIS compatibility range — what
  differs is the official support window of the .NET runtime itself:

  | Target framework | Supported Windows Server versions | Matching IIS versions | .NET official support |
  | --- | --- | --- | --- |
  | `net10.0` | 2012 / 2012 R2 / 2016 / 2019 / 2022 / 23H2 / 2025 (incl. Server Core; Nano Server 2019 / 2022 / 2025) | IIS 8.0 / 8.5 / 10.0 | ✅ LTS, supported until **Nov 2028** — **recommended** |
  | `net8.0` | 2012 / 2012 R2 / 2016 / 2019 / 2022 / 23H2 / 2025 (incl. Server Core) | IIS 8.0 / 8.5 / 10.0 | ⚠️ support ended **Nov 10, 2026** — existing environments only |
  | `net6.0` | 2012 / 2012 R2 / 2016 / 2019 / 2022 (incl. Server Core) | IIS 8.0 / 8.5 / 10.0 | ❌ support ended **Nov 12, 2024** — existing environments only |

  - The minimum for all three TFMs is **Windows Server 2012 (IIS 8.0)**. For production, prefer the
    `gateway-net10.0-*.zip` package with **ASP.NET Core Runtime 10 / .NET Hosting Bundle 10**.
  - On the minimum **Windows Server 2012 / 2012 R2** (IIS 8.0 / 8.5), install the
    **Microsoft Visual C++ 2015-2019 Redistributable** as well, or you may hit a missing
    `api-ms-win-crt-runtime-l1-1-0.dll`.
  - Note: Windows Server 2012 / 2012 R2 reached end of official support (Oct 2023; ESU can
    extend it). For production environments that need security patches, prefer **2016+ (IIS 10.0)**.
  - Client Windows: .NET 10 officially supports current **Windows 11** releases plus
    **Windows 10 LTSC (1607 / 1809 / 21H2) and Enterprise editions**; consumer Windows 10
    (Home / Pro 22H2) is past its support window — use Windows 11 or Win10 LTSC / Enterprise
    for development and testing.
- **Local dev & testing on Windows 10 / 11**: .NET 6, 8 and 10 can all run; three ways to
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
2. Download the latest `gateway-net10.0-v<version>.zip` (version looks like `1.1.2026.1120`;
   target machine has the .NET 10 runtime installed — the recommended LTS choice); use
   `gateway-net8.0-v<version>.zip` for a .NET 8 environment, or `gateway-net6.0-v<version>.zip`
   if the machine only has .NET 6.
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
# 1) Enter the repo root (folder name = repo name, contains DotNet.Yarp.Reverse.Proxy.Gateway.slnx)
cd DotNet.Yarp.Reverse.Proxy.Gateway

# 2) Restore and build (multi-targeting, Release)
dotnet build -c Release

# 2.1) Config precheck (optional, like `nginx -t`): validates route/cluster uniqueness and backend addresses
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net10.0 -o publish
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
DotNet.Yarp.Reverse.Proxy.Gateway/   # Repo root (local work dir may be named DotNet.Nginx; same code)
├── DotNet.Yarp.Reverse.Proxy.Gateway.slnx   # Solution
├── src/
│   ├── DotNet.Yarp.Reverse.Proxy.Gateway.Core/   # Class library: self-built governance (BCL + NewLife.Core)
│   │   ├── GatewayOptions.cs             # Config binding (maps to "Gateway" in appsettings.json)
│   │   ├── GatewayExtensions.cs          # Registration & pipeline (AddGateway / UseGatewayGovernance)
│   │   ├── SimpleRateLimiter.cs          # Self-built token bucket (rate/burst/delay, remaining query)
│   │   ├── RateLimitState.cs             # Rate-limit shared state + middleware (X-Rate-Limit-*, 429/delay)
│   │   ├── ConcurrencyLimitMiddleware.cs # Concurrency cap state + middleware (Interlocked CAS, equiv limit_conn)
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
    ├── Competitive-Analysis.zh-CN.md     # Competitive analysis (Chinese, incl. 2026 newcomers)
    ├── Competitive-Analysis.en.md        # Competitive Analysis (English)
    ├── IIS-Deployment.zh-CN.md           # IIS deployment guide (Chinese)
    └── IIS-Deployment.en.md              # IIS Deployment Guide (English)
```

## 7. Request pipeline (outer → inner)

```
Request → UseForwardedHeaders (restore X-Forwarded-*)
        → NginxAccessLogMiddleware (optional: nginx combined/common access log, counts compressed bytes)
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
             → ConcurrencyLimit (global concurrency cap, optional queueing)
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
| `NginxAccessLog:Enabled` | `false` | Enable nginx-compatible access log (off by default to avoid overlap with ProxyLogging) |
| `NginxAccessLog:Format` | `combined` | `combined` (with referer/user-agent) or `common` |
| `NginxAccessLog:Path` | `logs/access.log` | Log file path; relative paths are based on the publish directory |
| `NginxAccessLog:MaxFileBytes` | `10485760` | Rotation threshold in bytes; 0 = no rotation; rolls over to `.1`/`.2` |
| `NginxAccessLog:RetainedFiles` | `5` | Number of rotated files to keep |
| `NginxAccessLog:IgnoreStatusCodes` | `[]` | Requests with these status codes are not written to the access log |
| `NginxAccessLog:IncludeCorrelationId` | `false` | Append the correlation ID as a trailing field (off by default to keep strict nginx format) |
| `CorrelationId:Enabled` | `true` | Enable the request correlation ID |
| `CorrelationId:HeaderName` | `X-Request-ID` | Request / response header name |
| `CorrelationId:EchoResponseHeader` | `true` | Echo the ID in the response header for client-side tracing |
| `CorrelationId:TrustIncoming` | `true` | Trust and pass through a valid client-supplied ID (invalid IDs are always regenerated) |
| `ConcurrencyLimit:Enabled` | `false` | Enable the global concurrency cap (off by default, no change to existing deployments) |
| `ConcurrencyLimit:MaxConcurrent` | `100` | Max in-flight requests (equivalent to nginx `limit_conn` concurrency ceiling) |
| `ConcurrencyLimit:WaitEnabled` | `false` | Queue excess requests for a free slot (smooth mode); default rejects immediately |
| `ConcurrencyLimit:MaxWaitMs` | `1000` | Max queue wait in ms; on timeout returns `StatusCode` |
| `ConcurrencyLimit:StatusCode` | `503` | HTTP status on concurrency overflow (matches nginx `limit_conn` default), also writes `Retry-After: 1` and `X-Concurrency-Limit` headers |

### 8.3 Configuration hot reload (equivalent to nginx -s reload)

Edit and save `appsettings.json` and **all `Gateway` governance settings take effect automatically — no restart, no manual reload**. Built on the already-referenced NewLife.Core; no new dependencies.

| Stage | Mechanism |
| --- | --- |
| Change detection | `JsonConfigProvider` (FileSystemWatcher events, ≤1s) + periodic polling fallback (~40s, covers atomic replacement / network drives / watcher failure) |
| In-place mapping | `ConfigHelper.MapTo` writes new values into the already-injected Options singletons (including nested objects and arrays); middleware holding the references sees new values with zero code changes |
| Derived-state rebuild | Rate-limit token bucket and IP CIDR rules are rebuilt via `ApplyOptions` in the `Changed` callback; the concurrency cap only swaps the config reference and **does not reset the in-flight counter** (avoids momentarily relaxing the cap during reload) |
| Validation gate | Before applying, the same `ConfigValidator` as `--test-config` runs (including YARP route transform validation); invalid config is rejected, the last valid config is kept, `lastError` is recorded, and the service stays up |

- **Routes / clusters** (`ReverseProxy`) keep using YARP's `IConfiguration` reloadOnChange — behavior unchanged.
- **Manual fallback**: `POST /admin/reload` (localhost only) forces a disk reload through the same validation gate; usable when file watching fails.
- **Observability**: `GET /admin/status` returns a `hotReload` node with `reloadCount` / `lastReloadAt` / `lastAttemptAt` / `lastError`.
- **Note**: atomic file replacement (e.g. `sed -i`, "delete-then-write" tools) breaks the watcher handle; in that case the ~40s polling fallback applies. Normal editor saves (in-place writes) take effect in ≤1s.

### 8.4 nginx migration toolkit

Two high-friction migration pain points — **config rewriting** and **log-format breakage** — are solved by two pure-BCL tools with no new dependencies.

**(1) nginx.conf converter (`--convert-nginx`)**

Converts an existing `nginx.conf` into a YARP `ReverseProxy` JSON fragment, greatly reducing rewrite effort:

```bash
# Output to stdout (preview)
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --convert-nginx C:\nginx\nginx.conf

# Output to a file, then merge its ReverseProxy node into appsettings.json
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll --convert-nginx C:\nginx\nginx.conf -o converted.json
```

Automatic mappings:

| nginx | Conversion result |
| --- | --- |
| Multiple `server` in `upstream` with `weight=N` | Cluster Destinations + `Metadata.Weight` + `LoadBalancingPolicy: Weighted` |
| `server_name` | Route `Match.Hosts` |
| `location /api/` + `proxy_pass http://pool` | Route `Match.Path: /api/{**catch-all}` + `ClusterId` |
| `proxy_pass http://host:port/v1/` (with URI) | `PathRemovePrefix` + `PathPrefix` transforms |
| Static-value `proxy_set_header X Y` | YARP `RequestHeader` / `Set` transform |
| `down` nodes / `unix:` sockets | Skipped with a note |

> Directives that cannot be statically equivalent (regex locations, `$variables`, TLS certs, `root`, `rewrite`, rate limits, etc.) are written to the output JSON's `GatewayMigrationHints` node and to stderr for manual review; that node is not part of the runtime config and can be ignored when merging.
> Always validate with `--test-config` after converting, and confirm each item against the [migration guide](docs/Nginx-to-Gateway-Migration.en.md).

**(2) nginx-compatible access log (`Gateway:NginxAccessLog`)**

When enabled, emits standard combined format so existing GoAccess / ELK dashboards keep working — identical observability before and after migration:

```
127.0.0.1 - - [21/Nov/2026:21:30:01 +0800] "GET /api/test?x=1 HTTP/1.1" 200 342 "http://ref.test/" "curl/8.22.0"
```

- Mounted outside compression, so `$body_bytes_sent` counts bytes actually sent to the client; upgrade requests (WebSocket/gRPC) count as 0.
- Rotates by `MaxFileBytes` size, keeping `RetainedFiles` copies; default path is `logs/access.log` under the publish directory.
- Complements `ProxyLogging` (details/body logs): this one feeds general log analysis, the other feeds troubleshooting; both can be enabled together.
- Toggle / format / ignore codes take effect via the 8.3 hot reload; **changing `Path` requires a restart** to rebuild the writer.

**(3) GUI tool (`tools/NginxConfigConverter.UI`)**

When you prefer not to use the command line, a WinForms desktop tool is included in the repo (separate project, added to the solution). It reuses the **same converter source** as the CLI (source-linked, zero third-party NuGet) and supports:

- Dual input: pick a file or paste config text;
- One-click copy JSON / save to file;
- A migration-notes checklist (each item needing manual review);
- "Backup & merge into appsettings.json": backs up the original (`.bak.<timestamp>`) then replaces the `ReverseProxy` node, leaving other nodes untouched.

**Get it (recommended)**: every Release includes a separate asset `nginx-config-converter-gui-win-x64-v<version>.zip`.
It is a **self-contained single-file** app — just unzip and double-click `NginxConfigConverter.UI.exe`. **No .NET / Desktop Runtime needs to be installed** on the machine, and it does not depend on the gateway itself.

Build & run yourself (optional, Windows + .NET 10 SDK required):

```bash
dotnet build tools/NginxConfigConverter.UI -c Release
# Run: double-click NginxConfigConverter.UI.exe under bin/Release/net10.0-windows, or:
dotnet run --project tools/NginxConfigConverter.UI
```

> This tool only builds on Windows (WinForms); it does not affect the gateway's Linux/Windows multi-target build. It ships as a **separate Release asset**, not inside the gateway zip.
> After merging, run `--test-config` and restart the service.

## 9. Publish to IIS

See **[System Architecture (English)](docs/Architecture.en.md)** /
**[系统架构（中文）](docs/Architecture.zh-CN.md)** for the design,
**[nginx → Gateway Migration Guide (English)](docs/Nginx-to-Gateway-Migration.en.md)** /
**[nginx → 网关迁移对照（中文）](docs/Nginx-to-Gateway-Migration.md)** for migration from nginx,
**[Competitive Analysis (English)](docs/Competitive-Analysis.en.md)** /
**[竞品分析（中文）](docs/Competitive-Analysis.zh-CN.md)** for the competitive landscape, and
**[IIS Deployment Guide (English)](docs/IIS-Deployment.en.md)** /
**[IIS 安装部署指南（中文）](docs/IIS-Deployment.zh-CN.md)** for the deployment steps.

Quick steps:

```bash
# Publish (net10.0 shown; net8.0 / net6.0 also supported)
dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net10.0 -o "C:\inetpub\wwwroot\gateway"
```

Then create an IIS site pointing to that folder, set the app pool to **No Managed Code** (Integrated
pipeline). `web.config` is copied on publish and configured for the inprocess hosting model.

## 10. Build artifacts

- Multi-targeting: `net6.0`, `net8.0` and `net10.0`; `dotnet build` emits all three TFM assemblies.
- To target a single runtime, specify `-f` on publish (e.g. `-f net10.0`).
- Log directory: XTrace logs are written under `publish/Log` (or `bin/Release/<tfm>/Log`).

## 11. Versioning & automated releases

### Version number (NewLife rule: major.minor.year.MDD)

To stay consistent with the automated releases of
[NewLife.EFBM](https://github.com/cuiwenyuan/NewLife.EFBM), this project adopts the
**NewLife-style four-part version `major.minor.year.MDD`** (month **not** zero-padded). For example,
`1.1.2026.1120` means base 1.1, released on 2026-11-20.

- The **base** (major.minor) lives in [`Directory.Build.props`](Directory.Build.props) under
  `VersionPrefix` (currently `1.1`); only major/minor is maintained by hand.
- The `.year.MDD` part is generated at release time: taken from the tag (minus the `v`) for tag
  pushes, or supplied / derived from today's date for manual runs.
- Validation gate: a version that does not match `major.minor.year.MDD` aborts the release — no
  package or Release is produced.
- Assembly version: `AssemblyVersion / FileVersion` = the full four-part version; the informational
  version gets the Git commit appended by the SDK (e.g. `1.1.2026.1120+6a44d70`), visible in the
  `version` field of `/admin/status` for production reconciliation.

### Release flow (automatic via GitHub Actions)

```bash
# After committing, create a tag and push (version = tag without "v", must match major.minor.year.MDD)
git tag v1.1.2026.1120
git push origin v1.1.2026.1120
```

Pushing a `v*` tag triggers [`release.yml`](.github/workflows/release.yml) automatically:

1. **Build gate**: multi-TFM `dotnet build -c Release` (net6.0 + net8.0 + net10.0), zero errors;
2. **Config precheck gate**: `--test-config` (equivalent to `nginx -t`, run against the net10.0
   artifacts), abort on failure;
3. **Determine version**: extract from the tag and validate against the NewLife rule, abort on failure;
4. **Publish & package**: publish net10.0 / net8.0 / net6.0 (framework-dependent) →
   `gateway-net10.0-v1.1.2026.1120.zip`, `gateway-net8.0-v1.1.2026.1120.zip`,
   `gateway-net6.0-v1.1.2026.1120.zip`;
5. **Create Release**: titled `Gateway v1.1.2026.1120`, upload all three zips + auto-generated release
   notes, and mark it as latest.

### Deployment artifact

Each zip contains the complete publish folder (dll / appsettings.json / web.config / wwwroot) —
**extract it into the IIS site folder to deploy**. The target machine needs the matching ASP.NET
Core Runtime / Hosting Bundle (framework-dependent).

> Manual release (without a tag): GitHub Actions → Release workflow → **Run workflow**, optionally
> supply a version (e.g. `1.1.2026.1120`); leave it blank to derive the base + today's date. Manual
> runs also create a Release and tag, and must pass all the gates above.

## 12. License & disclaimer

This project is licensed under the **[MIT License](LICENSE)** (see the `LICENSE` file at the repo
root) — free to use, modify, and redistribute. For production, please:

- Move API keys / secrets out of config into a secret store or config center;
- Disable `ProxyLogging:LogResponseBody` or limit `MaxBodyBytes` to avoid memory pressure on large responses;
- Terminate TLS (HTTPS binding) in front of IIS and rely on `UseForwardedHeaders` to restore the real scheme.

---

For self-hosting on Linux with systemd + Kestrel, or integrating Prometheus / health dashboards,
feel free to open an Issue.
