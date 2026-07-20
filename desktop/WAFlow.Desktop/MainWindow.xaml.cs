using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Desktop.Pages;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly DashboardView _dashboard;
    private readonly LeadIntelligenceView _intelligence;
    private readonly CustomersView _customers;
    private readonly WhatsAppInboxView _inbox;
    private readonly CampaignsView _campaigns;
    private Button? _activeButton;
    private int _onboardingStep;
    private static readonly GuideStep[] GuideSteps =
    [
        new("欢迎使用 AI Sales OS", "这是一套本地原生的 WhatsApp 商机管理与销售自动化工具。客户资料、任务和同步消息默认保存在当前电脑，不需要启动本地网页服务。", "先认识左侧主导航", "Dashboard 查看销售脉搏；商机智能用于评分与 AI 建议；客户列表保存所有原表字段；WhatsApp Inbox 管理会话；自动化群发建立逐人发送任务。"),
        new("先完成企业与 AI 设置", "企业名称、主营产品、优势和默认销售语言是 AI 分析与话术生成的基础资料，属于首次使用必须完成的设置。DeepSeek API Key 仅在使用 AI 功能时需要。", "必须设置：右上角“企业与 AI 设置”", "API Key 只写入 Windows 凭据管理器，不写入数据库和日志。企业销售资料保存后可继续下一步。", true),
        new("导入并维护客户数据", "在客户列表或商机智能页面点击“导入客户”。Excel / CSV 的每一列都会保留，表格自定义列也可以成为群发话术字段。", "建议先导入客户并检查号码", "请补充有效 WhatsApp 号码；营销自动发送还要求勾选“客户已同意接收 WhatsApp 营销消息”，已退订客户会被强制排除。"),
        new("连接 WhatsApp Inbox", "进入 WhatsApp Inbox，选择个人账号并扫描二维码。连接后会同步联系人、历史消息和客户侧栏资料。", "必须连接：WhatsApp Inbox", "非官方个人号连接存在限制或封号风险。公网 IP 会每 60 秒检测一次；网络或 VPN 变化时软件会提醒，但无法保证账号安全。"),
        new("建立自动化群发任务", "先保存话术模板并插入客户字段，再单选或多选客户，选择即时或北京时间定时任务，设置逐条发送间隔，预览后人工批准。", "开始使用：WhatsApp 自动化群发", "每位客户会生成独立文本快照。发送间隔只是节奏控制，不能规避 WhatsApp 风控；任务运行期间请保持软件开启且账号已连接。")
    ];

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _dashboard = new DashboardView(services);
        _intelligence = new LeadIntelligenceView(services);
        _customers = new CustomersView(services);
        _inbox = new WhatsAppInboxView(services);
        _campaigns = new CampaignsView(services);
        _intelligence.ImportRequested += OpenImport;
        _intelligence.DataChanged += async (_, _) => await RefreshAllAsync();
        _customers.ImportRequested += OpenImport;
        _customers.DataChanged += async (_, _) => await RefreshAllAsync();
        _inbox.DataChanged += async (_, _) => await RefreshAllAsync();
        _campaigns.DataChanged += async (_, _) => await RefreshAllAsync();
        Loaded += MainWindow_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTaskbarIdentity.ApplyWindowIcon(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        WindowsTaskbarIdentity.ReleaseWindowIcon();
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await UpdateProviderStateAsync();
        await NavigateAsync("dashboard", DashboardButton);
        var onboarding = await _services.Repository.GetOnboardingStateAsync();
        if (!onboarding.Completed) ShowGuide(0);
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) await NavigateAsync(page, button);
    }

    private async Task NavigateAsync(string page, Button button)
    {
        if (_activeButton is not null) { _activeButton.Background = System.Windows.Media.Brushes.Transparent; _activeButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(201, 219, 212)); }
        _activeButton = button; button.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 77, 61)); button.Foreground = System.Windows.Media.Brushes.White;
        (object Content, string Title, string Subtitle) target = page switch
        {
            "intelligence" => ((object)_intelligence, "商机智能", "评分证据、客户画像与下一步动作"),
            "customers" => ((object)_customers, "客户列表", "搜索、标签、阶段、等级和负责人筛选"),
            "inbox" => ((object)_inbox, "WhatsApp Inbox", "个人账号二维码连接、会话和客户资料联动"),
            "broadcast" => ((object)_campaigns, "WhatsApp 自动化群发", "动态字段话术、客户选择、即时 / 定时任务与发送审计"),
            _ => ((object)_dashboard, "Dashboard", "全球买家商机总览")
        };
        ContentHost.Content = target.Content; TopTitle.Text = target.Title; TopSubtitle.Text = target.Subtitle;
        if (ContentHost.Content is IRefreshableView view) await view.RefreshAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await OpenSettingsAsync();
    }

    private async Task OpenSettingsAsync()
    {
        var window = new SettingsWindow(_services) { Owner = this };
        if (window.ShowDialog() == true) { await UpdateProviderStateAsync(); await RefreshAllAsync(); }
    }

    private void ShowGuide_Click(object sender, RoutedEventArgs e) => ShowGuide(0);

    private void ShowGuide(int step)
    {
        _onboardingStep = Math.Clamp(step, 0, GuideSteps.Length - 1);
        var item = GuideSteps[_onboardingStep];
        OnboardingStepText.Text = $"第 {_onboardingStep + 1} / {GuideSteps.Length} 步";
        OnboardingTitle.Text = item.Title;
        OnboardingBody.Text = item.Body;
        OnboardingTarget.Text = item.Target;
        OnboardingTips.Text = item.Tips;
        OnboardingSettingsButton.Visibility = item.ShowSettings ? Visibility.Visible : Visibility.Collapsed;
        OnboardingBackButton.IsEnabled = _onboardingStep > 0;
        OnboardingNextButton.Content = _onboardingStep == GuideSteps.Length - 1 ? "完成并开始使用" : "下一步";
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void SkipGuide_Click(object sender, RoutedEventArgs e) => OnboardingOverlay.Visibility = Visibility.Collapsed;

    private void OnboardingBack_Click(object sender, RoutedEventArgs e) => ShowGuide(_onboardingStep - 1);

    private async void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep < GuideSteps.Length - 1) { ShowGuide(_onboardingStep + 1); return; }
        if (await _services.Repository.GetSalesProfileAsync() is null)
        {
            MessageBox.Show("请先完成企业名称、主营产品、优势和默认销售语言，再结束首次使用引导。", "需要完成企业销售资料", MessageBoxButton.OK, MessageBoxImage.Information);
            await OpenSettingsAsync();
            if (await _services.Repository.GetSalesProfileAsync() is null) return;
        }
        await _services.Repository.SaveOnboardingStateAsync(new OnboardingState { Completed = true, GuideVersion = 1, CompletedAt = DateTimeOffset.Now });
        OnboardingOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnboardingSettings_Click(object sender, RoutedEventArgs e)
    {
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        await OpenSettingsAsync();
        OnboardingOverlay.Visibility = Visibility.Visible;
        ShowGuide(_onboardingStep);
    }

    private async void OpenImport(object? sender, EventArgs e)
    {
        var window = new ImportWindow(_services) { Owner = this };
        if (window.ShowDialog() == true) await RefreshAllAsync();
    }

    private async Task UpdateProviderStateAsync()
    {
        var configured = _services.DeepSeek.HasApiKey();
        ProviderText.Text = configured ? "DeepSeek 已配置" : "DeepSeek 未配置";
        ProviderBadge.Background = (System.Windows.Media.Brush)FindResource(configured ? "SuccessSoft" : "WarningSoft");
        ProviderText.Foreground = new System.Windows.Media.SolidColorBrush(configured ? System.Windows.Media.Color.FromRgb(15, 112, 79) : System.Windows.Media.Color.FromRgb(138, 97, 16));
        await Task.CompletedTask;
    }

    private async Task RefreshAllAsync()
    {
        await _dashboard.RefreshAsync(); await _intelligence.RefreshAsync(); await _customers.RefreshAsync(); await _inbox.RefreshAsync(); await _campaigns.RefreshAsync();
    }

    private sealed record GuideStep(string Title, string Body, string Target, string Tips, bool ShowSettings = false);
}
