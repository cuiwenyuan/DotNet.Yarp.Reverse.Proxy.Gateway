# System Architecture (English)

This document explains the design of `DotNet.Yarp.Reverse.Proxy.Gateway` from an architectural
perspective: deployment shape, layers, request pipeline, core components, configuration model, and
resilience design. Intended audience: engineers maintaining or extending the gateway.

> Companion docs: [README](../README.en.md) · [IIS Deployment Guide](IIS-Deployment.en.md) ·
> [nginx → Gateway Migration Guide](Nginx-to-Gateway-Migration.en.md) ·
> **中文版： [系统架构](Architecture.zh-CN.md)**

---

## 1. System shape

A **single-process, single-config-file** reverse-proxy gateway that replaces nginx's core
"forwarding + edge governance" capabilities on Windows Server with .NET:

- **Deployment**: by default hosted behind IIS (ASP.NET Core Module, inprocess); Kestrel
  self-hosting is also supported.
- **Forwarding core**: Microsoft's official **YARP** (Yet Another Reverse Proxy) — route matching,
  cluster selection, load balancing, health checks, connection pooling, and protocol pass-through
  (HTTP/1.1, HTTP/2, WebSocket, gRPC).
- **Governance**: all handled by self-built middleware in the `Gateway.Core` library (rate limiting,
  auth, circuit breaking, retry, IP filtering, caching, security headers, logging, metrics) — no
  third-party gateway components.
- **Configuration**: a single `appsettings.json` with two top-level nodes: `ReverseProxy` (YARP
  contract) and `Gateway` (self-built governance); hot reload supported (see section 7).

### 1.1 Overall architecture

```
                    ┌──────────────────────────────────────────────────────┐
                    │                 Windows Server                        │
                    │                                                        │
   Public client ──HTTPS──▶  IIS (ANCM v2, inprocess)                        │
                    │   │  gateway runs inside w3wp.exe                     │
                    │   ▼                                                  │
                    │  ┌──────────────────────────────────────────────┐    │
                    │  │     DotNet.Yarp.Reverse.Proxy.Gateway.Web    │    │
                    │  │  ──────────────────────────────────────────  │    │
                    │  │   Program.cs: pipeline assembly / precheck   │    │
                    │  │   ConfigValidator (--test-config)            │    │
                    │  │   WeightedLoadBalancingPolicy (weighted)     │    │
                    │  ├──────────────────────────────────────────────┤    │
                    │  │       Gateway.Core (self-built governance)    │    │
                    │  │  ┌────────────────────────────────────────┐  │    │
                    │  │  │ Middleware pipeline (sec. 4, outer→inner)│  │    │
                    │  │  │  Metrics → Compression → ProxyLogging   │  │    │
                    │  │  │  → Retry → governance group →           │  │    │
                    │  │  │  ResponseCache → LocationRewrite        │  │    │
                    │  │  └────────────────────────────────────────┘  │    │
                    │  │  Shared state: RateLimitState / IpFilterState│    │
                    │  │  (hot-reloadable via POST /admin/reload)     │    │
                    │  └──────────────────────────────────────────────┘    │
                    │               │  YARP forwarding (HTTP client pool)   │
                    │               ▼                                      │
                    │  Backends: api-cluster (5101/5102)  web-cluster (5200)│
                    └──────────────────────────────────────────────────────┘
```

---

## 2. Layered design

| Layer | Location | Responsibility | Dependencies |
| --- | --- | --- | --- |
| **Host (Web)** | `src/DotNet.Yarp.Reverse.Proxy.Gateway.Web` | Pipeline assembly (`Program.cs`), config precheck (`ConfigValidator`), host-side YARP types (`WeightedLoadBalancingPolicy`) | `Yarp.ReverseProxy` (NuGet) + project ref to Core |
| **Governance (Core)** | `src/DotNet.Yarp.Reverse.Proxy.Gateway.Core` | All self-built middleware, config binding (`GatewayOptions`), shared state & hot reloader, logging wrapper | `NewLife.Core` (NuGet) + `Microsoft.AspNetCore.App` (framework ref) |
| **Forwarding core** | YARP (in-process) | Route matching, cluster/destination selection, health checks, connection management, protocol pass-through | —— |

