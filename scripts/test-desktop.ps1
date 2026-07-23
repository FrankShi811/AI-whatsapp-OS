[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$localDotnet = Join-Path $root 'work\dotnet8\dotnet.exe'
$dotnet = $env:WAFLOW_DOTNET_PATH
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
  $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$work = Join-Path $root 'work'
$env:DOTNET_CLI_HOME = $work
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$desktopProject = Join-Path $root 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj'
$coreProject = Join-Path $root 'desktop\WAFlow.Core\WAFlow.Core.csproj'
$macProject = Join-Path $root 'desktop\WAFlow.Mac\WAFlow.Mac.csproj'
$appXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\App.xaml')
$themeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ThemeManager.cs')
$desktopVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $desktopProject)).Project.PropertyGroup.Version | Select-Object -First 1
$coreVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $coreProject)).Project.PropertyGroup.Version | Select-Object -First 1
$macVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $macProject)).Project.PropertyGroup.Version | Select-Object -First 1
if ($desktopVersion -notmatch '^\d+\.\d+\.\d+$' -or $desktopVersion -ne $coreVersion) {
  throw "Desktop/Core versions must be the same semantic version. desktop=$desktopVersion core=$coreVersion"
}
if ($env:ENABLE_MACOS_RELEASE -eq 'true') {
  if ($desktopVersion -ne $macVersion) {
    throw "macOS release is enabled, so Desktop/Core/macOS versions must match. desktop=$desktopVersion core=$coreVersion mac=$macVersion"
  }
  Write-Host "PASS  cross-platform version contract: $desktopVersion"
}
else {
  Write-Host "PASS  Windows release version contract: $desktopVersion (macOS release paused at $macVersion)"
}

$requiredBrushes = @(
  'Ink', 'InkSecondary', 'Muted', 'Primary', 'AiAccent', 'AiProcessing',
  'Surface', 'Canvas', 'Line', 'Success', 'Warning', 'Danger', 'Info',
  'GradeA', 'GradeB', 'GradeC', 'GradeD', 'ChatOutbound', 'ChatInbound'
)
foreach ($key in $requiredBrushes) {
  if ($appXaml -notmatch "x:Key=`"$key`"" -or $themeSource -notmatch [regex]::Escape("[`"$key`"]")) {
    throw "AI Sales OS 2.0 semantic brush is missing from App.xaml or ThemeManager: $key"
  }
}
$requiredStyles = @(
  'HolographicCard', 'ConfidenceMeter', 'ReasoningStepCard', 'PriorityCard',
  'InboundMessageBubble', 'OutboundMessageBubble', 'WorkflowNodeCard',
  'PageTitle', 'SectionTitle', 'BodyText', 'LabelText', 'MicroText',
  'GlassCard', 'AmbientHeroCard', 'IntelligenceGlassCard', 'ElevatedMetricCard'
)
foreach ($key in $requiredStyles) {
  if ($appXaml -notmatch "x:Key=`"$key`"") { throw "AI Sales OS 2.0 component style is missing: $key" }
}
Write-Host 'PASS  AI Sales OS 2.x Figma/Stitch/WPF design-system contract'

& $dotnet build (Join-Path $root 'desktop\WAFlow.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop build failed.' }
& $dotnet run --project (Join-Path $root 'desktop\WAFlow.SmokeTests\WAFlow.SmokeTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop smoke tests failed.' }
