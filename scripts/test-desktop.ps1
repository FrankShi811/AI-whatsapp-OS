[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$localDotnet = Join-Path $root 'work\dotnet8\dotnet.exe'
$dotnet = $env:WAFLOW_DOTNET_PATH
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
  $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$node = $env:WAFLOW_NODE_PATH
if (-not $node -or -not (Test-Path -LiteralPath $node)) {
  $node = (Get-Command node -ErrorAction Stop).Source
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
$uiScaleSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\UiScaleManager.cs')
$uiScaleHostSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Controls\UiScaleHost.cs')
$windowsInstallerTestSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'scripts\test-windows-installer.ps1')
$themeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ThemeManager.cs')
$mainWindowXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\MainWindow.xaml')
$mainWindowSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\MainWindow.xaml.cs')
$dashboardXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\DashboardView.xaml')
$dashboardSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\DashboardView.xaml.cs')
$customersXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CustomersView.xaml')
$customersSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CustomersView.xaml.cs')
$campaignsXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CampaignsView.xaml')
$campaignsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CampaignsView.xaml.cs')
$todayBriefSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\TodayBriefService.cs')
$customerBrainModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\CustomerBrainModels.cs')
$leadIntelligenceXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\LeadIntelligenceView.xaml')
$leadIntelligenceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\LeadIntelligenceView.xaml.cs')
$leadAutomationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\LeadIntelligenceAutomationService.cs')
$whatsAppInboxXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\WhatsAppInboxView.xaml')
$emailInboxXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\EmailInboxView.xaml')
$bridgeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\index.mjs')
$bridgeMessageSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\message-content.mjs')
$bridgeOutboundRoutingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\outbound-routing.mjs')
$bridgeConversationRoutingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\conversation-routing.mjs')
$whatsAppInboxSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\WhatsAppInboxView.xaml.cs')
$emailInboxSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\EmailInboxView.xaml.cs')
$whatsAppSyncSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppSyncService.cs')
$whatsAppNamingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppConversationNaming.cs')
$whatsAppManagerSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppConnectionManager.cs')
$whatsAppNumberValidationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppNumberValidationService.cs')
$campaignAutomationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CampaignAutomationService.cs')
$customerSuccessSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerSuccessAgentCoordinator.cs')
$emailServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\EmailService.cs')
$emailAssistantSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\EmailAssistantService.cs')
$messagingSyncSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\MessagingSyncService.cs')
$emailAccountXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\EmailAccountWindow.xaml')
$emailAccountSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\EmailAccountWindow.xaml.cs')
$knowledgeBaseXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\KnowledgeBaseView.xaml')
$knowledgeBaseSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\KnowledgeBaseView.xaml.cs')
$knowledgeServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\KnowledgeBaseService.cs')
$knowledgeModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\KnowledgeModels.cs')
$localRepositorySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\LocalRepository.cs')
$guideCatalogSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\GuideCatalog.cs')
$releaseCatalogSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ReleaseCatalog.cs')
$updateStateSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Updates\ApplicationUpdateState.cs')
$updateServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Updates\VelopackUpdateService.cs')
$settingsXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\SettingsWindow.xaml')
$settingsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\SettingsWindow.xaml.cs')
$domainModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\Models.cs')
$deepSeekSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\DeepSeekService.cs')
$conversationAssistantSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\ConversationAssistantService.cs')
$customerAnalysisSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerAnalysisService.cs')
$customerBrainSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerBrainService.cs')
$customerSuccessAgentSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerSuccessAgentService.cs')
$buyerIdentitySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\BuyerIdentity.cs')
$customerIdentitySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerIdentityService.cs')
$importModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\ImportModels.cs')
$importServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\ImportService.cs')
$customerDimensionSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\CustomerDimensionCatalog.cs')
$customerEditXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\CustomerEditWindow.xaml')
$customerEditSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\CustomerEditWindow.xaml.cs')
$knowledgeProcessingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\KnowledgeProcessingComponents.cs')
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
  'UnreadBadgeBackground', 'UnreadBadgeText',
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
  'SidebarActive', 'SidebarText', 'SidebarMuted', 'UnreadBadgeBackground', 'UnreadBadgeText', 'Success', 'SuccessSoft', 'Warning',
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
$navIconCount = ([regex]::Matches($mainWindowXaml, 'Style="\{StaticResource NavIconFrame\}"')).Count
if ($navTextCount -ne 10 -or
    $navIconCount -ne 9 -or
    $appXaml -notmatch 'x:Key="NavText"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}' -or
    $appXaml -notmatch 'x:Key="NavIconFrame"[\s\S]*?<Setter Property="Width" Value="18"/>' -or
    $appXaml -notmatch 'x:Key="NavIconPath"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}[\s\S]*?<Setter Property="StrokeThickness" Value="2"/>' -or
    $appXaml -notmatch 'x:Key="NavIconRectangle"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}' -or
    $appXaml -notmatch 'x:Key="NavIconEllipse"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}') {
  throw "Every sidebar module must use one aligned 18px vector icon and a semantic foreground. expected_text=10 actual_text=$navTextCount expected_icons=9 actual_icons=$navIconCount"
}
if ($mainWindowXaml -notmatch '<Button Style="\{StaticResource NavButton\}" Click="CommandButton_Click">\s*<TextBlock Text="[^"]*Ctrl \+ K" Style="\{StaticResource NavText\}"/>\s*</Button>' -or
    $mainWindowXaml -match 'Content="[^"]*Ctrl \+ K"') {
  throw 'The sidebar shortcut footer must use NavText instead of implicit Ink text on the dark sidebar.'
}
if ($domainModelsSource -notmatch 'enum WhatsAppRegistrationStatus' -or
    $domainModelsSource -notmatch 'WhatsAppRegistrationStatus\.Registered when WhatsAppRegistrationMatchesCurrentPhone => "有效"' -or
    $domainModelsSource -notmatch 'WhatsAppRegistrationStatus\.NotRegistered when WhatsAppRegistrationMatchesCurrentPhone => "无效"' -or
    $importServiceSource -notmatch 'lead\.QueueWhatsAppRegistrationCheck\(\)' -or
    $whatsAppNumberValidationSource -notmatch 'LookupRegistrationAsync' -or
    $whatsAppNumberValidationSource -notmatch 'WhatsAppRegistrationStatus\.RetryableFailed' -or
    $whatsAppNumberValidationSource -notmatch 'TimeSpan\.FromMilliseconds\(900\)' -or
    $whatsAppManagerSource -notmatch 'LookupRegistrationAsync' -or
    $customersXaml -notmatch 'Header="WhatsApp 状态"') {
  throw 'Spreadsheet imports must queue rate-limited real WhatsApp registration checks and only explicit provider results may become valid or invalid.'
}
if ($dashboardXaml -notmatch 'Text="\{Binding CustomerLabel\}"' -or
    $dashboardXaml -notmatch 'Text="\{Binding ActionLabel\}"' -or
    $dashboardXaml -notmatch 'Text="\{Binding ReasonLabel\}"' -or
    $todayBriefSource -notmatch [regex]::Escape('CustomerName = customerName') -or
    $todayBriefSource -match [regex]::Escape('CustomerName = customerId') -or
    $todayBriefSource -notmatch [regex]::Escape('digits[^4..]')) {
  throw 'Today Brief must show readable customer identity, explicit next action and supporting reason instead of internal buyer IDs.'
}
$buyerIdentityContracts = [ordered]@{
  domain_field = $domainModelsSource.Contains('public string BuyerId { get; set; } = "";')
  import_field = $importModelsSource.Contains('Ignore, Custom, BuyerId, Name')
  import_priority = $importServiceSource.Contains('byBuyerId.TryGetValue(buyerKey')
  conflict_guard = $importServiceSource.Contains('BuyerIdentity.Resolve(duplicate)') -and
    $importServiceSource.Contains('duplicate = null;')
  identity_api = $localRepositorySource.Contains('GetLeadByIdentityAsync')
  buyer_lookup = $localRepositorySource.Contains('GetLeadsByBuyerIdAsync')
  canonical_key = $buyerIdentitySource.Contains('return $"buyer:{buyerId}"')
  whatsapp_resolution = $customerIdentitySource.Contains('CustomerIdentityMatchMethod.ExactBuyerId')
  edit_field = $customerEditXaml.Contains('x:Name="BuyerIdBox"')
  edit_guard = $customerEditSource.Contains('IsBuyerIdAvailableAsync')
}
$missingBuyerIdentityContracts = $buyerIdentityContracts.GetEnumerator() |
  Where-Object { -not $_.Value } |
  ForEach-Object { $_.Key }
if ($missingBuyerIdentityContracts.Count -gt 0) {
  throw "Buyer ID must be the authoritative cross-module customer identity, with phone fallback and conflict-safe writes. missing=$($missingBuyerIdentityContracts -join ',')"
}
Write-Host 'PASS  Buyer ID primary identity, phone fallback and cross-module memory contract'

if ($customersXaml -match 'TagFilterBox|OwnerFilterBox|CustomValueFilterBox' -or
    $customersXaml -notmatch 'x:Name="PageSizeBox"' -or
    $customersXaml -notmatch 'x:Name="PreviousPageButton"' -or
    $customersXaml -notmatch 'x:Name="NextPageButton"' -or
    $customersXaml -notmatch 'x:Name="SelectAllCheckBox"' -or
    $customersSource -notmatch 'new PageSizeOption\([^,]+,\s*10\)' -or
    $customersSource -notmatch 'new PageSizeOption\([^,]+,\s*30\)' -or
    $customersSource -notmatch 'new PageSizeOption\([^,]+,\s*50\)' -or
    $customersSource -notmatch [regex]::Escape('Skip(startIndex).Take(_pageSize)') -or
    $customersSource -match [regex]::Escape('条 / 页') -or
    $customersXaml -notmatch 'x:Name="PageSizeBox" Width="112"' -or
    $customersXaml -notmatch 'x:Key="CustomerScrollBar"' -or
    $customersXaml -notmatch 'BasedOn="\{StaticResource CustomerScrollBar\}"' -or
    $customersSource -notmatch [regex]::Escape('CustomerDimensionCatalog.Build(leads)') -or
    $customerDimensionSource -notmatch [regex]::Escape('ImportService.IsCoreDimension(sourceKey)') -or
    $customersSource -notmatch [regex]::Escape('ImportService.ResolveField(header) == ImportField.Name')) {
  throw 'Customer list must remove advanced text filters, merge canonical source aliases into system fields, and paginate by 10, 30 or 50 rows.'
}
Write-Host 'PASS  compact customer list, Buyer Nickname merge and 10/30/50 pagination contract'

if ($leadIntelligenceXaml -match 'DataGridTextColumn Header="公司"' -or
    $leadIntelligenceXaml -notmatch 'DataGridTextColumn Header="客户"' -or
    $leadIntelligenceXaml -notmatch 'DataGridTextColumn Header="市场"') {
  throw 'Lead Intelligence opportunity queue must omit the company column while retaining customer and market dimensions.'
}
Write-Host 'PASS  Lead Intelligence compact opportunity dimensions without company column'

if ($todayBriefSource -match '"identity"' -or
    $todayBriefSource -match 'identityPending' -or
    $dashboardSource -match '身份确认' -or
    $customerBrainModelsSource -match 'IdentityPendingCount') {
  throw 'Dashboard Today Brief must not create or count identity-confirmation work; unmatched Inbox contacts wait for manual CRM creation.'
}
Write-Host 'PASS  Today Brief known-customer workflow without identity-confirmation tasks'

if ($customerDimensionSource -notmatch [regex]::Escape('RemoveDuplicateSuffix(visibleLabel)') -or
    $customerDimensionSource -notmatch [regex]::Escape('$"未命名维度 {unnamedOrdinal}"') -or
    $customerDimensionSource -notmatch [regex]::Escape('NormalizeForStorage') -or
    $customersSource -notmatch [regex]::Escape('CustomerDimensionCatalog.ResolveValue(fields, dimension)') -or
    $importServiceSource -notmatch [regex]::Escape('$"未命名列 {index + 1}"')) {
  throw 'Customer dynamic columns must merge equivalent visible headers and replace blank or invisible headers with stable labels.'
}
Write-Host 'PASS  customer dynamic-column deduplication and nonblank-header contract'

$customerEditCountryFailures = @()
if ($customerEditXaml.Contains('{StaticResource AiLine}')) { $customerEditCountryFailures += 'undefined_ai_line' }
if (-not $customerEditXaml.Contains('BorderBrush="{DynamicResource AiAccent}"')) { $customerEditCountryFailures += 'dynamic_ai_border' }
if ($customersXaml -notmatch 'Header="[^"]+" Binding="\{Binding Country\}"') { $customerEditCountryFailures += 'customer_country_header' }
if ($customersXaml -match 'Header="[^"]*/[^"]*" Binding="\{Binding Country\}"') { $customerEditCountryFailures += 'legacy_customer_country_header' }
if ($emailInboxXaml -match 'Text="[^"]*/[^"]+"[^>]*?/>\s*<TextBox x:Name="CountryBox"') { $customerEditCountryFailures += 'legacy_email_country_label' }
if ($customerEditXaml -match 'Text="[^"]*/[^"]+"[^>]*?/>\s*<TextBox x:Name="CountryBox"') { $customerEditCountryFailures += 'legacy_editor_country_label' }
if (-not $importServiceSource.Contains('"countryemail"')) { $customerEditCountryFailures += 'country_email_alias' }
if (-not $customerEditSource.Contains('ImportService.IsCoreDimension(pair.Key)')) { $customerEditCountryFailures += 'canonical_dimension_filter' }
if ($customerEditCountryFailures.Count -gt 0) {
  throw "Customer editing must load only defined theme resources and unify country aliases into one canonical country field. missing=$($customerEditCountryFailures -join ',')"
}
Write-Host 'PASS  customer editor theme resource and canonical country-field contract'

if ($importServiceSource -notmatch [regex]::Escape('MergeCustomDimensions(lead, customValues, isNew)') -or
    $importServiceSource -match [regex]::Escape('lead.CustomFields.Clear()') -or
    $importServiceSource -notmatch [regex]::Escape('SetExact(ImportField.Name, x => lead.Name = x);') -or
    $importServiceSource -notmatch [regex]::Escape('SetExact(ImportField.Notes, x => lead.LatestMessage = x);') -or
    $guideCatalogSource -notmatch [regex]::Escape('["customers"] = ModuleGuideVersion + 6')) {
  throw 'Spreadsheet reimports must merge by Buyer ID: update present columns, add new dimensions and preserve absent old dimensions.'
}
Write-Host 'PASS  Buyer ID incremental spreadsheet-merge contract'

if ($mainWindowSource -notmatch [regex]::Escape('_updates.StartMonitoring();') -or
    $mainWindowSource -notmatch [regex]::Escape('ApplyAndRestart();') -or
    $mainWindowSource -notmatch [regex]::Escape('已下载 · 点击更新并重启') -or
    $updateStateSource -notmatch 'IAsyncDisposable' -or
    $updateStateSource -notmatch [regex]::Escape('void StartMonitoring();') -or
    $updateServiceSource -notmatch [regex]::Escape('TimeSpan.FromMinutes(2)') -or
    $updateServiceSource -notmatch [regex]::Escape('private async Task MonitorAsync') -or
    $updateServiceSource -notmatch [regex]::Escape('await CheckAndDownloadAsync(cancellationToken: cancellationToken);') -or
    $updateServiceSource -notmatch [regex]::Escape('if (versionComparison <= 0)') -or
    $appStartupSource -notmatch [regex]::Escape('Updates.DisposeAsync().AsTask().GetAwaiter().GetResult();')) {
  throw 'The app must continuously monitor GitHub Releases, download only newer versions, expose a direct update-and-restart action and stop the monitor cleanly.'
}
Write-Host 'PASS  continuous GitHub update monitoring, automatic download and user-confirmed restart contract'

if ($customerDimensionSource -notmatch [regex]::Escape('PrimaryCategoryPreferenceLabel = "一级品类偏好"') -or
    $customerDimensionSource -notmatch [regex]::Escape('ResolvePrimaryCategoryPreference') -or
    $customersXaml -notmatch 'x:Name="CategoryPreferenceFilter"' -or
    $customersXaml -notmatch 'Header="一级品类偏好" Binding="\{Binding PrimaryCategoryPreference\}"' -or
    $customersSource -notmatch [regex]::Escape('Where(dimension => !CustomerDimensionCatalog.IsPrimaryCategoryPreference(dimension))') -or
    $customersSource -notmatch [regex]::Escape('CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(lead)') -or
    $campaignsXaml -notmatch 'x:Name="CustomerCategoryPreferenceFilterBox"' -or
    $campaignsXaml -notmatch 'Header="一级品类偏好" Binding="\{Binding PrimaryCategoryPreference\}"' -or
    $campaignsSource -notmatch [regex]::Escape('row.PrimaryCategoryPreference.Equals(category') -or
    $guideCatalogSource -notmatch [regex]::Escape('["broadcast"] = ModuleGuideVersion + 2')) {
  throw 'Customer List and Automation Campaigns must share one primary-category preference resolver, column and filter without duplicating the dynamic source field.'
}
Write-Host 'PASS  shared primary-category preference display and filtering contract'

if (-not ($leadIntelligenceSource.Contains('var allLeads = await _services.Repository.GetLeadsAsync();')) -or
    -not ($leadIntelligenceSource.Contains('allLeads.Count(lead => lead.AnalysisStatus == AnalysisStatus.RetryableFailed)')) -or
    -not ($leadIntelligenceSource.Contains('private void UpdateBulkAnalyzeButtonRunningContent(int completed, int total)')) -or
    -not ($leadIntelligenceSource.Contains('UpdateBulkAnalyzeButtonRunningContent(0, allLeads.Count);')) -or
    -not ($leadIntelligenceSource.Contains('UpdateBulkAnalyzeButtonRunningContent(progress.Completed, progress.Total);')) -or
    -not ($leadIntelligenceSource.Contains('if (_bulkCancellation is null) return;')) -or
    -not ($guideCatalogSource.Contains('["intelligence"] = ModuleGuideVersion + 3'))) {
  throw 'Lead Intelligence bulk action must show global idle counts, live running progress, ignore late callbacks and explain filter scope.'
}
Write-Host 'PASS  Lead Intelligence global retry count and live bulk-action text contract'

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
  @('SidebarMuted', 'Sidebar'), @('SidebarMuted', 'SidebarElevated'),
  @('UnreadBadgeText', 'UnreadBadgeBackground')
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
Write-Host 'PASS  light/dark-theme dynamic resources, aligned vector navigation icons and high-contrast text contract'

if ($appStartupSource -notmatch [regex]::Escape('DesktopShortcutService.EnsureForInstalledApp();') -or
    $desktopShortcutSource -notmatch [regex]::Escape('ShortcutLocation.Desktop') -or
    $desktopShortcutSource -notmatch [regex]::Escape('ShortcutLocation.StartMenuRoot') -or
    $desktopShortcutSource -notmatch [regex]::Escape('VelopackLocator.IsCurrentSet') -or
    $desktopShortcutSource -notmatch [regex]::Escape('updateOnly: false') -or
    $desktopShortcutSource -notmatch [regex]::Escape('WScript.Shell') -or
    $desktopShortcutSource -notmatch [regex]::Escape('Environment.SpecialFolder.DesktopDirectory') -or
    $desktopShortcutSource -notmatch [regex]::Escape('Environment.SpecialFolder.Programs') -or
    $desktopShortcutSource -notmatch [regex]::Escape('File.Exists(shortcutPath)') -or
    $windowsInstallerTestSource -notmatch [regex]::Escape('$shortcutBackups[$shortcutPath] = [IO.File]::ReadAllBytes($shortcutPath)') -or
    $windowsInstallerTestSource -notmatch [regex]::Escape('[IO.File]::WriteAllBytes($shortcutPath, $shortcutBackups[$shortcutPath])') -or
    $windowsInstallerTestSource -notmatch [regex]::Escape('ShortcutsVerified = $shortcutTargets.Count -eq 2') -or
    $velopackBuildSource -notmatch [regex]::Escape("--shortcuts 'Desktop,StartMenuRoot'")) {
  throw 'Windows install/update must recreate and verify shortcuts, while isolated QA restores the real user links.'
}
Write-Host 'PASS  Velopack plus verified Windows-native post-update shortcut repair contract'
if ($domainModelsSource -notmatch [regex]::Escape('public int UiScalePercentage { get; set; } = 100;') -or
    $uiScaleSource -notmatch [regex]::Escape('SupportedPercentages = [80, 90, 100, 110, 125]') -or
    $uiScaleHostSource -notmatch 'class UiScaleHost : Decorator' -or
    $mainWindowXaml -notmatch 'x:Name="MainScaleHost"' -or
    $settingsXaml -notmatch 'x:Name="SettingsScaleHost"' -or
    $settingsXaml -notmatch 'x:Name="UiScaleBox"' -or
    $mainWindowXaml -notmatch '<TextBlock Grid.Column="1" Text="设置" Style="\{StaticResource NavText\}"' -or
    $mainWindowXaml -notmatch '<Button Content="设置" ToolTip="AI 模型、界面缩放、主题与本地体验"' -or
    $mainWindowSource -notmatch [regex]::Escape('ApplyUiScale(settings.UiScalePercentage);') -or
    $settingsSource -notmatch [regex]::Escape('_settings.UiScalePercentage = UiScaleManager.Normalize(') -or
    $settingsSource -match [regex]::Escape('throw new InvalidOperationException("当前 Provider 的 API Key 未通过验证。")')) {
  throw 'Unified Settings must persist and apply 80-125% UI scaling without requiring an API key.'
}
Write-Host 'PASS  unified sidebar Settings and persisted responsive UI scaling contract'
if ($velopackBuildSource -notmatch [regex]::Escape('[Text.UTF8Encoding]::new($true)') -or
    $velopackBuildSource -notmatch [regex]::Escape('NotesMarkdown.Contains([char]0xFFFD)')) {
  throw 'Velopack release notes must be BOM-marked UTF-8 and reject replacement characters before publishing.'
}
Write-Host 'PASS  Velopack Chinese release-notes encoding contract'

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

if ($mainWindowXaml -notmatch 'Tag="knowledge"' -or
    $knowledgeBaseXaml -notmatch 'x:Name="UploadButton"' -or
    $knowledgeBaseXaml -notmatch 'x:Name="RetrievalQueryBox"' -or
    $knowledgeBaseXaml -notmatch 'x:Name="ConflictGrid"' -or
    $knowledgeBaseSource -notmatch [regex]::Escape('ResolveConflictAsync') -or
    $knowledgeServiceSource -notmatch [regex]::Escape('MaximumFileSize = 50L * 1024 * 1024') -or
    $knowledgeServiceSource -notmatch [regex]::Escape('ValidateOfficeArchive') -or
    $knowledgeServiceSource -notmatch [regex]::Escape('SourceKind == KnowledgeSourceKind.AiDraft') -or
    $knowledgeModelsSource -notmatch 'KnowledgeScopeKind[\s\S]*?Global[\s\S]*?Account[\s\S]*?Customer[\s\S]*?Conversation[\s\S]*?Temporary' -or
    $localRepositorySource -notmatch 'CREATE TABLE IF NOT EXISTS knowledge_documents' -or
    $localRepositorySource -notmatch 'CREATE TABLE IF NOT EXISTS knowledge_retrieval_logs') {
  throw 'Knowledge Base must expose governed upload/review/search/conflict UI, immutable local storage, five strict scopes and durable retrieval audit.'
}
Write-Host 'PASS  Knowledge Base governed RAG, scope and audit contract'

if ($bridgeSource -notmatch 'receiptBelongsToPhone' -or
    $bridgeSource -notmatch 'targetVerified:\s*true' -or
    $bridgeSource -notmatch 'whatsapp_target_mismatch' -or
    $bridgeSource -notmatch 'whatsapp_server_message_id_missing') {
  throw 'WhatsApp bridge must verify the recipient and require a server message id before confirming a send.'
}

if ($bridgeOutboundRoutingSource -notmatch 'jidNormalizedUser' -or
    $bridgeSource -notmatch [regex]::Escape('getUSyncDevices([senderIdentity, targetJid], false, false)') -or
    $bridgeSource -notmatch 'senderDeviceSyncPrepared:\s*true' -or
    $bridgeSource -notmatch 'whatsapp_sender_device_sync_unavailable') {
  throw 'WhatsApp outbound sends must strip device-qualified JIDs and refresh sender/recipient devices before customer delivery.'
}
& $node (Join-Path $root 'bridge\scripts\outbound-routing-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge outbound-routing smoke test failed.' }
Write-Host 'PASS  WhatsApp sender multi-device synchronization contract'

$groupContractFailures = @()
if ($bridgeConversationRoutingSource -notmatch 'normalizeGroupJid') { $groupContractFailures += 'group JID normalizer' }
if ($bridgeConversationRoutingSource -notmatch [regex]::Escape('@g.us')) { $groupContractFailures += 'group JID server' }
if ($bridgeSource -notmatch [regex]::Escape('resolveConversationJid')) { $groupContractFailures += 'inbound conversation resolver' }
if ($bridgeSource -notmatch 'isGroup:\s*true') { $groupContractFailures += 'group chat normalization' }
if ($bridgeSource -notmatch 'participantName') { $groupContractFailures += 'participant attribution' }
if ($whatsAppSyncSource -notmatch 'GetWhatsAppConversationByIdAsync') { $groupContractFailures += 'group persistence lookup' }
if ($whatsAppSyncSource -notmatch [regex]::Escape('IsGroup = isGroup')) { $groupContractFailures += 'group persistence flag' }
if ($customerSuccessSource -notmatch [regex]::Escape('if (message.IsGroup) return;')) { $groupContractFailures += 'agent isolation' }
if ($whatsAppInboxXaml -notmatch [regex]::Escape('Text="{Binding SenderLabel}"')) { $groupContractFailures += 'member label UI' }
if ($whatsAppInboxSource -notmatch [regex]::Escape('ChatModeBadgeText.Text = conversation.IsGroup ? "GROUP VIEW"') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('Customer Brain')) { $groupContractFailures += 'group safety explanation' }
if ($groupContractFailures.Count -gt 0) {
  throw "WhatsApp groups must synchronize as durable unread conversations with participant labels and strict CRM/AI isolation. missing=$($groupContractFailures -join ', ')"
}
& $node (Join-Path $root 'bridge\scripts\conversation-routing-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge conversation-routing smoke test failed.' }
Write-Host 'PASS  WhatsApp group receive, unread, participant attribution and CRM/AI isolation contract'

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

if ($customerSuccessSource -notmatch 'requestedMode == ConversationAgentMode\.CopilotActive' -or
    $customerSuccessSource -notmatch 'CustomerSuccessRunStatus\.CopilotDraftReady' -or
    $customerSuccessSource -notmatch 'RaiseRunCompleted') {
  throw 'Customer Success Agent copilot mode must auto-generate a review draft and publish a visible completion event without entering the send path.'
}
foreach ($requiredControl in @(
  'AgentModeGuideTitleText',
  'AgentModeTriggerText',
  'AgentModeOutputText',
  'AgentModeSendText',
  'AgentRunStatusText',
  'AgentRunReplyText',
  'GenerateAgentSuggestionButton',
  'UseAgentDraftButton'
)) {
  if ($whatsAppInboxXaml -notmatch [regex]::Escape("x:Name=`"$requiredControl`"")) {
    throw "WhatsApp Inbox agent-mode explanation or output control is missing: $requiredControl"
  }
}
if ($whatsAppInboxSource -notmatch 'AgentModeCombo_SelectionChanged' -or
    $whatsAppInboxSource -notmatch 'CustomerSuccessCoordinator_RunCompleted' -or
    $whatsAppInboxSource -notmatch 'UseAgentDraft_Click') {
  throw 'WhatsApp Inbox must explain each agent mode, refresh background output and let the user place review drafts into the composer.'
}
if ($whatsAppInboxXaml -match 'LinkedAccountsText|AccountRelationshipText|关联账号：|本账号关系：' -or
    $whatsAppInboxSource -match 'LinkedAccountsText|AccountRelationshipText') {
  throw 'WhatsApp Customer Success card must not expose linked-account, primary-account or per-account relationship internals.'
}
Write-Host 'PASS  Customer Success Agent mode behavior and visible-output contract'

if ($bridgeSource -notmatch [regex]::Escape('proto.WebMessageInfo.StubType.CIPHERTEXT') -or
    $bridgeSource -notmatch [regex]::Escape('update.update?.message') -or
    $bridgeSource -notmatch [regex]::Escape('if (numericStatus == null) continue') -or
    $bridgeMessageSource -notmatch [regex]::Escape('normalizeMessageContent') -or
    $whatsAppInboxSource -match '媒体消息' -or
    $whatsAppInboxSource -notmatch 'ShouldReplaceContentWith') {
  throw 'WhatsApp Inbox must recover ciphertext placeholders, replace stale bubbles and never mislabel empty text as media.'
}
& $node (Join-Path $root 'bridge\scripts\message-content-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge message-content smoke test failed.' }
Write-Host 'PASS  WhatsApp placeholder recovery and accurate message classification contract'

$emailProviderContractTokens = @(
  'EmailProviderGuide',
  'https://myaccount.google.com/apppasswords',
  'Outlook.com',
  'OAuth2 / Modern Auth',
  'https://login.yahoo.com/account/security',
  'https://account.apple.com/account/manage/section/security',
  'SecureSocketOptions.StartTls;'
)
$missingEmailProviderTokens = @($emailProviderContractTokens | Where-Object { -not $emailServiceSource.Contains($_) })
if ($missingEmailProviderTokens.Count -gt 0) {
  throw "Email providers must expose accurate platform-specific setup, credential and encrypted transport guidance. missing=$($missingEmailProviderTokens -join ', ')"
}
if ($emailAccountXaml -notmatch 'x:Name="GuideStepsText"' -or
    $emailAccountXaml -notmatch 'x:Name="ProviderSetupButton"' -or
    $emailAccountXaml -notmatch 'x:Name="PasswordHintText"' -or
    $emailAccountXaml -notmatch 'x:Name="ResetPresetButton"' -or
    -not ($emailAccountSource.Contains('account?.Provider ?? EmailProviderKind.Gmail')) -or
    -not ($emailAccountSource.Contains('UserNameBox.Text = EmailBox.Text.Trim()')) -or
    -not ($emailAccountSource.Contains('ImapHostBox.Clear()')) -or
    -not ($emailAccountSource.Contains('UseShellExecute = true')) -or
    -not ($guideCatalogSource.Contains('["email"] = ModuleGuideVersion + 3'))) {
  throw 'Email account window must provide provider steps, direct official links, field hints, username sync and preset recovery.'
}
Write-Host 'PASS  provider-specific email onboarding and compatibility guidance contract'

if ($appStartupSource -notmatch [regex]::Escape('Services.MessagingSync.StartAsync()') -or
    $appStartupSource -notmatch [regex]::Escape('Services.MessagingSync.DisposeAsync()') -or
    $appStartupSource -notmatch [regex]::Escape('Services.Email.DisposeAsync()') -or
    $messagingSyncSource -notmatch [regex]::Escape('GetWhatsAppAccountsAsync') -or
    $messagingSyncSource -notmatch [regex]::Escape('EnsureConnectedAsync(account.Id') -or
    $messagingSyncSource -notmatch [regex]::Escape('HasStoredSession(account.Id)') -or
    $whatsAppManagerSource -notmatch [regex]::Escape('IsAutoReconnectEnabled') -or
    $emailServiceSource -notmatch [regex]::Escape('IdleAsync') -or
    $emailServiceSource -notmatch [regex]::Escape('ImapCapabilities.Idle') -or
    $emailServiceSource -notmatch [regex]::Escape('TimeSpan.FromSeconds(30)')) {
  throw 'All saved WhatsApp and email accounts must remain connected and synchronize globally for the application lifetime.'
}
if ($mainWindowXaml -notmatch 'x:Name="WhatsAppUnreadBadge"' -or
    $mainWindowXaml -notmatch 'x:Name="EmailUnreadBadge"' -or
    $mainWindowXaml -notmatch 'DynamicResource UnreadBadgeBackground' -or
    $mainWindowSource -notmatch [regex]::Escape('GetInboxUnreadTotalsAsync') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.WhatsAppSync.MessageSynchronized += MessagingUnreadChanged') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.WhatsAppSync.SynchronizationChanged += WhatsAppSynchronizationChanged') -or
    $mainWindowSource -notmatch [regex]::Escape('Interval = TimeSpan.FromSeconds(5)') -or
    $localRepositorySource -notmatch [regex]::Escape('allowUnreadIncrease') -or
    $whatsAppSyncSource -notmatch [regex]::Escape('allowUnreadIncrease: unreadIncreased') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('FindOwnedPeerAccount') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.Email.SynchronizationChanged += EmailSynchronizationChanged')) {
  throw 'WhatsApp and email sidebar navigation must expose durable live application-wide unread counters with periodic reconciliation.'
}
Write-Host 'PASS  application-wide account synchronization, durable unread cursor and sidebar badge contract'

if ($whatsAppNamingSource -notmatch [regex]::Escape('lead?.DisplayName') -or
    $whatsAppSyncSource -notmatch 'WhatsAppConversationNaming\.Resolve' -or
    $localRepositorySource -notmatch 'WhatsAppConversationNaming\.Resolve' -or
    $whatsAppInboxSource -notmatch 'WhatsAppConversationNaming\.Resolve') {
  throw 'WhatsApp conversation naming must prefer the unique CRM phone match and fall back to the native phone remark.'
}
Write-Host 'PASS  WhatsApp conversation CRM-name priority and phone-remark fallback contract'

if ($emailInboxXaml -notmatch [regex]::Escape('Visibility="{Binding UnreadVisibility}"') -or
    $emailInboxXaml -notmatch [regex]::Escape('Text="{Binding UnreadLabel}"') -or
    $emailInboxXaml -notmatch [regex]::Escape('DynamicResource PrimarySoft') -or
    $emailInboxSource -notmatch [regex]::Escape('class EmailConversationItem') -or
    $emailInboxSource -notmatch [regex]::Escape('Unread > 99 ? "99+"') -or
    $emailInboxSource -notmatch [regex]::Escape('item.MarkRead(DateTimeOffset.Now)') -or
    $emailInboxSource -notmatch [regex]::Escape('MarkEmailConversationReadAsync(conversation.Id)')) {
  throw 'Email Inbox must show a per-conversation unread badge, cap it at 99+, and clear it immediately with the durable read cursor.'
}
Write-Host 'PASS  Email Inbox per-conversation unread badge and immediate read-state contract'

if ($emailInboxXaml -notmatch 'x:Name="NewEmailButton"' -or
    $emailInboxXaml -notmatch 'x:Name="RecipientBox"' -or
    $emailInboxXaml -notmatch 'x:Name="EmailAiInstructionBox"' -or
    $emailInboxXaml -notmatch 'x:Name="AiSidebarScoreRing"' -or
    $emailInboxSource -notmatch [regex]::Escape('NewEmail_Click') -or
    $emailInboxSource -notmatch [regex]::Escape('_services.EmailAssistant.AnalyzeAsync') -or
    $emailInboxSource -notmatch [regex]::Escape('UseEmailDraft_Click') -or
    $emailAssistantSource -notmatch [regex]::Escape('AiModuleKeys.EmailInbox') -or
    $emailAssistantSource -notmatch [regex]::Escape('userInstruction') -or
    $emailAssistantSource -notmatch [regex]::Escape('CompleteStructuredAsync<EmailAssistantResult>') -or
    $settingsSource -notmatch [regex]::Escape('Email Sales Copilot') -or
    $guideCatalogSource -notmatch [regex]::Escape('Email Sales Copilot') -or
    $guideCatalogSource -notmatch [regex]::Escape('GlobalGuideVersion = 9') -or
    $guideCatalogSource -notmatch 'Ctrl\+1 . Ctrl\+8' -or
    $guideCatalogSource -match 'Ctrl\+1 . Ctrl\+7' -or
    $guideCatalogSource -notmatch [regex]::Escape('["customers"] = ModuleGuideVersion + 6') -or
    $guideCatalogSource -notmatch [regex]::Escape('["broadcast"] = ModuleGuideVersion + 2') -or
    $guideCatalogSource -notmatch [regex]::Escape('["analytics"] = ModuleGuideVersion + 1') -or
    $guideCatalogSource -notmatch [regex]::Escape('["settings"] = ModuleGuideVersion + 6')) {
  throw 'Email Inbox must support new-message composition, CRM/Customer Brain-aware AI drafting, manual-send safety and current module guidance.'
}
Write-Host 'PASS  Email Inbox new-message, Customer Intelligence, AI draft and all-module guide audit contract'

$aiRoutingUiTokens = @(
  'x:Name="ReasoningEffortBox"',
  'x:Name="UseGlobalAiConfigurationBox"',
  'x:Name="ModuleRoutingItems"',
  'x:Name="AiRoutingSummaryText"',
  'x:Name="ModuleRoutingPanel"'
)
$missingAiRoutingUiTokens = @($aiRoutingUiTokens | Where-Object { -not $settingsXaml.Contains($_) })
if ($missingAiRoutingUiTokens.Count -gt 0) {
  throw "AI settings must expose global/per-module model routing and fail-safe reasoning guidance. missing=$($missingAiRoutingUiTokens -join ', ')"
}
$aiRoutingCoreTokens = @(
  'UseGlobalAiConfiguration',
  'DefaultReasoningEffort',
  'AiModulePreferences',
  'ModelCapabilities',
  'supported_efforts',
  'ApplyReasoningEffort',
  'ReasoningEffort == AiReasoningEfforts.Auto'
)
$combinedAiRoutingSource = $domainModelsSource + $deepSeekSource + $settingsSource
$missingAiRoutingCoreTokens = @($aiRoutingCoreTokens | Where-Object { -not $combinedAiRoutingSource.Contains($_) })
if ($missingAiRoutingCoreTokens.Count -gt 0) {
  throw "AI routing must persist module overrides, parse API capabilities and suppress undeclared reasoning parameters. missing=$($missingAiRoutingCoreTokens -join ', ')"
}
if (-not $conversationAssistantSource.Contains('AiModuleKeys.WhatsAppInbox') -or
    -not $customerSuccessAgentSource.Contains('AiModuleKeys.WhatsAppInbox') -or
    -not $domainModelsSource.Contains('AiModuleKeys') -or
    -not $domainModelsSource.Contains('public const string LeadIntelligence') -or
    -not $settingsSource.Contains('AiModuleKeys.LeadIntelligence') -or
    -not $deepSeekSource.Contains('ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence') -or
    -not $leadAutomationSource.Contains('ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence') -or
    -not $customerBrainSource.Contains('AiModuleKeys.Customers') -or
    -not $customerAnalysisSource.Contains('AiModuleKeys.CustomerAnalytics') -or
    -not $knowledgeProcessingSource.Contains('AiModuleKeys.KnowledgeBase')) {
  throw 'Every current AI workload, including Lead Intelligence, must route through its own module key.'
}
Write-Host 'PASS  global/per-module AI model, token-cost and declared reasoning-depth routing contract'

if (-not $leadIntelligenceSource.Contains('ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence') -or
    -not $leadIntelligenceSource.Contains('execution.Model') -or
    $leadIntelligenceSource.Contains('settings.DeepSeekModel') -or
    $settingsSource -match 'if \(!string\.IsNullOrWhiteSpace\(activeKey\)\)\s*await _services\.LeadAutomation\.NotifyProviderConfiguredAsync\(\);' -or
    -not $settingsSource.Contains('_ = ResumeQueuedLeadAnalysisAsync()') -or
    $mainWindowSource -notmatch [regex]::Escape('await _intelligence.RefreshAiRouteAsync();') -or
    $mainWindowSource -match 'await UpdateThemeStateAsync\(\);\s*await RefreshAllAsync\(\);') {
  throw 'Saving module AI routes must be non-blocking, and Lead Intelligence must display the same resolved route used by its real requests.'
}
Write-Host 'PASS  non-blocking module-route save and actual Lead Intelligence route display contract'

if ($todayBriefSource.Contains('"cross_account"') -or
    $todayBriefSource.Contains('GetAgentStatesAsync') -or
    -not $todayBriefSource.Contains('CrossAccountFollowUpCount = 0')) {
  throw 'Today Brief must treat one customer appearing in multiple WhatsApp accounts as normal unified customer context, not a follow-up task.'
}
Write-Host 'PASS  unified cross-account customer context without duplicate-responsibility reminders'

& $dotnet build (Join-Path $root 'desktop\WAFlow.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop build failed.' }
& $dotnet run --project (Join-Path $root 'desktop\WAFlow.SmokeTests\WAFlow.SmokeTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop smoke tests failed.' }
