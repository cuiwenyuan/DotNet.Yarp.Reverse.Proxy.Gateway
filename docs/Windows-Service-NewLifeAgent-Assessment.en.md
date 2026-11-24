# Windows Service Deployment Assessment: NewLife.Agent vs IIS / ANCM

> Subject: hosting `DotNet.Yarp.Reverse.Proxy.Gateway` as a Windows Service (NewLife.Agent) versus
> the current IIS + ANCM deployment.
>
> Date: 2026-11-23 · Status: **S1 implemented (2026-11-24)** · Verdict: **feasible; `NewLife.Agent` adopted, dual hosts running**

> **Decision update (2026-11-23)**: the third NuGet package `NewLife.Agent` is approved, and the chosen shape is a
> **standalone host project** — a new `DotNet.Yarp.Reverse.Proxy.Gateway.Service` (Windows Service host) plus a
> `DotNet.Yarp.Reverse.Proxy.Gateway.Hosting` (shared pipeline assembly), while `Gateway.Core`'s dependency
> constraint is unchanged. Option A in section 3 ("add the package to `Web` + three-way entry split") is therefore
> **superseded** by "A': standalone host + shared Hosting". Timing, task breakdown and acceptance criteria live in
> **[Roadmap](Roadmap.en.md)** — **S1 landed on 2026-11-24 and passed dual-host smoke testing**; deployment steps
> are in the **[Windows Service Deployment Guide](Windows-Service-Deployment.en.md)**.
> Sections 3/5/6 below keep the original analysis (including its original shape
> assumption) for traceability; the conclusions and the trap list remain valid.

---

## 0. Executive summary

| Question | Answer |
| --- | --- |
| Is it technically feasible? | **Yes.** Proven by a PoC: `WebApplication` started inside `ServiceBase.StartWork`, Kestrel listening normally, and the three entry paths (CLI / service / direct run) do not interfere. |
| Code reuse? | **~96%**. The `Core` project (30 files, 5,097 lines) needs **zero changes**; edits are confined to the host-assembly section of `Program.cs` (~110 lines of mechanical refactoring) plus a new host shell (~150 lines). |
| Implementation difficulty? | **Low–medium**. Host integration is a half-day job; the real work is that **the deployment topology changes** (own TLS termination, own port 80, service account privileges, connection draining on stop). |
| Advantages over IIS? | One less hop in the request path, process-level self-healing (memory/thread/handle thresholds + scheduled restart + watchdog), native install/uninstall/start/stop commands, no IIS role, no `web.config`/app-pool recycling concepts, more predictable gRPC/WebSocket pass-through. |
| Disadvantages vs IIS? | **You own TLS certificate management** (IIS gives SNI/CCS/renewal-friendly tooling for free), port 80 conflicts with IIS (**mutually exclusive**), the default service account is `LocalSystem` (too privileged, must change), no GUI ops console, and the IIS diagnostics ecosystem (Failed Request Tracing, W3C logs) is lost. |
| Biggest obstacle? | **The NuGet package count goes from 2 to 3**, breaking the project rule of "only Yarp.ReverseProxy + NewLife.Core". This is the only real decision point; everything else is engineering detail. **→ Decided on 2026-11-23: approved (see the decision update above)** |

**One-line recommendation**: make the Windows Service a **second, optional hosting form** that coexists with IIS rather than replacing it. There is no technical blocker, and the package question is settled; proceed through S1→S3 in the [Roadmap](Roadmap.en.md) — step 1 alone delivers a usable service.

---

## 1. Scope and method

This assessment answers four questions only: **can it be done, how hard is it, how much can be reused, and how does it compare with IIS**. No production code is changed.

Three layers of evidence, to avoid an "architecture-on-paper" result:

1. **Package inspection** — downloaded the `NewLife.Agent` 10.18.2026.1024 `.nupkg`, parsed its `nuspec` dependency graph and API docs (`NewLife.Agent.xml`) for real types, members and settings.
2. **Hands-on PoC** — a throwaway project (`Sdk=Microsoft.NET.Sdk.Web` + `NewLife.Agent`, three target frameworks) hosting a real `WebApplication` on `ServiceBase`, validating commands, ports, working directory and exit codes.
3. **Baseline comparison** — judged every file under `src/` as "change / no change" against the current code, which is how the reuse figure was derived.

