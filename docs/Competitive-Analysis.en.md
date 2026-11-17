# Competitive Analysis (English)

> Data collected **2026-11-18**. Stars / versions come from GitHub, NuGet and official project docs;
> third-party aggregators are used only as corroboration. This space moves fast — re-check links before deciding.

## 0. TL;DR

**Yes — there are plenty of new competitors, and the .NET gateway space clearly heated up in 2026.**
But one fact works in our favour:

> Almost every new contender targets **Dashboard-driven dynamic config, Kubernetes / microservice service discovery,
> and Linux/container deployment**. Almost none treats "**sitting behind IIS via ANCM, replacing nginx on Windows Server**"
> as a first-class goal.

That is the niche of `DotNet.Yarp.Reverse.Proxy.Gateway` — we don't compete on feature count; we own
"native Windows/IIS, drop-in, low-dependency and auditable".

Four to keep an eye on:

| Competitor | Why it matters |
| --- | --- |
| **Ocelot 25.x** | The most mature .NET gateway, 8.7k★, still shipping in Oct 2026, now with a built-in circuit breaker (no Polly needed) |
| **Aneiang.Yarp** | The most feature-complete YARP enhancement (Dashboard / WAF / AI assistant / discovery / config rollback), MIT, the closest direct competitor |
| **FastGateway** | New in 2026, .NET 10 native + ACME auto-certificates, growing — represents the "new players use .NET 10 as the wedge" play |
| **Stella Ops Router** | 2026 architectural novelty: services connect *out* to the gateway, no inbound ports, commercial licensing |

---

## 1. The landscape

### 1.1 Layer 1: libraries / toolkits (our dependency — and where competitors start)

| Project | Status | Licence | Notes |
| --- | --- | --- | --- |
| **YARP 2.3.0** (Microsoft) | Last stable 2025-02-27; 40M+ NuGet downloads | MIT | Routing / load balancing / health checks / forwarding only. Rate limiting, circuit breaking, CORS, auth, logging must be built yourself — **the reason this project exists**. github.com/dotnet/yarp |
| **YARP 3.0** | **preview** (main line VersionPrefix 3.0.0 / preview.1), repo still active | MIT | Not released yet. Watch item: if 3.0 absorbs governance features, our value proposition shrinks |
| **Ocelot 25.0.2** | 2026-10-15, 8.7k★, 1.7k forks, monthly releases aligned with .NET SDK patches | MIT | .NET 8 / 9 / 10; built-in "express" QoS circuit breaker (no Polly), WebSockets middleware + security options, multi `ocelot.*.json` merging, dynamic routing. ocelot.readthedocs.io |

### 1.2 Layer 2: "finished gateways" built on YARP (direct competitors, and where the new players cluster)

| Project | Born / active | Licence | TFMs | Highlights | Deployment focus |
| --- | --- | --- | --- | --- | --- |
| **Aneiang.Yarp** | v2.3.0.30 / 2026-09 | MIT | .NET 8 / 9 (**no .NET 10**) | 11 built-in plugins, Dashboard, WAF (SQLi/XSS/path traversal), AI assistant (40 tools), Consul/Nacos/Eureka/K8s discovery, config snapshots/rollback/audit, one-line service registration | Microservices / Dashboard |
| **FastGateway** | New in 2026, ~173★ (Nov 2026) | Apache-2.0 | **.NET 10 native** | Zero-restart dynamic routes/clusters, Let's Encrypt ACME (HTTP-01/DNS-01), SNI multi-cert, Dashboard, rate limit + IP allow/deny; FreeSql backend | Docker Compose / systemd, Linux-first |
| **Switchboard** | v5.2.3 / 2026-10, 8★ (very early) | MIT | .NET 8 / 10 | **Embeddable as a NuGet library** or standalone; 6 load-balancing modes, rate limits, callback auth, REST management API + React dashboard, OpenTelemetry, SQLite/MySQL/Postgres/SQL Server | Embedded / Docker; no IIS story stated |
| **Carp.Gateway** | Last commit 2025-12 | MIT | .NET 8 / 9 | K8s Ingress Controller, Consul discovery, IP allow/deny, retries, gRPC/WebSocket | Kubernetes-first, slowing down |
| **SKYNET Reversify** | 2026-02 | unspecified | .NET 8 | Kestrel on 80/443, SNI multi-cert, JSON hot reload, DDoS detection module | Small self-hosted |

### 1.3 Layer 3: architectural novelty (different race, but know it)

- **Stella Ops Router** (newest, 2026): services **connect out** to the gateway over TCP/TLS/UDP/RabbitMQ/Valkey —
  no inbound port, no load balancer entry; endpoints are declared with attributes in C# and carry claims.
  Licensing: free for non-production, free in production up to 500 endpoints per installation; beyond that or
  third-party hosting requires a commercial Order; client packages Apache-2.0, each version converts to
  Apache-2.0 four years after first publication. Has a Windows service deployment option.
  → Not a classic reverse proxy, so it won't take our nginx-replacement use case head-on, but it is the most
  different architectural idea in the field.

### 1.4 Layer 4: the incumbents we replace

- **nginx / nginx Plus**: powerful and ubiquitous, but painful on Windows (service management, config, certs,
  dynamic changes — see README §1). This is our reason to exist.
