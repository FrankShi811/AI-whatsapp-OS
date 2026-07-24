using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;
using WAFlow.Desktop.Controls;
using WAFlow.Desktop.Pages;
using WAFlow.Desktop.Updates;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly IApplicationUpdateService _updates;
    private readonly DashboardView _dashboard;
    private readonly LeadIntelligenceView _intelligence;
    private readonly CustomersView _customers;
    private readonly WhatsAppInboxView _inbox;
    private readonly EmailInboxView _email;
    private readonly CampaignsView _campaigns;
    private readonly AnalyticsView _analytics;
    private Button? _activeButton;
    private OnboardingState _onboardingState = new();
    private bool _onboardingReady;
    private string _currentPage = "dashboard";

    public MainWindow(AppServices services, IApplicationUpdateService updates)
    {
        InitializeComponent();
        GuideCatalog.ValidateCoverage();
        SidebarVersionText.Text = $"当前版本  v{ReleaseCatalog.CurrentVersion}";
        _services = services;
        _updates = updates;
        _dashboard = new DashboardView(services);
        _intelligence = new LeadIntelligenceView(services);
        _customers = new CustomersView(services);
        _inbox = new WhatsAppInboxView(services);
        _email = new EmailInboxView(services);
        _campaigns = new CampaignsView(services);
        _analytics = new AnalyticsView(services);
        _dashboard.NavigateRequested += Dashboard_NavigateRequested;
        _intelligence.ImportRequested += OpenImport;
        _intelligence.DataChanged += async (_, _) => await RefreshAllAsync();
        _customers.ImportRequested += OpenImport;
        _customers.DataChanged += async (_, _) => await RefreshAllAsync();
        _inbox.DataChanged += async (_, _) => await RefreshAllAsync();
        _email.DataChanged += async (_, _) => await RefreshAllAsync();
        _campaigns.DataChanged += async (_, _) => await RefreshAllAsync();
        _analytics.DataChanged += async (_, _) => await RefreshAllAsync();
        _services.Campaigns.SafetyStopped += Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged += LeadAutomation_AnalysisChanged;
        _updates.StateChanged += Updates_StateChanged;
        ApplyUpdateState(_updates.State);
        OnboardingGuide.CloseRequested += OnboardingGuide_CloseRequested;
        OnboardingGuide.FinishedRequested += OnboardingGuide_FinishedRequested;
        OnboardingGuide.SettingsRequested += OnboardingGuide_SettingsRequested;
        OnboardingGuide.GlobalRequested += OnboardingGuide_GlobalRequested;
        Loaded += MainWindow_Loaded;
    }

    private void VersionHistory_Click(object sender, RoutedEventArgs e) =>
        new VersionHistoryWindow(_updates) { Owner = this }.ShowDialog();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTaskbarIdentity.ApplyWindowIcon(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        WindowsTaskbarIdentity.ReleaseWindowIcon();
        _services.Campaigns.SafetyStopped -= Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged -= LeadAutomation_AnalysisChanged;
        _updates.StateChanged -= Updates_StateChanged;
        OnboardingGuide.CloseRequested -= OnboardingGuide_CloseRequested;
        OnboardingGuide.FinishedRequested -= OnboardingGuide_FinishedRequested;
        OnboardingGuide.SettingsRequested -= OnboardingGuide_SettingsRequested;
        OnboardingGuide.GlobalRequested -= OnboardingGuide_GlobalRequested;
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await UpdateProviderStateAsync();
        await UpdateThemeStateAsync();
        await NavigateAsync("dashboard", DashboardButton);
        _onboardingState = await _services.Repository.GetOnboardingStateAsync();
        if (GuideCatalog.MigrateLegacyState(_onboardingState))
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
        _onboardingReady = true;
        if (!GuideCatalog.IsSeen(_onboardingState, "global"))
            OnboardingGuide.ShowGuide(GuideCatalog.Global);
        else
            await ShowModuleGuideIfNeededAsync(_currentPage);
        _ = _updates.CheckAndDownloadAsync();
    }

    private void Updates_StateChanged(object? sender, ApplicationUpdateState state) =>
        _ = Dispatcher.InvokeAsync(() => ApplyUpdateState(state));

    private void ApplyUpdateState(ApplicationUpdateState state)
    {
        SidebarUpdateIcon.Foreground = (Brush)FindResource("Success");
        VersionButton.ToolTip = "查看版本与更新";
        switch (state.Stage)
        {
            case ApplicationUpdateStage.Checking:
                SidebarUpdateIcon.Text = "◌";
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = "正在检查 GitHub Release…";
                break;
            case ApplicationUpdateStage.Downloading:
                SidebarUpdateIcon.Text = "↓";
                SidebarVersionText.Text = $"正在下载  v{state.LatestVersion}";
                SidebarUpdateText.Text = $"下载进度 {state.DownloadProgress}%";
                break;
            case ApplicationUpdateStage.ReadyToInstall:
                SidebarUpdateIcon.Text = "●";
                SidebarUpdateIcon.Foreground = (Brush)FindResource("Warning");
                SidebarVersionText.Text = $"新版本  v{state.LatestVersion}";
                SidebarUpdateText.Text = "已下载 · 点击安装并重启";
                VersionButton.ToolTip = "更新已下载，点击安装并重启";
                break;
            case ApplicationUpdateStage.Failed:
                SidebarUpdateIcon.Text = "!";
                SidebarUpdateIcon.Foreground = (Brush)FindResource("Danger");
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = "检查失败 · 点击查看详情";
                break;
            case ApplicationUpdateStage.Disabled:
                SidebarUpdateIcon.Text = "↻";
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = state.Message;
                break;
            default:
                SidebarUpdateIcon.Text = "✓";
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = state.Stage == ApplicationUpdateStage.UpToDate ? "已是最新版本" : "启动后自动检查更新";
                break;
        }
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) await NavigateAsync(page, button);
    }

    private async void Dashboard_NavigateRequested(object? sender, string page)
    {
        var button = page switch
        {
            "intelligence" => IntelligenceButton,
            "inbox" => InboxButton,
            "email" => EmailButton,
            "broadcast" => BroadcastButton,
            "customers" => CustomersButton,
            "analytics" => AnalyticsButton,
            _ => DashboardButton
        };
        await NavigateAsync(page, button);
    }

    private async Task NavigateAsync(string page, Button button)
    {
        _currentPage = page;
        if (_activeButton is not null)
        {
            _activeButton.Background = Brushes.Transparent;
            _activeButton.Foreground = (Brush)FindResource("SidebarText");
            _activeButton.BorderBrush = Brushes.Transparent;
            _activeButton.FontWeight = FontWeights.Medium;
        }
        _activeButton = button;
        button.Background = (Brush)FindResource("SidebarActive");
        button.Foreground = (Brush)FindResource("SidebarText");
        button.BorderBrush = (Brush)FindResource("Primary");
        button.FontWeight = FontWeights.SemiBold;
        (object Content, string Title, string Subtitle) target = page switch
        {
            "intelligence" => ((object)_intelligence, "商机智能", "AI 评分证据、客户画像与下一步决策"),
            "customers" => ((object)_customers, "客户列表", "统一客户数据、动态字段与批量运营"),
            "inbox" => ((object)_inbox, "WhatsApp Inbox", "会话、客户资料与 AI 销售信号实时联动"),
            "email" => ((object)_email, "邮件 Inbox", "邮件收发、历史归档与 CRM 客户资料实时联动"),
            "broadcast" => ((object)_campaigns, "多渠道自动化触达", "WhatsApp 与邮件任务、动态字段、发送节奏与分渠道审计"),
            "analytics" => ((object)_analytics, "客户智能分析", "全量客户数据、AI 商业判断、报告版本与管理层导出"),
            _ => ((object)_dashboard, "Dashboard", "今天最值得推进的商机与动作")
        };
        ContentHost.Opacity = 0;
        ContentHost.RenderTransform = new TranslateTransform(0, 8);
        ContentHost.Content = target.Content;
        TopTitle.Text = target.Title;
        TopSubtitle.Text = target.Subtitle;
        PageGuideButton.ToolTip = $"查看“{target.Title}”的功能介绍和操作步骤";
        if (ContentHost.Content is IRefreshableView view) await view.RefreshAsync();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        ((TranslateTransform)ContentHost.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
        if (_onboardingReady && !OnboardingGuide.IsOpen) await ShowModuleGuideIfNeededAsync(page);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await OpenSettingsAsync();
    }

    private async Task OpenSettingsAsync()
    {
        var window = new SettingsWindow(_services) { Owner = this };
        var saved = window.ShowDialog() == true;
        _onboardingState = await _services.Repository.GetOnboardingStateAsync();
        if (saved) { await UpdateProviderStateAsync(); await RefreshAllAsync(); }
    }

    private void ShowGuide_Click(object sender, RoutedEventArgs e) => OnboardingGuide.ShowGuide(GuideCatalog.ForModule(_currentPage));

    private async Task ShowModuleGuideIfNeededAsync(string page)
    {
        if (!_onboardingReady || OnboardingGuide.IsOpen) return;
        if (!GuideCatalog.IsSeen(_onboardingState, page))
            OnboardingGuide.ShowGuide(GuideCatalog.ForModule(page));
        await Task.CompletedTask;
    }

    private async Task MarkModuleGuideSeenAsync(string key)
    {
        GuideCatalog.MarkSeen(_onboardingState, key);
        await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
    }

    private async Task CloseGuideAsync()
    {
        var definition = OnboardingGuide.CurrentDefinition;
        OnboardingGuide.HideGuide();
        if (definition is { IsGlobal: false })
            await MarkModuleGuideSeenAsync(definition.Key);
        else
        {
            GuideCatalog.MarkSeen(_onboardingState, "global");
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
            await ShowModuleGuideIfNeededAsync(_currentPage);
        }
    }

    private async void OnboardingGuide_CloseRequested(object? sender, EventArgs e) => await CloseGuideAsync();

    private async void OnboardingGuide_FinishedRequested(object? sender, EventArgs e)
    {
        if (OnboardingGuide.CurrentDefinition is not { } definition) return;
        if (definition.IsGlobal)
        {
            if (!_services.DeepSeek.HasApiKey())
            {
                MessageBox.Show("请先配置 DeepSeek 或兼容 AI 接口的 API Key，并从自动拉取的列表中选择模型，再结束首次使用引导。", "需要配置 AI API", MessageBoxButton.OK, MessageBoxImage.Information);
                await OpenSettingsAsync();
                if (!_services.DeepSeek.HasApiKey()) return;
            }
            GuideCatalog.MarkSeen(_onboardingState, "global");
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
            OnboardingGuide.HideGuide();
            await ShowModuleGuideIfNeededAsync(_currentPage);
            return;
        }
        await MarkModuleGuideSeenAsync(definition.Key);
        OnboardingGuide.HideGuide();
    }

    private async void OnboardingGuide_SettingsRequested(object? sender, EventArgs e)
    {
        var definition = OnboardingGuide.CurrentDefinition;
        var step = OnboardingGuide.CurrentStepIndex;
        OnboardingGuide.HideGuide();
        await OpenSettingsAsync();
        if (definition is not null) OnboardingGuide.ShowGuide(definition, step);
    }

    private void OnboardingGuide_GlobalRequested(object? sender, EventArgs e) => OnboardingGuide.ShowGuide(GuideCatalog.Global);

    private async void OpenImport(object? sender, EventArgs e)
    {
        var window = new ImportWindow(_services) { Owner = this };
        if (window.ShowDialog() == true) await RefreshAllAsync();
    }

    private async Task UpdateProviderStateAsync()
    {
        var configured = _services.DeepSeek.HasApiKey();
        var settings = await _services.Repository.GetAppSettingsAsync();
        ProviderText.Text = configured ? $"AI 已配置 · {settings.DeepSeekModel}" : "AI API 未配置";
        ProviderBadge.Background = (System.Windows.Media.Brush)FindResource(configured ? "SuccessSoft" : "WarningSoft");
        ProviderText.Foreground = (Brush)FindResource(configured ? "Success" : "Warning");
    }

    private async Task UpdateThemeStateAsync()
    {
        var settings = await _services.Repository.GetAppSettingsAsync();
        ThemeText.Text = ThemeManager.Glyph(settings.ThemeMode);
        ThemeButton.ToolTip = $"当前：{ThemeManager.Label(settings.ThemeMode)}；点击切换";
    }

    private async void Theme_Click(object sender, RoutedEventArgs e)
    {
        var settings = await _services.Repository.GetAppSettingsAsync();
        settings.ThemeMode = ThemeManager.Next(settings.ThemeMode);
        await _services.Repository.SaveAppSettingsAsync(settings);
        ThemeManager.Apply(settings.ThemeMode);
        await UpdateThemeStateAsync();
    }

    private void CommandButton_Click(object sender, RoutedEventArgs e) => ToggleCommandOverlay(true);

    private void ToggleCommandOverlay(bool show)
    {
        CommandOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        CommandOverlay.Opacity = 0;
        CommandOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
    }

    private void CommandOverlay_MouseDown(object sender, MouseButtonEventArgs e) => ToggleCommandOverlay(false);

    private void CommandPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        ToggleCommandOverlay(false);
        switch (action)
        {
            case "import": OpenImport(this, EventArgs.Empty); break;
            case "intelligence": await NavigateAsync(action, IntelligenceButton); break;
            case "inbox": await NavigateAsync(action, InboxButton); break;
            case "email": await NavigateAsync(action, EmailButton); break;
            case "broadcast": await NavigateAsync(action, BroadcastButton); break;
            case "analytics": await NavigateAsync(action, AnalyticsButton); break;
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && OnboardingGuide.IsOpen)
        {
            await CloseGuideAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && CommandOverlay.Visibility == Visibility.Visible)
        {
            ToggleCommandOverlay(false);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.K)
        {
            ToggleCommandOverlay(CommandOverlay.Visibility != Visibility.Visible);
            e.Handled = true;
            return;
        }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var target = e.Key switch
        {
            Key.D1 => ("dashboard", DashboardButton),
            Key.D2 => ("intelligence", IntelligenceButton),
            Key.D3 => ("customers", CustomersButton),
            Key.D4 => ("inbox", InboxButton),
            Key.D5 => ("email", EmailButton),
            Key.D6 => ("broadcast", BroadcastButton),
            Key.D7 => ("analytics", AnalyticsButton),
            _ => ((string Page, Button Button)?)null
        };
        if (target is null) return;
        await NavigateAsync(target.Value.Page, target.Value.Button);
        e.Handled = true;
    }

    private void LeadAutomation_AnalysisChanged(object? sender, LeadAnalysisAutomationEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await _dashboard.RefreshAsync();
            await _intelligence.RefreshAsync();
        });
    }

    private async Task RefreshAllAsync()
    {
        await _dashboard.RefreshAsync(); await _intelligence.RefreshAsync(); await _customers.RefreshAsync(); await _inbox.RefreshAsync(); await _email.RefreshAsync(); await _campaigns.RefreshAsync(); await _analytics.RefreshAsync();
    }

    private void Campaigns_SafetyStopped(object? sender, CampaignSafetyStoppedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var sent = e.Campaigns.Sum(item => item.Sent);
            var failed = e.Campaigns.Sum(item => item.Failed);
            var skipped = e.Campaigns.Sum(item => item.Skipped);
            var completed = e.Campaigns.Sum(item => item.Sent + item.Failed + item.Skipped + item.Cancelled);
            var remaining = e.Campaigns.Sum(item => item.Queued);
            var details = string.Join(Environment.NewLine, e.Campaigns.Select(item => $"• {item.Name}：已处理 {item.Progress}，成功 {item.Sent}，失败 {item.Failed}，跳过 {item.Skipped}，待发送 {item.Queued}；停止位置 {item.StopOrNext}"));
            MessageBox.Show(
                $"检测到公网 IP 与任务触发前不一致，所有自动触达任务已经停止。\n\nIP：{e.PreviousIp} → {e.CurrentIp}\n已完成处理：{completed}\n已成功发送：{sent}\n发送失败：{failed}\n已跳过：{skipped}\n尚未发送：{remaining}\n\n{details}\n\n请确认网络环境后，在群发页面手动继续任务；继续时会重新建立 IP 基线。",
                "WhatsApp 群发安全阀门已触发",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }
}