> **Verification boundary**: the assessment machine runs as a standard user (not administrator) with IIS absent, so "service install → SCM start → working directory under the service account" was **not** verified on a real machine. Such items are marked `[needs on-machine verification]`. Everything else is backed by real output.

---

## 2. Factual baseline

### 2.1 Package and dependencies

| Item | Fact | Implication |
| --- | --- | --- |
| Id / version | `NewLife.Agent` **10.18.2026.1024** | Same versioning rhythm as the project (`year.MDD`) |
| Dependencies | **only** `NewLife.Core` **12.0.2026.1024** | **Exactly the version already in use** → no upgrade/downgrade drift |
| Target frameworks | `net40;net45;net461;netstandard2.0;2.1;net6.0;net7.0;net8.0;net9.0;net10.0` | Covers `net6.0;net8.0;net10.0` with no conditional compilation |
| License | MIT | No commercial constraint |
| Transitive packages | 0 (besides NewLife.Core) | No extra supply-chain surface |

Actual resolution in the PoC (`net10.0`):

```
Top-level     NewLife.Agent  10.18.2026.1024
Transitive    NewLife.Core   12.0.2026.1024     ← same as the main project
```

**Key point**: this is the lowest-friction way to add a package — one extra ~1 MB managed assembly, and **your carefully pinned NewLife.Core version does not move**. Adding the `PackageReference` only to the `Web` project keeps `Core` at "YARP + NewLife.Core", so the governance kernel stays portable.

### 2.2 Built-in commands (CLI conflict check)

`-i` / `-install` / `-u` / `-uninstall` / `-reinstall` / `-start` / `-stop` / `-restart` / `-status` / `-run` / `-s` / `-watch`.

Existing gateway switches: `--test-config` / `-t`, `--diff-config`, `--convert-nginx`, `-o`, `--contentRoot`, `--configPath`.

**The intersection is empty.** Still, the entry point must short-circuit CLI modes *before* handing control to `ServiceBase.Main` (see 7.5).

### 2.3 Defaults generated on first run (must know)

Three files appear under `Config/`: `Agent.config`, `Core.config`, `Socket.config`. The ones that matter:

```xml
<EnableWebPanel>true</EnableWebPanel>   <!-- management panel ON by default -->
<WebPort>5580</WebPort>                 <!-- extra listening port -->
<WebPassword>admin</WebPassword>        <!-- default credential -->
<WebBind>127.0.0.1</WebBind>            <!-- loopback only -->
```

**These defaults must not ship to production as-is** (see 7.3).

---

## 3. Three candidate options

### Option A — `NewLife.Agent` + a thin hand-written host shell (recommended)

One `PackageReference` in `Web`; a new `GatewayService : ServiceBase` wrapping the existing `WebApplication`: build it in `StartWork` and call `RunAsync()` without blocking, then `StopAsync(timeout)` in `StopWork`.

- New packages: **1** · New code: **~150 lines**
- Capabilities: install/uninstall/start/stop/reinstall/status, health monitoring (`MaxMemory`/`MaxThread`/`MaxHandle`), scheduled restart (`AutoRestart` + `RestartTimeRange`), watchdog, web panel, multi-instance via distinct `ServiceName`
- Does **not** need `NewLife.Extensions.Hosting.AgentService` — the PoC shows ~60 lines of wrapping is enough, so no fourth package

### Option B — `NewLife.Extensions.Hosting.AgentService` (not recommended)

`UseAgentService()` on the host builder; the package supplies `IHostLifetime` bridging.

- New packages: **2** · New code: **~80 lines**
- Why not: the project uses `WebApplication`, not a bare `HostBuilder`. Bridging lifetimes is exactly the part Option A already solves in 40 lines. An extra package with extra version coupling for a small gain is a bad trade.

### Option C — hand-rolled SCM shell (keeps the two-package rule)

P/Invoke straight to the Service Control Manager — `StartServiceCtrlDispatcherW`, `RegisterServiceCtrlHandlerExW`, `SetServiceStatus` — with install/uninstall delegated to the built-in `sc.exe`.

