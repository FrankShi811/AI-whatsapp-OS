using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Core.Services;

namespace WAFlow.Mac;

public sealed partial class MainWindow
{
    private async Task<Control> BuildCampaignsAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "多渠道自动化触达",
            "统一编排 WhatsApp 与邮件任务；字段替换、受众、节奏、结果和失败原因都可追踪。",
            "OMNICHANNEL AUTOMATION"));
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        toolbar.Children.Add(BodyText(
            $"已发送 {_dashboard.CampaignSent:N0} · 失败 {_dashboard.CampaignFailed:N0} · 排队 {_dashboard.CampaignQueued:N0} · {_operationStatus}",
            Muted,
            12));
        var create = ActionButton("新建自动化任务", async () => await ShowCampaignEditorAsync(null), primary: true);
        Grid.SetColumn(create, 1);
        toolbar.Children.Add(create);
        page.Children.Add(toolbar);

        var campaigns = await _services.Repository.GetCampaignsAsync(null, _lifetime.Token);
        page.Children.Add(MetricGrid(
            ("任务总数", campaigns.Count.ToString("N0"), "WhatsApp 与邮件", "#087A5E"),
            ("运行 / 排期", campaigns.Count(item => item.Status is CampaignStatus.Running or CampaignStatus.Scheduled).ToString("N0"), "等待或正在发送", "#0B74C8"),
            ("安全停止", campaigns.Count(item => item.Status == CampaignStatus.SafetyStopped).ToString("N0"), "需人工检查后恢复", "#B42318"),
            ("已完成", campaigns.Count(item => item.Status == CampaignStatus.Completed).ToString("N0"), "保留完整执行审计", "#7A5AF8")));

        var rows = new StackPanel { Spacing = 0 };
        rows.Children.Add(TableHeader(
            ["任务", "渠道 / 账号", "状态", "计划 / 节奏", "受众", "操作"],
            [1.4, 1.1, .85, 1.35, .65, 2.1]));
        foreach (var item in campaigns.OrderByDescending(item => item.UpdatedAt))
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (item.IsEditable)
                actions.Children.Add(ActionButton("编辑", async () => await ShowCampaignEditorAsync(item)));
            actions.Children.Add(ActionButton("预览", async () => await PreviewCampaignAsync(item)));
            if (item.Status == CampaignStatus.Draft)
                actions.Children.Add(ActionButton("审批并排期", async () => await ApproveCampaignAsync(item), primary: true));
            if (item.Status is CampaignStatus.Running or CampaignStatus.Scheduled)
                actions.Children.Add(ActionButton("暂停", async () =>
                {
                    await _services.Campaigns.PauseAsync(item, cancellationToken: _lifetime.Token);
                    await RenderCurrentPageAsync();
                }));
            if (item.Status is CampaignStatus.Paused or CampaignStatus.SafetyStopped)
                actions.Children.Add(ActionButton("恢复", async () =>
                {
                    await _services.Campaigns.ResumeAsync(item, _lifetime.Token);
                    await RenderCurrentPageAsync();
                }, primary: true));
            if (item.Status is not CampaignStatus.Completed and not CampaignStatus.Cancelled)
                actions.Children.Add(ActionButton("取消", async () =>
                {
                    if (!await ConfirmAsync("取消自动化任务", $"确认取消“{item.Name}”？未发送受众将不再触达。", "确认取消")) return;
                    await _services.Campaigns.CancelAsync(item, _lifetime.Token);
                    await RenderCurrentPageAsync();
                }, danger: true));
            rows.Children.Add(TableRow(
                [
                    TextCell(Fallback(item.Name, "未命名任务"), true, item.MessageTemplate),
                    TextCell(item.ChannelLabel, false, item.AccountId),
                    BadgeCell(item.StatusLabel, item.Status == CampaignStatus.SafetyStopped ? Brush.Parse("#FDE7E4") : PrimarySoft),
                    TextCell(item.ScheduleLabel, false, $"每 {item.EffectiveIntervalValue} {(item.IntervalUnit == CampaignIntervalUnit.Seconds ? "秒" : "分钟")}"),
                    TextCell(item.SelectedLeadIds.Count.ToString("N0")),
                    actions
                ],
                [1.4, 1.1, .85, 1.35, .65, 2.1]));
        }
        if (campaigns.Count == 0)
            rows.Children.Add(EmptyState(
                "暂无自动化任务",
                "新建任务、选择渠道和客户，预览后由人工审批再执行。"));
        page.Children.Add(SectionCard("自动化任务", $"{campaigns.Count:N0} 个", rows));

        var history = await _services.Campaigns.GetExecutionHistoryAsync(_lifetime.Token);
        var historyRows = new StackPanel { Spacing = 0 };
        historyRows.Children.Add(TableHeader(
            ["任务", "渠道", "状态", "成功", "失败", "跳过", "停止 / 下一位置"],
            [1.4, .7, .8, .55, .55, .55, 1.8]));
        foreach (var run in history.Take(100))
            historyRows.Children.Add(TableRow(
                [
                    TextCell(run.Name, true),
                    TextCell(run.Channel),
                    TextCell(run.Status),
                    TextCell(run.Sent.ToString("N0")),
                    TextCell(run.Failed.ToString("N0")),
                    TextCell(run.Skipped.ToString("N0")),
                    TextCell(run.StopOrNext)
                ],
                [1.4, .7, .8, .55, .55, .55, 1.8]));
        page.Children.Add(SectionCard("执行历史", $"{history.Count:N0} 条", historyRows));
        return page;
    }

    private async Task ShowCampaignEditorAsync(WhatsAppCampaign? source)
    {
        var campaign = source ?? new WhatsAppCampaign();
        var name = new TextBox { Text = campaign.Name, Watermark = "例如：A级客户新品跟进" };
        var channel = new ComboBox
        {
            ItemsSource = new[] { "WhatsApp", "邮件" },
            SelectedIndex = campaign.Channel == CampaignChannel.Email ? 1 : 0
        };
        var whatsAppAccounts = await _services.Repository.GetWhatsAppAccountsAsync(_lifetime.Token);
        var emailAccounts = await _services.Repository.GetEmailAccountsAsync(_lifetime.Token);
        var account = new ComboBox { MinWidth = 260 };
        void RefreshAccounts()
        {
            var ids = channel.SelectedIndex == 1
                ? emailAccounts.Select(item => (item.Id, item.DisplayLabel)).ToList()
                : whatsAppAccounts.Select(item => (item.Id, item.DisplayLabel)).ToList();
            account.ItemsSource = ids.Select(item => item.DisplayLabel).ToList();
            var selectedIndex = ids.FindIndex(item => item.Id.Equals(campaign.AccountId, StringComparison.OrdinalIgnoreCase));
            account.SelectedIndex = selectedIndex >= 0 ? selectedIndex : ids.Count > 0 ? 0 : -1;
            account.Tag = ids;
        }
        channel.SelectionChanged += (_, _) => RefreshAccounts();
        RefreshAccounts();
        var templates = await _services.Repository.GetCampaignMessageTemplatesAsync(_lifetime.Token);
        var savedTemplate = new ComboBox
        {
            ItemsSource = templates.Select(item => item.Name).ToList(),
            SelectedIndex = templates.FindIndex(item => item.Id.Equals(campaign.TemplateId, StringComparison.OrdinalIgnoreCase))
        };
        CampaignMessageTemplate? currentTemplate = savedTemplate.SelectedIndex >= 0 ? templates[savedTemplate.SelectedIndex] : null;
        var templateName = new TextBox { Text = currentTemplate?.Name ?? "", Watermark = "例如：新品首次跟进" };
        var subject = new TextBox { Text = campaign.EmailSubjectTemplate, Watermark = "邮件主题，可用 {name} 等字段" };
        var message = new TextBox
        {
            Text = campaign.MessageTemplate,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 130,
            Watermark = "消息正文，可用 {name}、{company}、{product} 等动态字段"
        };
        savedTemplate.SelectionChanged += (_, _) =>
        {
            if (savedTemplate.SelectedIndex < 0 || savedTemplate.SelectedIndex >= templates.Count) return;
            currentTemplate = templates[savedTemplate.SelectedIndex];
            templateName.Text = currentTemplate.Name;
            message.Text = currentTemplate.Body;
        };
        var templateFields = await _services.Campaigns.GetTemplateFieldsAsync(_lifetime.Token);
        var fieldBox = new ComboBox
        {
            ItemsSource = templateFields.Select(item => $"{item.Token} · {item.Label}").ToList(),
            SelectedIndex = templateFields.Count > 0 ? 0 : -1,
            MinWidth = 230
        };
        var insertBody = ActionButton("插入正文", () =>
        {
            if (fieldBox.SelectedIndex >= 0 && fieldBox.SelectedIndex < templateFields.Count)
                InsertAtSelection(message, templateFields[fieldBox.SelectedIndex].Token);
            return Task.CompletedTask;
        });
        var insertSubject = ActionButton("插入主题", () =>
        {
            if (fieldBox.SelectedIndex >= 0 && fieldBox.SelectedIndex < templateFields.Count)
                InsertAtSelection(subject, templateFields[fieldBox.SelectedIndex].Token);
            return Task.CompletedTask;
        });
        var saveTemplate = ActionButton("保存话术模板", async () =>
        {
            try
            {
                var template = currentTemplate ?? new CampaignMessageTemplate();
                template.Name = templateName.Text?.Trim() ?? "";
                template.Body = message.Text?.Trim() ?? "";
                currentTemplate = await _services.Campaigns.SaveMessageTemplateAsync(template, _lifetime.Token);
                campaign.TemplateId = currentTemplate.Id;
                await ShowMessageAsync("话术模板已保存", "任务仍处于编辑状态；批准前可继续修改受众和节奏。");
            }
            catch (Exception error) { await ShowMessageAsync("模板保存失败", error.Message); }
        });
        var deleteTemplate = ActionButton("删除模板", async () =>
        {
            if (currentTemplate is null) return;
            if (!await ConfirmAsync("删除模板", $"删除话术模板“{currentTemplate.Name}”？已建立任务中的发送快照不会改变。", "确认删除")) return;
            await _services.Campaigns.DeleteMessageTemplateAsync(currentTemplate, _lifetime.Token);
            currentTemplate = null;
            savedTemplate.SelectedIndex = -1;
            templateName.Text = "";
            await ShowMessageAsync("模板已删除", "当前任务正文保留不变。");
        }, danger: true);
        var interval = new TextBox { Text = campaign.EffectiveIntervalValue.ToString() };
        var intervalUnit = new ComboBox
        {
            ItemsSource = new[] { "分钟", "秒" },
            SelectedIndex = campaign.IntervalUnit == CampaignIntervalUnit.Seconds ? 1 : 0
        };
        var dailyLimit = new TextBox { Text = campaign.DailyLimit.ToString() };
        var scheduleMode = new ComboBox
        {
            ItemsSource = new[] { "即时任务 · 批准后立即开始", "定时任务 · 北京时间" },
            SelectedIndex = campaign.ScheduleMode == CampaignScheduleMode.Immediate ? 0 : 1
        };
        var startsAt = new TextBox { Text = FormatBeijing(campaign.StartsAt), Watermark = "2026-07-20 18:30" };
        void ApplyScheduleMode() => startsAt.IsEnabled = scheduleMode.SelectedIndex != 0;
        scheduleMode.SelectionChanged += (_, _) => ApplyScheduleMode();
        ApplyScheduleMode();
        var audienceSearch = new TextBox { Watermark = "搜索客户、公司、电话、邮箱、标签" };
        var gradeFilter = new ComboBox
        {
            ItemsSource = new[] { "全部等级", "A", "B", "C", "D" },
            SelectedIndex = 0
        };
        var stages = new List<(string Label, LeadStage? Value)> { ("全部阶段", null) };
        stages.AddRange(Enum.GetValues<LeadStage>().Select(value => (Labels.Stage(value), (LeadStage?)value)));
        var stageFilter = new ComboBox { ItemsSource = stages.Select(item => item.Label).ToList(), SelectedIndex = 0 };
        var categories = new[] { "全部一级品类" }.Concat(_leads
            .Select(CustomerDimensionCatalog.ResolvePrimaryCategoryPreference)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)).ToList();
        var categoryFilter = new ComboBox { ItemsSource = categories, SelectedIndex = 0 };
        var leadChecks = _leads.Select(item => (
            Lead: item,
            Check: new CheckBox
            {
                Content = $"{item.DisplayName} · {item.Grade} · {item.StageLabel} · {Fallback(item.PhoneE164, item.Email)}",
                IsChecked = campaign.SelectedLeadIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)
            })).ToList();
        var audience = new StackPanel { Spacing = 5 };
        foreach (var item in leadChecks) audience.Children.Add(item.Check);
        void ApplyAudienceFilter()
        {
            var query = audienceSearch.Text?.Trim() ?? "";
            var grade = gradeFilter.SelectedItem?.ToString() ?? "全部等级";
            var stage = stageFilter.SelectedIndex > 0 ? stages[stageFilter.SelectedIndex].Value : null;
            var category = categoryFilter.SelectedIndex > 0 ? categories[categoryFilter.SelectedIndex] : "";
            foreach (var item in leadChecks)
            {
                var haystack = string.Join(" ", item.Lead.DisplayName, item.Lead.Company, item.Lead.PhoneE164, item.Lead.Email,
                    item.Lead.TagsLabel, CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(item.Lead));
                item.Check.IsVisible =
                    (grade == "全部等级" || item.Lead.Grade.Equals(grade, StringComparison.OrdinalIgnoreCase)) &&
                    (stage is null || item.Lead.Stage == stage) &&
                    (string.IsNullOrWhiteSpace(category) || CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(item.Lead)
                        .Equals(category, StringComparison.CurrentCultureIgnoreCase)) &&
                    (query.Length == 0 || haystack.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            }
        }
        audienceSearch.TextChanged += (_, _) => ApplyAudienceFilter();
        gradeFilter.SelectionChanged += (_, _) => ApplyAudienceFilter();
        stageFilter.SelectionChanged += (_, _) => ApplyAudienceFilter();
        categoryFilter.SelectionChanged += (_, _) => ApplyAudienceFilter();
        var selectAll = ActionButton("选择当前客户", () =>
        {
            foreach (var item in leadChecks.Where(item => item.Check.IsVisible)) item.Check.IsChecked = true;
            return Task.CompletedTask;
        });
        var clear = ActionButton("清空选择", () =>
        {
            foreach (var item in leadChecks) item.Check.IsChecked = false;
            return Task.CompletedTask;
        });
        var panel = new StackPanel { Spacing = 11, Margin = new Thickness(24) };
        panel.Children.Add(TitleText(source is null ? "新建自动化任务" : $"编辑 · {campaign.Name}", 23));
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 9
        };
        var fields = new Control[]
        {
            Field("任务名称", name), Field("渠道", channel),
            Field("发送账号", account), Field("每日上限", dailyLimit),
            Field("发送间隔", interval), Field("间隔单位", intervalUnit)
        };
        for (var index = 0; index < fields.Length; index++)
        {
            Grid.SetRow(fields[index], index / 2);
            Grid.SetColumn(fields[index], index % 2);
            form.Children.Add(fields[index]);
        }
        panel.Children.Add(form);
        panel.Children.Add(TitleText("01 · 话术模板与任务设置", 17));
        var savedTemplateRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto"), ColumnSpacing = 8 };
        savedTemplateRow.Children.Add(Field("已保存话术模板", savedTemplate));
        var templateNameField = Field("模板名称", templateName);
        Grid.SetColumn(templateNameField, 1);
        savedTemplateRow.Children.Add(templateNameField);
        Grid.SetColumn(saveTemplate, 2);
        savedTemplateRow.Children.Add(saveTemplate);
        Grid.SetColumn(deleteTemplate, 3);
        savedTemplateRow.Children.Add(deleteTemplate);
        panel.Children.Add(savedTemplateRow);
        var insertRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        insertRow.Children.Add(fieldBox);
        insertRow.Children.Add(insertBody);
        insertRow.Children.Add(insertSubject);
        panel.Children.Add(Field("动态字段", insertRow, "字段来自固定客户属性和原始导入表格的全部动态维度。"));
        panel.Children.Add(Field("邮件主题", subject, "WhatsApp 任务会忽略此字段。"));
        panel.Children.Add(Field("消息模板", message));
        var schedule = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        schedule.Children.Add(Field("触发方式", scheduleMode));
        var startsAtField = Field("开始时间（北京时间）", startsAt);
        Grid.SetColumn(startsAtField, 1);
        schedule.Children.Add(startsAtField);
        panel.Children.Add(schedule);
        panel.Children.Add(TitleText("02 · 受众筛选、资格与逐人预览", 17));
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("1.4*,.7*,.9*,1*"), ColumnSpacing = 8 };
        filters.Children.Add(audienceSearch);
        Grid.SetColumn(gradeFilter, 1);
        filters.Children.Add(gradeFilter);
        Grid.SetColumn(stageFilter, 2);
        filters.Children.Add(stageFilter);
        Grid.SetColumn(categoryFilter, 3);
        filters.Children.Add(categoryFilter);
        panel.Children.Add(filters);
        var audienceHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        audienceHeader.Children.Add(TitleText("受众", 17));
        audienceHeader.Children.Add(selectAll);
        audienceHeader.Children.Add(clear);
        panel.Children.Add(audienceHeader);
        panel.Children.Add(new ScrollViewer
        {
            Content = audience,
            MaxHeight = 260,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = DialogWindow(source is null ? "新建自动化任务" : "编辑自动化任务", new ScrollViewer { Content = panel }, 800, 850);
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => dialog.Close(false);
        var save = new Button { Content = "保存草稿" };
        save.Classes.Add("primary");
        save.Click += async (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("请填写任务名称。");
                if (string.IsNullOrWhiteSpace(message.Text)) throw new InvalidOperationException("请填写消息模板。");
                var accountIds = account.Tag as List<(string Id, string DisplayLabel)> ?? [];
                if (account.SelectedIndex < 0 || account.SelectedIndex >= accountIds.Count)
                    throw new InvalidOperationException("请先连接对应渠道账号。");
                campaign.Name = name.Text.Trim();
                campaign.Channel = channel.SelectedIndex == 1 ? CampaignChannel.Email : CampaignChannel.WhatsApp;
                campaign.AccountId = accountIds[account.SelectedIndex].Id;
                campaign.EmailSubjectTemplate = subject.Text?.Trim() ?? "";
                campaign.MessageTemplate = message.Text.Trim();
                campaign.TemplateId = currentTemplate?.Id ?? campaign.TemplateId;
                campaign.IntervalValue = int.TryParse(interval.Text, out var intervalValue) ? Math.Max(1, intervalValue) : 5;
                campaign.IntervalUnit = intervalUnit.SelectedIndex == 1 ? CampaignIntervalUnit.Seconds : CampaignIntervalUnit.Minutes;
                campaign.DailyLimit = int.TryParse(dailyLimit.Text, out var limit) ? Math.Clamp(limit, 1, 1000) : 50;
                campaign.ScheduleMode = scheduleMode.SelectedIndex == 0 ? CampaignScheduleMode.Immediate : CampaignScheduleMode.Scheduled;
                campaign.StartsAt = campaign.ScheduleMode == CampaignScheduleMode.Immediate ? DateTimeOffset.Now : ParseBeijing(startsAt.Text ?? "");
                campaign.SelectedLeadIds = leadChecks.Where(item => item.Check.IsChecked == true).Select(item => item.Lead.Id).ToList();
                await _services.Campaigns.SaveDraftAsync(campaign, _lifetime.Token);
                dialog.Close(true);
            }
            catch (Exception error) { await ShowMessageAsync("无法保存任务", error.Message); }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        if (await dialog.ShowDialog<bool>(this)) await RenderCurrentPageAsync();
    }

    private async Task PreviewCampaignAsync(WhatsAppCampaign campaign)
    {
        try
        {
            var audience = await _services.Campaigns.PreviewAudienceAsync(campaign, _lifetime.Token);
            var eligible = audience.Count(item => item.Eligible);
            var excluded = audience.Count - eligible;
            var examples = string.Join("\n\n", audience.Take(5).Select(item =>
                $"{item.Lead.DisplayName} · {(item.Eligible ? "可发送" : "排除")}\n{item.Reason}\n{item.PreviewMessage}"));
            await ShowMessageAsync(
                "受众与消息预览",
                $"共 {audience.Count:N0} 位：可发送 {eligible:N0}，排除 {excluded:N0}。\n\n{examples}");
        }
        catch (Exception error) { await ShowMessageAsync("预览失败", error.Message); }
    }

    private async Task ApproveCampaignAsync(WhatsAppCampaign campaign)
    {
        var audience = await _services.Campaigns.PreviewAudienceAsync(campaign, _lifetime.Token);
        var eligible = audience.Count(item => item.Eligible);
        if (!await ConfirmAsync(
                "人工审批自动化任务",
                $"任务“{campaign.Name}”将通过 {campaign.ChannelLabel} 触达 {eligible:N0} 位合格客户。\n" +
                $"每日上限 {campaign.DailyLimit:N0}，间隔 {campaign.EffectiveIntervalValue} {(campaign.IntervalUnit == CampaignIntervalUnit.Seconds ? "秒" : "分钟")}。\n\n" +
                "请确认账号、受众、模板和退订状态均已核对。",
                "批准并排期"))
            return;
        try
        {
            var count = await _services.Campaigns.ApproveAndScheduleAsync(campaign, cancellationToken: _lifetime.Token);
            await ShowMessageAsync("任务已排期", $"已为 {count:N0} 位合格客户创建发送队列。");
            await RenderCurrentPageAsync();
        }
        catch (Exception error) { await ShowMessageAsync("无法排期", error.Message); }
    }

    private async Task<Control> BuildKnowledgeAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "知识库",
            "批准资料、真实互动和结果验证经验分层治理；所有检索都受作用域、版本、冲突、时效和人工接管约束。",
            "KNOWLEDGE GOVERNANCE"));
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        toolbar.Children.Add(BodyText(
            $"存储目录：{Path.Combine(Path.GetDirectoryName(_services.Repository.DatabasePath)!, "knowledge")}",
            Muted,
            11));
        var upload = ActionButton("上传知识文件", async () => await UploadKnowledgeAsync(), primary: true);
        Grid.SetColumn(upload, 1);
        toolbar.Children.Add(upload);
        page.Children.Add(toolbar);
        var documents = await _services.KnowledgeBase.GetDocumentsAsync(includeDeleted: false, cancellationToken: _lifetime.Token);
        page.Children.Add(MetricGrid(
            ("知识文档", documents.Count.ToString("N0"), "仅存放在本机", "#087A5E"),
            ("已启用", documents.Count(item => item.Status == KnowledgeDocumentStatus.Active).ToString("N0"), "可供 AI 检索", "#0B74C8"),
            ("待审核", documents.Count(item => item.Status == KnowledgeDocumentStatus.ReadyForReview).ToString("N0"), "需人工批准", "#9A6700"),
            ("冲突 / 风险", documents.Count(item => item.Status == KnowledgeDocumentStatus.Conflicted || item.RiskLevel >= KnowledgeRiskLevel.High).ToString("N0"), "禁止自动使用", "#B42318")));
        var rows = new StackPanel { Spacing = 0 };
        rows.Children.Add(TableHeader(
            ["文档", "分类 / 作用域", "状态", "版本 / 知识块", "风险", "操作"],
            [1.5, 1.25, .75, .9, .7, 1.7]));
        foreach (var document in documents.OrderByDescending(item => item.UpdatedAt))
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(ActionButton("查看详情", async () => await ShowKnowledgeDetailAsync(document)));
            if (document.CanActivate)
                actions.Children.Add(ActionButton("审核并启用", async () =>
                {
                    if (!await ConfirmAsync("启用知识", $"确认“{document.Title}”内容已人工核对，允许 AI 在匹配作用域内检索？", "确认启用")) return;
                    try { await _services.KnowledgeBase.ActivateAsync(document.Id, cancellationToken: _lifetime.Token); }
                    catch (Exception error) { await ShowMessageAsync("无法启用知识", error.Message); }
                    await RenderCurrentPageAsync();
                }, primary: true));
            if (document.Status == KnowledgeDocumentStatus.Active)
                actions.Children.Add(ActionButton("停用", async () =>
                {
                    await _services.KnowledgeBase.DisableAsync(document.Id, cancellationToken: _lifetime.Token);
                    await RenderCurrentPageAsync();
                }));
            actions.Children.Add(ActionButton("新版本", async () => await UploadKnowledgeAsync(document)));
            actions.Children.Add(ActionButton("删除", async () =>
            {
                if (!await ConfirmAsync("删除知识文档", $"确认删除“{document.Title}”？原件与版本记录将按本地知识库规则处理。", "确认删除")) return;
                await _services.KnowledgeBase.DeleteAsync(document.Id, cancellationToken: _lifetime.Token);
                await RenderCurrentPageAsync();
            }, danger: true));
            rows.Children.Add(TableRow(
                [
                    TextCell(document.Title, true, document.OriginalFileName),
                    TextCell(document.CategoryLabel, false, document.ScopeLabel),
                    BadgeCell(document.StatusLabel, document.Status == KnowledgeDocumentStatus.Active ? PrimarySoft : Brush.Parse("#EEF1F0")),
                    TextCell($"{document.VersionLabel} · {document.ChunkCount:N0} 块"),
                    TextCell(document.RiskLevel.ToString(), false, string.Join("；", document.RiskFlags.Take(2))),
                    actions
                ],
                [1.5, 1.25, .75, .9, .7, 1.7]));
        }
        if (documents.Count == 0)
            rows.Children.Add(EmptyState(
                "知识库为空",
                "上传公司政策、产品资料、销售 SOP 或客户专属资料；处理后仍需人工审核启用。"));
        page.Children.Add(SectionCard("知识文档", $"{documents.Count:N0} 份", rows));

        var retrievalQuery = new TextBox
        {
            Watermark = "输入业务问题，验证真实知识检索、作用域与引用结果",
            MinWidth = 480
        };
        var retrievalResults = new StackPanel { Spacing = 8 };
        var retrievalButton = ActionButton("执行真实检索", async () =>
        {
            retrievalResults.Children.Clear();
            if (string.IsNullOrWhiteSpace(retrievalQuery.Text))
            {
                retrievalResults.Children.Add(BodyText("请输入要验证的业务问题。", Warning, 12));
                return;
            }
            try
            {
                var result = await _services.KnowledgeRetrieval.RetrieveAsync(new KnowledgeRetrievalRequest
                {
                    Query = retrievalQuery.Text.Trim(),
                    UsageContext = "knowledge_retrieval_test",
                    Limit = 12,
                    MinimumScore = 0.12
                }, _lifetime.Token);
                retrievalResults.Children.Add(BodyText(
                    result.SufficientToAnswer
                        ? $"可引用 {result.Hits.Count} 个知识块；检索 ID：{result.Id}。结果只表示相关性，不代表业务因果或自动批准。"
                        : $"知识不足：{result.InsufficiencyReason} 检索 ID：{result.Id}",
                    result.SufficientToAnswer ? Primary : Warning,
                    12));
                foreach (var hit in result.Hits)
                    retrievalResults.Children.Add(Card(new StackPanel
                    {
                        Spacing = 5,
                        Children =
                        {
                            TitleText($"{hit.RelevanceScore:P0} · {hit.CitationLabel}", 14),
                            BodyText($"{hit.Scope.Label} · {KnowledgeLabels.Category(hit.Category)}", Muted, 11),
                            BodyText(hit.Content, Ink, 12)
                        }
                    }));
            }
            catch (Exception error) { retrievalResults.Children.Add(BodyText(error.Message, Danger, 12)); }
        }, primary: true);
        var retrievalHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        retrievalHeader.Children.Add(retrievalQuery);
        Grid.SetColumn(retrievalButton, 1);
        retrievalHeader.Children.Add(retrievalButton);
        var retrievalPanel = new StackPanel { Spacing = 10 };
        retrievalPanel.Children.Add(retrievalHeader);
        retrievalPanel.Children.Add(retrievalResults);
        page.Children.Add(SectionCard("检索测试", "只读验证", retrievalPanel));

        var candidates = await _services.KnowledgeLearning.RefreshCandidatesAsync(_lifetime.Token);
        var candidateRows = new StackPanel { Spacing = 0 };
        candidateRows.Children.Add(TableHeader(
            ["候选知识", "证据等级", "样本 / 回复 / 推进 / 成交", "审核状态", "人工操作"],
            [1.7, .9, 1.2, .8, 1.6]));
        foreach (var candidate in candidates.OrderByDescending(item => item.UpdatedAt).Take(100))
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (candidate.Status == KnowledgeCandidateStatus.Proposed)
            {
                actions.Children.Add(ActionButton("批准", async () =>
                {
                    await _services.KnowledgeLearning.ReviewAsync(candidate.Id, true, cancellationToken: _lifetime.Token);
                    await RenderCurrentPageAsync();
                }, primary: true));
            }
            if (candidate.Status is KnowledgeCandidateStatus.Proposed or KnowledgeCandidateStatus.Approved)
            {
                actions.Children.Add(ActionButton("拒绝", async () =>
                {
                    await _services.KnowledgeLearning.ReviewAsync(candidate.Id, false, cancellationToken: _lifetime.Token);
                    await RenderCurrentPageAsync();
                }, danger: true));
            }
            if (candidate.Status == KnowledgeCandidateStatus.Approved)
            {
                actions.Children.Add(ActionButton("发布为知识", async () =>
                {
                    await _services.KnowledgeBase.PublishCandidateAsync(candidate.Id, cancellationToken: _lifetime.Token);
                    await RenderCurrentPageAsync();
                }, primary: true));
            }
            candidateRows.Children.Add(TableRow(
                [
                    TextCell(candidate.Title, true, candidate.ReviewNote),
                    TextCell(candidate.EvidenceLabel),
                    TextCell($"{candidate.SampleSize} / {candidate.Replies} / {candidate.StageProgressions} / {candidate.Conversions}"),
                    TextCell(candidate.Status switch
                    {
                        KnowledgeCandidateStatus.Proposed => "待审核",
                        KnowledgeCandidateStatus.Approved => "已批准",
                        KnowledgeCandidateStatus.Rejected => "已拒绝",
                        KnowledgeCandidateStatus.Published => "已发布",
                        _ => candidate.Status.ToString()
                    }),
                    actions
                ],
                [1.7, .9, 1.2, .8, 1.6]));
        }
        if (candidates.Count == 0)
            candidateRows.Children.Add(EmptyState("暂无知识候选", "只有达到真实发送样本门槛的话术才会进入人工审核队列。"));
        page.Children.Add(SectionCard("真实互动知识候选", $"{candidates.Count:N0} 项", candidateRows));
        return page;
    }

    private async Task ShowKnowledgeDetailAsync(KnowledgeDocument document)
    {
        var versions = await _services.KnowledgeBase.GetVersionsAsync(document.Id, _lifetime.Token);
        var chunks = await _services.KnowledgeBase.GetChunksAsync(document.Id, cancellationToken: _lifetime.Token);
        var conflicts = await _services.KnowledgeBase.GetConflictsAsync(document.Id, _lifetime.Token);
        var title = new TextBox { Text = document.Title };
        var categoryValues = Enum.GetValues<KnowledgeCategory>().ToList();
        var category = new ComboBox
        {
            ItemsSource = categoryValues.Select(KnowledgeLabels.Category).ToList(),
            SelectedIndex = categoryValues.IndexOf(document.Category)
        };
        var usageValues = Enum.GetValues<KnowledgeUsageMode>().ToList();
        var usage = new ComboBox
        {
            ItemsSource = usageValues.Select(KnowledgeUsageLabel).ToList(),
            SelectedIndex = usageValues.IndexOf(document.UsageMode)
        };
        var tags = new TextBox { Text = string.Join("，", document.Tags), Watermark = "多个标签用逗号分隔" };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(TitleText($"{document.Title} · {document.VersionLabel}", 23));
        panel.Children.Add(BodyText(
            $"{document.StatusLabel} · {document.CategoryLabel} · {document.ScopeLabel} · {document.RiskLevel}\n" +
            $"原件：{document.OriginalFileName} · 语言：{Fallback(document.DetectedLanguage, "未识别")} · 知识块：{document.ChunkCount:N0}\n" +
            $"摘要：{Fallback(document.Summary, "尚无摘要")}\n" +
            $"风险与警告：{string.Join("；", document.RiskFlags.DefaultIfEmpty("无"))}",
            Muted,
            12));
        var metadata = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.4*,*,*"),
            ColumnSpacing = 8
        };
        metadata.Children.Add(Field("标题", title));
        var categoryField = Field("分类", category);
        Grid.SetColumn(categoryField, 1);
        metadata.Children.Add(categoryField);
        var usageField = Field("使用方式", usage);
        Grid.SetColumn(usageField, 2);
        metadata.Children.Add(usageField);
        panel.Children.Add(metadata);
        panel.Children.Add(Field("标签", tags));
        var metadataActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        metadataActions.Children.Add(ActionButton("保存审核信息", async () =>
        {
            await _services.KnowledgeBase.UpdateReviewMetadataAsync(
                document.Id,
                title.Text ?? "",
                categoryValues[Math.Max(0, category.SelectedIndex)],
                usageValues[Math.Max(0, usage.SelectedIndex)],
                (tags.Text ?? "").Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                document.EffectiveFrom,
                document.EffectiveUntil,
                _lifetime.Token);
            await ShowMessageAsync("审核信息已保存", "当前知识版本、作用域和审计记录保持不变。");
        }, primary: true));
        metadataActions.Children.Add(ActionButton("打开原件", async () =>
        {
            try
            {
                var path = await _services.KnowledgeBase.GetOriginalPathAsync(document.Id, cancellationToken: _lifetime.Token);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception error) { await ShowMessageAsync("无法打开原件", error.Message); }
        }));
        metadataActions.Children.Add(ActionButton("上传新版本", async () => await UploadKnowledgeAsync(document)));
        panel.Children.Add(metadataActions);

        var original = versions.FirstOrDefault(item => item.Id == document.CurrentVersionId) ?? versions.FirstOrDefault();
        panel.Children.Add(SectionCard(
            "原文与解析",
            original is null ? "无版本" : $"{original.ParserName} · {original.FileSize / 1024d:N1} KB · SHA256 {original.Sha256[..Math.Min(12, original.Sha256.Length)]}",
            BodyText(original is null
                ? "尚无可预览版本。"
                : Fallback(original.ExtractedText, string.Join("\n", original.Warnings.DefaultIfEmpty("当前版本未提取到文本。"))),
                Ink,
                12)));
        var chunkRows = new StackPanel { Spacing = 0 };
        chunkRows.Children.Add(TableHeader(["序号", "标题 / 定位", "内容", "状态"], [.5, 1.2, 2.5, .7]));
        foreach (var chunk in chunks.Take(300))
            chunkRows.Children.Add(TableRow(
                [
                    TextCell((chunk.Ordinal + 1).ToString()),
                    TextCell(Fallback(chunk.Heading, "正文"), false, Fallback(chunk.Locator, "—")),
                    TextCell(chunk.Content),
                    TextCell(chunk.IsActive ? "已启用" : "未启用")
                ],
                [.5, 1.2, 2.5, .7]));
        panel.Children.Add(SectionCard("知识块", $"{chunks.Count:N0} 块", chunkRows));
        var versionRows = new StackPanel { Spacing = 0 };
        versionRows.Children.Add(TableHeader(["版本", "原始文件", "解析器", "知识块", "状态 / 时间"], [.6, 1.4, .9, .6, 1.2]));
        foreach (var version in versions)
            versionRows.Children.Add(TableRow(
                [
                    TextCell($"V{version.Version}", true),
                    TextCell(version.OriginalFileName),
                    TextCell($"{version.ParserName} {version.ParserVersion}"),
                    TextCell(version.ChunkCount.ToString("N0")),
                    TextCell(KnowledgeLabels.Status(version.Status), false, version.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
                ],
                [.6, 1.4, .9, .6, 1.2]));
        panel.Children.Add(SectionCard("版本历史", $"{versions.Count:N0} 版", versionRows));
        var conflictRows = new StackPanel { Spacing = 0 };
        conflictRows.Children.Add(TableHeader(["主题", "冲突说明", "状态", "人工操作"], [1, 2.1, .7, 1]));
        foreach (var conflict in conflicts)
        {
            Control action = conflict.Status == KnowledgeConflictStatus.Open
                ? ActionButton("保留当前文档", async () =>
                {
                    if (!await ConfirmAsync("人工解决知识冲突", $"确认“{document.Title}”是本次应保留的资料？另一份资料会保持停用，当前资料回到待审核状态。", "确认保留")) return;
                    await _services.KnowledgeBase.ResolveConflictAsync(conflict.Id, document.Id, cancellationToken: _lifetime.Token);
                    await RenderCurrentPageAsync();
                }, primary: true)
                : BodyText("已处理", Primary, 12);
            conflictRows.Children.Add(TableRow(
                [TextCell(conflict.Topic), TextCell(conflict.Detail), TextCell(conflict.Status.ToString()), action],
                [1, 2.1, .7, 1]));
        }
        if (conflicts.Count == 0) conflictRows.Children.Add(EmptyState("没有知识冲突", "当前文档未检测到待人工处理的冲突。"));
        panel.Children.Add(SectionCard("版本与冲突", $"{conflicts.Count:N0} 项", conflictRows));
        var close = ActionButton("完成", () => Task.CompletedTask, primary: true);
        var dialog = DialogWindow("知识详情", new ScrollViewer { Content = panel }, 1060, 860);
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(close);
        await dialog.ShowDialog(this);
    }

    private async Task UploadKnowledgeAsync(KnowledgeDocument? existing = null)
    {
        var path = await PickOpenFileAsync(
            existing is null ? "选择知识文件" : "选择新版本文件",
            "*.pdf", "*.docx", "*.pptx", "*.xlsx", "*.csv", "*.txt", "*.md", "*.html",
            "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.tif", "*.tiff");
        if (string.IsNullOrWhiteSpace(path)) return;
        var title = Path.GetFileNameWithoutExtension(path);
        if (!await ConfirmAsync(
                existing is null ? "上传知识文件" : "上传新版本",
                $"文件：{Path.GetFileName(path)}\n" +
                $"标题：{(existing?.Title ?? title)}\n" +
                "作用域：全局\n来源：人工批准资料\n\n" +
                "系统会在本机解析、分块和风险扫描；上传后仍需人工审核才能启用。",
                "开始处理"))
            return;
        try
        {
            var result = await _services.KnowledgeBase.UploadAsync(path, new KnowledgeUploadOptions
            {
                ExistingDocumentId = existing?.Id ?? "",
                Title = existing?.Title ?? title,
                SourceKind = KnowledgeSourceKind.ApprovedDocument,
                UsageMode = KnowledgeUsageMode.StyleReference,
                Scope = new KnowledgeScope { Kind = KnowledgeScopeKind.Global }
            }, _lifetime.Token);
            await ShowMessageAsync(
                "知识处理完成",
                $"“{result.Title}”状态：{result.StatusLabel}，版本 {result.VersionLabel}，知识块 {result.ChunkCount:N0}。");
            await RenderCurrentPageAsync();
        }
        catch (Exception error) { await ShowMessageAsync("知识处理失败", error.Message); }
    }

    private async Task<Control> BuildAnalyticsAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "客户智能分析",
            "整合 CRM、WhatsApp、邮件、自动化、历史 AI 分析和已批准知识，输出带证据台账、版本历史与可导出文件的中文管理报告。",
            "CUSTOMER INTELLIGENCE"));
        if (_leads.Count == 0)
        {
            page.Children.Add(EmptyState("暂无客户", "导入客户后才能生成客户情报报告。"));
            return page;
        }
        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            ColumnSpacing = 16,
            MinHeight = 690
        };
        var search = new TextBox { Watermark = "搜索客户、公司、Buyer ID、邮箱", Margin = new Thickness(0, 0, 0, 10) };
        var customerList = new StackPanel { Spacing = 6 };
        var customerButtons = new List<(Lead Lead, Button Button)>();
        var detail = new StackPanel { Spacing = 12 };
        Lead selectedLead = _leads[0];
        CustomerAnalysisReport? currentReport = null;
        List<CustomerAnalysisReport> reports = [];

        Task RenderReportAsync(CustomerAnalysisReport? report)
        {
            currentReport = report;
            detail.Children.Clear();
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            var identity = new StackPanel { Spacing = 5 };
            identity.Children.Add(TitleText(selectedLead.DisplayName, 29));
            identity.Children.Add(BodyText(
                $"{Fallback(selectedLead.Company, "公司待补充")} · {selectedLead.StageLabel} · {selectedLead.Country} · {selectedLead.Grade} 级 {selectedLead.Score} 分",
                Muted,
                12));
            header.Children.Add(identity);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            actions.Children.Add(ActionButton("生成新报告", async () => await GenerateCustomerReportAsync(selectedLead), primary: true));
            if (report is not null)
            {
                actions.Children.Add(ActionButton("版本对比", async () =>
                {
                    var previous = reports
                        .Where(item => item.Status == CustomerReportStatus.Succeeded && item.Version < report.Version)
                        .OrderByDescending(item => item.Version)
                        .FirstOrDefault();
                    await ShowReportComparisonAsync(previous, report);
                }));
                actions.Children.Add(ActionButton("导出 Word", async () => await ExportCustomerReportAsync(report, true)));
                actions.Children.Add(ActionButton("导出 PDF", async () => await ExportCustomerReportAsync(report, false)));
            }
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            detail.Children.Add(header);
            var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            history.Children.Add(BodyText("报告版本", Muted, 11));
            foreach (var version in reports)
                history.Children.Add(ActionButton(version.VersionLabel, async () => await RenderReportAsync(version),
                    primary: report?.Id == version.Id));
            detail.Children.Add(new ScrollViewer
            {
                Content = history,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            });
            detail.Children.Add(BuildAnalyticsReport(report, selectedLead));
            return Task.CompletedTask;
        }

        async Task SelectLeadAsync(Lead lead)
        {
            selectedLead = lead;
            reports = await _services.Repository.GetCustomerAnalysisReportsAsync(lead.Id, _lifetime.Token);
            await RenderReportAsync(reports.FirstOrDefault());
        }

        foreach (var lead in _leads)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10),
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 3,
                            Children =
                            {
                                TitleText(lead.DisplayName, 13),
                                BodyText(Fallback(lead.Company, lead.ProductInterest), Muted, 10)
                            }
                        },
                        BadgeCell($"{lead.Grade} · {lead.Score}", GradeBrush(lead.Grade))
                    }
                }
            };
            if (button.Content is Grid row) Grid.SetColumn(row.Children[1], 1);
            button.Click += async (_, _) => await SelectLeadAsync(lead);
            customerButtons.Add((lead, button));
            customerList.Children.Add(button);
        }
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? "";
            foreach (var item in customerButtons)
            {
                var haystack = string.Join(" ", item.Lead.DisplayName, item.Lead.Company, item.Lead.BuyerId, item.Lead.Email, item.Lead.PhoneE164);
                item.Button.IsVisible = query.Length == 0 || haystack.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            }
        };
        var left = new Border
        {
            Background = Surface,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
            Child = new DockPanel
            {
                Children =
                {
                    search,
                    new ScrollViewer
                    {
                        Content = customerList,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    }
                }
            }
        };
        DockPanel.SetDock(search, Dock.Top);
        workspace.Children.Add(left);
        var detailScroll = new ScrollViewer
        {
            Content = detail,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetColumn(detailScroll, 1);
        workspace.Children.Add(detailScroll);
        page.Children.Add(workspace);
        await SelectLeadAsync(selectedLead);
        return page;
    }

    private static Control BuildAnalyticsReport(CustomerAnalysisReport? report, Lead lead)
    {
        if (report is null)
            return EmptyState(
                "尚未生成报告",
                $"选择“{lead.DisplayName}”并点击“生成新报告”；系统会分五步生成并保留版本。");
        var content = report.Report;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SectionCard(
            $"{report.CustomerName} · {report.VersionLabel}",
            report.StatusLabel,
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    TitleText(Fallback(content.ExecutiveSummary.OneLinePositioning, content.ManagementSummary), 16),
                    BodyText(
                        $"价值判断：{Fallback(content.ExecutiveSummary.OverallValueJudgment, "待生成")}\n" +
                        $"当前建议：{Fallback(content.ExecutiveSummary.CurrentSalesRecommendation, "待生成")}\n" +
                        $"成交概率：{content.OpportunityJudgment.DealProbability}% · AI 评分：{content.OpportunityJudgment.AiScore}/100 · 等级：{content.OpportunityJudgment.Grade}",
                        Ink,
                        13),
                    BodyText(
                        $"资料快照：WhatsApp {report.SourceSnapshot.WhatsAppMessages.Count:N0} 条 · 邮件 {report.SourceSnapshot.EmailMessages.Count:N0} 条 · 自动化 {report.SourceSnapshot.CampaignTouches.Count:N0} 次 · 时间线 {report.SourceSnapshot.Timeline.Count:N0} 条 · 知识引用 {report.SourceSnapshot.KnowledgeReferences.Count:N0} 项",
                        Muted,
                        11)
                }
            }));
        panel.Children.Add(ReportSection(
            "01 · 基础画像与业务背景",
            $"客户类型：{Fallback(content.BasicProfile.CustomerType, "待核实")}\n" +
            $"业务模式：{JoinReport(content.BasicProfile.BusinessModels)}\n" +
            $"产品方向：{Fallback(content.BasicProfile.ProductDirection, "待核实")}\n" +
            $"经营规模：{Fallback(content.BasicProfile.OperatingScale, "待核实")} · 发展阶段：{Fallback(content.BasicProfile.DevelopmentStage, "待核实")}\n" +
            $"核心优势：{JoinReport(content.BusinessBackground.CoreAdvantages)}\n" +
            $"当前限制：{JoinReport(content.BusinessBackground.CurrentLimitations)}\n" +
            $"增长机会：{JoinReport(content.BusinessBackground.GrowthOpportunities)}"));
        panel.Children.Add(ReportSection(
            "02 · 痛点、动机与 WhatsApp 信号",
            $"表层痛点：{JoinReport(content.PainAnalysis.SurfacePains)}\n" +
            $"深层问题：{JoinReport(content.PainAnalysis.DeepBusinessProblems)}\n" +
            $"兴趣原因：{JoinReport(content.PurchaseMotivation.InterestReasons)}\n" +
            $"触发事件：{JoinReport(content.PurchaseMotivation.TriggerEvents)}\n" +
            $"决策因素：{JoinReport(content.PurchaseMotivation.DecisionFactors)}\n" +
            $"互动程度：{Fallback(content.WhatsAppAnalysis.EngagementLevel, "待核实")} · 关注：{JoinReport(content.WhatsAppAnalysis.FocusTopics)}\n" +
            $"购买信号：{JoinReport(content.WhatsAppAnalysis.PurchaseSignals)}\n" +
            $"顾虑：{JoinReport(content.WhatsAppAnalysis.Concerns)}"));
        panel.Children.Add(ReportSection(
            "03 · 商机判断与产品匹配",
            $"正向因素：{JoinReport(content.OpportunityJudgment.PositiveFactors)}\n" +
            $"负向因素：{JoinReport(content.OpportunityJudgment.NegativeFactors)}\n" +
            $"高匹配点：{JoinReport(content.ProductFit.HighMatchPoints)}\n" +
            $"低匹配点：{JoinReport(content.ProductFit.LowMatchPoints)}\n" +
            $"待验证问题：{JoinReport(content.ProductFit.QuestionsToValidate)}"));
        panel.Children.Add(ReportSection(
            "04 · 销售策略与风险",
            $"建议话术：{Fallback(content.SalesStrategy.RecommendedTalkTrack, "待生成")}\n" +
            $"待确认问题：{JoinReport(content.SalesStrategy.PendingQuestions)}\n" +
            $"成交风险：{JoinReport(content.RiskAnalysis.DealRisks)}\n" +
            $"采用风险：{JoinReport(content.RiskAnalysis.AdoptionRisks)}\n" +
            $"流失风险：{JoinReport(content.RiskAnalysis.ChurnRisks)}"));
        var actionRows = new StackPanel { Spacing = 0 };
        actionRows.Children.Add(TableHeader(["时点", "动作", "理由", "成功标准"], [.7, 1.4, 1.5, 1.2]));
        foreach (var action in content.SalesStrategy.Actions)
            actionRows.Children.Add(TableRow(
                [TextCell(action.Timeframe), TextCell(action.Action), TextCell(action.Rationale), TextCell(action.SuccessCriterion)],
                [.7, 1.4, 1.5, 1.2]));
        panel.Children.Add(SectionCard("05 · 下一步行动计划", $"{content.SalesStrategy.Actions.Count:N0} 项", actionRows));
        var evidenceRows = new StackPanel { Spacing = 0 };
        evidenceRows.Children.Add(TableHeader(["性质", "主题", "陈述", "证据 / 来源", "置信度"], [.6, .9, 1.8, 1.6, .6]));
        foreach (var evidence in content.EvidenceLedger.Take(300))
            evidenceRows.Children.Add(TableRow(
                [
                    TextCell(evidence.Nature),
                    TextCell(evidence.Topic),
                    TextCell(evidence.Statement),
                    TextCell(evidence.Evidence, false, evidence.Source),
                    TextCell($"{evidence.Confidence:P0}")
                ],
                [.6, .9, 1.8, 1.6, .6]));
        panel.Children.Add(SectionCard("证据台账", $"{content.EvidenceLedger.Count:N0} 条", evidenceRows));
        var references = new StackPanel { Spacing = 7 };
        foreach (var hit in content.KnowledgeReferences)
            references.Children.Add(BodyText($"• {hit.CitationLabel} · {hit.RelevanceScore:P0}\n  {hit.Content}", Ink, 11));
        if (content.KnowledgeReferences.Count == 0)
            references.Children.Add(BodyText("本版本未引用知识库内容。", Muted, 11));
        panel.Children.Add(SectionCard("知识引用", $"{content.KnowledgeReferences.Count:N0} 项", references));
        return Card(panel);
    }

    private async Task ShowReportComparisonAsync(CustomerAnalysisReport? previous, CustomerAnalysisReport current)
    {
        if (previous is null)
        {
            await ShowMessageAsync("版本对比", "当前报告没有可比较的上一成功版本。");
            return;
        }
        var previousScore = previous.Report.OpportunityJudgment.AiScore;
        var currentScore = current.Report.OpportunityJudgment.AiScore;
        var previousProbability = previous.Report.OpportunityJudgment.DealProbability;
        var currentProbability = current.Report.OpportunityJudgment.DealProbability;
        await ShowMessageAsync(
            "客户情报版本对比",
            $"{previous.VersionLabel}  →  {current.VersionLabel}\n\n" +
            $"等级：{previous.Report.OpportunityJudgment.Grade} → {current.Report.OpportunityJudgment.Grade}\n" +
            $"AI 评分：{previousScore} → {currentScore}（{currentScore - previousScore:+#;-#;0}）\n" +
            $"成交概率：{previousProbability}% → {currentProbability}%（{currentProbability - previousProbability:+#;-#;0}%）\n\n" +
            $"上一版判断：\n{previous.Report.ExecutiveSummary.OverallValueJudgment}\n\n" +
            $"当前判断：\n{current.Report.ExecutiveSummary.OverallValueJudgment}");
    }

    private async Task GenerateCustomerReportAsync(Lead lead)
    {
        var progress = new Progress<CustomerAnalysisProgress>(item =>
        {
            _operationStatus = $"{item.Sequence}/{item.Total} · {item.Message}";
            PageSubtitle.Text = _operationStatus;
        });
        try
        {
            var report = await _services.CustomerAnalysis.GenerateAsync(lead.Id, progress, _lifetime.Token);
            await ShowMessageAsync("客户报告已生成", $"{report.CustomerName} · {report.VersionLabel} · {report.StatusLabel}");
            await RenderCurrentPageAsync();
        }
        catch (Exception error) { await ShowMessageAsync("报告生成失败", error.Message); }
    }

    private async Task ExportCustomerReportAsync(CustomerAnalysisReport report, bool word)
    {
        var extension = word ? ".docx" : ".pdf";
        var path = await PickSaveFileAsync(
            word ? "导出 Word 客户报告" : "导出 PDF 客户报告",
            $"{SafeFileName(report.CustomerName)}_客户背景调查报告_V{report.Version}{extension}",
            extension);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (word) await _services.CustomerReportExports.ExportWordAsync(report, path, _lifetime.Token);
            else await _services.CustomerReportExports.ExportPdfAsync(report, path, _lifetime.Token);
            await ShowMessageAsync("报告已导出", path);
        }
        catch (Exception error) { await ShowMessageAsync("报告导出失败", error.Message); }
    }

    private static void InsertAtSelection(TextBox box, string value)
    {
        var text = box.Text ?? "";
        var start = Math.Clamp(box.SelectionStart, 0, text.Length);
        box.Text = text.Insert(start, value);
        box.SelectionStart = start + value.Length;
        box.SelectionEnd = box.SelectionStart;
        box.Focus();
    }

    private static DateTimeOffset ParseBeijing(string value)
    {
        if (!DateTime.TryParse(value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var local) &&
            !DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out local))
            throw new InvalidOperationException("开始时间格式无效，请填写例如 2026-07-20 18:30。");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var zone = BeijingZone();
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string FormatBeijing(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, BeijingZone()).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo BeijingZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    private static string KnowledgeUsageLabel(KnowledgeUsageMode value) => value switch
    {
        KnowledgeUsageMode.ExactTemplate => "原文模板",
        KnowledgeUsageMode.StyleReference => "表达风格参考",
        KnowledgeUsageMode.PolicyReference => "政策参考",
        KnowledgeUsageMode.AnalysisReference => "分析参考",
        KnowledgeUsageMode.Excluded => "禁止检索",
        _ => value.ToString()
    };

    private static Control ReportSection(string title, string body) =>
        SectionCard(title, "", BodyText(body, Ink, 12));

    private static string JoinReport(IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return items.Count == 0 ? "待核实" : string.Join("；", items);
    }
}
