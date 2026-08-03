[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
docker compose --project-directory $scriptRoot --file (Join-Path $scriptRoot 'compose.yml') down
if ($LASTEXITCODE -ne 0) { throw "SearXNG 停止失败，docker compose 退出码：$LASTEXITCODE" }
Write-Host 'AI Sales OS 本地 SearXNG 容器已停止；本地配置和主程序数据均已保留。'
