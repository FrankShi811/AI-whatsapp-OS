[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Read-Utf8([string]$RelativePath) {
  Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root $RelativePath)
}

function Require-Text(
  [string]$Text,
  [string[]]$Needles,
  [string]$Area
) {
  foreach ($needle in $Needles) {
    if ($Text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
      throw "macOS parity gate failed [$Area]: missing '$needle'"
    }
  }
}

function Project-Version([string]$RelativePath) {
  $project = [xml](Read-Utf8 $RelativePath)
  [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
}

$coreVersion = Project-Version 'desktop\WAFlow.Core\WAFlow.Core.csproj'
$windowsVersion = Project-Version 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj'
$macVersion = Project-Version 'desktop\WAFlow.Mac\WAFlow.Mac.csproj'
if ($coreVersion -ne $windowsVersion -or $coreVersion -ne $macVersion) {
  throw "macOS parity gate failed [version]: Core=$coreVersion Windows=$windowsVersion Mac=$macVersion"
}

$shell = Read-Utf8 'desktop\WAFlow.Mac\MainWindow.axaml'
$shellCode = Read-Utf8 'desktop\WAFlow.Mac\MainWindow.axaml.cs'
$styles = Read-Utf8 'desktop\WAFlow.Mac\App.axaml'
$theme = Read-Utf8 'desktop\WAFlow.Mac\MacThemeManager.cs'
$customers = Read-Utf8 'desktop\WAFlow.Mac\MainWindow.Customers.cs'
$messaging = Read-Utf8 'desktop\WAFlow.Mac\MainWindow.Messaging.cs'
$operations = Read-Utf8 'desktop\WAFlow.Mac\MainWindow.Operations.cs'
$settings = Read-Utf8 'desktop\WAFlow.Mac\MainWindow.Settings.cs'
$project = Read-Utf8 'desktop\WAFlow.Mac\WAFlow.Mac.csproj'
$emailService = Read-Utf8 'desktop\WAFlow.Core\Services\EmailService.cs'
$macPackager = Read-Utf8 'scripts\package-macos-app.py'

Require-Text $shell @(
  'Width="1440" Height="900" MinWidth="1120" MinHeight="700"',
  'WindowState="Maximized"',
  'ColumnDefinitions="60,*"',
  'RowDefinitions="72,*"',
  'x:Name="SidebarHost"',
  'x:Name="CommandOverlay"',
  'x:Name="WhatsAppUnreadBadge"',
  'x:Name="EmailUnreadBadge"',
  'Tag="settings"'
) 'shell'

$moduleKeys = @('dashboard', 'intelligence', 'customers', 'inbox', 'email', 'broadcast', 'knowledge', 'analytics')
foreach ($module in $moduleKeys) {
  Require-Text $shell @("Tag=`"$module`"") "navigation:$module"
  Require-Text $shellCode @("`"$module`"") "routing:$module"
}

Require-Text $styles @(
  'Duration="0:0:0.24"',
  'Duration="0:0:0.22"',
  'Delay="0:0:0.06"',
  'Border#SidebarHost.expanded',
  'Button.nav.selected',
  'Button:focus-visible',
  'TextBox:focus-visible',
  'ComboBox:focus-visible',
  'Window.reduce-motion'
) 'motion-and-states'

Require-Text $shellCode @(
  'KeyModifiers.Meta',
  'KeyModifiers.Control',
  'Key.D1',
  'Key.D8',
  'Key.K',
  'Task.Delay(110, motion.Token)',
  'RunUiSmokeAsync',
  'PrefersReducedMotion',
  'RefreshCurrentPageCoalescedAsync',
  'MacThemeManager.Apply("Dark")',
  'ApplyUiScale(settings.UiScalePercentage)'
) 'interaction'
Require-Text $shell @('Duration="0:0:0.235"') 'page-transition'
Require-Text $emailService @(
  'StartBackgroundSyncAsync',
  'RequestSyncAsync',
  'BackgroundAccountMonitor',
  'RunConnectedAccountSessionAsync'
) 'v5.10.2-inbox-performance'
Require-Text $macPackager @(
  'source_mode = path.stat().st_mode',
  'stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH'
) 'macos-zip-executable-permissions'

$semanticKeys = [regex]::Matches($theme, '\["([^"]+)"\]\s*=') |
  ForEach-Object { $_.Groups[1].Value } |
  Sort-Object -Unique
$requiredSemanticKeys = @(
  'Ink', 'InkSecondary', 'Muted', 'Primary', 'PrimaryHover', 'PrimarySoft',
  'AiAccent', 'AiProcessing', 'AiSurface', 'Surface', 'SurfaceElevated',
  'SurfaceMuted', 'SurfaceInput', 'Canvas', 'Line', 'LineStrong', 'Sidebar',
  'SidebarHover', 'SidebarActive', 'SidebarText', 'UnreadBadgeBackground',
  'Success', 'Warning', 'Danger', 'Info', 'GradeA', 'GradeB', 'GradeC',
  'GradeD', 'ChatOutbound', 'ChatInbound', 'Overlay', 'GlassSurface'
)
foreach ($key in $requiredSemanticKeys) {
  if ($semanticKeys -notcontains $key) {
    throw "macOS parity gate failed [theme]: missing semantic token '$key'"
  }
}
Require-Text $theme @('"Light"', '"Dark"', '"System"', 'ThemeVariant.Light', 'ThemeVariant.Dark') 'theme-modes'

Require-Text $customers @(
  'ImportCustomersAsync',
  'ShowLeadEditorAsync',
  'Repository.DeleteLeadAsync',
  'DeleteLeadAsync',
  'CustomerDimensionCatalog.Build',
  'ResolvePrimaryCategoryPreference',
  'PhoneState',
  'ItemsSource = new[] { 10, 30 }',
  'RunBulkAnalysisAsync',
  'AnalyzeAllLeadsAsync',
  'ScoreFactors',
  'selected.Risks',
  'selected.RiskWarning'
) 'customers-and-opportunities'

Require-Text $messaging @(
  'AddWhatsAppAccountAsync',
  'ConnectWhatsAppAsync',
  'LogoutWhatsAppAsync',
  'CreateWhatsAppGroupAsync',
  'SendTextAsync',
  'SendReplyTextAsync',
  'SendMediaAsync',
  'SendReplyMediaAsync',
  'RevokeMessageAsync',
  'SetChatPinnedAsync',
  'ConversationAgentMode.AutoActive',
  'TakeOverAsync',
  'ResolveHandoffAsync',
  'ShowEmailAccountEditorAsync',
  'EmailService.Guide',
  'account.ImapUseSsl = imapSsl.IsChecked == true',
  'account.SmtpUseSsl = smtpSsl.IsChecked == true',
  'SaveAndTestAccountAsync',
  'SyncInboxAsync',
  '_services.Email.SendAsync',
  'EmailAssistant'
) 'messaging'

Require-Text $operations @(
  'GetCampaignMessageTemplatesAsync',
  'SaveMessageTemplateAsync',
  'DeleteMessageTemplateAsync',
  'GetTemplateFieldsAsync',
  'ParseBeijing',
  'CustomerDimensionCatalog.ResolvePrimaryCategoryPreference',
  'PreviewAudienceAsync',
  'ApproveAndScheduleAsync',
  'GetExecutionHistoryAsync',
  'ShowKnowledgeDetailAsync',
  'GetOriginalPathAsync',
  'UpdateReviewMetadataAsync',
  'GetVersionsAsync',
  'GetChunksAsync',
  'GetConflictsAsync',
  'ResolveConflictAsync',
  'KnowledgeRetrieval.RetrieveAsync',
  'KnowledgeLearning.RefreshCandidatesAsync',
  'PublishCandidateAsync',
  'GenerateCustomerReportAsync',
  'ShowReportComparisonAsync',
  'EvidenceLedger',
  'ExportWordAsync',
  'ExportPdfAsync'
) 'automation-knowledge-analytics'

Require-Text $settings @(
  'ConfiguredAiProviders',
  'AiModulePreferences',
  'MacKeychainSecretStore',
  'UiScalePercentage',
  'ThemeMode',
  'MacThemeManager.Apply',
  'ApplyUiScale'
) 'settings'

Require-Text $project @(
  '..\WAFlow.Core\WAFlow.Core.csproj',
  '..\WAFlow.Desktop\GuideCatalog.cs',
  '..\WAFlow.Desktop\ReleaseCatalog.cs'
) 'shared-source'

$allMacSource = "$shell`n$shellCode`n$customers`n$messaging`n$operations`n$settings"
foreach ($forbidden in @('localStorage', 'indexedDB', 'pwa/', 'github.io/AI-whatsapp-OS')) {
  if ($allMacSource.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "macOS parity gate failed [native-boundary]: forbidden PWA dependency '$forbidden'"
  }
}

Write-Host "PASS macOS/Windows parity static gate version=$macVersion modules=$($moduleKeys.Count) semanticTokens=$($semanticKeys.Count)"
