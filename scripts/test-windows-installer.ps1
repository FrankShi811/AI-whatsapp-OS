[CmdletBinding()]
param(
  [string]$InstallerPath = '',
  [string]$QaDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $InstallerPath) { $InstallerPath = Join-Path $root 'dist\installers\AI Sales OS Setup.exe' }
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Installer is missing: $InstallerPath" }
if (-not $QaDirectory) { $QaDirectory = Join-Path $root 'work\windows-installer-qa' }
$QaDirectory = [IO.Path]::GetFullPath($QaDirectory)
$workRoot = [IO.Path]::GetFullPath((Join-Path $root 'work'))
if (-not $QaDirectory.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw "QA directory must stay below the workspace work directory: $QaDirectory"
}
if (Test-Path -LiteralPath $QaDirectory) { [IO.Directory]::Delete($QaDirectory, $true) }

$database = Join-Path $env:LOCALAPPDATA 'WAFlow\waflow.db'
function Get-HashWithRetry([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return 'MISSING' }
  for ($attempt = 1; $attempt -le 10; $attempt++) {
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    catch [System.IO.IOException] {
      if ($attempt -eq 10) { throw }
      Start-Sleep -Milliseconds 500
    }
  }
}
$beforeHash = Get-HashWithRetry $database
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$QaDirectory`"")
$installer = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($installer.ExitCode -ne 0) { throw "Installer exited with code $($installer.ExitCode)." }

$appPath = Join-Path $QaDirectory 'current\AISalesOS.exe'
if (-not (Test-Path -LiteralPath $appPath)) { throw "Installed application is missing: $appPath" }
$app = Start-Process -FilePath $appPath -PassThru
Start-Sleep -Seconds 8
if (-not $app.HasExited) {
  $app.CloseMainWindow() | Out-Null
  if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force }
}
$qaProcesses = Get-CimInstance Win32_Process | Where-Object {
  $_.ExecutablePath -and $_.ExecutablePath.StartsWith($QaDirectory, [StringComparison]::OrdinalIgnoreCase)
}
foreach ($process in $qaProcesses) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

$afterHash = Get-HashWithRetry $database
$installedVersion = (Get-Item -LiteralPath $appPath).VersionInfo.FileVersion
$updatePath = Join-Path $QaDirectory 'Update.exe'
$uninstallExit = $null
if (Test-Path -LiteralPath $updatePath) {
  $uninstall = Start-Process -FilePath $updatePath -ArgumentList @('--uninstall', '--silent') -Wait -PassThru -WindowStyle Hidden
  $uninstallExit = $uninstall.ExitCode
}

[pscustomobject]@{
  InstallerExit = $installer.ExitCode
  InstalledExeVersion = $installedVersion
  ApplicationStarted = $true
  DatabaseHashBefore = $beforeHash
  DatabaseHashAfter = $afterHash
  DatabaseUnchanged = $beforeHash -eq $afterHash
  UninstallExit = $uninstallExit
  QaDirectoryStillExists = Test-Path -LiteralPath $QaDirectory
}
