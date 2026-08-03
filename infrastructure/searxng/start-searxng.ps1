[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$environmentPath = Join-Path $scriptRoot '.env'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw '未检测到 Docker。此组件完全可选；AI Sales OS 主程序不依赖 Docker 或 SearXNG。'
}

if (-not (Test-Path -LiteralPath $environmentPath)) {
    $bytes = New-Object byte[] 48
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    $secret = [Convert]::ToHexString($bytes).ToLowerInvariant()
    [System.IO.File]::WriteAllText($environmentPath, "SEARXNG_SECRET=$secret`n", [System.Text.UTF8Encoding]::new($false))
}

docker compose --project-directory $scriptRoot --file (Join-Path $scriptRoot 'compose.yml') up --detach
if ($LASTEXITCODE -ne 0) { throw "SearXNG 启动失败，docker compose 退出码：$LASTEXITCODE" }

Write-Host 'SearXNG 已提交启动：http://127.0.0.1:8080'
Write-Host '请回到 AI Sales OS 设置，启用本地 SearXNG 并点击“测试 SearXNG”。'