**Dependency hard rule**: the whole solution references only two NuGet packages —
`Yarp.ReverseProxy` (Web layer) and `NewLife.Core` (Core layer). `Microsoft.AspNetCore.App` is a
framework reference (not NuGet) providing `HttpContext` / `RequestDelegate` and other middleware
infrastructure. All governance logic is implemented in pure C# / BCL, which keeps the audit surface
small, avoids supply-chain risk, and keeps dual-target compilation (net6.0/net8.0) working.

**Layering rule**: Core never references YARP types (e.g. `ILoadBalancingPolicy`, `ClusterState`).
Anything that depends on YARP types (the weighted load-balancing policy) lives in the Web host layer,
so Core stays independently testable and reusable.

---

## 3. Request lifecycle (data flow)

A request passes through the following stages from entry to response:

```
① Entry → IIS/ANCM → Kestrel
② UseForwardedHeaders: restore X-Forwarded-For/Proto/Host (real client IP & scheme)
③ MetricsMiddleware: count/time every request (/metrics, /health short-circuit)
④ ResponseCompression: gzip (logging inside sees original bytes)
⑤ ProxyLogging: log request/response details (optional JSON, fine-grained BodyLogRules)
⑥ RetryMiddleware: idempotent requests re-run the downstream pipeline on failure
⑦ Governance group (UseGatewayGovernance, outer → inner):
   AspxBlocker(ext blacklist) → SecurityHeaders → IpFilter(IP allow/deny)
   → RequestSize(body limit) → CORS → RateLimit → CircuitBreaker
   → ApiKeyAuth → JwtAuth → ResponseCache → LocationRewrite
⑧ FileServer: wwwroot static files
⑨ YARP: route match → load balance (RoundRobin/Weighted/...) → forward to backend
⑩ Response returns along the same path (after-callbacks: breaker counting, cache fill, log flush)
```

**Key design decisions**:

- **Block first**: AspxBlocker / IpFilter / RequestSize sit at the outermost edge of the governance
  group, so malicious and oversized requests are short-circuited before CORS/rate limiting, saving
  CPU.
- **Auth before forwarding**: ApiKey / JWT run before YARP, so the backend only ever sees
  authenticated requests; after JWT validation the `sub` claim is forwarded as the `X-Jwt-Sub`
  header (no re-parsing in the backend).
- **Logging outside compression**: ProxyLogging sits inside ResponseCompression, so it reads the
  **original uncompressed bytes** — the log matches what the backend actually returned; the response
  body is captured via a MemoryStream and restored afterwards.
- **Retry outside governance**: on failure the retry middleware re-runs the **entire** governance +
  forwarding pipeline (equivalent to the client resending), which naturally switches nodes; the
  governance middleware (rate limit/breaker) itself is not part of the retry loop, so quotas are not
  double-charged.

---

## 4. Core components

### 4.1 Forwarding core (YARP)

| Capability | Config location | Notes |
| --- | --- | --- |
| Route matching | `ReverseProxy:Routes` | Path / Host / Headers match (catch-all, header-based canary split) |
| Clusters & destinations | `ReverseProxy:Clusters` | `Destinations` define backend addresses; `LoadBalancingPolicy` selects strategy |
| Load balancing | cluster node | RoundRobin / PowerOfTwoChoices / **Weighted** (self-built, random by `Metadata.Weight`) |
| Health checks | cluster node | Active (periodic `/health` probe, `ConsecutiveFailures` policy) |
| Session affinity | cluster node | `SessionAffinity` (Cookie policy), same client sticks to one backend |
| Timeouts | cluster `HttpRequest.ActivityTimeout` + connect timeout in `Program.cs` | per-request activity / TCP connect timeout |
| Protocol pass-through | built-in | WebSocket / gRPC / HTTP/2 out of the box |

### 4.2 Governance middleware (Gateway.Core)

