[CmdletBinding()]
param(
  [string]$Version,
  [string]$RepositoryUrl = 'https://github.com/FrankShi811/AI-whatsapp-OS',
  [switch]$Velopack,
  [ValidateSet('Both', 'AppleSilicon', 'Intel')]
  [string]$Architecture = 'Both'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'desktop\WAFlow.Mac\WAFlow.Mac.csproj'
$work = Join-Path $root 'work'
$localDotnet = Join-Path $work 'dotnet8\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$python = if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' } else { '' }
if (-not $python -or -not (Test-Path -LiteralPath $python)) {
  $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
  if (-not $pythonCommand) { $pythonCommand = Get-Command python3 -ErrorAction Stop }
  $python = $pythonCommand.Source
}
if (-not $Version) { $Version = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $project)).Project.PropertyGroup.Version | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') { throw "Invalid version: $Version" }

$env:DOTNET_CLI_HOME = $work
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_XMLDOC_MODE = 'skip'
$isMacHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
  [Runtime.InteropServices.OSPlatform]::OSX
)
if ($Velopack -and -not $isMacHost) {
  Write-Warning 'Velopack macOS PKG/update packages require a macOS host. This host will validate self-contained .app ZIP assets only; GitHub Actions macOS runners create the PKG and update feed.'
}

$targets = switch ($Architecture) {
  'AppleSilicon' { @(@{ Rid='osx-arm64'; Arch='arm64'; Label='Apple-Silicon' }) }
  'Intel' { @(@{ Rid='osx-x64'; Arch='x64'; Label='Intel' }) }
  default { @(@{ Rid='osx-arm64'; Arch='arm64'; Label='Apple-Silicon' }, @{ Rid='osx-x64'; Arch='x64'; Label='Intel' }) }
}

$artifacts = @()
foreach ($target in $targets) {
  $friendlyPkg = Join-Path $root "dist\installers\AI Sales OS macOS $($target.Label) Chinese Preview.pkg"
  if (-not $isMacHost -and (Test-Path -LiteralPath $friendlyPkg)) {
    Remove-Item -LiteralPath $friendlyPkg -Force
  }
  $publish = Join-Path $work "macos-publish\$($target.Rid)"
  if (Test-Path -LiteralPath $publish) { [IO.Directory]::Delete($publish, $true) }
  New-Item -ItemType Directory -Force -Path $publish | Out-Null
  & $dotnet publish $project -c Release -r $target.Rid --self-contained true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:UseAppHost=true -p:Version=$Version `
    "-p:GitHubRepositoryUrl=$RepositoryUrl" `
    -o $publish --disable-build-servers
  if ($LASTEXITCODE -ne 0) { throw "macOS $($target.Rid) publish failed." }

  $appHost = Join-Path $publish 'AISalesOS.Mac'
  if (-not (Test-Path -LiteralPath $appHost)) { throw "macOS apphost is missing: $appHost" }
  $magic = ([IO.File]::ReadAllBytes($appHost))[0..3] | ForEach-Object { $_.ToString('X2') }
  if (($magic -join '') -notin @('CFFAEDFE','FEEDFACF')) { throw "macOS apphost is not a 64-bit Mach-O executable: $($magic -join '')" }

  $output = Join-Path $root "dist\installers\AI Sales OS macOS $($target.Label) Chinese Preview.zip"
  $bundle = Join-Path $work "macos-bundles\$($target.Rid)\AI Sales OS.app"
  & $python (Join-Path $root 'scripts\package-macos-app.py') `
    --publish $publish --output $output --arch $target.Arch --version $Version `
    --icon (Join-Path $root 'desktop\WAFlow.Desktop\Assets\AI-Sales-OS.png') --bundle-output $bundle
  if ($LASTEXITCODE -ne 0) { throw "macOS $($target.Arch) bundle packaging failed." }

  if ($Velopack -and $isMacHost) {
    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool restore failed.' }
    $velopackOutput = Join-Path $root "dist\velopack-macos-$($target.Arch)"
    if (Test-Path -LiteralPath $velopackOutput) { [IO.Directory]::Delete($velopackOutput, $true) }
    New-Item -ItemType Directory -Force -Path $velopackOutput | Out-Null
    $packId = "AISalesOS.Mac.$($target.Arch)"
    $channel = "osx-$($target.Arch)"
    & $dotnet tool run vpk -- pack `
      --packId $packId --packVersion $Version --packDir $bundle --mainExe 'AISalesOS.Mac' `
      --outputDir $velopackOutput --channel $channel --runtime $target.Rid `
      --packAuthors 'AI Sales OS' --packTitle 'AI Sales OS' `
      --releaseNotes (Join-Path $root "docs\releases\v$Version.md") `
      --instReadme (Join-Path $root 'docs\MACOS_NATIVE_PORT.md')
    if ($LASTEXITCODE -ne 0) { throw "Velopack macOS $($target.Rid) package creation failed." }

    $portable = Get-ChildItem -LiteralPath $velopackOutput -File -Filter '*.zip' | Sort-Object Length -Descending | Select-Object -First 1
    $pkg = Get-ChildItem -LiteralPath $velopackOutput -File -Filter '*.pkg' | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $portable) { throw "Velopack macOS $($target.Rid) portable zip was not created." }
    if ($pkg) {
      Copy-Item -LiteralPath $pkg.FullName -Destination $friendlyPkg -Force
      $artifacts += Get-Item -LiteralPath $friendlyPkg
    }
  }
  $artifacts += Get-Item -LiteralPath $output
  [IO.Directory]::Delete($publish, $true)
  if (Test-Path -LiteralPath (Split-Path $bundle -Parent)) { [IO.Directory]::Delete((Split-Path $bundle -Parent), $true) }
}

foreach ($artifact in $artifacts) {
  $hash = Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
  Write-Host "Created: $($artifact.FullName)"
  Write-Host "Version: $Version"
  Write-Host "Size: $([Math]::Round($artifact.Length / 1MB, 2)) MB"
  Write-Host "SHA256: $($hash.Hash)"
}
