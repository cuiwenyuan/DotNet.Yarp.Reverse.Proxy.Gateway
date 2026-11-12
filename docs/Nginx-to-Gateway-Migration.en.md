# nginx → Gateway Migration Guide

> This guide is based on a **real production nginx.conf** and shows the full equivalent
> configuration for the same forwarding semantics in this gateway
> (`DotNet.Yarp.Reverse.Proxy.Gateway`). All real hostnames, domains, and internal addresses have
> been sanitized (see the appendix). Ports are kept as-is for easier comparison.
>
> Companions: [README](../README.en.md) · [System Architecture](Architecture.en.md) ·
> [IIS Deployment Guide](IIS-Deployment.en.md) ·
> **中文版： [nginx → 网关 迁移对照文档](Nginx-to-Gateway-Migration.md)**

---

## 1. Sanitized summary of the source config

Key facts of the source nginx site (sanitized):

- **Site**: `gw.example.com`, listening on both HTTP `8080` and HTTPS `443` (same domain, same set
  of forwarding rules).
- **Static root**: `location / { root html; index index.html index.htm; }`.
- **Four prefix proxies** (`proxy_pass` with a trailing `/`, i.e. **prefix stripping** semantics):

| location prefix | Backend | Path after forwarding |
| --- | --- | --- |
| `/lnapps/` | `http://10.0.0.10:802/` | `/lnapps/xxx` → backend `/xxx` |
| `/itemupc/` | `http://10.0.0.10:801/` | `/itemupc/xxx` → backend `/xxx` |
| `/infoship/` | `http://backend-prod-01:6012/` | `/infoship/xxx` → backend `/xxx` |
| `/infoshiptest/` | `http://backend-test-01:6012/` | `/infoshiptest/xxx` → backend `/xxx` |

- **Forwarded headers**: `Host $host`, `X-Real-IP $remote_addr`, `REMOTE-HOST $remote_addr`,
  `X-Forwarded-For $proxy_add_x_forwarded_for`.
- **gzip**: not enabled (commented out).
- **TLS**: terminated at nginx (certificate `example.com.pem/.key`), HTTP and HTTPS dual entry.

## 2. Directive mapping

| nginx directive | Equivalent in this gateway | Notes |
| --- | --- | --- |
| `worker_processes` / `events.worker_connections` | Not needed | Kestrel manages connections by cores/system resources |
| `server { listen 8080; listen 443 ssl; }` | IIS site bindings 8080 / 443 (same gateway process) | One process serves all ports; YARP routes are port-agnostic |
| `server_name <production domain>` | IIS binding host header / `AllowedHosts` | In production, set the domain in the IIS binding |
| `location / { root html; index }` | `UseFileServer` + `wwwroot/index.html` | Built-in |
| `location /X/ { proxy_pass http://host:port/; }` | Route `X-route` + Cluster `X-cluster` + `PathRemovePrefix` | Trailing slash = prefix strip; equivalent |
| `proxy_set_header Host $host` | YARP preserves the original Host by default | See difference ③ |
| `proxy_set_header X-Real-IP $remote_addr` | `{ "RequestHeader": "X-Real-IP", "Set": "{RemoteIpAddress}" }` | `{RemoteIpAddress}` is a YARP placeholder |
| `proxy_set_header REMOTE-HOST $remote_addr` | `{ "RequestHeader": "REMOTE-HOST", "Set": "{RemoteIpAddress}" }` | Same as above |
| `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for` | `UseForwardedHeaders` restores + YARP appends X-Forwarded-* on forward | No config needed |
| `ssl_certificate` / `ssl_ciphers` | IIS HTTPS binding (per-site cert / centralized store) | The gateway does not handle TLS; see deployment guide §5 |
| `error_page 500 502 503 504 /50x.html` | Governance middleware returns status codes directly (503 with Retry-After) | See difference ④ |
| `gzip on` (not enabled) | ResponseCompression enabled by default | An enhancement; no need to disable |
| `keepalive_timeout 65` | Kestrel default (130s) | Not needed |
| (not configured) rate limit / breaker / retry / auth | `Gateway` governance middleware enabled by default | Enhancements; see §3.2 |

