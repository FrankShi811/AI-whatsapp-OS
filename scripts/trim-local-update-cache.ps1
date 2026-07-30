[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)]
  [string]$CacheDirectory,
  [string]$CurrentVersion = '',
  [ValidateRange(0, 20)]
  [int]$RollbackVersionLimit = 3
)

$ErrorActionPreference = 'Stop'
$cacheRoot = [IO.Path]::GetFullPath($CacheDirectory).TrimEnd(
  [IO.Path]::DirectorySeparatorChar,
  [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $cacheRoot -PathType Container)) {
  Write-Host "Update cache does not exist: $cacheRoot"
  return
}

$packagePattern = '^AISalesOS-(?<version>\d+\.\d+\.\d+)-(?<kind>full|delta)\.nupkg$'
$packages = @(
  Get-ChildItem -LiteralPath $cacheRoot -File -Filter 'AISalesOS-*.nupkg' |
    ForEach-Object {
      if ($_.Name -match $packagePattern) {
        [pscustomobject]@{
          File = $_
          Version = [version]$Matches.version
          VersionText = $Matches.version
        }
      }
    }
)
if ($packages.Count -eq 0) {
  Write-Host "No versioned AI Sales OS update packages found: $cacheRoot"
  return
}

if (-not $CurrentVersion) {
  $CurrentVersion = ($packages | Sort-Object Version -Descending | Select-Object -First 1).VersionText
}
if ($CurrentVersion -notmatch '^\d+\.\d+\.\d+$') {
  throw "CurrentVersion must use x.y.z format: $CurrentVersion"
}
$current = [version]$CurrentVersion
$rollbackVersions = @(
  $packages |
    Where-Object { $_.Version -lt $current } |
    Sort-Object Version -Descending -Unique |
    Select-Object -First $RollbackVersionLimit |
    ForEach-Object VersionText
)
$retainedVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $packages) {
  if ($package.Version -ge $current) { [void]$retainedVersions.Add($package.VersionText) }
}
foreach ($version in $rollbackVersions) { [void]$retainedVersions.Add($version) }
$deletePackages = @($packages | Where-Object { -not $retainedVersions.Contains($_.VersionText) })
if ($deletePackages.Count -eq 0) {
  Write-Host "PASS local update cache already satisfies current + $RollbackVersionLimit rollback versions: $cacheRoot"
  return
}

$rootPrefix = $cacheRoot + [IO.Path]::DirectorySeparatorChar
foreach ($package in $deletePackages) {
  $resolved = [IO.Path]::GetFullPath($package.File.FullName)
  if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
      [IO.Path]::GetDirectoryName($resolved) -ne $cacheRoot) {
    throw "Refusing to delete a package outside the exact cache directory: $resolved"
  }
}

$feedPath = Join-Path $cacheRoot 'releases.win.json'
$feedTemp = "$feedPath.retention.tmp"
if (Test-Path -LiteralPath $feedPath) {
  $feed = Get-Content -Raw -Encoding utf8 -LiteralPath $feedPath | ConvertFrom-Json
  $feed.Assets = @($feed.Assets | Where-Object {
    -not $_.Version -or $retainedVersions.Contains([string]$_.Version)
  })
  [IO.File]::WriteAllText(
    $feedTemp,
    ($feed | ConvertTo-Json -Depth 20),
    [Text.UTF8Encoding]::new($false))
  [void](Get-Content -Raw -Encoding utf8 -LiteralPath $feedTemp | ConvertFrom-Json)
}

$releasesPath = Join-Path $cacheRoot 'RELEASES'
$releasesTemp = "$releasesPath.retention.tmp"
if (Test-Path -LiteralPath $releasesPath) {
  $deletedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($package in $deletePackages) { [void]$deletedNames.Add($package.File.Name) }
  $retainedLines = @(
    Get-Content -Encoding utf8 -LiteralPath $releasesPath |
      Where-Object {
        $line = $_
        -not ($deletedNames | Where-Object { $line -like "*$_*" })
      }
  )
  [IO.File]::WriteAllLines($releasesTemp, $retainedLines, [Text.UTF8Encoding]::new($false))
}

$releasedBytes = [long]0
foreach ($package in $deletePackages) {
  if ($PSCmdlet.ShouldProcess($package.File.FullName, 'Delete old AI Sales OS update package')) {
    $releasedBytes += $package.File.Length
    Remove-Item -LiteralPath $package.File.FullName -Force
  }
}
if (-not $WhatIfPreference) {
  if (Test-Path -LiteralPath $feedTemp) {
    Move-Item -LiteralPath $feedTemp -Destination $feedPath -Force
  }
  if (Test-Path -LiteralPath $releasesTemp) {
    Move-Item -LiteralPath $releasesTemp -Destination $releasesPath -Force
  }
}
else {
  Remove-Item -LiteralPath $feedTemp, $releasesTemp -Force -ErrorAction SilentlyContinue
}

Write-Host (
  "PASS local update cache retained current/future versions plus $RollbackVersionLimit rollback versions; " +
  "deleted=$($deletePackages.Count), released=$([Math]::Round($releasedBytes / 1MB, 2)) MB, root=$cacheRoot")
