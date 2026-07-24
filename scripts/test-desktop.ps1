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
$appStartupSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\App.xaml.cs')
$desktopShortcutSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\DesktopShortcutService.cs')
$themeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ThemeManager.cs')
$mainWindowXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\MainWindow.xaml')
$whatsAppInboxXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\WhatsAppInboxView.xaml')
$bridgeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\index.mjs')
$whatsAppInboxSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\WhatsAppInboxView.xaml.cs')
$whatsAppSyncSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppSyncService.cs')
$campaignAutomationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CampaignAutomationService.cs')
$customerSuccessSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerSuccessAgentCoordinator.cs')
$releaseCatalogSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ReleaseCatalog.cs')
$velopackBuildSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'scripts\build-velopack-release.ps1')
$allDesktopXaml = (Get-ChildItem -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop') -Recurse -Filter '*.xaml' |
  ForEach-Object { Get-Content -Raw -Encoding utf8 -LiteralPath $_.FullName }) -join "`n"
$desktopVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $desktopProject)).Project.PropertyGroup.Version | Select-Object -First 1
$coreVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $coreProject)).Project.PropertyGroup.Version | Select-Object -First 1
$macVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $macProject)).Project.PropertyGroup.Version | Select-Object -First 1
if ($desktopVersion -notmatch '^\d+\.\d+\.\d+$' -or $desktopVersion -ne $coreVersion) {
  throw "Desktop/Core versions must be the same semantic version. desktop=$desktopVersion core=$coreVersion"
}
if ($releaseCatalogSource -notmatch [regex]::Escape("new(`"$desktopVersion`"")) {
  throw "ReleaseCatalog must contain the current Desktop/Core semantic version. version=$desktopVersion"
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
  'GlassCard', 'AmbientHeroCard', 'IntelligenceGlassCard', 'ElevatedMetricCard',
  'NavText'
)
foreach ($key in $requiredStyles) {
  if ($appXaml -notmatch "x:Key=`"$key`"") { throw "AI Sales OS 2.0 component style is missing: $key" }
}
Write-Host 'PASS  AI Sales OS 2.x Figma/Stitch/WPF design-system contract'

$allSemanticBrushes = @(
  'Ink', 'InkSecondary', 'Muted', 'MutedSubtle', 'Primary', 'PrimaryDark', 'PrimaryHover',
  'PrimarySoft', 'PrimarySurface', 'AiAccent', 'AiAccentDeep', 'AiProcessing', 'AiSoft',
  'AiSurface', 'Surface', 'SurfaceElevated', 'SurfaceMuted', 'SurfaceInput', 'Canvas',
  'CanvasDeep', 'Line', 'LineStrong', 'Sidebar', 'SidebarElevated', 'SidebarHover',
  'SidebarActive', 'SidebarText', 'SidebarMuted', 'Success', 'SuccessSoft', 'Warning',
  'WarningSoft', 'Danger', 'DangerSoft', 'Info', 'InfoSoft', 'GradeA', 'GradeB', 'GradeC',
  'GradeD', 'ChatOutbound', 'ChatInbound', 'Overlay', 'GlassSurface', 'GlassSurfaceStrong',
  'GlassLine', 'AuroraAmbient', 'AuroraBorder'
)
$semanticStaticPattern = '\{StaticResource\s+(?:' + (($allSemanticBrushes | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\}'
if ([regex]::IsMatch($allDesktopXaml, $semanticStaticPattern)) {
  throw 'Theme-sensitive semantic brushes must use DynamicResource so an in-app theme switch cannot retain light-theme colors.'
}
$hardcodedColorPattern = '(?:Foreground|Background|BorderBrush)="(?:#[0-9A-Fa-f]{3,8}|Black|White|Gray|DarkGray|LightGray)"'
if ([regex]::IsMatch($allDesktopXaml, $hardcodedColorPattern)) {
  throw 'Desktop XAML contains a hard-coded foreground/background/border color that bypasses the semantic light/dark theme.'
}
if ($appXaml -notmatch '<Style TargetType="\{x:Type TextBlock\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource Ink\}"' -or
    $appXaml -notmatch '<Style TargetType="\{x:Type Label\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource InkSecondary\}"' -or
    $appXaml -notmatch '<Style TargetType="\{x:Type RadioButton\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource Ink\}"' -or
    $appXaml -notmatch '<Style TargetType="\{x:Type GroupBox\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource Ink\}"') {
  throw 'Implicit WPF text controls must inherit high-contrast semantic foregrounds in dark mode.'
}
$navTextCount = ([regex]::Matches($mainWindowXaml, 'Style="\{StaticResource NavText\}"')).Count
if ($navTextCount -ne 14 -or
    $appXaml -notmatch 'x:Key="NavText"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}') {
  throw "Every sidebar navigation icon and label must inherit the owning NavButton foreground. expected=14 actual=$navTextCount"
}

function Convert-HexToRgb([string]$hex) {
  $value = $hex.TrimStart('#')
  return @(
    [Convert]::ToInt32($value.Substring(0, 2), 16),
    [Convert]::ToInt32($value.Substring(2, 2), 16),
    [Convert]::ToInt32($value.Substring(4, 2), 16)
  )
}
function Get-RelativeLuminance([string]$hex) {
  $linear = Convert-HexToRgb $hex | ForEach-Object {
    $channel = $_ / 255.0
    if ($channel -le 0.04045) { $channel / 12.92 } else { [Math]::Pow(($channel + 0.055) / 1.055, 2.4) }
  }
  return 0.2126 * $linear[0] + 0.7152 * $linear[1] + 0.0722 * $linear[2]
}
function Get-ContrastRatio([string]$foreground, [string]$background) {
  $first = Get-RelativeLuminance $foreground
  $second = Get-RelativeLuminance $background
  $lighter = [Math]::Max($first, $second)
  $darker = [Math]::Min($first, $second)
  return ($lighter + 0.05) / ($darker + 0.05)
}
$lightPalette = @{}
$darkPalette = @{}
[regex]::Matches($themeSource, '\["(?<key>[^"]+)"\]\s*=\s*\("(?<light>#[0-9A-Fa-f]{6})",\s*"(?<dark>#[0-9A-Fa-f]{6})"\)') |
  ForEach-Object {
    $lightPalette[$_.Groups['key'].Value] = $_.Groups['light'].Value
    $darkPalette[$_.Groups['key'].Value] = $_.Groups['dark'].Value
  }
$contrastPairs = @(
  @('Ink', 'Canvas'), @('Ink', 'Surface'), @('Ink', 'SurfaceElevated'),
  @('Ink', 'SurfaceMuted'), @('Ink', 'AiSurface'), @('InkSecondary', 'Surface'),
  @('InkSecondary', 'SurfaceElevated'), @('Muted', 'Canvas'), @('Muted', 'Surface'),
  @('Muted', 'SurfaceElevated'), @('Warning', 'WarningSoft'), @('Danger', 'DangerSoft'),
  @('SidebarText', 'Sidebar'), @('SidebarText', 'SidebarActive'),
  @('SidebarMuted', 'Sidebar'), @('SidebarMuted', 'SidebarElevated')
)
foreach ($paletteEntry in @(@('Light', $lightPalette), @('Dark', $darkPalette))) {
  $mode = $paletteEntry[0]
  $palette = $paletteEntry[1]
  foreach ($pair in $contrastPairs) {
    $ratio = Get-ContrastRatio $palette[$pair[0]] $palette[$pair[1]]
    if ($ratio -lt 4.5) {
      throw "$mode theme contrast is below WCAG AA for $($pair[0]) on $($pair[1]): $([Math]::Round($ratio, 2)):1"
    }
  }
}
Write-Host 'PASS  light/dark-theme dynamic resources and high-contrast text contract'

if ($appStartupSource -notmatch [regex]::Escape('DesktopShortcutService.EnsureForInstalledApp();') -or
    $desktopShortcutSource -notmatch [regex]::Escape('ShortcutLocation.Desktop') -or
    $desktopShortcutSource -notmatch [regex]::Escape('ShortcutLocation.StartMenuRoot') -or
    $desktopShortcutSource -notmatch [regex]::Escape('VelopackLocator.IsCurrentSet') -or
    $velopackBuildSource -notmatch [regex]::Escape("--shortcuts 'Desktop,StartMenuRoot'")) {
  throw 'Windows install/update must create or repair both desktop and Start menu shortcuts.'
}
Write-Host 'PASS  Velopack install and post-update desktop shortcut repair contract'

$profileTextMatch = [regex]::Match(
  $whatsAppInboxXaml,
  '<TextBlock\s+x:Name="AiSidebarProfileText"(?<attributes>[\s\S]*?)/>'
)
if (-not $profileTextMatch.Success) {
  throw 'WhatsApp Inbox AI Sales Brief profile text control is missing.'
}
$profileTextAttributes = $profileTextMatch.Groups['attributes'].Value
if ($profileTextAttributes -notmatch 'TextWrapping="Wrap"' -or
    $profileTextAttributes -match 'MaxHeight=' -or
    $profileTextAttributes -match 'TextTrimming="CharacterEllipsis"') {
  throw 'WhatsApp Inbox AI Sales Brief must show the full customer profile without fixed-height or ellipsis clipping.'
}
if ($whatsAppInboxXaml -match 'Margin="0,108,0,0"') {
  throw 'WhatsApp Inbox AI Sales Brief next action must use adaptive rows instead of a fixed overlay margin.'
}
Write-Host 'PASS  WhatsApp Inbox AI Sales Brief adaptive full-text layout contract'

if ($bridgeSource -notmatch 'receiptBelongsToPhone' -or
    $bridgeSource -notmatch 'targetVerified:\s*true' -or
    $bridgeSource -notmatch 'whatsapp_target_mismatch' -or
    $bridgeSource -notmatch 'whatsapp_server_message_id_missing') {
  throw 'WhatsApp bridge must verify the recipient and require a server message id before confirming a send.'
}

if ($whatsAppInboxSource -notmatch 'string\.IsNullOrWhiteSpace\(id\)' -or
    $whatsAppInboxSource -notmatch '!Bool\(result, "targetVerified"\)' -or
    $whatsAppInboxSource -notmatch 'WhatsAppMessageStatus\.Pending') {
  throw 'WhatsApp Inbox must keep unconfirmed sends pending instead of inventing a successful message.'
}

if ($whatsAppSyncSource -notmatch 'WhatsAppMessageStatus\.Pending' -or
    $campaignAutomationSource -notmatch 'target_not_verified' -or
    $customerSuccessSource -notmatch 'customer_success_auto_reply_pending') {
  throw 'All WhatsApp sending paths must share the real acknowledgement and target-verification contract.'
}

if ($customerSuccessSource -match 'holding-\{Guid\.NewGuid') {
  throw 'Customer Success Agent must not invent a provider message id for an unconfirmed send.'
}

Write-Host 'PASS  WhatsApp real-send acknowledgement contract'

& $dotnet build (Join-Path $root 'desktop\WAFlow.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop build failed.' }
& $dotnet run --project (Join-Path $root 'desktop\WAFlow.SmokeTests\WAFlow.SmokeTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop smoke tests failed.' }