- **IIS ARR 3.0 + URL Rewrite**: Microsoft's native IIS reverse proxy / load balancer, Works with IIS 7–10,
  but the product itself has seen no meaningful update for years; configuration is through IIS Manager, with
  no modern API / governance. We don't "replace ARR" so much as give IIS users a modern, programmable option.
- **Envoy / Kong / Traefik / HAProxy**: heavyweight cross-platform players whose home turf is Linux / containers /
  service meshes. Windows Server + IIS is not their target — no direct competition.

---

## 2. Capability matrix

| Dimension | **This project** | Ocelot 25 | Aneiang.Yarp | FastGateway | Switchboard | IIS ARR | nginx (Win) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Delivery | **deployable zip, turnkey** | NuGet library | NuGet + Dashboard | finished app + console | embeddable lib + standalone | IIS extension | standalone exe |
| IIS / ANCM first-class | **✅ web.config + inprocess** | ❌ | ❌ | ❌ | ❌ | ✅ (it *is* an extension) | ⚠️ runs, not native |
| .NET 10 support | **✅ (6/8/10 multi-target)** | ✅ | ❌ | ✅ | ✅ | N/A | N/A |
| Minimum Windows Server | **2012 (IIS 8.0)** | depends on .NET | depends on .NET | Linux-first | depends on .NET | 2008+ | poor desktop UX |
| Governance out of the box (rate limit / breaker / CORS / auth / logging) | **✅ all self-built** | partial (breaker built in) | ✅✅ (plugins) | partial | partial | little | hand-written config |
| Dashboard / dynamic config | ❌ | ❌ | ✅ | ✅ | ✅ | IIS Manager | ❌ |
| ACME auto-certificates | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ |
| WAF | partial (IP filter) | ❌ | ✅ | IP allow/deny | ❌ | ❌ | ❌ |
| Service discovery | ❌ | ✅ (many providers) | ✅ (many providers) | ❌ | ❌ | ❌ | ❌ |
| NuGet dependencies | **2 (YARP + NewLife.Core)** | many | many | many | many | — | — |
| `nginx -t` equivalent precheck + migration guide | **✅ unique** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## 3. Our moat

1. **First-class IIS citizen**: `web.config` + ANCM inprocess + "No Managed Code" app pool — operators need zero
   .NET knowledge. None of the new contenders (Ocelot / Aneiang / FastGateway / Switchboard) do this.
2. **A complete path off nginx**: `--test-config` (equivalent to `nginx -t`) plus bilingual nginx→gateway migration
   docs lower the migration barrier for Windows admins — nobody else offers this.
3. **Minimal, auditable dependencies**: only 2 NuGet packages for the whole solution; all other governance is
   pure BCL. For government / enterprise / supply-chain review scenarios this is a real advantage that feature
   count can't buy.
4. **Legacy server coverage**: net6/8/10 multi-targeting + Windows Server 2012+ covers installed Windows estates.

## 4. Weaknesses and risks (honestly)

| Weakness | Impact | Priority |
| --- | --- | --- |
| No Dashboard / dynamic config reload | Table stakes for new competitors; visible ops-experience gap | **High** (start with NuGet-free `IConfiguration` reload + lightweight admin endpoint) |
| No WAF / service discovery / ACME | Lose microservice or public-edge comparisons to Aneiang / FastGateway | Medium (drive by demand; don't break the "two packages" rule for features) |
| Small community | Slow word-of-mouth and contributor growth | Medium (publishing this analysis is itself content marketing) |
| YARP 3.0 may absorb governance features | Dilutes our self-built-governance value | Watch (2.3.0 is still the latest stable — no short-term threat) |
| Ocelot 25 has built-in breaker and monthly releases | It is the default choice in a "pure .NET gateway" evaluation | Lean into IIS scenarios instead of fighting on features |

## 5. Recommended actions

- **Positioning**: keep the README first screen on "a turnkey gateway replacing nginx on Windows Server / IIS".
  Don't describe ourselves as "yet another .NET API gateway" — that's Ocelot's and Aneiang's battlefield.
- **Short term**: add this analysis to the docs (done); add one line to README §1 noting that existing .NET
  gateways are mostly libraries to integrate or K8s/container-oriented, with no IIS-native turnkey option.
- **Medium term**: evaluate configuration hot reload (`IOptionsMonitor` / `ChangeToken`, zero new dependencies) —
  the biggest experience gap versus new competitors, at manageable cost.
- **Long term**: track the YARP 3.0 stable release and the Stella Router pattern; if YARP officially ships
  governance middleware, re-evaluate our positioning.

---

## 6. Sources

- YARP: github.com/dotnet/yarp (releases, activity), www.nuget.org/packages/Yarp.ReverseProxy
- Ocelot: github.com/ThreeMammals/Ocelot, ocelot.readthedocs.io/en/25.0/releasenotes.html
- Aneiang.Yarp: github.com/AneiangSoft/Aneiang.Yarp, yarp.aneiang.com
- FastGateway: GitHub repo profile snapshots (reporank / kaiyuanbang, Nov 2026)
- Switchboard: github.com/jchristn/Switchboard, NuGet `SwitchboardApplicationProxy`
- Stella Ops Router: stella-ops.org/docs/modules/router/guides (incl. 2026-11-08 comparison and licensing pages)
- IIS ARR: iis.net/downloads/microsoft/application-request-routing
- .NET support matrix: learn.microsoft.com/dotnet/core/install/windows
