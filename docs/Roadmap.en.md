# Roadmap and Backlog

> This is a **trigger-based backlog**, not a delivery commitment. Every item has an explicit trigger — if it is not triggered, it is not built. That keeps complexity from arriving early just to look busy.
>
> Status: `Approved` (decided, waiting for trigger) · `Under evaluation` (idea only) · `Rejected` (declined, reason kept)

---

## 1. Approved · waiting for trigger: Windows Service host (NewLife.Agent)

### 1.1 Decision record

| Item | Content |
| --- | --- |
| Date | 2026-11-23 |
| Decision | **Allow a third NuGet package, `NewLife.Agent`**, for Windows Service deployment |
| Shape | **A standalone host project**; `Gateway.Web` is not modified, and `Gateway.Core`'s dependency constraint is unchanged |
| Timing | **Not implemented now** — wait for a real deployment need (see 1.3) |
| Basis | [Windows Service Deployment Assessment](Windows-Service-NewLifeAgent-Assessment.en.md) (with PoC evidence) |

### 1.2 Target shape

Key trade-off: **the Windows Service host gets its own project instead of a branch inside `Gateway.Web`**, so the two hosts do not contaminate each other — the IIS artifact should not carry `NewLife.Agent.dll`, and the service artifact should not carry `web.config`.

```
src/
├── DotNet.Yarp.Reverse.Proxy.Gateway.Core        # governance middleware (only 2 packages: NewLife.Core)
│                                                  no YARP dependency, stays portable
├── DotNet.Yarp.Reverse.Proxy.Gateway.Hosting     # [new] pipeline assembly + ConfigValidator + WeightedLoadBalancingPolicy
│                                                  depends on Core + Yarp.ReverseProxy (total package count unchanged)
├── DotNet.Yarp.Reverse.Proxy.Gateway.Web         # IIS/ANCM host (references Hosting)
└── DotNet.Yarp.Reverse.Proxy.Gateway.Service     # [new, standalone folder] Windows Service host
                                                   references Hosting + NewLife.Agent (the only third package)
```

**Why the extra `Hosting` layer**: the pipeline order currently lives in `Web/Program.cs`, and `ConfigValidator` plus `WeightedLoadBalancingPolicy` also sit under `Web` — but all three are *application* concerns, not *host* concerns. Both hosts need them, so extracting them is what makes "one pipeline, two hostings" possible; otherwise the two middleware orders will drift apart sooner or later.

Cost: `Gateway.Hosting` will depend on YARP while `Core` still will not — **the package count stays at 2** (`Yarp.ReverseProxy` + `NewLife.Core`), merely redistributed across projects; the third package appears only in `Service`. The substance of the rule (no third-party governance libraries) is untouched.

### 1.3 Triggers (any one is enough to start)

1. A deployment target where **IIS cannot be installed** (minimal OS, locked-down server, container, cloud host).
2. Need for **process-level self-healing**: memory/thread/handle threshold restarts, scheduled restart windows, watchdog — an IIS app pool can only bluntly stop after repeated crashes.
3. Need to move the gateway to **Linux systemd** or make it a container entry point (the same `ServiceBase` code is cross-platform).
4. The operational friction of IIS sites and the gateway **fighting over port 80** becomes unacceptable and the gateway should own the entry point.
5. Need `WebSocket`/`gRPC` but the environment cannot install the IIS WebSocket feature.

> Conversely: if the target already has IIS and only needs one reverse-proxy site, **staying on IIS is less work** — TLS, certificates, logging and the GUI are free. Do not do this "because the architecture looks cleaner".

### 1.4 Phased tasks