## 3. Equivalent gateway configuration

### 3.1 `ReverseProxy` node (drop-in replacement for the same node in `appsettings.json`)

```json
"ReverseProxy": {
  "Routes": {
    "lnapps-route": {
      "ClusterId": "lnapps-cluster",
      "Match": { "Path": "/lnapps/{**catch-all}" },
      "Transforms": [
        { "PathRemovePrefix": "/lnapps" },
        { "RequestHeader": "X-Real-IP", "Set": "{RemoteIpAddress}" },
        { "RequestHeader": "REMOTE-HOST", "Set": "{RemoteIpAddress}" }
      ]
    },
    "itemupc-route": {
      "ClusterId": "itemupc-cluster",
      "Match": { "Path": "/itemupc/{**catch-all}" },
      "Transforms": [
        { "PathRemovePrefix": "/itemupc" },
        { "RequestHeader": "X-Real-IP", "Set": "{RemoteIpAddress}" },
        { "RequestHeader": "REMOTE-HOST", "Set": "{RemoteIpAddress}" }
      ]
    },
    "infoshiptest-route": {
      "Order": 1,
      "ClusterId": "infoshiptest-cluster",
      "Match": { "Path": "/infoshiptest/{**catch-all}" },
      "Transforms": [
        { "PathRemovePrefix": "/infoshiptest" },
        { "RequestHeader": "X-Real-IP", "Set": "{RemoteIpAddress}" },
        { "RequestHeader": "REMOTE-HOST", "Set": "{RemoteIpAddress}" }
      ]
    },
    "infoship-route": {
      "ClusterId": "infoship-cluster",
      "Match": { "Path": "/infoship/{**catch-all}" },
      "Transforms": [
        { "PathRemovePrefix": "/infoship" },
        { "RequestHeader": "X-Real-IP", "Set": "{RemoteIpAddress}" },
        { "RequestHeader": "REMOTE-HOST", "Set": "{RemoteIpAddress}" }
      ]
    }
  },
  "Clusters": {
    "lnapps-cluster": {
      "Destinations": { "lnapps1": { "Address": "http://10.0.0.10:802/" } }
    },
    "itemupc-cluster": {
      "Destinations": { "itemupc1": { "Address": "http://10.0.0.10:801/" } }
    },
    "infoship-cluster": {
      "Destinations": { "infoship1": { "Address": "http://backend-prod-01:6012/" } }
    },
    "infoshiptest-cluster": {
      "Destinations": { "infoshiptest1": { "Address": "http://backend-test-01:6012/" } }
    }
  }
}
```

### 3.2 `Gateway` governance node (enhancements nginx lacks; defaults are safe)

| Node | Default behavior | vs nginx |
| --- | --- | --- |
| `RateLimit` | Token bucket 600 req/min per IP, 429 over quota (optional delay smoothing) | nginx needs a separate `limit_req` |
| `CircuitBreaker` | Opens after 5 consecutive failures for 30s, half-open probe recovers (503) | Not in open-source nginx |
| `Retry` | Idempotent requests retry on another node once on 502/504 | Needs `proxy_next_upstream` |
| `Security` | Extension blacklist 403 (.aspx/.php/.jsp…) | Needs `location` regex |
| `IpFilter` | IP/CIDR allow/deny 403 (hot-reloadable) | `allow`/`deny` |
| `RequestSize` | Max request body 10 MB, 413 over limit | `client_max_body_size` |
| `Cache` | In-memory cache of GET/HEAD 200 (TTL 30s) | `proxy_cache` |
| `SecurityHeaders` | nosniff / X-Frame-Options / CSP etc. | Needs `add_header` |
| `ProxyLogging` | Request/response body detail logging (JSON / fine-grained rules) | `access_log` + custom format |

