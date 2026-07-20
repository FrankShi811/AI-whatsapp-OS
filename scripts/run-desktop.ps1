[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$exe = Join-Path $root 'outputs\WAFlow-Windows-x64.exe'
if (-not (Test-Path -LiteralPath $exe)) {
  throw 'outputs\WAFlow-Windows-x64.exe does not exist. Run .\scripts\build-desktop.ps1 first.'
}
Start-Process -FilePath $exe