| Middleware | Status code | Core mechanism | Hot reload |
| --- | --- | --- | --- |
| `AspxBlockerMiddleware` | 403 | Extension blacklist (.aspx/.php/.jsp/...) | No (startup-bound) |
| `SecurityHeadersMiddleware` | —— | X-Frame-Options / nosniff / Referrer-Policy / CSP; existing headers not overridden | No |
| `IpFilterMiddleware` | 403 | CIDR allow/deny (IPv4/IPv6); deny wins | **Yes** (`IpFilterState`) |
| `RequestSizeLimitMiddleware` | 413 | Reject when Content-Length exceeds the limit | No |
| `CorsMiddleware` | —— | Origin/method/header allowlist + preflight | No |
| `RateLimitMiddleware` | 429 (or delay) | **Token bucket**: rate (refill) / burst (capacity) / delay (smooth wait); per-client-IP buckets; emits `X-Rate-Limit-Limit/Remaining/Reset` | **Yes** (`RateLimitState`) |
| `CircuitBreakerMiddleware` | 503 | Per-path-prefix breaker state machine (Closed/Open/HalfOpen); opens after consecutive failures; half-open probe recovers | No |
| `ApiKeyAuthMiddleware` | 401 | `X-Api-Key` validation (constant-time compare) | No |
| `JwtAuthMiddleware` | 401 | Pure-BCL HS256/RS256 validation (exp/nbf/iss/aud); forwards `sub` | No |
| `ResponseCacheMiddleware` | —— | In-memory cache of GET/HEAD 200; skips Authorization/Cookie requests; prefers Cache-Control max-age | No |
| `LocationRewriteMiddleware` | —— | Rewrites `Location` on 3xx (request-host substitution or explicit rules) | No |
| `RetryMiddleware` | —— | Idempotent methods + 502/504/conn error re-run downstream (max 1 retry, storm-guard) | No |
| `ProxyLoggingMiddleware` | —— | Method/URL/IP/latency/status/request body/response body; JSON output; fine-grained BodyLogRules | No |
| `MetricsMiddleware` | —— | `/metrics` Prometheus text + `/health` probe (Interlocked counters) | No |
| `AdminStatusMiddleware` | —— | `/admin/status` JSON summary + `POST /admin/reload` (localhost only) | No |

### 4.3 Shared state & hot reload

To support **online updates** of `RateLimit` / `IpFilter`, those two middlewares do not inject the
startup-bound options singletons — they inject **shared state objects**:

```
appsettings.json ──(edit)──▶ POST /admin/reload
                                  │
                                  ▼
                     GatewayConfigReloader.Reload()
                                  │  re-bind config → ApplyOptions()
                                  ▼
              RateLimitState (ConcurrentDictionary<IP, bucket>)
              IpFilterState   (CIDR list, atomically swapped)
```

- `RateLimitState`: holds a token bucket per client IP; `ApplyOptions` updates rate/capacity/mode
  while preserving existing IP buckets (in-flight rate limiting is not interrupted).
- `IpFilterState`: `ApplyOptions` atomically swaps the CIDR list; the next request uses the new rules.
- All other governance config remains startup-bound singletons — a pool recycle (IIS) or process
  restart is required after editing.

---

## 5. Configuration model

A single `appsettings.json` with two levels and three application mechanisms:

```
appsettings.json
├── ReverseProxy            ← YARP contract (routes/clusters/health checks/session affinity)
│                              Applied: YARP auto hot-reload (reloadOnChange)
└── Gateway                 ← self-built governance (bound to GatewayOptions)
     ├── RateLimit / IpFilter   Applied: POST /admin/reload hot reload
     └── other nodes            Applied: pool recycle / restart
```

**Design intent**: separate "forwarding config" (changes often: backend scaling, canary, health
checks) from "governance config" (security policy, changes rarely); and make the two most
operation-sensitive items — rate limiting and IP filtering — hot-reloadable, balancing security and
availability. `--test-config` validates both levels before startup/publish (ID uniqueness, address
validity, governance binding).

