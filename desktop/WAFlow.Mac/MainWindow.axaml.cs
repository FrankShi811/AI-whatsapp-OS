using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;
using WAFlow.Desktop;
using WAFlow.Desktop.Updates;

namespace WAFlow.Mac;

public sealed partial class MainWindow : Window
{
    private static IBrush Ink => ResourceBrush("Ink", "#15251E");
    private static IBrush Muted => ResourceBrush("Muted", "#586B64");
    private static IBrush Primary => ResourceBrush("Primary", "#087A59");
    private static IBrush PrimarySoft => ResourceBrush("PrimarySoft", "#D9F5EB");
    private static IBrush Border => ResourceBrush("Line", "#DDE5E1");
    private static IBrush Surface => ResourceBrush("Surface", "#FFFFFF");
    private static IBrush SurfaceMuted => ResourceBrush("SurfaceMuted", "#F4F7F5");
    private static IBrush Danger => ResourceBrush("Danger", "#A52D2D");
    private static IBrush Warning => ResourceBrush("Warning", "#8A5A00");
    private readonly AppServices _services;
    private readonly IApplicationUpdateService _updates = new VelopackUpdateService();
    private readonly CancellationTokenSource _lifetime = new();
    private List<Lead> _leads = [];
    private DashboardSnapshot _dashboard = new();
    private string _currentPage = "dashboard";
    private string _activeWhatsAppAccountId = "primary";
    private string _selectedWhatsAppConversationId = "";
    private string _whatsAppSearch = "";
    private string _activeEmailAccountId = "";
    private string _selectedEmailConversationId = "";
    private string _emailSearch = "";
    private int _customerPage = 1;
    private int _customerPageSize = 30;
    private string _customerSearch = "";
    private string _customerGradeFilter = "全部";
    private LeadStage? _customerStageFilter;
    private string _customerCategoryFilter = "";
    private string _customerDimensionKey = "";
    private readonly HashSet<string> _selectedCustomerIds = new(StringComparer.OrdinalIgnoreCase);
    private string _leadSearch = "";
    private string _leadGradeFilter = "全部";
    private string _selectedLeadId = "";
    private CancellationTokenSource? _bulkAnalysisCancellation;
    private string _whatsAppQrDataUrl = "";
    private string _whatsAppState = "disconnected";
    private string _operationStatus = "就绪";
    private bool _sidebarPointerInside;
    private bool _sidebarKeyboardExpanded;
    private bool _commandVisible;
    private readonly bool _reduceMotion;
    private readonly TaskCompletionSource<bool> _openedReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _navigationMotionCancellation;
    private readonly DispatcherTimer _unreadBadgeTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5)
    };

    public MainWindow()
    {
        InitializeComponent();
        _reduceMotion = PrefersReducedMotion();
        Classes.Set("reduce-motion", _reduceMotion);
        var repository = new LocalRepository(PlatformDataPaths.DatabasePath);
        _services = new AppServices(
            repository,
            target => new MacKeychainSecretStore(target));
        _updates.StateChanged += Updates_StateChanged;
        _services.WhatsApp.EventReceived += WhatsApp_EventReceived;
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSync_SynchronizationChanged;
        _services.Email.SynchronizationChanged += Email_SynchronizationChanged;
        _services.Campaigns.SafetyStopped += Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged += LeadAutomation_AnalysisChanged;
        _unreadBadgeTimer.Tick += UnreadBadgeTimer_Tick;
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        try
        {
            await _services.InitializeAsync(_lifetime.Token);
            var settings = await _services.Repository.GetAppSettingsAsync(_lifetime.Token);
            MacThemeManager.Apply(settings.ThemeMode);
            ApplyUiScale(settings.UiScalePercentage);
            await _services.Campaigns.StartAsync(_lifetime.Token);
            await _services.LeadAutomation.StartAsync(_lifetime.Token);
            await _services.MessagingSync.StartAsync(_lifetime.Token);
            await ReloadAsync();
            await UpdateUnreadBadgesAsync();
            await RenderCurrentPageAsync();
            await UpdateProviderStateAsync();
            _unreadBadgeTimer.Start();
            _ = _updates.CheckAndDownloadAsync();
            _openedReady.TrySetResult(true);
        }
        catch (Exception error)
        {
            ContentHost.Content = MessagePanel(
                "初始化失败",
                error.Message,
                "程序没有删除或覆盖数据库。请保留此提示用于排查。",
                Danger);
            _openedReady.TrySetResult(false);
        }
    }

    internal async Task<IReadOnlyList<string>> RunUiSmokeAsync()
    {
        if (!await _openedReady.Task.WaitAsync(TimeSpan.FromSeconds(25)))
            throw new InvalidOperationException("主窗口初始化失败。");
        var sample = await _services.Repository.GetLeadByBuyerIdAsync("mac-ui-smoke");
        if (sample is null)
        {
            sample = new Lead
            {
                BuyerId = "mac-ui-smoke",
                Name = "macOS UI Smoke Customer",
                Company = "AI Sales OS",
                Country = "US",
                PhoneE164 = "+14155552671",
                Email = "smoke@example.com",
                PhoneValid = true,
                ProductInterest = "Native macOS parity",
                Stage = LeadStage.Interested
            };
            await _services.Repository.UpsertLeadAsync(sample, _lifetime.Token);
        }
        await ReloadAsync();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dashboard"] = "Dashboard",
            ["intelligence"] = "商机智能",
            ["customers"] = "客户列表",
            ["inbox"] = "WhatsApp Inbox",
            ["email"] = "邮件 Inbox",
            ["broadcast"] = "多渠道自动化触达",
            ["knowledge"] = "知识库",
            ["analytics"] = "客户智能分析"
        };
        var visited = new List<string>();
        var captureDirectory = Environment.GetEnvironmentVariable("WAFLOW_UI_SMOKE_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captureDirectory)) Directory.CreateDirectory(captureDirectory);
        async Task CaptureAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(captureDirectory)) return;
            await Task.Delay(320);
            var pixelSize = new PixelSize(
                Math.Max(1120, (int)Math.Ceiling(Bounds.Width)),
                Math.Max(700, (int)Math.Ceiling(Bounds.Height)));
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            bitmap.Render(this);
            bitmap.Save(Path.Combine(captureDirectory, fileName));
        }
        foreach (var item in expected)
        {
            await NavigateAsync(item.Key);
            if (ContentHost.Content is null || PageTitle.Text != item.Value)
                throw new InvalidOperationException($"页面冒烟失败：{item.Key}");
            await CaptureAsync($"mac-{item.Key}.png");
            visited.Add(item.Key);
        }
        MacThemeManager.Apply("Dark");
        await NavigateAsync("dashboard");
        await CaptureAsync("mac-dashboard-dark.png");
        MacThemeManager.Apply("Light");
        await CaptureAsync("mac-dashboard-light.png");
        MacThemeManager.Apply("System");
        foreach (var scale in new[] { 80, 90, 100, 110, 125, 100 }) ApplyUiScale(scale);
        _sidebarPointerInside = true;
        UpdateSidebarExpansionState();
        if (!SidebarHost.Classes.Contains("expanded"))
            throw new InvalidOperationException("侧栏展开状态未生效。");
        _sidebarPointerInside = false;
        UpdateSidebarExpansionState();
        Classes.Set("reduce-motion", true);
        if (!Classes.Contains("reduce-motion"))
            throw new InvalidOperationException("减少动态效果状态未生效。");
        Classes.Set("reduce-motion", _reduceMotion);
        ToggleCommandOverlay(true);
        if (!CommandOverlay.IsVisible) throw new InvalidOperationException("命令面板未打开。");
        ToggleCommandOverlay(false);
        await NavigateAsync("dashboard");
        return visited;
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        _lifetime.Cancel();
        _unreadBadgeTimer.Stop();
        _unreadBadgeTimer.Tick -= UnreadBadgeTimer_Tick;
        _navigationMotionCancellation?.Cancel();
        _navigationMotionCancellation?.Dispose();
        _bulkAnalysisCancellation?.Cancel();
        _bulkAnalysisCancellation?.Dispose();
        _updates.StateChanged -= Updates_StateChanged;
        _services.WhatsApp.EventReceived -= WhatsApp_EventReceived;
        _services.WhatsAppSync.SynchronizationChanged -= WhatsAppSync_SynchronizationChanged;
        _services.Email.SynchronizationChanged -= Email_SynchronizationChanged;
        _services.Campaigns.SafetyStopped -= Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged -= LeadAutomation_AnalysisChanged;
        try { await _services.MessagingSync.DisposeAsync(); } catch { }
        try { await _services.LeadAutomation.DisposeAsync(); } catch { }
        try { await _services.Campaigns.DisposeAsync(); } catch { }
        try { await _services.Email.DisposeAsync(); } catch { }
        try { await _services.WhatsApp.DisposeAsync(); } catch { }
        _services.CustomerSuccessCoordinator.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReloadAsync()
    {
        _leads = await _services.Repository.GetLeadsAsync(cancellationToken: _lifetime.Token);
        _dashboard = await _services.Repository.GetDashboardAsync(_lifetime.Token);
    }

    private async void Navigate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        await NavigateAsync(target);
    }

    private async Task NavigateAsync(string target)
    {
        if (target.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            await OpenSettingsAsync();
            return;
        }
        var changed = !_currentPage.Equals(target, StringComparison.OrdinalIgnoreCase);
        _currentPage = target;
        if (!changed)
        {
            await RenderCurrentPageAsync();
            return;
        }

        _navigationMotionCancellation?.Cancel();
        _navigationMotionCancellation?.Dispose();
        var motion = new CancellationTokenSource();
        _navigationMotionCancellation = motion;
        try
        {
            ContentHost.Opacity = 0;
            if (!_reduceMotion) await Task.Delay(110, motion.Token);
            await RenderCurrentPageAsync();
            motion.Token.ThrowIfCancellationRequested();
            ContentHost.Opacity = 1;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_navigationMotionCancellation, motion))
            {
                _navigationMotionCancellation = null;
                motion.Dispose();
            }
        }
    }

    private async Task RenderCurrentPageAsync()
    {
        try
        {
            SetNavigationState(_currentPage);
            await ReloadAsync();
            var (title, subtitle, content) = _currentPage switch
            {
                "intelligence" => ("商机智能", "AI 评分证据、客户画像与下一步决策", await BuildLeadIntelligenceAsync()),
                "customers" => ("客户列表", "统一客户数据、动态字段与批量运营", await BuildCustomersAsync()),
                "inbox" => ("WhatsApp Inbox", "会话、客户资料与 AI 销售信号实时联动", await BuildWhatsAppAsync()),
                "email" => ("邮件 Inbox", "邮件收发、历史归档与 CRM 客户资料实时联动", await BuildEmailAsync()),
                "broadcast" => ("多渠道自动化触达", "WhatsApp 与邮件任务、字段替换、受众、节奏和结果统一追踪", await BuildCampaignsAsync()),
                "knowledge" => ("知识库", "批准资料、真实互动和结果验证经验分层治理", await BuildKnowledgeAsync()),
                "analytics" => ("客户智能分析", "客户情报报告、版本历史、证据台账与 Word / PDF 导出", await BuildAnalyticsAsync()),
                _ => ("Dashboard", "今天最值得优先处理的销售动作", await BuildDashboardAsync())
            };
            PageTitle.Text = title;
            PageSubtitle.Text = subtitle;
            PageGuideButton.SetValue(ToolTip.TipProperty, $"查看“{title}”的功能介绍和操作步骤");
            ContentHost.Content = content;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ContentHost.Content = MessagePanel(
                "读取失败",
                error.Message,
                "请稍后重试；现有客户数据不会被改动。",
                Danger);
        }
    }

    private void SetNavigationState(string target)
    {
        foreach (var button in new[]
        {
            DashboardNav, IntelligenceNav, CustomersNav, WhatsAppNav, EmailNav,
            CampaignsNav, KnowledgeNav, AnalyticsNav
        })
            button.Classes.Set("selected", string.Equals(button.Tag as string, target, StringComparison.OrdinalIgnoreCase));
    }

    private void PageScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Avalonia ScrollViewer measures descendants with an unbounded width. Constraining the
        // page canvas to the viewport keeps star-sized dashboards and inbox panes responsive.
        ContentHost.Width = Math.Max(640, e.NewSize.Width - 64);
    }

    private void SidebarHost_PointerEntered(object? sender, PointerEventArgs e)
    {
        _sidebarPointerInside = true;
        UpdateSidebarExpansionState();
    }

    private void SidebarHost_PointerExited(object? sender, PointerEventArgs e)
    {
        _sidebarPointerInside = false;
        UpdateSidebarExpansionState();
    }

    private void SidebarHost_GotFocus(object? sender, GotFocusEventArgs e)
    {
        _sidebarKeyboardExpanded = true;
        UpdateSidebarExpansionState();
    }

    private void SidebarHost_LostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _sidebarKeyboardExpanded = SidebarHost.IsKeyboardFocusWithin;
            UpdateSidebarExpansionState();
        }, DispatcherPriority.Background);
    }

    private void UpdateSidebarExpansionState() =>
        SidebarHost.Classes.Set("expanded", _sidebarPointerInside || _sidebarKeyboardExpanded);

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        var content = await BuildSettingsAsync();
        await ShowPanelDialogAsync(
            "AI Sales OS · 设置",
            new ScrollViewer
            {
                Content = content,
                Margin = new Thickness(26),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            1040,
            820);
        var settings = await _services.Repository.GetAppSettingsAsync(_lifetime.Token);
        MacThemeManager.Apply(settings.ThemeMode);
        ApplyUiScale(settings.UiScalePercentage);
        await UpdateProviderStateAsync();
        SetNavigationState(_currentPage);
        _sidebarKeyboardExpanded = false;
        UpdateSidebarExpansionState();
    }

    private void ApplyUiScale(int percentage)
    {
        var normalized = new[] { 80, 90, 100, 110, 125 }
            .OrderBy(value => Math.Abs(value - percentage))
            .First();
        var scale = normalized / 100d;
        MainScaleHost.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private async void ShowGuide_Click(object? sender, RoutedEventArgs e) =>
        await ShowGuideAsync(GuideCatalog.ForModule(_currentPage));

    private async Task ShowGuideAsync(GuideDefinition definition)
    {
        var stepIndex = 0;
        var productArea = BodyText(definition.ProductArea, ResourceBrush("AiAccent", "#6659B8"), 11);
        productArea.FontWeight = FontWeight.SemiBold;
        var counter = BodyText("", Muted, 11);
        var title = TitleText("", 24);
        var summary = BodyText("", Ink, 13);
        var feature = BodyText("", Primary, 12);
        feature.FontWeight = FontWeight.SemiBold;
        var actions = new StackPanel { Spacing = 8 };
        var tip = BodyText("", Muted, 11);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 7 };
        var back = new Button { Content = "上一步" };
        var next = new Button();
        next.Classes.Add("primary");
        var close = new Button { Content = "关闭" };
        var footer = BodyText(definition.Footer, Muted, 10);
        var dialogPanel = new StackPanel { Spacing = 15, Margin = new Thickness(28) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(productArea);
        Grid.SetColumn(counter, 1);
        header.Children.Add(counter);
        dialogPanel.Children.Add(header);
        dialogPanel.Children.Add(TitleText(definition.Title, 16));
        dialogPanel.Children.Add(title);
        dialogPanel.Children.Add(summary);
        dialogPanel.Children.Add(feature);
        dialogPanel.Children.Add(actions);
        dialogPanel.Children.Add(new Border
        {
            Background = ResourceBrush("AiSurface", "#F4F1FF"),
            BorderBrush = ResourceBrush("AiSoft", "#E8E3FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            Child = tip
        });
        dialogPanel.Children.Add(progress);
        dialogPanel.Children.Add(footer);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(close);
        buttons.Children.Add(back);
        buttons.Children.Add(next);
        dialogPanel.Children.Add(buttons);
        var dialog = DialogWindow("本页使用手册", dialogPanel, 760, 680);

        void Render()
        {
            var step = definition.Steps[stepIndex];
            counter.Text = $"第 {stepIndex + 1} / {definition.Steps.Count} 步";
            title.Text = step.Title;
            summary.Text = step.Summary;
            feature.Text = step.Feature;
            actions.Children.Clear();
            for (var index = 0; index < step.Actions.Count; index++)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*"), ColumnSpacing = 9 };
                row.Children.Add(BadgeCell((index + 1).ToString("00"), PrimarySoft));
                var action = BodyText(step.Actions[index], Ink, 12);
                action.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(action, 1);
                row.Children.Add(action);
                actions.Children.Add(row);
            }
            tip.Text = step.Tip;
            progress.Value = 100d * (stepIndex + 1) / definition.Steps.Count;
            back.IsEnabled = stepIndex > 0;
            next.Content = stepIndex == definition.Steps.Count - 1 ? "完成本页指南" : "下一步";
        }

        back.Click += (_, _) =>
        {
            if (stepIndex == 0) return;
            stepIndex--;
            Render();
        };
        next.Click += async (_, _) =>
        {
            if (stepIndex < definition.Steps.Count - 1)
            {
                stepIndex++;
                Render();
                return;
            }
            var onboarding = await _services.Repository.GetOnboardingStateAsync(_lifetime.Token);
            GuideCatalog.MarkSeen(onboarding, definition.Key);
            await _services.Repository.SaveOnboardingStateAsync(onboarding, _lifetime.Token);
            dialog.Close();
        };
        close.Click += (_, _) => dialog.Close();
        Render();
        await dialog.ShowDialog(this);
    }

    private void CommandOverlay_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        ToggleCommandOverlay(false);

    private void CommandPanel_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    private void ToggleCommandOverlay(bool show)
    {
        if (_commandVisible == show) return;
        _commandVisible = show;
        if (show)
        {
            CommandOverlay.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                CommandOverlay.Opacity = 1;
                FirstQuickActionButton.Focus();
            }, DispatcherPriority.Background);
            return;
        }
        CommandOverlay.Opacity = 0;
        if (_reduceMotion)
        {
            CommandOverlay.IsVisible = false;
            return;
        }
        DispatcherTimer.RunOnce(() =>
        {
            if (!_commandVisible) CommandOverlay.IsVisible = false;
        }, TimeSpan.FromMilliseconds(165));
    }

    private static bool PrefersReducedMotion()
    {
        var forced = Environment.GetEnvironmentVariable("WAFLOW_REDUCED_MOTION");
        if (forced is "1" or "true" or "TRUE") return true;
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/defaults",
                ArgumentList = { "read", "com.apple.universalaccess", "reduceMotion" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(350)) return false;
            var value = process.StandardOutput.ReadToEnd().Trim();
            return value is "1" or "true" or "TRUE";
        }
        catch { return false; }
    }

    private async void QuickAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        ToggleCommandOverlay(false);
        if (action == "import")
        {
            await ImportCustomersAsync();
            return;
        }
        await NavigateAsync(action);
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
                      e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape && _commandVisible)
        {
            ToggleCommandOverlay(false);
            e.Handled = true;
            return;
        }
        if (command && e.Key == Key.K)
        {
            ToggleCommandOverlay(!_commandVisible);
            e.Handled = true;
            return;
        }
        if (!command) return;
        var target = e.Key switch
        {
            Key.D1 => "dashboard",
            Key.D2 => "intelligence",
            Key.D3 => "customers",
            Key.D4 => "inbox",
            Key.D5 => "email",
            Key.D6 => "broadcast",
            Key.D7 => "knowledge",
            Key.D8 => "analytics",
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(target)) return;
        await NavigateAsync(target);
        e.Handled = true;
    }

    private async Task UpdateProviderStateAsync()
    {
        var configured = _services.DeepSeek.HasApiKey();
        var settings = await _services.Repository.GetAppSettingsAsync(_lifetime.Token);
        AiStateText.Text = configured
            ? settings.UseGlobalAiConfiguration
                ? $"AI 已配置 · {settings.DeepSeekModel}"
                : $"AI 已配置 · 分板块模型（默认 {settings.DeepSeekModel}）"
            : "AI API 未配置";
        ProviderBadge.Background = configured
            ? ResourceBrush("SuccessSoft", "#E0F7EF")
            : ResourceBrush("WarningSoft", "#FFF2D6");
        AiStateText.Foreground = configured
            ? ResourceBrush("Success", "#16B889")
            : ResourceBrush("Warning", "#8A5A00");
    }

    private async void UnreadBadgeTimer_Tick(object? sender, EventArgs e) =>
        await UpdateUnreadBadgesAsync();

    private async Task UpdateUnreadBadgesAsync()
    {
        try
        {
            var unread = await _services.Repository.GetInboxUnreadTotalsAsync(_lifetime.Token);
            SetUnreadBadge(WhatsAppUnreadBadge, WhatsAppUnreadText, WhatsAppNav, unread.WhatsApp, "WhatsApp");
            SetUnreadBadge(EmailUnreadBadge, EmailUnreadText, EmailNav, unread.Email, "邮件");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private static void SetUnreadBadge(
        Border badge,
        TextBlock text,
        Button button,
        int count,
        string channel)
    {
        badge.IsVisible = count > 0;
        text.Text = count > 99 ? "99+" : count.ToString();
        AutomationProperties.SetName(button, count > 0 ? $"{channel} Inbox，{count} 条未读" : $"{channel} Inbox");
    }

    private async Task<Control> BuildDashboardAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "今天应该做什么？",
            "把高价值商机、待跟进客户、AI 分析进度和触达风险压缩到一个可执行视图。",
            "SALES COMMAND CENTER"));

        page.Children.Add(MetricGrid(
            ("全部商机", _dashboard.TotalLeads.ToString("N0"), "统一客户工作区", "#087A5E"),
            ("优先商机 A / B", (_dashboard.Grades.GetValueOrDefault("A") + _dashboard.Grades.GetValueOrDefault("B")).ToString("N0"), "值得优先人工推进", "#6659B8"),
            ("24 小时内待跟进", _dashboard.PendingFollowUps.ToString("N0"), "销售承诺与下一步", "#8A5A00"),
            ("进行中自动化", _dashboard.ActiveCampaigns.ToString("N0"), _dashboard.SafetyStoppedCampaigns > 0 ? $"{_dashboard.SafetyStoppedCampaigns} 个任务被 IP 安全阀停止" : "排期、运行或暂停", "#4E8CF7")));

        var brief = await _services.TodayBrief.GetAsync(_lifetime.Token);
        var briefPanel = new StackPanel { Spacing = 10 };
        briefPanel.Children.Add(BodyText(
            brief.Items.Count == 0
                ? "今天暂无待处理行动；新客户回复、人工接管或 AI 建议会自动进入这里。"
                : $"待处理 {brief.Items.Count} 项｜逾期 {brief.OverdueCount}｜今天到期 {brief.DueTodayCount}｜人工接管 {brief.HumanHandoffCount}｜知识审核 {brief.KnowledgeReviewCount}｜知识冲突 {brief.KnowledgeConflictCount}｜候选审批 {brief.KnowledgeCandidateCount}",
            Ink,
            12));
        foreach (var item in brief.Items.Take(6))
        {
            var action = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            action.Children.Add(TextCell(item.CustomerLabel, true, $"{item.CategoryLabel} · {item.ActionLabel}\n{item.ReasonLabel}"));
            var due = BadgeCell(item.DueLabel, item.Priority is FollowUpPriority.High or FollowUpPriority.Urgent
                ? ResourceBrush("WarningSoft", "#FFF2D6")
                : PrimarySoft);
            Grid.SetColumn(due, 1);
            action.Children.Add(due);
            briefPanel.Children.Add(new Border
            {
                Background = SurfaceMuted,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
                Child = action
            });
        }

        var learning = new StackPanel { Spacing = 9 };
        learning.Children.Add(TitleText(
            brief.Learning.Accepted == 0 ? "完成率 —" : $"完成率 {brief.Learning.CompletionRate:0.#}%",
            18));
        learning.Children.Add(BodyText(
            brief.Learning.FeedbackCount == 0 ? "有效反馈 —" : $"有效反馈 {brief.Learning.HelpfulRate:0.#}%",
            Primary,
            12));
        learning.Children.Add(BodyText(
            brief.Learning.Executed == 0
                ? "真实结果尚未形成"
                : $"真实回复 {brief.Learning.ResponseRate:0.#}% · 阶段推进 {brief.Learning.ProgressionRate:0.#}% · 成交观察 {brief.Learning.DealRate:0.#}%",
            Ink,
            12));
        learning.Children.Add(BodyText(
            $"已接受 {brief.Learning.Accepted} · 已执行 {brief.Learning.Executed} · 观察中 {brief.Learning.AwaitingOutcome} · 复购 {brief.Learning.RepeatPurchases}",
            Muted,
            11));
        learning.Children.Add(BodyText(
            Fallback(brief.Learning.StrategyReview, "系统仅基于真实回复和 CRM 阶段变化复盘"),
            Muted,
            11));

        var briefGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.65*,*"), ColumnSpacing = 12 };
        briefGrid.Children.Add(SectionCard("今日行动简报", "CUSTOMER BRAIN", briefPanel));
        var learningCard = SectionCard("个人 AI 建议效果", "REAL OUTCOMES", learning);
        Grid.SetColumn(learningCard, 1);
        briefGrid.Children.Add(learningCard);
        page.Children.Add(briefGrid);

        var coverage = _dashboard.TotalLeads == 0 ? 0 : 100d * _dashboard.AnalyzedLeads / _dashboard.TotalLeads;
        var analysis = new StackPanel { Spacing = 9 };
        analysis.Children.Add(TitleText($"{coverage:0}%", 26));
        analysis.Children.Add(BodyText($"已完成 AI 分析 {_dashboard.AnalyzedLeads} / {_dashboard.TotalLeads}", Ink, 12));
        analysis.Children.Add(new ProgressBar { Value = coverage, Maximum = 100 });
        analysis.Children.Add(BodyText(
            _dashboard.QueuedAnalyses > 0
                ? $"{_dashboard.QueuedAnalyses} 个客户正在等待或分析中"
                : _dashboard.FailedAnalyses > 0
                    ? $"{_dashboard.FailedAnalyses} 个分析可重试"
                    : "AI 队列已清空",
            Muted,
            11));
        analysis.Children.Add(ActionButton("进入商机智能", async () => await NavigateAsync("intelligence")));

        var gradeRows = new StackPanel { Spacing = 8 };
        foreach (var grade in new[] { "A", "B", "C", "D" })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 9 };
            row.Children.Add(BadgeCell(grade, GradeBrush(grade)));
            var label = BodyText(grade == "D" ? "未分析客户保持 D / 0 分" : $"{grade} 级客户", Ink, 11);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var count = TitleText(_dashboard.Grades.GetValueOrDefault(grade).ToString("N0"), 17);
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            gradeRows.Children.Add(row);
        }

        var stages = new StackPanel { Spacing = 8 };
        foreach (var stage in Enum.GetValues<LeadStage>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(BodyText(Labels.Stage(stage), Ink, 11));
            var count = BodyText(_dashboard.Stages.GetValueOrDefault(stage).ToString("N0"), Primary, 11);
            count.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            stages.Children.Add(row);
        }
        var signalGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        signalGrid.Children.Add(SectionCard("AI 分析覆盖", "AI SCORE", analysis));
        var grades = SectionCard("等级分布", "AI SCORE", gradeRows);
        Grid.SetColumn(grades, 1);
        signalGrid.Children.Add(grades);
        var stageCard = SectionCard("阶段漏斗", "PIPELINE", stages);
        Grid.SetColumn(stageCard, 2);
        signalGrid.Children.Add(stageCard);
        page.Children.Add(signalGrid);

        var priority = new StackPanel { Spacing = 0 };
        priority.Children.Add(TableHeader(
            ["客户", "市场", "等级", "AI 分数", "阶段", "AI 建议下一步"],
            [1.4, .85, .55, .65, .8, 2.2]));
        foreach (var lead in _dashboard.PriorityLeads.Take(10))
        {
            var row = TableRow(
                [
                    TextCell(lead.DisplayName, true, lead.PhoneE164),
                    TextCell(Fallback(lead.Country, "—")),
                    BadgeCell(lead.Grade, GradeBrush(lead.Grade)),
                    TextCell(lead.Score.ToString()),
                    TextCell(lead.StageLabel),
                    TextCell(Fallback(lead.NextAction, "等待补充信息"))
                ],
                [1.4, .85, .55, .65, .8, 2.2]);
            row.PointerPressed += async (_, _) =>
            {
                _customerSearch = lead.DisplayName;
                _customerPage = 1;
                await NavigateAsync("customers");
            };
            priority.Children.Add(row);
        }
        if (_dashboard.PriorityLeads.Count == 0)
            priority.Children.Add(EmptyState("暂无优先商机", "导入客户并运行 AI 分析后，这里会显示最值得推进的客户。"));
        var quality = new StackPanel { Spacing = 9 };
        quality.Children.Add(MetricGrid(
            ("成功", _dashboard.CampaignSent.ToString("N0"), "累计自动化发送结果", "#087A5E"),
            ("排队", _dashboard.CampaignQueued.ToString("N0"), "等待发送", "#4E8CF7"),
            ("失败", _dashboard.CampaignFailed.ToString("N0"), "可查看失败原因", "#A52D2D")));
        var attempts = _dashboard.CampaignSent + _dashboard.CampaignFailed;
        quality.Children.Add(BodyText(
            attempts == 0
                ? "暂无发送历史；建立任务后将在这里看到执行质量。"
                : $"发送到位率 {(100d * _dashboard.CampaignSent / attempts):0.0}% · 共尝试 {attempts} 条",
            Muted,
            11));
        quality.Children.Add(ActionButton("查看自动化任务", async () => await NavigateAsync("broadcast")));

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("1.6*,*"), ColumnSpacing = 12 };
        bottom.Children.Add(SectionCard("今日优先商机", "只展示已完成 AI 分析的 A / B 级客户", priority));
        var qualityCard = SectionCard("触达质量", "LIVE", quality);
        Grid.SetColumn(qualityCard, 1);
        bottom.Children.Add(qualityCard);
        page.Children.Add(bottom);
        page.Children.Add(BodyText($"最近导入：{_dashboard.LastImportText}", Muted, 10));
        return page;
    }

    private void Updates_StateChanged(object? sender, ApplicationUpdateState state) =>
        Dispatcher.UIThread.Post(() => { });

    private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        var state = _updates.State;
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(26) };
        panel.Children.Add(TitleText("版本中心"));
        var status = BodyText(state.Message);
        panel.Children.Add(status);
        panel.Children.Add(BodyText($"当前版本 {state.CurrentVersion} · 通道 {UpdateConfiguration.Load().Channel}"));
        panel.Children.Add(Accessible(new TextBox
        {
            Text = string.IsNullOrWhiteSpace(state.ReleaseNotes) ? "暂无远程更新日志。" : state.ReleaseNotes,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 240
        }, "版本更新日志"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var check = ActionButton("重新检查", async () =>
        {
            status.Text = "正在检查 GitHub Release…";
            await _updates.CheckAndDownloadAsync(force: true);
            status.Text = _updates.State.Message;
        });
        var install = ActionButton("安装更新并重启", () =>
        {
            _updates.ApplyAndRestart();
            return Task.CompletedTask;
        }, primary: true);
        install.IsEnabled = state.CanInstall;
        buttons.Children.Add(check);
        buttons.Children.Add(install);
        panel.Children.Add(buttons);
        await ShowPanelDialogAsync("AI Sales OS · 版本与更新", panel, 700, 590);
    }

    private void WhatsApp_EventReceived(object? sender, WhatsAppBridgeEvent e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (!string.IsNullOrWhiteSpace(e.AccountId) &&
                !e.AccountId.Equals(_activeWhatsAppAccountId, StringComparison.OrdinalIgnoreCase))
                return;
            if (e.Name == "qr" && e.Data.TryGetProperty("dataUrl", out var qr))
            {
                _whatsAppQrDataUrl = qr.GetString() ?? "";
                _whatsAppState = "waiting_qr";
            }
            else if (e.Name == "connection")
            {
                _whatsAppState = e.Data.TryGetProperty("state", out var state)
                    ? state.GetString() ?? "disconnected"
                    : "disconnected";
                if (_whatsAppState == "connected")
                {
                    _whatsAppQrDataUrl = "";
                    await SaveLinkedWhatsAppAccountAsync(e);
                }
            }
            _operationStatus = $"WhatsApp：{WhatsAppStateLabel(_whatsAppState)}";
            if (_currentPage == "inbox") await RenderCurrentPageAsync();
        });

    private void WhatsAppSync_SynchronizationChanged(object? sender, WhatsAppSyncProgress progress) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (!progress.AccountId.Equals(_activeWhatsAppAccountId, StringComparison.OrdinalIgnoreCase)) return;
            _operationStatus = progress.State switch
            {
                "syncing" => $"WhatsApp 正在同步 {progress.Phase}{(progress.Progress is null ? "" : $" {progress.Progress}%")}",
                "complete" => $"已同步 {progress.Chats} 会话 / {progress.Contacts} 联系人 / {progress.Messages} 消息",
                "failed" => $"WhatsApp 同步失败：{progress.Error}",
                _ => _operationStatus
            };
            if (_currentPage == "inbox" && progress.State is "complete" or "data" or "failed")
                await RenderCurrentPageAsync();
        });

    private void Email_SynchronizationChanged(object? sender, EmailSynchronizationState state) =>
        Dispatcher.UIThread.Post(async () =>
        {
            _operationStatus = state.State == "error"
                ? $"邮件同步失败：{state.Error}"
                : $"邮件已同步 {state.Imported:N0} 封";
            if (_currentPage == "email") await RenderCurrentPageAsync();
        });

    private void Campaigns_SafetyStopped(object? sender, CampaignSafetyStoppedEventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            _operationStatus = "自动化安全阀门已触发，活动任务已暂停";
            await ShowMessageAsync(
                "自动化已安全停止",
                "检测到网络环境或任务风险，活动 Campaign 已暂停。请检查 IP、账号连接和审计记录后再恢复。");
            if (_currentPage == "broadcast") await RenderCurrentPageAsync();
        });

    private void LeadAutomation_AnalysisChanged(object? sender, LeadAnalysisAutomationEventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            _operationStatus = e.Message;
            if (_currentPage == "intelligence") await RenderCurrentPageAsync();
        });

    private async Task SaveLinkedWhatsAppAccountAsync(WhatsAppBridgeEvent e)
    {
        try
        {
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync(_lifetime.Token);
            var account = accounts.FirstOrDefault(item =>
                item.Id.Equals(_activeWhatsAppAccountId, StringComparison.OrdinalIgnoreCase));
            if (account is null) return;
            var user = JsonText(e.Data, "user");
            var name = JsonText(e.Data, "name");
            var phone = new string(user.Split(':')[0].Where(char.IsDigit).ToArray());
            if (phone.Length > 0) account.LinkedPhone = "+" + phone;
            if (!string.IsNullOrWhiteSpace(name) && account.Name.StartsWith("个人号 ", StringComparison.Ordinal))
                account.Name = name;
            await _services.Repository.SaveWhatsAppAccountsAsync(accounts, _lifetime.Token);
        }
        catch
        {
            // A display-name persistence failure must not interrupt the live bridge.
        }
    }

    private static StackPanel PageStack() =>
        new() { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static Control PageLead(string title, string subtitle, string eyebrow = "")
    {
        var panel = new StackPanel { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(eyebrow))
        {
            var label = BodyText(eyebrow, ResourceBrush("AiAccent", "#6659B8"), 11);
            label.FontWeight = FontWeight.SemiBold;
            panel.Children.Add(label);
        }
        panel.Children.Add(TitleText(title));
        panel.Children.Add(BodyText(subtitle));
        return panel;
    }

    private static TextBlock TitleText(string text, double size = 28) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeight.Bold,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap
        };

    private static TextBlock BodyText(string text, IBrush? foreground = null, double size = 13) =>
        new()
        {
            Text = text,
            Foreground = foreground ?? Muted,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap
        };

    private static Border Card(Control content, Thickness? padding = null, IBrush? background = null) =>
        new()
        {
            Background = background ?? Surface,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = padding ?? new Thickness(18),
            Child = content
        };

    private static Border SectionCard(string title, string badge, Control content)
    {
        var panel = new StackPanel { Spacing = 14 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(TitleText(title, 18));
        var badgeText = BodyText(badge, Primary, 12);
        badgeText.FontWeight = FontWeight.SemiBold;
        Grid.SetColumn(badgeText, 1);
        heading.Children.Add(badgeText);
        panel.Children.Add(heading);
        panel.Children.Add(content);
        return Card(panel);
    }

    private static Grid MetricGrid(params (string Label, string Value, string Detail, string Accent)[] metrics)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", metrics.Length))),
            ColumnSpacing = 12
        };
        for (var index = 0; index < metrics.Length; index++)
        {
            var metric = metrics[index];
            var panel = new StackPanel { Spacing = 7 };
            panel.Children.Add(BodyText(metric.Label, Muted, 12));
            panel.Children.Add(new TextBlock
            {
                Text = metric.Value,
                FontSize = 28,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse(metric.Accent)
            });
            panel.Children.Add(BodyText(metric.Detail, Muted, 11));
            var card = Card(panel);
            Grid.SetColumn(card, index);
            grid.Children.Add(card);
        }
        return grid;
    }

    private static Button ActionButton(
        string text,
        Func<Task> action,
        bool primary = false,
        bool danger = false,
        double minWidth = 96)
    {
        var button = new Button { Content = text, MinWidth = minWidth };
        if (primary) button.Classes.Add("primary");
        if (danger) button.Classes.Add("danger");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static Grid TableHeader(IReadOnlyList<string> labels, IReadOnlyList<double> widths)
    {
        var grid = TableGrid(widths, Brush.Parse("#EEF4F1"));
        for (var index = 0; index < labels.Count; index++)
        {
            var text = BodyText(labels[index], Ink, 11);
            text.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(text, index);
            grid.Children.Add(text);
        }
        return grid;
    }

    private static Grid TableRow(IReadOnlyList<Control> cells, IReadOnlyList<double> widths)
    {
        var grid = TableGrid(widths, Surface);
        for (var index = 0; index < cells.Count; index++)
        {
            Grid.SetColumn(cells[index], index);
            grid.Children.Add(cells[index]);
        }
        return grid;
    }

    private static Grid TableGrid(IReadOnlyList<double> widths, IBrush background)
    {
        var columns = new ColumnDefinitions();
        foreach (var width in widths) columns.Add(new ColumnDefinition(width, GridUnitType.Star));
        return new Grid
        {
            ColumnDefinitions = columns,
            ColumnSpacing = 14,
            Background = background,
            MinHeight = 54,
            Margin = new Thickness(0, 0, 0, 1)
        }.WithPadding(new Thickness(14, 10));
    }

    private static Control TextCell(string text, bool strong = false, string detail = "")
    {
        var panel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = Fallback(text, "—"),
            Foreground = Ink,
            FontSize = 13,
            FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(detail))
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = Muted,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        return panel;
    }

    private static Control BadgeCell(string text, IBrush background)
    {
        var label = BodyText(text, Ink, 11);
        label.FontWeight = FontWeight.SemiBold;
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label
        };
    }

    private static Border EmptyState(string title, string detail)
    {
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(TitleText(title, 17));
        panel.Children.Add(BodyText(detail));
        return new Border
        {
            Background = SurfaceMuted,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24, 34),
            Child = panel
        };
    }

    private static Control MessagePanel(string title, string message, string detail, IBrush accent)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(TitleText(title, 22));
        panel.Children.Add(BodyText(message, accent, 14));
        panel.Children.Add(BodyText(detail));
        return Card(panel, new Thickness(24));
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(TitleText(title, 22));
        panel.Children.Add(BodyText(message, Ink, 13));
        var close = new Button { Content = "知道了", HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("primary");
        var dialog = DialogWindow(title, panel, 560, 330);
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(close);
        await dialog.ShowDialog(this);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText = "确认")
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(TitleText(title, 22));
        panel.Children.Add(BodyText(message, Ink, 13));
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var dialog = DialogWindow(title, panel, 560, 340);
        var cancel = new Button { Content = "取消" };
        var confirm = new Button { Content = confirmText };
        confirm.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        panel.Children.Add(row);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowPanelDialogAsync(string title, Control content, double width, double height)
    {
        var dialog = DialogWindow(title, content, width, height);
        await dialog.ShowDialog(this);
    }

    private static Window DialogWindow(string title, Control content, double width, double height) =>
        new()
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = Math.Min(width, 520),
            MinHeight = Math.Min(height, 300),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ResourceBrush("Canvas", "#F4F7F5"),
            Content = content
        };

    private static StackPanel Field(string label, Control input, string hint = "")
    {
        var panel = new StackPanel { Spacing = 5 };
        var title = BodyText(label, Ink, 12);
        title.FontWeight = FontWeight.SemiBold;
        AutomationProperties.SetName(input, label);
        AutomationProperties.SetLabeledBy(input, title);
        if (!string.IsNullOrWhiteSpace(hint)) AutomationProperties.SetHelpText(input, hint);
        panel.Children.Add(title);
        panel.Children.Add(input);
        if (!string.IsNullOrWhiteSpace(hint)) panel.Children.Add(BodyText(hint, Muted, 10));
        return panel;
    }

    private static T Accessible<T>(T control, string name, string helpText = "") where T : StyledElement
    {
        AutomationProperties.SetName(control, name);
        if (!string.IsNullOrWhiteSpace(helpText)) AutomationProperties.SetHelpText(control, helpText);
        return control;
    }

    private async Task<string?> PickOpenFileAsync(string title, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(title) { Patterns = patterns }
            ]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType(extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "Word")
                {
                    Patterns = ["*" + extension]
                }
            ]
        });
        return file?.TryGetLocalPath();
    }

    private static Bitmap? DecodeDataUrl(string value)
    {
        try
        {
            var comma = value.IndexOf(',');
            if (comma < 0) return null;
            using var stream = new MemoryStream(Convert.FromBase64String(value[(comma + 1)..]));
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private static string JsonText(System.Text.Json.JsonElement data, string name) =>
        data.ValueKind == System.Text.Json.JsonValueKind.Object &&
        data.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "客户" : safe;
    }

    private static IBrush ResourceBrush(string key, string fallback)
    {
        if (Application.Current is { } application &&
            application.TryFindResource(key, application.ActualThemeVariant, out var value) &&
            value is IBrush brush)
            return brush;
        return Brush.Parse(fallback);
    }

    private static IBrush GradeBrush(string grade) => grade.ToUpperInvariant() switch
    {
        "A" => Brush.Parse("#DDF6EB"),
        "B" => Brush.Parse("#E7F0FF"),
        "C" => Brush.Parse("#FFF4D6"),
        _ => Brush.Parse("#EDF1EF")
    };

    private static string WhatsAppStateLabel(string state) => state switch
    {
        "connected" => "已连接",
        "connecting" => "连接中",
        "waiting_qr" => "等待扫码",
        "logged_out" => "登录已失效",
        _ => "已断开"
    };
}

internal static class AvaloniaGridExtensions
{
    public static Grid WithPadding(this Grid grid, Thickness padding)
    {
        grid.Margin = padding;
        return grid;
    }
}
