# nginx → 网关 迁移对照文档

> 本文档基于一份**真实生产 nginx.conf** 整理，给出了同一份转发语义在本网关
> （`DotNet.Yarp.Reverse.Proxy.Gateway`）中的完整对应配置。所有真实主机名、域名、内网地址
> 已脱敏（见文末附录），端口号予以保留便于对照。
>
> 配套：[README（中文）](../README.md) · [系统架构（中文）](Architecture.zh-CN.md) ·
> [IIS 部署指南（中文）](IIS-Deployment.zh-CN.md) ·
> **English version: [nginx → Gateway Migration Guide](Nginx-to-Gateway-Migration.en.md)**

---

## 1. 源配置脱敏摘要

源 nginx 站点（脱敏后）的关键事实：

- **站点**：`gw.example.com`，HTTP `8080` 与 HTTPS `443` 双端口监听（同一域名、同一套转发规则）。
- **静态根**：`location / { root html; index index.html index.htm; }`。
- **四个前缀代理**（`proxy_pass` 均带尾斜杠 `/`，即**前缀剥离**语义）：

| location 前缀 | 后端 | 转发后路径 |
| --- | --- | --- |
| `/lnapps/` | `http://10.0.0.10:802/` | `/lnapps/xxx` → 后端 `/xxx` |
| `/itemupc/` | `http://10.0.0.10:801/` | `/itemupc/xxx` → 后端 `/xxx` |
| `/infoship/` | `http://backend-prod-01:6012/` | `/infoship/xxx` → 后端 `/xxx` |
| `/infoshiptest/` | `http://backend-test-01:6012/` | `/infoshiptest/xxx` → 后端 `/xxx` |

- **转发头**：`Host $host`、`X-Real-IP $remote_addr`、`REMOTE-HOST $remote_addr`、
  `X-Forwarded-For $proxy_add_x_forwarded_for`。
- **gzip**：未启用（注释状态）。
- **TLS**：nginx 终止（证书 `example.com.pem/.key`，HTTP→HTTPS 双入口）。

## 2. 指令映射总表

| nginx 指令 | 本系统对应 | 说明 |
| --- | --- | --- |
| `worker_processes` / `events.worker_connections` | 无需配置 | Kestrel 自动按内核数/系统资源管理连接 |
| `server { listen 8080; listen 443 ssl; }` | IIS 站点绑定 8080 / 443（同一网关进程） | 一个进程服务所有端口，YARP 路由不分端口 |
| `server_name <生产域名>` | IIS 站点绑定主机头 / `AllowedHosts` | 生产建议在 IIS 绑定填写域名 |
| `location / { root html; index }` | `UseFileServer` + `wwwroot/index.html` | 已内置 |
| `location /X/ { proxy_pass http://host:port/; }` | Route `X-route` + Cluster `X-cluster` + `PathRemovePrefix` | 尾斜杠 = 前缀剥离，二者等价 |
| `proxy_set_header Host $host` | YARP 默认保留原始 Host | 见差异点 ③ |
| `proxy_set_header X-Real-IP $remote_addr` | `{ "RequestHeader": "X-Real-IP", "Set": "{RemoteIpAddress}" }` | `{RemoteIpAddress}` 为 YARP 占位符 |
| `proxy_set_header REMOTE-HOST $remote_addr` | `{ "RequestHeader": "REMOTE-HOST", "Set": "{RemoteIpAddress}" }` | 同上 |
| `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for` | `UseForwardedHeaders` 还原 + YARP 转发自动追加 | 无需配置 |
| `ssl_certificate` / `ssl_ciphers` | IIS HTTPS 绑定（站点证书 / 集中式证书） | 网关不处理 TLS，见部署指南第 5 节 |
| `error_page 500 502 503 504 /50x.html` | 治理中间件直接返回状态码（503 带 Retry-After） | 见差异点 ④ |
| `gzip on`（未启用） | ResponseCompression 默认开启 | 属增强，无需关闭 |
| `keepalive_timeout 65` | Kestrel 默认（130s） | 无需配置 |
| （未配置）限流 / 熔断 / 重试 / 认证 | `Gateway` 节点治理中间件默认开启 | 属增强，见 §3.2 |

## 3. 对应的网关配置

### 3.1 `ReverseProxy` 节点（可直接替换 `appsettings.json` 中同名节点）

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

### 3.2 `Gateway` 治理节点（nginx 没有的增强，默认值已安全）

| 节点 | 默认行为 | 与 nginx 对比 |
| --- | --- | --- |
| `RateLimit` | 每 IP 600 次/分钟令牌桶，超限 429（可开 delay 平滑） | nginx 需另配 `limit_req` |
| `CircuitBreaker` | 连续 5 次失败熔断 30s，半开探测恢复（503） | nginx 开源版无 |
| `Retry` | 幂等请求 502/504 换节点重试 1 次 | nginx 需配 `proxy_next_upstream` |
| `Security` | 扩展名黑名单 403（.aspx/.php/.jsp…） | 需 `location` 正则 |
| `IpFilter` | IP/CIDR 黑白名单 403（可热重载） | `allow`/`deny` |
| `RequestSize` | 请求体上限 10MB，超限 413 | `client_max_body_size` |
| `Cache` | GET/HEAD 200 内存缓存（TTL 30s） | `proxy_cache` |
| `SecurityHeaders` | nosniff / X-Frame-Options / CSP 等 | 需 `add_header` |
| `ProxyLogging` | 请求/响应体详情日志（可 JSON / 细粒度规则） | `access_log` + 自定义格式 |

