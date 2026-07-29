using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
    private static readonly IBrush Ink = Brush.Parse("#062C24");
    private static readonly IBrush Muted = Brush.Parse("#5D746D");
    private static readonly IBrush Primary = Brush.Parse("#0B9B75");
    private static readonly IBrush PrimarySoft = Brush.Parse("#E4F7F0");
    private static readonly IBrush Border = Brush.Parse("#D8E4DF");
    private static readonly IBrush Surface = Brushes.White;
    private static readonly IBrush SurfaceMuted = Brush.Parse("#F4F8F6");
    private static readonly IBrush Danger = Brush.Parse("#B42318");
    private static readonly IBrush Warning = Brush.Parse("#9A6700");
    private readonly AppServices _services;
    private readonly IApplicationUpdateService _updates = new VelopackUpdateService();
    private readonly CancellationTokenSource _lifetime = new();
    private List<Lead> _leads = [];
    private DashboardSnapshot _dashboard = new();
    private string _currentPage = "dashboard";
    private string _activeWhatsAppAccountId = "primary";
    private string _selectedWhatsAppConversationId = "";
    private string _activeEmailAccountId = "";
    private string _selectedEmailConversationId = "";
    private int _customerPage = 1;
    private int _customerPageSize = 30;
    private string _customerSearch = "";
    private string _whatsAppQrDataUrl = "";
    private string _whatsAppState = "disconnected";
    private string _operationStatus = "就绪";

    public MainWindow()
    {
        InitializeComponent();
        var repository = new LocalRepository(PlatformDataPaths.DatabasePath);
        _services = new AppServices(
            repository,
            target => new MacKeychainSecretStore(target));
        VersionText.Text = $"版本 {ReleaseCatalog.CurrentVersion} · 本地数据";
        _updates.StateChanged += Updates_StateChanged;
        _services.WhatsApp.EventReceived += WhatsApp_EventReceived;
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSync_SynchronizationChanged;
        _services.Email.SynchronizationChanged += Email_SynchronizationChanged;
        _services.Campaigns.SafetyStopped += Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged += LeadAutomation_AnalysisChanged;
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        try
        {
            await _services.InitializeAsync(_lifetime.Token);
            await _services.Campaigns.StartAsync(_lifetime.Token);
            await _services.LeadAutomation.StartAsync(_lifetime.Token);
            await _services.MessagingSync.StartAsync(_lifetime.Token);
            await ReloadAsync();
            await RenderCurrentPageAsync();
            AiStateText.Text = _services.DeepSeek.HasApiKey() ? "● AI 已配置" : "● AI 待配置";
            _ = _updates.CheckAndDownloadAsync();
        }
        catch (Exception error)
        {
            ContentHost.Content = MessagePanel(
                "初始化失败",
                error.Message,
                "程序没有删除或覆盖数据库。请保留此提示用于排查。",
                Danger);
        }
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        _lifetime.Cancel();
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
        _currentPage = target;
        await RenderCurrentPageAsync();
    }

    private async Task NavigateAsync(string target)
    {
        _currentPage = target;
        await RenderCurrentPageAsync();
    }

    private async Task RenderCurrentPageAsync()
    {
        try
        {
            SetNavigationState(_currentPage);
            await ReloadAsync();
            var (title, subtitle, content) = _currentPage switch
            {
                "intelligence" => ("商机智能", "AI 评分、证据、风险和下一步动作", await BuildLeadIntelligenceAsync()),
                "customers" => ("客户列表", "统一客户身份、动态维度、导入和批量运营", await BuildCustomersAsync()),
                "inbox" => ("WhatsApp Inbox", "原生 Bridge 扫码登录、实时收发、群组与 AI 辅助", await BuildWhatsAppAsync()),
                "email" => ("邮件 Inbox", "IMAP / SMTP 登录、同步、收发与 AI 邮件草稿", await BuildEmailAsync()),
                "campaigns" => ("自动化群发", "WhatsApp / 邮件任务、动态字段、审批和安全阀门", await BuildCampaignsAsync()),
                "knowledge" => ("知识库", "本地资料解析、审核、启用、冲突和检索", await BuildKnowledgeAsync()),
                "analytics" => ("客户智能分析", "完整客户报告、版本历史与 Word / PDF 导出", await BuildAnalyticsAsync()),
                "settings" => ("API 与数据设置", "模型路由、macOS 钥匙串、本地目录和版本更新", await BuildSettingsAsync()),
                _ => ("Dashboard", "今天最值得优先处理的销售动作", await BuildDashboardAsync())
            };
            PageTitle.Text = title;
            PageSubtitle.Text = subtitle;
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
            CampaignsNav, KnowledgeNav, AnalyticsNav, SettingsNav
        })
            button.Classes.Set("selected", string.Equals(button.Tag as string, target, StringComparison.OrdinalIgnoreCase));
    }

    private void PageScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Avalonia ScrollViewer measures descendants with an unbounded width. Constraining the
        // page canvas to the viewport keeps star-sized dashboards and inbox panes responsive.
        ContentHost.Width = Math.Max(640, e.NewSize.Width - 64);
    }

    private async Task<Control> BuildDashboardAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "销售脉搏",
            "从优先商机、待办、渠道未读和自动化执行状态开始，所有资料只保存在这台 Mac。"));

        var unread = await _services.Repository.GetInboxUnreadTotalsAsync(_lifetime.Token);
        page.Children.Add(MetricGrid(
            ("客户总数", _dashboard.TotalLeads.ToString("N0"), $"{_dashboard.AnalyzedLeads:N0} 位已完成 AI 分析", "#087A5E"),
            ("待跟进", _dashboard.PendingFollowUps.ToString("N0"), "下一跟进时间已到", "#9A6700"),
            ("WhatsApp 未读", unread.WhatsApp.ToString("N0"), "进入 Inbox 处理", "#0B74C8"),
            ("邮件未读", unread.Email.ToString("N0"), $"{_dashboard.ActiveCampaigns:N0} 个自动化任务", "#7A5AF8")));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ActionButton("导入客户", async () => await ImportCustomersAsync()));
        actions.Children.Add(ActionButton("连接 WhatsApp", async () => await NavigateAsync("inbox"), primary: true));
        actions.Children.Add(ActionButton("连接邮箱", async () => await NavigateAsync("email")));
        actions.Children.Add(ActionButton("运行 AI 分析", async () => await NavigateAsync("intelligence")));
        page.Children.Add(actions);

        var priority = new StackPanel { Spacing = 0 };
        priority.Children.Add(TableHeader(
            ["客户", "公司 / 产品", "等级", "阶段", "下一步动作"],
            [1.5, 1.6, .55, .8, 2.2]));
        foreach (var lead in _dashboard.PriorityLeads.Take(10))
        {
            var row = TableRow(
                [
                    TextCell(lead.DisplayName, true, lead.PhoneE164),
                    TextCell(Fallback(lead.Company, "—"), false, lead.ProductInterest),
                    BadgeCell($"{lead.Grade} · {lead.Score}", GradeBrush(lead.Grade)),
                    TextCell(lead.StageLabel),
                    TextCell(Fallback(lead.NextAction, "等待补充信息"))
                ],
                [1.5, 1.6, .55, .8, 2.2]);
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
        page.Children.Add(SectionCard("今日优先商机", $"{_dashboard.PriorityLeads.Count:N0} 位", priority));
        return page;
    }

    private void Updates_StateChanged(object? sender, ApplicationUpdateState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            VersionText.Text = state.Stage switch
            {
                ApplicationUpdateStage.Downloading => $"版本 {state.CurrentVersion} · 下载 {state.DownloadProgress}%",
                ApplicationUpdateStage.ReadyToInstall => $"新版本 {state.LatestVersion} 已下载",
                _ => $"版本 {state.CurrentVersion} · 本地数据"
            };
            UpdateHeadlineText.Text = state.Stage == ApplicationUpdateStage.ReadyToInstall
                ? "有更新可安装"
                : "macOS 原生桌面版";
        });

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
            if (_currentPage == "campaigns") await RenderCurrentPageAsync();
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

    private static Control PageLead(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 5 };
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
            Background = Brush.Parse("#F4F8F6"),
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
