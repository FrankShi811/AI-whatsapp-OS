[CmdletBinding()]
param(
  [switch]$SkipAppBuild,
  [string]$VelopackSetup = '',
  [string]$InstallerOutput = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
$canonicalExe = Join-Path $root 'AI Sales OS.exe'
$iss = Join-Path $root 'installer\windows\AI-Sales-OS.iss'
$output = Join-Path $root 'dist\installers\AI Sales OS Setup.exe'
$languageDirectory = Join-Path $root 'work\installer-languages'
$chineseMessages = Join-Path $languageDirectory 'ChineseSimplified.isl'

if (-not $SkipAppBuild) {
  & (Join-Path $root 'scripts\build-desktop.ps1')
  if ($LASTEXITCODE -ne 0) { throw 'AI Sales OS application build failed.' }
}
if (-not $VelopackSetup) {
  $VelopackSetup = (Get-ChildItem -LiteralPath (Join-Path $root 'dist\velopack') -Filter '*-Setup.exe' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
}
if (-not $VelopackSetup -or -not (Test-Path -LiteralPath $VelopackSetup)) { throw 'Velopack Setup is missing. Build the Velopack release first.' }

$isccCandidates = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required. Install JRSoftware.InnoSetup with winget.' }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
New-Item -ItemType Directory -Force -Path $languageDirectory | Out-Null
if (-not (Test-Path -LiteralPath $chineseMessages)) {
  Invoke-WebRequest 'https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl' -OutFile $chineseMessages
}
$translationText = Get-Content -Raw -Encoding utf8 -LiteralPath $chineseMessages
if (($translationText -notmatch 'LanguageID=\$0804') -or ($translationText -notmatch 'LanguageCodePage=936')) { throw 'Downloaded Inno Setup Chinese translation is invalid.' }
& $iscc "/DMyAppVersion=$version" "/DVelopackSetupPath=$([IO.Path]::GetFullPath($VelopackSetup))" "/DChineseMessagesFile=$chineseMessages" $iss
if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }
if (-not (Test-Path -LiteralPath $output)) { throw 'Windows installer was not created.' }

if ($InstallerOutput) {
  $requestedOutput = [IO.Path]::GetFullPath($InstallerOutput)
  if ($requestedOutput -ne [IO.Path]::GetFullPath($output)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $requestedOutput) | Out-Null
    Copy-Item -LiteralPath $output -Destination $requestedOutput -Force
    $output = $requestedOutput
  }
}

$file = Get-Item -LiteralPath $output
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $output
Write-Host "Created: $($file.FullName)"
Write-Host "Version: $version"
Write-Host "Size: $([Math]::Round($file.Length / 1MB, 2)) MB"
Write-Host "SHA256: $($hash.Hash)"