| Stage | Tasks | Acceptance |
| --- | --- | --- |
| **S1 host works** | ① Create `Gateway.Hosting`; move the pipeline section of `Program.cs`, `ConfigValidator`, `WeightedLoadBalancingPolicy` into it ② Switch `Gateway.Web` to consume `Hosting` (behaviour unchanged, regression passes) ③ Create `Gateway.Service`: `GatewayService : ServiceBase`, three-way entry split, `ContentRootPath=AppContext.BaseDirectory`, Agent web panel off | After `-install`: service starts, proxies, hot-reloads; `-status` reports running; the IIS deployment path still passes regression |
| **S2 production-ready** | ① Kestrel endpoints and TLS (prefer certificate-store `Subject`+`Store` over plaintext passwords) ② Configurable graceful-shutdown drain window (30 s default, aligned with `HostOptions.ShutdownTimeout`) ③ Low-privilege service account + directory ACL script ④ Install/uninstall scripts, publish artifact `gateway-service-<tfm>-v<version>.zip` ⑤ Docs `docs/Windows-Service-Deployment.{zh-CN,en}.md` + README sync | A full install → proxy → hot reload → stop → uninstall cycle on the target machine; `sc.exe failure` recovery actions verified |
| **S3 validation** | ① Real SCM lifecycle (including power-loss/reboot recovery) ② Same-config IIS vs Service load test (small high-frequency + large transfers; RPS, p50/p99, CPU, working set) ③ Long-lived connection drain test | Load-test results written into the assessment; status upgraded from "assessed" to "verified" |

**Estimate**: S1 ≈ 1–1.5 person-days (half a day more than the original estimate, because of the `Hosting` extraction), S2 ≈ 2 person-days, S3 depends on the load-test environment.

### 1.5 Four traps to read before implementing

From PoC measurements; see [assessment section 7](Windows-Service-NewLifeAgent-Assessment.en.md):

1. Under a service, `CurrentDirectory` is `C:\Windows\System32` → set `ContentRootPath=AppContext.BaseDirectory` explicitly, or every route 404s.
2. Running with no arguments opens the Agent menu (and throws with no TTY) → split the entry three ways to keep the "`dotnet xxx.dll` starts the gateway" contract.
3. The Agent web panel is on by default with `admin/admin` on port 5580 → production must set `EnableWebPanel=false`.
4. `StartWork`/`StopWork` overrides must be `public` (`protected` fails with CS0507).

### 1.6 Explicitly out of scope

- No changes to `Core` middleware logic, the YARP configuration model, or existing CLI switch semantics.
- No configuration UI or graphical installer for service mode.
- Does not replace IIS deployment — both coexist, the deployer chooses.

---

## 2. Under evaluation

| Item | Value | Prerequisite |
| --- | --- | --- |
| Windows Event Log output | Ops habit: errors land in Event Viewer instead of a file | Needs either `Microsoft.Extensions.Logging.EventLog` or an `EventLog.WriteEntry` P/Invoke; both conflict with the "few packages" stance and need a trade-off |
| Lightweight WAF (rule set) | Baseline blocking of SQL injection / XSS / path traversal | Rule source and false-positive rate must be validated first; in-house cost is high |
| Read-only dashboard page | Make `/admin/status` + `/metrics` human-readable | Overlaps with the Agent panel; decide "for whom" first |
| Dedicated gRPC / WebSocket validation | Today it is "should pass through", with no test proving it | Needs a backend stub and a client load script |

## 3. Done (archive for traceability)

| Item | Date | Note |
| --- | --- | --- |
| Full ingress governance middleware set | 2026-08 | Rate limit, CORS, auth, cache, circuit breaker, retry, IP filter, JWT |
| Config hot reload + validation gate | 2026-11-20 | NewLife.Core watcher + fingerprint de-dup + rollback on invalid config |
| Governance metrics in `/metrics` | 2026-11-23 | Prometheus text + `governance` node |
| Test gate (22 assertions) | 2026-11-23 | Zero-NuGet console runner, wired into CI |
| nginx converter, tier 1 | 2026-11-23 | include/map/rewrite/multi-server + `--diff-config` |
| Architecture diagrams (PNG + collapsed Mermaid) | 2026-11-23 | Static images that do not depend on a Mermaid renderer |
| Windows Service assessment | 2026-11-23 | See section 1 above |

---

> See also: [Windows Service Deployment Assessment](Windows-Service-NewLifeAgent-Assessment.en.md) · [System Architecture](Architecture.en.md) · [IIS Deployment Guide](IIS-Deployment.en.md)