> Migration principle: **forwarding semantics match the source nginx exactly** (prefix stripping,
> header pass-through, static root); governance capabilities are enabled by default as security
> enhancements. If your backends have no `/health` endpoint, **do not** add `HealthCheck.Active` to
> the clusters (see difference ②).

## 4. Key differences & caveats

1. **Overlapping prefixes require `Order`**: `/infoship/` and `/infoshiptest/` overlap in the
   source. nginx resolves this with **longest-prefix-first**; YARP matches by `Order` (higher wins),
   so `infoshiptest-route` explicitly sets `"Order": 1`. **If omitted, `/infoshiptest/xxx` would be
   matched by the `/infoship/` route first** — the most common migration pitfall.
2. **Do not copy the sample `HealthCheck.Active`**: the sample `api-cluster` has an active `/health`
   probe; if your backends lack that endpoint, probes fail and nodes get removed, causing **total
   502s**. The migration config above omits health checks, matching the source nginx (open-source
   nginx has no active health checks).
3. **`Host` header**: YARP forwards the original Host header (including port) by default; nginx
   `$host` excludes the port. On default ports 80/443 there is no observable difference; if the
   backend is port-sensitive, add
   `{ "RequestHeader": "Host", "Set": "{Host}" }` (`{Host}` is the port-less hostname).
4. **Error-page semantics differ**: nginx `error_page` is an internal redirect; this system returns
   503 (breaker) / 502 as plain text from the governance middleware (breaker 503 includes a
   `Retry-After` header). A branded 50x page would require a small custom middleware.
5. **TLS moves to IIS**: the source terminates TLS at nginx; after migration, bind the certificate
   at **IIS** (per-site binding or Centralized Certificate Store, deployment guide §5.1/5.2).
   `UseForwardedHeaders` restores `X-Forwarded-Proto` automatically — the app is unaware of TLS.

## 5. IIS deployment mapping

1. `dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net10.0 -o "C:\inetpub\wwwroot\gateway"`
2. App pool: `No Managed Code` + Integrated pipeline (`DotNetGatewayAppPool`).
3. Site bindings: `http :8080` and `https :443` (host header `gw.example.com`, pick the cert on 443).
4. Put the static home page in `wwwroot/index.html` (nginx `root html` equivalent).
5. Optional: `--test-config` precheck → smoke test (§6) → point DNS / load balancer to this gateway.

## 6. Verification checklist

| # | Check | Command (gateway on 5000) | Expected |
| --- | --- | --- | --- |
| 1 | Static site | `curl http://localhost:5000/` | 200 + index.html |
| 2 | `/lnapps/` prefix strip | `curl http://localhost:5000/lnapps/xxx` | backend receives `/xxx` |
| 3 | `/itemupc/` prefix strip | `curl http://localhost:5000/itemupc/xxx` | backend receives `/xxx` |
| 4 | `/infoshiptest/` not shadowed by `/infoship/` | `curl http://localhost:5000/infoshiptest/xxx` | hits `backend-test-01:6012` |
| 5 | `/infoship/` normal | `curl http://localhost:5000/infoship/xxx` | hits `backend-prod-01:6012` |
| 6 | `X-Real-IP` / `REMOTE-HOST` headers | check backend echo | real client IP |
| 7 | Governance enhancements | load test / request `.aspx` | 429 / 403 per config |
| 8 | Admin endpoint | `curl http://localhost:5000/admin/status` | 200 JSON summary |

## Appendix: sanitization map

> To protect production information, this table does not show the real values — it only describes
> the sanitization rule and the mapped example values used in this document.

| Original (sanitized description) | Used in this doc | Notes |
| --- | --- | --- |
| Production domain (real value sanitized) | `gw.example.com` | Domain |
| Internal IP | `10.0.0.10` | Example reserved range |
| Production backend hostname | `backend-prod-01` | Internal hostname (production) |
| Test backend hostname | `backend-test-01` | Internal hostname (test) |
| Certificate path (real value sanitized) | `example.com.pem/.key` | Certificate path |
| Ports `8080 / 443 / 802 / 801 / 6012` | kept as-is | Ports are not identity data; kept for comparison |
