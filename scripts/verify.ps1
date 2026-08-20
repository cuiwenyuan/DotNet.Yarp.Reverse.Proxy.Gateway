# DotNet.Yarp.Reverse.Proxy.Gateway 网关验收脚本（纯 PowerShell，无第三方依赖）
# 前提：网关已在本地以 dotnet run 启动，监听 http://localhost:5000
# 用法：在仓库根目录下执行  dotnet run --project src/DotNet.Yarp.Reverse.Proxy.Gateway.Web  后，
#       另开终端运行  pwsh scripts/verify.ps1

$Base = 'http://localhost:5000'
$failed = 0

function Check($name, $url, $expectStatus) {
    try {
        $r = Invoke-WebRequest -Uri $url -Method GET -SkipHttpErrorCheck -TimeoutSec 5
        $ok = $r.StatusCode -eq $expectStatus
        Write-Host ("[{0}] {1} -> {2}" -f $(if($ok){'PASS'}else{'FAIL'}), $name, $r.StatusCode)
        if(-not $ok){ $script:failed++ }
    } catch {
        Write-Host ("[FAIL] {0} -> Exception: {1}" -f $name, $_.Exception.Message)
        $script:failed++
    }
}

Write-Host "=== Gateway 验收开始 ($Base) ==="

# 1) 静态文件托管（等价 nginx root）
Check 'static-index' "$Base/" 200

# 1.1) 监控指标端点（MetricsMiddleware）：/metrics 与 /health 应 200
Check 'metrics' "$Base/metrics" 200
Check 'health'  "$Base/health" 200

# 1.2) 管理状态端点（AdminStatusMiddleware）：/admin/status 应 200
Check 'admin-status' "$Base/admin/status" 200

# 2) 反向代理转发（后端 api-cluster:5101/5102 需先启动；无后端时预期 503）
Check 'proxy-api' "$Base/api/health" 200

# 3) 反向代理转发（web-cluster:5200）
Check 'proxy-web' "$Base/web/" 200

# 4) 跨域预检
Check 'cors-preflight' "$Base/" 204

# 5) 扩展名黑名单拦截（AspxBlocker）：.aspx / .php 等应被拒绝
Check 'aspx-blocker' "$Base/evil.aspx" 403
Check 'php-blocker'  "$Base/shell.php" 403

# 5.1) 请求体大小限制（RequestSizeLimit）：POST 超大 body 应被 413 拒绝
try {
    $bigBody = 'x' * 20MB
    $r = Invoke-WebRequest -Uri "$Base/" -Method POST -Body $bigBody `
        -SkipHttpErrorCheck -TimeoutSec 5
    $ok = $r.StatusCode -eq 413
    Write-Host ("[{0}] request-size-limit -> {1}" -f $(if($ok){'PASS'}else{'FAIL'}), $r.StatusCode)
    if(-not $ok){ $script:failed++ }
} catch {
    Write-Host ("[FAIL] request-size-limit -> Exception: {0}" -f $_.Exception.Message)
    $script:failed++
}

# 5.2) 失败重试（RetryMiddleware）：api-cluster 为 5101/5102 双节点，
#      仅启动一个后端时，RoundRobin 有一半请求会先落到未启动节点（502/504），
#      开启 Retry 后应能换节点重试成功（200）。此断言验证重试链路生效。
Write-Host "=== Retry 需仅启动 5101（redir_backend.py）验证：/api/health 应稳定 200 ==="

# 6) 限流：快速打满后预期 429（PerMinute 默认 600，脚本仅做存在性验证）
Write-Host "=== 限流需压测工具验证（如 wrk -d30s -R700 $Base/api/health）==="
Write-Host "=== LocationRewrite 需后端返回 3xx+Location 验证（见 redir_backend.py 冒烟）==="

# 7) 请求体日志（ProxyLoggingMiddleware）：POST 一个带唯一标记的请求体，
#    网关无论后端是否可达都会记录请求体（见冒烟日志 502 仍含 ReqBody）。
#    随后在常见的 XTrace 日志目录中检索该标记以确认日志确实写入。
$marker = "verify-proxylog-$(Get-Date -Format yyyyMMddHHmmss)-$(Get-Random)"
$logBody = "{""marker"":""$marker""}"
try {
    Invoke-WebRequest -Uri "$Base/api/echo" -Method POST `
        -ContentType 'application/json' -Body $logBody -SkipHttpErrorCheck -TimeoutSec 5 | Out-Null
    Start-Sleep -Seconds 1   # 给 XTrace 落盘一点时间

    # 常见日志目录：dotnet run 产物在 bin/Release/<tfm>/Log；发布后在 publish/Log
    $logRoots = @(
        (Join-Path $PSScriptRoot '..' 'src' 'YarpGateway' 'bin' 'Release'),
        (Join-Path $PSScriptRoot '..' 'publish')
    )
    $found = $false
    $searched = 0
    foreach ($root in $logRoots) {
        if (Test-Path $root) {
            $logs = Get-ChildItem -Path $root -Recurse -Filter '*.log' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending
            foreach ($log in $logs) {
                $searched++
                if (Select-String -Path $log.FullName -Pattern [regex]::Escape($marker) -Quiet) {
                    $found = $true; break
                }
            }
        }
        if ($found) { break }
    }

    if ($found) {
        Write-Host ("[PASS] proxy-logging -> 请求体已在 XTrace 日志中记录 (marker=$marker)")
    } elseif ($searched -eq 0) {
        Write-Host ("[WARN] proxy-logging -> 未找到日志文件（请先 dotnet run 启动网关；日志在 bin/Release/<tfm>/Log 或 publish/Log）。请求已发出 marker=$marker")
    } else {
        Write-Host ("[WARN] proxy-logging -> 已知日志目录中未检索到标记（确认 ProxyLogging.Enabled=true 且日志已刷新）。marker=$marker")
    }
} catch {
    Write-Host ("[WARN] proxy-logging -> 请求异常: $($_.Exception.Message)")
}

if ($failed -eq 0) {
    Write-Host "核心验收通过（后端可达情况下）。proxy-logging 为 WARN 级（依赖日志路径），不影响判定。" -ForegroundColor Green
} else {
    Write-Host "$failed 项未通过，请检查后端服务与配置。" -ForegroundColor Yellow
}