---

## 6. Resilience design

| Failure scenario | Protection | Behavior |
| --- | --- | --- |
| Single backend node down | Active health check removal + passive detection | Failing node leaves the available set |
| Backend 502/504 / connection failure | `RetryMiddleware` | Idempotent requests re-run the pipeline on another node (max 1) |
| Persistent backend failure | `CircuitBreaker` | Opens after consecutive failures → **503 fast-fail** (with Retry-After); half-open probe recovers |
| Client traffic spike | `RateLimit` (token bucket) | 429 over quota; `delay` mode queues smoothly |
| Malicious / oversized requests | AspxBlocker / IpFilter / RequestSize | Short-circuited before forwarding (403/413) |
| Slow backend responses | Cluster `ActivityTimeout` + connect timeout | Per-request timeout, no connection-pool exhaustion |
| Broken config | `--test-config` precheck | Fails fast before startup/publish |
| Process / deployment issues | IIS app-pool auto-recycle + XTrace logs | 500.31/502.5 diagnosed quickly |

**How rate limit / breaker / retry interact** (no interference):

```
Rate limit (ingress control) → Circuit breaker (downstream health) → Retry (per-request tolerance)
   429 / delay                   503 fast-fail                     502/504 node switch
```

- The 503 returned by an open breaker is **not** in the retryable status codes (avoids breaker
  storms).
- Retry wraps the governance group; re-running the pipeline does **not** consume rate-limit tokens
  again (the limiter is inside the retry loop).

---

## 7. Extension points

| Extension point | Mechanism | Example |
| --- | --- | --- |
| Load-balancing policy | Implement `ILoadBalancingPolicy`, register in DI | `WeightedLoadBalancingPolicy` (Metadata.Weight) |
| Traffic splitting | YARP `Match.Headers`, pure config | Canary (`X-Canary: 1` header split) |
| Dynamic config source | Implement `IProxyConfigProvider` | Reserved: Consul / k8s / admin write API |
| Response-cache storage | `SimpleMemoryCache` (swappable) | Reserved: distributed cache |
| Log format | `ProxyLogging:Json` + `BodyLogRules` | ELK/Loki ingestion |
| Metrics consumption | `/metrics` Prometheus text | Prometheus / Grafana |

---

## 8. Directory structure (architecture view)

```
src/
├── DotNet.Yarp.Reverse.Proxy.Gateway.Core/   # Governance layer (pure BCL + NewLife.Core)
│   ├── GatewayOptions.cs                     # Config binding model (appsettings "Gateway")
│   ├── GatewayExtensions.cs                  # DI registration + pipeline (AddGateway/UseGatewayGovernance)
│   ├── GatewayConfigReloader.cs              # Hot reloader (RateLimit/IpFilter)
│   ├── RateLimitState.cs                     # Rate-limit shared state + middleware (incl. SimpleRateLimiter)
│   ├── IpFilterState.cs                      # IP allow/deny shared state + middleware
│   ├── *Middleware.cs                        # Remaining governance/observability middleware
│   ├── CircuitBreaker.cs                     # Breaker state machine (separated for unit testing)
│   ├── IpAddressUtil.cs                      # CIDR matching utility
│   ├── SimpleMemoryCache.cs                  # In-memory cache (extension point)
│   └── GatewayLogger.cs                      # NewLife.Core XTrace wrapper
└── DotNet.Yarp.Reverse.Proxy.Gateway.Web/    # Host layer (YARP + project ref)
    ├── Program.cs                            # Pipeline assembly / --test-config / connect timeout
    ├── ConfigValidator.cs                    # Config precheck
    ├── WeightedLoadBalancingPolicy.cs        # Weighted LB policy (depends on YARP types)
    ├── appsettings.json                      # Two-level config
    └── web.config                            # IIS/ANCM hosting
```

**Rationale**: Core has no YARP type dependency → compiles/tests/reuses independently; the Web layer
is thin and focused → swapping the host (Kestrel/IIS/Windows service) does not affect governance
capabilities.