- New packages: **0** · New code: **~300–400 lines** plus tests
- Pros: the rule holds; consistent with `Core`'s zero-dependency style; fully transparent behaviour
- Cons: you implement the `SERVICE_STATUS` state machine (`START_PENDING`/`RUNNING`/`STOP_PENDING`/`STOPPED`), control codes (`Stop`/`Shutdown`/`PowerEvent`/`SessionChange`), and SCM timeouts (30 s default before it declares a start failure); no watchdog, no health monitoring, no panel; **harder to debug** because service mode cannot be attached to a debugger

### Side-by-side

| Dimension | A | B | C |
| --- | --- | --- | --- |
| New NuGet packages | 1 | 2 | **0** |
| New code | ~150 lines | ~80 lines | ~300–400 lines |
| Business-code changes | host layer only | host layer only | host layer only |
| Install/uninstall commands | built in | same as A | manual `sc.exe` |
| Self-healing | **built in** | same as A | build it yourself |
| Process management panel | built in | same as A | none |
| Cross-platform | Win/Linux/macOS, one codebase | same as A | Windows only |
| Supply-chain surface | +1 managed assembly (MIT) | +2 | 0 |
| Overall | ★★★★☆ (pending package decision) | ★★☆☆☆ | ★★★☆☆ (pick this if the rule is sacred) |

---

## 4. Comparison with IIS / ANCM

### 4.1 Matrix

| Dimension | IIS + ANCM (today) | Windows Service + Agent | Winner |
| --- | --- | --- | --- |
| Request path | client → http.sys (kernel) → ANCM → w3wp | client → Kestrel (user-mode socket) | Service has one hop less, but loses http.sys kernel caching / port sharing |
| TLS termination | **IIS**: bindings, SNI, Centralized Certificate Store, renewal-friendly | **Your job**: Kestrel with a pfx or a certificate-store lookup (`Subject` + `Store`, avoids plaintext passwords) | **IIS, clearly** |
| Ports 80/443 | IIS owns them | Service owns them | **Mutually exclusive** |
| Process model | one `w3wp.exe` per app pool | one process per service | Tie |
| Idle shutdown | app-pool `idleTimeout` (20 min default; must be disabled for a gateway) | no idle recycling, always resident | Service fits "a gateway must stay up" better |
| Periodic recycling | app-pool scheduled recycle (drops connections) | none forced; optional `AutoRestart` with a time window | Service is more controllable |
| Crash recovery | Rapid-Fail Protection (blunt: stops after repeated crashes) | SCM recovery actions + Agent health thresholds + watchdog | **Service, finer-grained** |
| Resource-threshold recovery | none | `MaxMemory`/`MaxThread`/`MaxHandle` trigger restart | **Service only** |
| Installation | add IIS role + site/app pool + ACLs | `xxx.exe -i` | **Service, much lighter** |
| Prerequisites | IIS role, ANCM v2, Hosting Bundle | .NET runtime only | **Service** |
| GUI operations | IIS Manager (mature) | `services.msc` + lightweight panel | IIS |
| Diagnostics ecosystem | W3C logs, Failed Request Tracing, ETW | XTrace file logs + the gateway's own `/metrics` and access log | IIS richer; the project already covers the essentials |
| Config hot reload | existing mechanism works | existing mechanism works (service account needs read access) | Tie |
| Upgrade | stop site → copy → start site | stop service → copy → start (`dll` is locked, so stopping first is mandatory) | Tie, service is stricter about file locks |
| Multiple instances | multiple sites | multiple services (distinct `ServiceName`, own directory/port) | Tie |
| Service account | app-pool virtual account (low privilege, safe default) | `LocalSystem` by default (**must change**) | **IIS default is safer** |
| Real client IP | via ANCM + `UseForwardedHeaders` (already implemented) | direct, `RemoteIpAddress` is the client | **Service, simpler** |
| WebSocket / gRPC | needs the IIS WebSocket feature; gRPC constrained by http.sys | native in Kestrel (HTTP/1.1, HTTP/2, WebSocket) | **Service** |
| Container migration | IIS cannot be containerised | same code becomes a Linux systemd/container entry point | **Service, more future-proof** |

### 4.2 The items that matter

