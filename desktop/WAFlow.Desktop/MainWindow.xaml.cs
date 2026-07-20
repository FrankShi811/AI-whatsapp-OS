using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
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
    private readonly DraftsView _drafts;
    private readonly CampaignsView _campaigns;
    private Button? _activeButton;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _dashboard = new DashboardView(services);
        _intelligence = new LeadIntelligenceView(services);
        _customers = new CustomersView(services);
        _inbox = new WhatsAppInboxView(services);
        _drafts = new DraftsView(services);
        _campaigns = new CampaignsView(services);
        _intelligence.ImportRequested += OpenImport;
        _intelligence.DataChanged += async (_, _) => await RefreshAllAsync();
        _customers.ImportRequested += OpenImport;
        _customers.DataChanged += async (_, _) => await RefreshAllAsync();
        _inbox.DataChanged += async (_, _) => await RefreshAllAsync();
        _drafts.DataChanged += async (_, _) => await RefreshAllAsync();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await UpdateProviderStateAsync();
        await NavigateAsync("dashboard", DashboardButton);
        if (await _services.Repository.GetSalesProfileAsync() is null)
        {
            var window = new SettingsWindow(_services) { Owner = this };
            if (window.ShowDialog() == true) await UpdateProviderStateAsync();
        }
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
            "drafts" => ((object)_drafts, "WhatsApp 草稿", "人工确认后复制或打开 WhatsApp"),
            "campaigns" => ((object)_campaigns, "Campaign Automation", "个人账号逐人排期、限速、退订拦截与发送审计"),
            _ => ((object)_dashboard, "Dashboard", "全球买家商机总览")
        };
        ContentHost.Content = target.Content; TopTitle.Text = target.Title; TopSubtitle.Text = target.Subtitle;
        if (ContentHost.Content is IRefreshableView view) await view.RefreshAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_services) { Owner = this };
        if (window.ShowDialog() == true) { await UpdateProviderStateAsync(); await RefreshAllAsync(); }
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
        await _dashboard.RefreshAsync(); await _intelligence.RefreshAsync(); await _customers.RefreshAsync(); await _inbox.RefreshAsync(); await _drafts.RefreshAsync(); await _campaigns.RefreshAsync();
    }
}