> 迁移原则：**转发语义与源 nginx 严格一致**（前缀剥离、头透传、静态根），治理能力作为安全增强
> 默认开启。如后端无 `/health` 端点，**不要**为集群添加 `HealthCheck.Active`（见差异点 ②）。

## 4. 关键差异与注意事项

1. **前缀重叠必须用 `Order` 控制**：源配置中 `/infoship/` 与 `/infoshiptest/` 前缀重叠。nginx 按
   **最长前缀优先**天然正确；YARP 按 `Order`（值大优先）匹配，故 `infoshiptest-route` 显式设置
   `"Order": 1`。**若省略，`/infoshiptest/xxx` 会被 `/infoship/` 路由先匹配**——这是迁移中最
   容易踩的坑。
2. **不要照抄项目示例的 `HealthCheck.Active`**：示例 `api-cluster` 带 `/health` 主动探测；若你的
   后端没有该端点，启用后会因探测失败被摘除节点，导致**全部 502**。迁移配置已省略健康检查，
   与源 nginx（开源版无主动健康检查）行为一致。
3. **`Host` 头**：YARP 默认把原始 Host 头（含端口）原样转发；nginx `$host` 不含端口。80/443
   默认端口下二者无感；若后端对 Host 端口敏感，可在 Transforms 增加
   `{ "RequestHeader": "Host", "Set": "{Host}" }`（`{Host}` 为不含端口的主机名）。
4. **错误页语义不同**：nginx `error_page` 是服务器内部跳转；本系统 503（熔断）/502 由治理中间件
   直接返回文本（熔断 503 带 `Retry-After` 头）。如需品牌化 50x 页面，需后续增加小中间件。
5. **TLS 位置迁移**：源配置在 nginx 终止 TLS；迁移后在 **IIS 处绑定证书**（站点证书绑定或集中式
   证书存储，见部署指南 5.1/5.2），网关内 `UseForwardedHeaders` 自动还原 `X-Forwarded-Proto`，
   应用无需感知。

## 5. IIS 部署对应

1. `dotnet publish src/DotNet.Yarp.Reverse.Proxy.Gateway.Web -c Release -f net10.0 -o "C:\inetpub\wwwroot\gateway"`
2. 应用池：`No Managed Code` + 集成管道（`DotNetGatewayAppPool`）。
3. 站点绑定：`http :8080` 与 `https :443`（主机头 `gw.example.com`，443 选择证书）。
4. 静态首页放入 `wwwroot/index.html`（对应 nginx `root html`）。
5. 可选：`--test-config` 预检 → 冒烟（见 §6）→ 切换 DNS/负载均衡指向本网关。

## 6. 验证清单

| # | 验证项 | 命令（网关在 5000 直跑时） | 预期 |
| --- | --- | --- | --- |
| 1 | 静态站点 | `curl http://localhost:5000/` | 200 + index.html |
| 2 | `/lnapps/` 前缀剥离 | `curl http://localhost:5000/lnapps/xxx` | 后端收到 `/xxx` |
| 3 | `/itemupc/` 前缀剥离 | `curl http://localhost:5000/itemupc/xxx` | 后端收到 `/xxx` |
| 4 | `/infoshiptest/` 不被 `/infoship/` 抢占 | `curl http://localhost:5000/infoshiptest/xxx` | 落到 `backend-test-01:6012` |
| 5 | `/infoship/` 正常 | `curl http://localhost:5000/infoship/xxx` | 落到 `backend-prod-01:6012` |
| 6 | `X-Real-IP` / `REMOTE-HOST` 头 | 后端回显检查 | 值为真实客户端 IP |
| 7 | 治理增强 | 压测 / 访问 `.aspx` | 429 / 403 按配置触发 |
| 8 | 管理端点 | `curl http://localhost:5000/admin/status` | 200 JSON 摘要 |

## 附录：脱敏对照

> 为保护生产环境信息，本对照表不展示真实值，仅说明脱敏规则与映射后的示例值。

| 原文（已脱敏描述） | 本文档使用 | 说明 |
| --- | --- | --- |
| 生产域名（真实值已脱敏） | `gw.example.com` | 域名 |
| 内网 IP | `10.0.0.10` | 示例保留地址 |
| 生产后端主机名 | `backend-prod-01` | 内网主机名（生产） |
| 测试后端主机名 | `backend-test-01` | 内网主机名（测试） |
| 证书路径（真实值已脱敏） | `example.com.pem/.key` | 证书路径 |
| 端口 `8080 / 443 / 802 / 801 / 6012` | 保留原值 | 端口非身份信息，保留便于对照 |