**① TLS is the biggest gap.** Under IIS a certificate is a site property with SNI, CCS and ACME-friendly renewal. Under a service it is application configuration, which forces three answers: where the certificate lives (local store / pfx file / shared folder), how the private-key password is protected (environment variable / service account credential / key-less store lookup), and how renewal takes effect (Kestrel restarts, or certificate hot-swap). **Treat this as its own work item**, not part of "swap the host".

**② Port 80 is a topology question, not a technical one.** Three workable topologies:
- (a) the service owns 80/443 and IIS sites move elsewhere → the gateway genuinely replaces nginx;
- (b) the service runs on a high port behind IIS/ARR → pointless (extra hop, still depends on IIS);
- (c) the service owns 80 (HTTP) while 443 terminates on an upstream LB/NLB → certificates move out, good for environments with an F5/SLB.

**③ The default `LocalSystem` account is unacceptable.** A public-facing reverse proxy running as `LocalSystem` turns any RCE into full machine compromise. Use `NetworkService` or a dedicated low-privilege account (Agent's `ServiceModel.User` supports this), then grant: read on the config directory, write on the log directory, read on the certificate private key.

**④ Graceful shutdown becomes mandatory, not optional.** If `StopWork` only allows 5 seconds, in-flight large downloads, SSE and WebSocket sessions are cut hard. App-pool recycling has the same problem under IIS, but with a service `-restart`/`-stop` is used far more often. Make the drain window configurable (start at 30 s) and align it with Kestrel's `HostOptions.ShutdownTimeout`.

**⑤ Some IIS capabilities are genuinely lost**: Failed Request Tracing, W3C IIS logs (the project's own nginx-style access log covers this), IIS URL Rewrite / request filtering (equivalents already exist in-house), and the "one IIS manages many sites" console. Irrelevant for a dedicated gateway, relevant if the box hosts a dozen sites.

---

## 5. Difficulty breakdown

### 5.1 Work items

| # | Item | Files | Difficulty | Risk | Effort |
| --- | --- | --- | --- | --- | --- |
| 1 | Extract `GatewayHost.Build(args)` (move `Program.cs` lines 146–256 into a static method; pass an empty `args`) | `Program.cs` | Low | Low | 0.5 h |
| 2 | Add `GatewayService : ServiceBase` (metadata, `StartWork`/`StopWork`, exception guard) | new `GatewayService.cs` | Low | Low | 2 h |
| 3 | Entry split: CLI modes → unchanged; Agent commands → `ServiceBase.Main`; **no args → run the web app directly (keep the existing contract)** | `Program.cs` | Medium | **Medium** (7.2) | 2 h |
| 4 | Point `ContentRootPath` at `AppContext.BaseDirectory` | `Program.cs` | Low | **High** (7.1) | 0.5 h |
| 5 | Disable the Agent web panel (or force a password and keep loopback binding) | `Config/Agent.config` | Low | **High** (7.3) | 0.5 h |
| 6 | Kestrel endpoints and TLS (`Kestrel:Endpoints`, certificate-store lookup to avoid plaintext passwords) | `appsettings.json` + docs | Medium | Medium | 4 h |
| 7 | Graceful shutdown: configurable drain window aligned with `ShutdownTimeout` | `GatewayService.cs` + options | Medium | Medium | 3 h |
| 8 | Service account and directory ACLs | deployment script | Medium | Medium | 3 h |
| 9 | Publish artifact and install script (`publish` → `-i` → `-start` → smoke) | `scripts/` + CI | Low | Low | 3 h |
| 10 | Docs: `docs/Windows-Service-Deployment.{zh-CN,en}.md` alongside the IIS guide | `docs/` + README | Low | Low | 4 h |
| 11 | Regression: prove the IIS deployment path still works (dual hosting) | tests + smoke | Medium | Medium | 3 h |

**Total ≈ 2.5 person-days** excluding the TLS certificate workflow and load testing.

### 5.2 Verdict on difficulty

- **Host integration itself is easy** — the PoC went from zero to running in about 20 minutes, including one compile error (`StartWork`/`StopWork` are `public`, not `protected` — an easy trap).
- **The difficulty is deployment semantics** — TLS, ports, accounts, draining: four things IIS used to do for you. Not hard to code, easy to miss.
- **No architectural blockers**: `Core` untouched, middleware order untouched, YARP config model untouched.

---

## 6. Code reuse, quantified

| Project / file | Files | Lines | Changes for service hosting |
| --- | --- | --- | --- |
| `Gateway.Core` (middleware, rate limiting, circuit breaker, cache, retry, hot reload, metrics, nginx converter, diff) | 30 | **5,097** | **0** |
| `Gateway.Web/ConfigValidator.cs` | 1 | 230 | **0** |
| `Gateway.Web/WeightedLoadBalancingPolicy.cs` | 1 | 71 | **0** |
| `Gateway.Web/Program.cs` — CLI modes | 1 | ~135 | **0** (needs only a guard before the Agent call) |
| `Gateway.Web/Program.cs` — pipeline assembly | 1 | ~111 | **mechanical refactor** (move into a method) |
| New host shell | 1–2 | ~150 | new |
| **Total** | **34–35** | **~5,654** | **~200 lines touched** |

**Reuse ≈ 96.5%** (about 200 of 5,654 lines are new or rewritten).

Service hosting only changes *who calls `app.Run()`*, never *what is inside `app`*. Everything below keeps working verbatim — confirmed in the PoC that the pipeline builds and listens:

- the full middleware chain (`ProxyLogging → Retry → AspxBlocker → IpFilter → RequestSize → CORS → RateLimit → ConcurrencyLimit → CircuitBreaker → ApiKey/JWT → Cache → LocationRewrite → FileServer → YARP`);
- config hot reload (`GatewayHotReload`: NewLife.Core watcher + 40 s polling fallback + SHA-256 fingerprint de-duplication + validation gate);
- `/metrics` (Prometheus text) and `/admin/status` (governance snapshot);
- `--test-config` / `--diff-config` / `--convert-nginx`;
- the three target frameworks.

It is **additive, not a replacement**: one publish output can be hosted by IIS via `web.config` *and* installed as a service via `-i`. The deployment owner picks one. That means **this change does not force existing IIS users to migrate** — the risk stays contained.

---

## 7. PoC findings (results and traps)

PoC host: `net10.0` (all three TFMs build, 0 errors, only the 2 expected `net6.0` EOL warnings), throwaway directory, main repository untouched.

### 7.1 [High] Under a service, `CurrentDirectory` is not the app directory

A Windows service starts with `C:\Windows\System32` as its working directory. ASP.NET Core defaults ContentRoot to `CurrentDirectory` → **`appsettings.json` is not found → `ReverseProxy` is empty → every route returns 404**. Same class of bug the project already hit when launching `dotnet xxx.dll` from a different directory.

One-line fix:

```csharp
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<String>(),
    ContentRootPath = AppContext.BaseDirectory,   // must be explicit
});
```

`[needs on-machine verification]` for the exact value under a real service account, but setting it explicitly is correct in every case.

### 7.2 [High] Running with no arguments opens an interactive menu

Actual output of `dotnet PoC.dll`:

```
Please select an operation
 1  show status   -status
 2  install       -i
 5  simulate run  -run
 0  exit
Enter the operation number:
```

And with redirected/no console input (CI, Task Scheduler, `> log.txt`) it throws an unhandled exception:

```
System.InvalidOperationException: Cannot read keys when either application does not have a console
or when console input has been redirected.
   at NewLife.Agent.ServiceBase.ReadMenuKey()
   at NewLife.Agent.ServiceBase.ProcessMenu(String[] args)
```

The current contract is "`dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll` = start the gateway" (used by the IIS guide, ops habits and smoke scripts). Going straight to Agent would **silently turn that into "waiting for a keypress"** — a serious behavioural break.

Fix (verified in the PoC) — split the entry three ways:

```csharp
if (args.Length == 0 || !args[0].StartsWith('-'))
{
    GatewayHost.Run();             // compatibility path: run directly, no menu
    return;
}
new GatewayService().Main(args);   // Agent commands: -i/-u/-start/-stop/-status/-run/-s
```

Measured output of the compatibility path: `Now listening on: http://127.0.0.1:18080`, with `/probe` reporting `contentRoot` pointing at the app directory.

### 7.3 [High] The web panel is on by default, with `admin/admin`, on port 5580

```
Http starting listener tcp://127.0.0.1:5580
[WebPanel] web management panel started on port 5580
```

Three consequences: (1) **security** — a default credential exists (the panel forces a change on loopback at first start, but "default password" is an audit red line); (2) **port** — an extra 5580 listener must enter firewall and scanning baselines; (3) **duplicate control planes** — the gateway already exposes `/admin/status` and `/metrics`, so "which console is authoritative" becomes murky.

Recommendation: **default `EnableWebPanel=false`**, document it as opt-in; if enabled, force a password and keep `WebBind=127.0.0.1`.

### 7.4 [Medium] `-run` requires an interactive terminal

`-run` calls `Console.ReadKey()` after starting and throws when input is redirected (same root cause as 7.2). **Do not use `-run` in CI smoke tests**; use the compatibility path plus `curl`, or install the service and check `-status`.

### 7.5 [Low] CLI switches could be swallowed by the command factory

The command factory discovers commands by reflection. `-t` (short form of `--test-config`) and `-o` (converter output) do not collide with built-in commands today, but the CLI branches **must** short-circuit before `ServiceBase.Main`. `Program.cs` already has the right order (all three CLI branches `return` before `CreateBuilder`); keep it that way.

### 7.6 [Low] Three extra `Config/` files appear in the publish directory

`Agent.config` (service metadata + health thresholds + panel), `Core.config`, `Socket.config`. They are **a second configuration system** alongside `appsettings.json`. Operators must know: service behaviour → `Config/Agent.config`, gateway behaviour → `appsettings.json`. To reduce confusion, hard-code `ServiceName`/`DisplayName` in C# (Agent writes code values into the config on first run — the PoC shows `ServiceName=YarpGatewayPoC` persisted) and only touch this file for health thresholds.

### 7.7 [Low] `-status` output is mojibake on non-UTF-8 consoles

Observed when piping output (GBK vs UTF-8 mismatch). Cosmetic only; document `chcp 65001` or point users at the log file.

### 7.8 [Low] `StartWork`/`StopWork` overrides must be `public`

The base members are `public`; `protected override` fails with `CS0507`. Even the official README example is unclear on this and it will trip up the first integration attempt.

---

## 8. Performance (no unsupported claims)

Service and IIS modes run **the same ASP.NET Core code, the same middleware, the same YARP**. Only the outermost access layer differs:

| Path | Structure |
| --- | --- |
| IIS in-process | client → http.sys (kernel) → ANCM → w3wp (ASP.NET Core in-process) |
| Service | client → Kestrel (user-mode socket) → ASP.NET Core |

In theory the service path removes one kernel↔user transition and the IIS module pipeline, **but the magnitude depends heavily on request size, connection reuse and where TLS terminates — an architecture diagram is not evidence**.

So this assessment **gives no numbers**, only an acceptance method:

1. Same `appsettings.json` (identical routes/clusters/limits), one run under IIS and one as a service;
2. Use `bombardier` (single Windows binary) or `wrk` (WSL);
3. Measure RPS, p50/p99 latency, CPU%, working set — 3 minutes × 3 repetitions, take the median;
4. Focus on two scenarios: small high-frequency requests (≤4 KB, access-layer overhead) and large transfers (10 MB+, zero-copy and back-pressure behaviour).

This belongs to the pre-existing out-of-scope item "real-environment soak/load testing" and should be done during implementation, since the access layer changes and earlier results no longer transfer.

---

## 9. Risks and mitigations

| # | Risk | Level | Mitigation |
| --- | --- | --- | --- |
| 1 | Breaks the "only two NuGet packages" rule | **High (decision)** | Section 11; Option C avoids it entirely |
| 2 | Wrong ContentRoot under a service → all 404 | High | Explicit `ContentRootPath` (7.1) + log the effective config path at startup |
| 3 | No-arg run opens a menu, breaking ops scripts | High | Three-way entry split (7.2) + regression test "no args → listening" |
| 4 | Default `admin/admin` panel on 5580 | High | Panel off by default (7.3); force password + loopback when enabled |
| 5 | `LocalSystem` privileges | High | Specify a low-privilege account at install; ship the ACL script (5.1 #8) |
| 6 | Fighting IIS over port 80 | Medium | Decide topology first (4.2 ②); installer detects port 80 and fails loudly |
| 7 | Certificate renewal does not take effect | Medium | Prefer certificate-store lookup over pfx; document the restart policy |
| 8 | Hard-cutting long-lived connections on stop | Medium | Configurable drain window (5.1 #7), 30 s default |
| 9 | `-run` crashes in CI | Low | Smoke via the compatibility path (7.4) |
| 10 | Two config systems confuse operators | Low | Hard-code metadata; document the split (7.6) |
| 11 | Breaks the existing IIS deployment | Medium | Dual hosting (6.3) + regression (5.1 #11) |

---

## 10. Recommended path

If approved, four independent, individually reversible steps:

| Stage | Content | Deliverable | Blocked by |
| --- | --- | --- | --- |
| **S0 Decision** | Allow the third package (`NewLife.Agent`) or switch to Option C | Decision record | — |
| **S1 Host works** | Items #1–#5: extract `GatewayHost`, add `GatewayService`, split the entry, ContentRoot, panel off. **At this point `-i` installs a service that runs every existing feature** | Installable Windows service (HTTP, high port) | S0 |
| **S2 Production-ready** | Items #6–#10: TLS endpoints, graceful shutdown, service account + ACLs, install scripts, docs; smoke covers both "service" and "IIS" modes | Deployable production option | S1 |
| **S3 Validation** | Item #11 + section 8 load tests + real SCM lifecycle (install/stop/restart/uninstall/power-loss recovery) | Assessment upgraded from "assessed" to "verified" | S2 |

**If you decide not to proceed** (perfectly reasonable), keep this document as the decision record and add one line to `docs/IIS-Deployment.*.md`: "to run as a Windows Service, see this assessment" — so it does not have to be re-argued in six months.

---

## 11. Open decisions

1. ~~**Is a third NuGet package allowed?**~~ **✅ Decided (2026-11-23): yes**, as a standalone host project (`Service` + `Hosting`, with `Core` still at two packages). The original three-way choice is closed:
   - ~~Yes → Option A~~ → now Option A' (standalone host + shared Hosting), see the [Roadmap](Roadmap.en.md)
   - ~~No → Option C~~ → no longer needed (recorded for the record: a hand-rolled P/Invoke SCM shell would be ~4–5 person-days)
   - ~~Compromise → package on `Web` only~~ → superseded by the standalone host project

2. **Which topology?** Service owns 80/443 (true nginx replacement) / high port behind a load balancer / service owns 80 with TLS offloaded upstream (4.2 ②).

3. **Where do certificates live?** Local certificate store (no password, recommended) / pfx + environment-variable password / shared-folder CCS style.

4. **Which service account?** `NetworkService` (sufficient, easy) / dedicated low-privilege domain account (tighter, needs ops involvement).

5. **Keep the Agent web panel?** Recommended off by default; if process-level management is wanted, force a password and manage port 5580.

---

## Appendix A — PoC essentials (reproducible)

```csharp
// PoC.csproj: Sdk=Microsoft.NET.Sdk.Web, TargetFrameworks=net6.0;net8.0;net10.0
// PackageReference: NewLife.Agent 10.18.2026.1024

public static class GatewayHost
{
    public static WebApplication Build(String[] args) => WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,                                // empty array: keep -s/-run out of ASP.NET Core config
        ContentRootPath = AppContext.BaseDirectory, // under a service, CurrentDirectory is System32
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
    public override void StartWork(String reason)      // must be public
    {
        _app = GatewayHost.Build(Array.Empty<String>());
        _ = _app.RunAsync();                            // non-blocking; let Agent run health checks
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

## Appendix B — Service operations

```powershell
# Install and start (administrator)
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -install

# Status / stop / start / restart / reinstall / uninstall
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -status
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -stop
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -start
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -restart
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -reinstall
.\DotNet.Yarp.Reverse.Proxy.Gateway.Web.exe -uninstall

# Compatibility path (keeps the "no args = run the gateway" contract; usable in automated smoke tests)
dotnet DotNet.Yarp.Reverse.Proxy.Gateway.Web.dll

# System-level safety net
sc.exe qc YarpGateway
sc.exe failure YarpGateway reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

---

> See also: [IIS Deployment Guide](IIS-Deployment.en.md) · [System Architecture](Architecture.en.md) · [nginx → Gateway Migration Guide](Nginx-to-Gateway-Migration.en.md)
