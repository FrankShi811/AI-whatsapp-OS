[CmdletBinding()]
param(
  [string]$Version,
  [string]$RepositoryUrl = '',
  [ValidateSet('win-x64')]
  [string]$Runtime = 'win-x64',
  [switch]$SkipBridgeBuild,
  [string]$ReleaseOutputDirectory = '',
  [string]$InstallerOutput = '',
  [string]$ReleaseNotesPath = '',
  [switch]$SkipCanonicalCopy
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj'
$work = Join-Path $root 'work'
$localDotnet = Join-Path $work 'dotnet8\dotnet.exe'
$dotnet = $env:WAFLOW_DOTNET_PATH
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
  $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}

if (-not $Version) {
  $Version = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $project)).Project.PropertyGroup.Version | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') { throw "Invalid release version: $Version" }

$env:DOTNET_CLI_HOME = $work
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $SkipBridgeBuild) {
  $bridgeBuild = Join-Path $root 'bridge\scripts\build-sea.mjs'
  $node = $env:WAFLOW_NODE_PATH
  if (-not $node -or -not (Test-Path -LiteralPath $node)) {
    $bundledNode = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
    if (Test-Path -LiteralPath $bundledNode) { $node = $bundledNode }
  }
  if (-not $node -or -not (Test-Path -LiteralPath $node)) {
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeCommand) { $node = $nodeCommand.Source }
  }
  if (-not $node -or -not (Test-Path -LiteralPath $node)) { throw 'Node.js is required to build the embedded WhatsApp bridge. Set WAFLOW_NODE_PATH.' }
  & $node $bridgeBuild
  if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge build failed.' }
}

$publish = Join-Path $work "velopack-publish\$Runtime-$([Guid]::NewGuid().ToString('N'))"
$releases = if ($ReleaseOutputDirectory) { [IO.Path]::GetFullPath($ReleaseOutputDirectory) } else { Join-Path $root 'dist\velopack' }
$installerDirectory = Join-Path $root 'dist\installers'
$releaseNotes = if ($ReleaseNotesPath) { [IO.Path]::GetFullPath($ReleaseNotesPath) } else { Join-Path $root "docs\releases\v$Version.md" }
$icon = Join-Path $root 'desktop\WAFlow.Desktop\Assets\AI-Sales-OS.ico'
$canonicalExe = Join-Path $root 'AI Sales OS.exe'
New-Item -ItemType Directory -Force -Path $publish, $releases, $installerDirectory | Out-Null
if (-not (Test-Path -LiteralPath $releaseNotes)) { throw "Release notes are missing: $releaseNotes" }

# A local rebuild or a re-run of the same GitHub tag must overwrite its release
# instead of failing because Velopack sees the same version in the output feed.
$existingFeed = Join-Path $releases 'releases.win.json'
if (Test-Path -LiteralPath $existingFeed) {
  try {
    $feed = Get-Content -Raw -Encoding utf8 -LiteralPath $existingFeed | ConvertFrom-Json
    if (@($feed.Assets | Where-Object { $_.Version -eq $Version }).Count -gt 0) {
      Get-ChildItem -LiteralPath $releases -File | Remove-Item -Force
      Write-Host "Removed existing Velopack assets for same-version rebuild: $Version"
    }
  }
  catch { throw "Existing Velopack feed is invalid: $existingFeed`n$($_.Exception.Message)" }
}

$publishArguments = @(
  'publish', $project,
  '-c', 'Release', '-r', $Runtime, '--self-contained', 'true',
  '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:PublishTrimmed=false', "-p:Version=$Version", "-p:GitHubRepositoryUrl=$RepositoryUrl",
  '-o', $publish
)
& $dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'AI Sales OS publish failed.' }

$publishedExe = Join-Path $publish 'AISalesOS.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) { throw 'Published AISalesOS.exe is missing.' }
if (-not $SkipCanonicalCopy) {
  try { Copy-Item -LiteralPath $publishedExe -Destination $canonicalExe -Force }
  catch [System.IO.IOException] { throw 'AI Sales OS.exe is running. Close it before building so the canonical file can be overwritten.' }
}

& $dotnet tool restore --tool-manifest (Join-Path $root '.config\dotnet-tools.json')
if ($LASTEXITCODE -ne 0) { throw 'Velopack tool restore failed.' }
& $dotnet tool run vpk -- pack `
  --packId AISalesOS `
  --packVersion $Version `
  --packDir $publish `
  --mainExe AISalesOS.exe `
  --packTitle 'AI Sales OS' `
  --packAuthors 'AI Sales OS' `
  --releaseNotes $releaseNotes `
  --icon $icon `
  --channel win `
  --runtime $Runtime `
  --shortcuts 'Desktop,StartMenuRoot' `
  --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'Velopack package creation failed.' }

$setup = Get-ChildItem -LiteralPath $releases -Filter '*Setup.exe' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $setup) { throw 'Velopack Setup executable was not created.' }
$friendlySetup = if ($InstallerOutput) { [IO.Path]::GetFullPath($InstallerOutput) } else { Join-Path $installerDirectory 'AI Sales OS Setup.exe' }
& (Join-Path $root 'scripts\build-windows-installer.ps1') -SkipAppBuild -VelopackSetup $setup.FullName -InstallerOutput $friendlySetup
if ($LASTEXITCODE -ne 0) { throw 'Chinese Windows installer wrapper creation failed.' }

$artifacts = @($friendlySetup)
if (-not $SkipCanonicalCopy) { $artifacts = @($canonicalExe) + $artifacts }
foreach ($artifact in $artifacts) {
  $file = Get-Item -LiteralPath $artifact
  $hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
  Write-Host "Created: $($file.FullName)"
  Write-Host "Version: $Version"
  Write-Host "Size: $([Math]::Round($file.Length / 1MB, 2)) MB"
  Write-Host "SHA256: $($hash.Hash)"
}

try { Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction Stop }
catch { Write-Warning "Temporary publish directory could not be removed: $publish" }
