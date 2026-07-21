using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Desktop;
using WAFlow.Desktop.Updates;

namespace WAFlow.Mac;

public sealed partial class MainWindow : Window
{
    private readonly LocalRepository _repository;
    private readonly MacKeychainSecretStore _secrets = new();
    private readonly IApplicationUpdateService _updates = new VelopackUpdateService();
    private List<Lead> _leads = [];
    private DashboardSnapshot _dashboard = new();

    public MainWindow()
    {
        InitializeComponent();
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "WAFlow");
        _repository = new LocalRepository(Path.Combine(dataDirectory, "waflow.db"));
        VersionText.Text = $"版本 {ReleaseCatalog.CurrentVersion} · 本地数据";
        _updates.StateChanged += Updates_StateChanged;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _repository.InitializeAsync();
            await ReloadAsync();
            ContentHost.Content = BuildDashboard();
            var configured = !string.IsNullOrWhiteSpace(_secrets.Read());
            AiStateText.Text = configured ? "● AI 已配置" : "● AI 待配置";
            _ = _updates.CheckAndDownloadAsync();
        }
        catch (Exception error)
        {
            ContentHost.Content = BuildMessagePage("初始化失败", error.Message, "请保留此提示并反馈；程序没有删除或覆盖原始数据库。", "#B42318");
        }
    }

    private void Updates_StateChanged(object? sender, ApplicationUpdateState state) =>
        Dispatcher.UIThread.Post(() => RenderUpdateState(state));

    private void RenderUpdateState(ApplicationUpdateState state)
    {
        VersionText.Text = state.Stage switch
        {
            ApplicationUpdateStage.Downloading => $"版本 {state.CurrentVersion} · 下载 {state.DownloadProgress}%",
            ApplicationUpdateStage.ReadyToInstall => $"新版本 {state.LatestVersion} 已下载",
            _ => $"版本 {state.CurrentVersion} · {state.Message}"
        };
        UpdateHeadlineText.Text = state.Stage == ApplicationUpdateStage.ReadyToInstall ? "有更新可安装" : "macOS 原生测试版";
    }

    private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        var state = _updates.State;
        var dialog = new Window
        {
            Title = "AI Sales OS · 版本与更新",
            Width = 620,
            Height = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var status = new TextBlock { Text = state.Message, TextWrapping = TextWrapping.Wrap, Foreground = Avalonia.Media.Brush.Parse("#526B63") };
        var notes = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(state.ReleaseNotes) ? "暂无远程更新日志。" : state.ReleaseNotes,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 230
        };
        var check = new Button { Content = "重新检查", Padding = new Thickness(18, 10) };
        var install = new Button { Content = "安装更新并重启", Padding = new Thickness(18, 10), IsEnabled = state.CanInstall, Background = Avalonia.Media.Brush.Parse("#0A9A75"), Foreground = Brushes.White };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "正在检查 GitHub Release…";
            await _updates.CheckAndDownloadAsync(force: true);
            var latest = _updates.State;
            status.Text = latest.Message;
            notes.Text = string.IsNullOrWhiteSpace(latest.ReleaseNotes) ? "暂无远程更新日志。" : latest.ReleaseNotes;
            install.IsEnabled = latest.CanInstall;
            check.IsEnabled = latest.CanCheck;
        };
        install.Click += (_, _) =>
        {
            try { _updates.ApplyAndRestart(); }
            catch (Exception error) { status.Text = $"安装失败：{error.Message}"; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(check);
        buttons.Children.Add(install);
        var content = new StackPanel { Margin = new Thickness(28), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = "版本中心", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Avalonia.Media.Brush.Parse("#062C24") });
        content.Children.Add(new TextBlock { Text = $"当前版本 {state.CurrentVersion}  ·  更新通道 {UpdateConfiguration.Load().Channel}", Foreground = Avalonia.Media.Brush.Parse("#526B63") });
        content.Children.Add(status);
        content.Children.Add(new TextBlock { Text = "更新日志", FontWeight = FontWeight.SemiBold, Foreground = Avalonia.Media.Brush.Parse("#062C24") });
        content.Children.Add(notes);
        content.Children.Add(buttons);
        dialog.Content = content;
        await dialog.ShowDialog(this);
    }

    private async Task ReloadAsync()
    {
        _leads = await _repository.GetLeadsAsync();
        _dashboard = await _repository.GetDashboardAsync();
    }

    private async void Navigate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string target) return;
        try
        {
            await ReloadAsync();
            (PageTitle.Text, PageSubtitle.Text, ContentHost.Content) = target switch
            {
                "intelligence" => ("商机智能", "AI 评分、客户价值依据和下一步动作", BuildLeadIntelligence()),
                "customers" => ("客户列表", "统一客户数据、动态字段和批量运营", BuildCustomerList()),
                "inbox" => ("WhatsApp Inbox", "历史会话与客户资料联动", await BuildInboxAsync()),
                "campaigns" => ("自动化群发", "话术模板、发送任务和质量记录", await BuildCampaignsAsync()),
                "analytics" => ("客户智能分析", "生成可供管理层使用的中文客户情报报告", BuildAnalytics()),
                "settings" => ("API 对接", "配置兼容 DeepSeek / OpenAI 的模型接口", await BuildSettingsAsync()),
                _ => ("Dashboard", "今天最值得优先处理的销售动作", BuildDashboard())
            };
        }
        catch (Exception error)
        {
            ContentHost.Content = BuildMessagePage("读取失败", error.Message, "请稍后重试，现有客户数据不会被改动。", "#B42318");
        }
    }

    private Control BuildDashboard()
    {
        var page = PageStack("销售脉搏", "先看优先商机、待办和自动化执行状态，再决定今天先跟进谁。");
        var metrics = new UniformGrid { Columns = 4 };
        metrics.Children.Add(MetricCard("商机总数", _dashboard.TotalLeads.ToString("N0"), "本地客户资产", "#087A5E"));
        metrics.Children.Add(MetricCard("A / B 级商机", (_dashboard.Grades.GetValueOrDefault("A") + _dashboard.Grades.GetValueOrDefault("B")).ToString("N0"), "优先人工跟进", "#6556D8"));
        metrics.Children.Add(MetricCard("24 小时内待办", _dashboard.PendingFollowUps.ToString("N0"), "需要销售动作", "#C07A00"));
        metrics.Children.Add(MetricCard("自动化任务", _dashboard.ActiveCampaigns.ToString("N0"), "运行 / 排期 / 暂停", "#0B74C8"));
        page.Children.Add(metrics);

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,0.85*"), ColumnSpacing = 16 };
        var priorities = Card("今日优先商机");
        var priorityStack = new StackPanel { Spacing = 0 };
        foreach (var lead in _dashboard.PriorityLeads.Take(8))
            priorityStack.Children.Add(LeadRow(lead, true));
        if (priorityStack.Children.Count == 0)
            priorityStack.Children.Add(EmptyText("暂无 A/B 级商机。完成 AI 分析后，优先客户会自动出现在这里。"));
        priorities.Child = priorityStack;
        split.Children.Add(priorities);

        var grades = Card("客户等级分布");
        var gradeStack = new StackPanel { Spacing = 13 };
        foreach (var grade in new[] { "A", "B", "C", "D" })
            gradeStack.Children.Add(GradeBar(grade, _dashboard.Grades.GetValueOrDefault(grade), Math.Max(1, _dashboard.TotalLeads)));
        grades.Child = gradeStack;
        Grid.SetColumn(grades, 1);
        split.Children.Add(grades);
        page.Children.Add(split);
        return page;
    }

    private Control BuildLeadIntelligence()
    {
        var page = PageStack("AI 商机优先级", "新客户默认 D / 0 分；只有 AI 分析成功后才更新等级和评分。");
        var summary = new UniformGrid { Columns = 4 };
        summary.Children.Add(MetricCard("待分析", _leads.Count(x => x.AnalysisStatus == AnalysisStatus.NotRun).ToString("N0"), "保持 D / 0 分", "#637A73"));
        summary.Children.Add(MetricCard("分析完成", _leads.Count(x => x.HasCurrentAiScore).ToString("N0"), "已有评分证据", "#087A5E"));
        summary.Children.Add(MetricCard("可重试", _leads.Count(x => x.AnalysisStatus == AnalysisStatus.RetryableFailed).ToString("N0"), "原始数据已保留", "#B42318"));
        summary.Children.Add(MetricCard("分析队列", _leads.Count(x => x.AnalysisStatus is AnalysisStatus.Queued or AnalysisStatus.Running).ToString("N0"), "正在处理", "#6556D8"));
        page.Children.Add(summary);
        var table = Card("客户评分列表");
        var rows = new StackPanel();
        rows.Children.Add(TableHeader(["客户", "国家 / 地区", "等级", "AI 评分", "阶段", "分析状态"], [2.2, 1.2, 0.7, 0.8, 1, 1.2]));
        foreach (var lead in _leads.Take(250)) rows.Children.Add(LeadRow(lead, false));
        if (_leads.Count == 0) rows.Children.Add(EmptyText("暂无客户。请先在 Windows 客户端导入数据，或将数据库复制到此 Mac 的 WAFlow 数据目录。"));
        table.Child = rows;
        page.Children.Add(table);
        return page;
    }

    private Control BuildCustomerList()
    {
        var page = PageStack("客户数据资产", $"当前 {_leads.Count:N0} 位客户。macOS 测试版读取与 Windows 相同结构的 SQLite 数据库。");
        var path = Card("本机数据位置");
        path.Child = new TextBlock { Text = _repository.DatabasePath, Foreground = Brush("#405A52"), TextWrapping = TextWrapping.Wrap };
        page.Children.Add(path);
        var table = Card("客户列表");
        var rows = new StackPanel();
        rows.Children.Add(TableHeader(["客户", "国家 / 地区", "WhatsApp", "负责人", "等级", "阶段"], [2.1, 1.2, 1.5, 1.1, 0.7, 1]));
        foreach (var lead in _leads.Take(500)) rows.Children.Add(CustomerRow(lead));
        if (_leads.Count == 0) rows.Children.Add(EmptyText("客户列表为空。安装验证阶段可先确认程序启动、导航、中文界面和 API 钥匙串保存是否正常。"));
        table.Child = rows;
        page.Children.Add(table);
        return page;
    }

    private async Task<Control> BuildInboxAsync()
    {
        var conversations = await _repository.GetWhatsAppConversationsAsync();
        var page = PageStack("会话与客户联动", "历史消息仍存放在本机数据库；当前 macOS 测试包不启动 Windows 版 WhatsApp Bridge。");
        page.Children.Add(Notice("人工测试范围", "可验证会话历史读取、中文界面和客户关联展示；扫码登录、收发与群发需要后续在 Mac 上构建并签名原生 Bridge。", "#FFF4D9", "#8A5A00"));
        var table = Card($"历史会话 · {conversations.Count:N0}");
        var rows = new StackPanel();
        rows.Children.Add(TableHeader(["联系人", "号码", "最近消息", "时间", "未读"], [1.4, 1.2, 2.8, 1, 0.6]));
        foreach (var item in conversations.Take(250)) rows.Children.Add(ConversationRow(item));
        if (conversations.Count == 0) rows.Children.Add(EmptyText("本机暂无 WhatsApp 历史。Windows 数据不会自动跨电脑同步。"));
        table.Child = rows;
        page.Children.Add(table);
        return page;
    }

    private async Task<Control> BuildCampaignsAsync()
    {
        var campaigns = await _repository.GetCampaignsAsync(null);
        var page = PageStack("自动化触达任务", "查看任务排期、状态和执行质量。未连接原生 Mac Bridge 时不会发送消息。");
        page.Children.Add(Notice("安全保护已开启", "macOS 测试版默认禁用真实 WhatsApp 发送，避免在 Bridge 尚未完成签名验证时误触达客户。", "#E8F7F2", "#087A5E"));
        var table = Card($"任务历史 · {campaigns.Count:N0}");
        var rows = new StackPanel();
        rows.Children.Add(TableHeader(["任务", "状态", "触发方式", "间隔", "客户数", "最近更新"], [2, 1, 1.2, 1, 0.8, 1.2]));
        foreach (var item in campaigns.Take(250)) rows.Children.Add(CampaignRow(item));
        if (campaigns.Count == 0) rows.Children.Add(EmptyText("暂无群发任务。"));
        table.Child = rows;
        page.Children.Add(table);
        return page;
    }

    private Control BuildAnalytics()
    {
        var page = PageStack("客户情报中心", "整合 CRM、WhatsApp、Lead Intelligence 和历史轨迹，输出中文专业报告。");
        page.Children.Add(Notice("报告生成条件", "需要先完成 API 对接，并为客户保留足够的资料或历史会话。报告不会覆盖 CRM 原始数据。", "#EEEAFE", "#5546C7"));
        var workflow = new UniformGrid { Columns = 5 };
        foreach (var (step, detail) in new[]
        {
            ("01 数据整理", "汇总全部来源"), ("02 事实提取", "区分事实与推断"), ("03 商业分析", "识别需求和风险"),
            ("04 销售策略", "制定推进动作"), ("05 报告生成", "Word / PDF 输出")
        }) workflow.Children.Add(MiniCard(step, detail));
        page.Children.Add(workflow);
        var leads = Card("可生成报告的客户");
        var stack = new StackPanel();
        foreach (var lead in _leads.Where(x => x.HasCurrentAiScore || !string.IsNullOrWhiteSpace(x.LatestMessage)).Take(40))
            stack.Children.Add(LeadRow(lead, true));
        if (stack.Children.Count == 0) stack.Children.Add(EmptyText("暂无具备完整分析上下文的客户。"));
        leads.Child = stack;
        page.Children.Add(leads);
        return page;
    }

    private async Task<Control> BuildSettingsAsync()
    {
        var settings = await _repository.GetAppSettingsAsync();
        var page = PageStack("AI Provider", "API Key 只写入 macOS 钥匙串，不进入数据库、日志或安装包。");
        var card = Card("兼容 DeepSeek / OpenAI 的 API 设置");
        var form = new StackPanel { Spacing = 12, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Label("Base URL"));
        var baseUrl = new TextBox { Text = settings.DeepSeekBaseUrl, Watermark = "https://api.deepseek.com", MinWidth = 650 };
        form.Children.Add(baseUrl);
        form.Children.Add(Label("API Key"));
        var apiKey = new TextBox { PasswordChar = '●', Watermark = _secrets.Read() is null ? "请输入 API Key" : "已安全保存在 macOS 钥匙串；留空则不修改" };
        form.Children.Add(apiKey);
        form.Children.Add(Label("模型"));
        var model = new TextBox { Text = settings.DeepSeekModel, Watermark = "deepseek-chat" };
        form.Children.Add(model);
        var state = new TextBlock { Foreground = Brush("#637A73") };
        var save = new Button { Content = "保存 API 设置", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(apiKey.Text)) _secrets.Save(apiKey.Text);
                settings.DeepSeekBaseUrl = (baseUrl.Text ?? "").Trim();
                settings.DeepSeekModel = (model.Text ?? "").Trim();
                await _repository.SaveAppSettingsAsync(settings);
                state.Text = "✓ 已保存。API Key 位于 macOS 钥匙串。";
                state.Foreground = Brush("#087A5E");
                AiStateText.Text = "● AI 已配置";
                apiKey.Text = "";
            }
            catch (Exception error)
            {
                state.Text = "保存失败：" + error.Message;
                state.Foreground = Brush("#B42318");
            }
        };
        form.Children.Add(save);
        form.Children.Add(state);
        card.Child = form;
        page.Children.Add(card);
        page.Children.Add(Notice("隐私说明", "客户分析数据会按所选 Provider 的接口传输。请根据所在地区和 Provider 条款确认数据处理边界。", "#FFF4D9", "#8A5A00"));
        return page;
    }

    private static StackPanel PageStack(string title, string subtitle) => new()
    {
        Spacing = 18,
        Children =
        {
            new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Brush("#062C24") },
            new TextBlock { Text = subtitle, FontSize = 14, Foreground = Brush("#637A73"), Margin = new Thickness(0, -12, 0, 4) }
        }
    };

    private static Border MetricCard(string title, string value, string detail, string accent)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brush("#637A73"), FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = value, Foreground = Brush(accent), FontSize = 30, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock { Text = detail, Foreground = Brush("#405A52"), FontSize = 12 });
        return new Border { Background = Brushes.White, BorderBrush = Brush("#DCE7E2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = panel };
    }

    private static Border MiniCard(string title, string detail) => new()
    {
        Background = Brushes.White, BorderBrush = Brush("#DCE7E2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
        Child = new StackPanel { Spacing = 7, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Brush("#087A5E") }, new TextBlock { Text = detail, Foreground = Brush("#637A73"), FontSize = 12, TextWrapping = TextWrapping.Wrap } } }
    };

    private static CardBorder Card(string title) => new(title);

    private sealed class CardBorder : Border
    {
        private readonly ContentControl _content;
        public CardBorder(string title)
        {
            _content = new ContentControl();
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brush("#062C24") });
            panel.Children.Add(_content);
            Background = Brushes.White;
            BorderBrush = Brush("#DCE7E2");
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(14);
            Padding = new Thickness(20);
            base.Child = panel;
        }
        public new Control? Child { get => _content.Content as Control; set => _content.Content = value; }
    }

    private static Control BuildMessagePage(string title, string message, string detail, string color)
    {
        var page = PageStack(title, detail);
        page.Children.Add(Notice(title, message, "#FFF0F0", color));
        return page;
    }

    private static Border Notice(string title, string detail, string background, string foreground) => new()
    {
        Background = Brush(background), CornerRadius = new CornerRadius(12), Padding = new Thickness(18),
        Child = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = Brush(foreground) }, new TextBlock { Text = detail, Foreground = Brush(foreground), Opacity = .88, TextWrapping = TextWrapping.Wrap } } }
    };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Foreground = Brush("#405A52") };
    private static TextBlock EmptyText(string text) => new() { Text = text, Foreground = Brush("#637A73"), Padding = new Thickness(12, 20), TextWrapping = TextWrapping.Wrap };

    private static Grid TableHeader(string[] values, double[] widths)
    {
        var grid = RowGrid(widths, "#F1F6F4");
        for (var i = 0; i < values.Length; i++) AddCell(grid, i, values[i], true, "#405A52");
        return grid;
    }

    private static Grid CustomerRow(Lead lead)
    {
        var grid = RowGrid([2.1, 1.2, 1.5, 1.1, .7, 1], "#FFFFFF");
        AddCell(grid, 0, lead.DisplayName); AddCell(grid, 1, lead.Country); AddCell(grid, 2, lead.PhoneE164);
        AddCell(grid, 3, lead.Owner); AddCell(grid, 4, lead.HasCurrentAiScore ? lead.Grade : "D", true, GradeColor(lead.HasCurrentAiScore ? lead.Grade : "D"));
        AddCell(grid, 5, StageText(lead.Stage));
        return grid;
    }

    private static Grid LeadRow(Lead lead, bool compact)
    {
        var grid = RowGrid(compact ? [2.2, 1.2, .7, .8, 1, 1.2] : [2.2, 1.2, .7, .8, 1, 1.2], "#FFFFFF");
        AddCell(grid, 0, lead.DisplayName, true); AddCell(grid, 1, lead.Country);
        var grade = lead.HasCurrentAiScore ? lead.Grade : "D";
        AddCell(grid, 2, grade, true, GradeColor(grade)); AddCell(grid, 3, (lead.HasCurrentAiScore ? lead.Score : 0).ToString());
        AddCell(grid, 4, StageText(lead.Stage)); AddCell(grid, 5, AnalysisText(lead.AnalysisStatus));
        return grid;
    }

    private static Grid ConversationRow(WhatsAppConversation item)
    {
        var grid = RowGrid([1.4, 1.2, 2.8, 1, .6], "#FFFFFF");
        AddCell(grid, 0, string.IsNullOrWhiteSpace(item.DisplayName) ? item.Phone : item.DisplayName, true); AddCell(grid, 1, item.Phone);
        AddCell(grid, 2, item.LastMessage); AddCell(grid, 3, item.LastTimeLabel); AddCell(grid, 4, item.UnreadCount.ToString());
        return grid;
    }

    private static Grid CampaignRow(WhatsAppCampaign item)
    {
        var grid = RowGrid([2, 1, 1.2, 1, .8, 1.2], "#FFFFFF");
        AddCell(grid, 0, item.Name, true); AddCell(grid, 1, CampaignStatusText(item.Status)); AddCell(grid, 2, item.ScheduleMode == CampaignScheduleMode.Immediate ? "即时任务" : "定时任务");
        AddCell(grid, 3, $"{item.EffectiveIntervalValue} {(item.IntervalUnit == CampaignIntervalUnit.Seconds ? "秒" : "分钟")}"); AddCell(grid, 4, item.SelectedLeadIds.Count.ToString());
        AddCell(grid, 5, item.UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm"));
        return grid;
    }

    private static Grid RowGrid(double[] widths, string background)
    {
        var grid = new Grid { Background = Brush(background), MinHeight = 48 };
        foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width, GridUnitType.Star)));
        return grid;
    }

    private static void AddCell(Grid grid, int column, string? text, bool strong = false, string color = "#18362E")
    {
        var block = new TextBlock { Text = string.IsNullOrWhiteSpace(text) ? "—" : text, Margin = new Thickness(10, 12), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush(color), FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal };
        Grid.SetColumn(block, column); grid.Children.Add(block);
    }

    private static Grid GradeBar(string grade, int value, int total)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,52"), Height = 28 };
        var label = new TextBlock { Text = grade, FontWeight = FontWeight.Bold, Foreground = Brush(GradeColor(grade)), VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { Background = Brush("#E8EFEC"), CornerRadius = new CornerRadius(4), Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Background = Brush(GradeColor(grade)), CornerRadius = new CornerRadius(4), Height = 8, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 260d * value / total) };
        var overlay = new Grid(); overlay.Children.Add(track); overlay.Children.Add(fill);
        Grid.SetColumn(overlay, 1);
        var count = new TextBlock { Text = value.ToString("N0"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#405A52") }; Grid.SetColumn(count, 2);
        grid.Children.Add(label); grid.Children.Add(overlay); grid.Children.Add(count); return grid;
    }

    private static IBrush Brush(string hex) => SolidColorBrush.Parse(hex);
    private static string GradeColor(string grade) => grade switch { "A" => "#087A5E", "B" => "#0B74C8", "C" => "#C07A00", _ => "#B45B62" };
    private static string StageText(LeadStage stage) => stage switch { LeadStage.New => "新商机", LeadStage.Contacted => "已联系", LeadStage.Interested => "有兴趣", LeadStage.Negotiation => "谈判中", LeadStage.Waiting => "等待中", LeadStage.Customer => "已成交", LeadStage.Lost => "已流失", _ => stage.ToString() };
    private static string AnalysisText(AnalysisStatus status) => status switch { AnalysisStatus.Queued => "等待 AI", AnalysisStatus.Running => "分析中", AnalysisStatus.Succeeded => "已完成", AnalysisStatus.RetryableFailed => "可重试", _ => "未分析" };
    private static string CampaignStatusText(CampaignStatus status) => status switch { CampaignStatus.Scheduled => "已排期", CampaignStatus.Running => "发送中", CampaignStatus.Paused => "已暂停", CampaignStatus.SafetyStopped => "安全停止", CampaignStatus.Completed => "已完成", CampaignStatus.Cancelled => "已取消", _ => "草稿" };
}
