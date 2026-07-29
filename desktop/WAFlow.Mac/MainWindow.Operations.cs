using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Mac;

public sealed partial class MainWindow
{
    private async Task<Control> BuildCampaignsAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "多渠道自动化触达",
            "WhatsApp 与邮件共用客户筛选、动态字段、人工审批、发送节奏和本地审计；真实发送前必须通过渠道资格检查。"));
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
        var subject = new TextBox { Text = campaign.EmailSubjectTemplate, Watermark = "邮件主题，可用 {name} 等字段" };
        var message = new TextBox
        {
            Text = campaign.MessageTemplate,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 130,
            Watermark = "消息正文，可用 {name}、{company}、{product} 等动态字段"
        };
        var interval = new TextBox { Text = campaign.EffectiveIntervalValue.ToString() };
        var intervalUnit = new ComboBox
        {
            ItemsSource = new[] { "分钟", "秒" },
            SelectedIndex = campaign.IntervalUnit == CampaignIntervalUnit.Seconds ? 1 : 0
        };
        var dailyLimit = new TextBox { Text = campaign.DailyLimit.ToString() };
        var immediate = new CheckBox
        {
            Content = "审批后立即执行",
            IsChecked = campaign.ScheduleMode == CampaignScheduleMode.Immediate
        };
        var leadChecks = _leads.Select(item => (
            Lead: item,
            Check: new CheckBox
            {
                Content = $"{item.DisplayName} · {item.Grade} · {item.StageLabel} · {Fallback(item.PhoneE164, item.Email)}",
                IsChecked = campaign.SelectedLeadIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)
            })).ToList();
        var audience = new StackPanel { Spacing = 5 };
        foreach (var item in leadChecks) audience.Children.Add(item.Check);
        var selectAll = ActionButton("选择全部客户", () =>
        {
            foreach (var item in leadChecks) item.Check.IsChecked = true;
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
        panel.Children.Add(immediate);
        panel.Children.Add(Field("邮件主题", subject, "WhatsApp 任务会忽略此字段。"));
        panel.Children.Add(Field("消息模板", message));
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
                campaign.IntervalValue = int.TryParse(interval.Text, out var intervalValue) ? Math.Max(1, intervalValue) : 5;
                campaign.IntervalUnit = intervalUnit.SelectedIndex == 1 ? CampaignIntervalUnit.Seconds : CampaignIntervalUnit.Minutes;
                campaign.DailyLimit = int.TryParse(dailyLimit.Text, out var limit) ? Math.Clamp(limit, 1, 1000) : 50;
                campaign.ScheduleMode = immediate.IsChecked == true ? CampaignScheduleMode.Immediate : CampaignScheduleMode.Scheduled;
                campaign.StartsAt = immediate.IsChecked == true ? DateTimeOffset.Now : DateTimeOffset.Now.AddMinutes(5);
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
            "本地知识中枢",
            "解析 PDF、Office、表格、文本和图片；只有人工审核并启用的知识才能进入 AI 检索。"));
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
        return page;
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
            "客户情报报告",
            "整合 CRM、WhatsApp、邮件、自动化、历史 AI 分析和已批准知识，输出带证据台账的中文管理报告。"));
        if (_leads.Count == 0)
        {
            page.Children.Add(EmptyState("暂无客户", "导入客户后才能生成客户情报报告。"));
            return page;
        }
        var customerBox = Accessible(new ComboBox
        {
            ItemsSource = _leads.Select(item => item.DisplayName).ToList(),
            SelectedIndex = 0,
            MinWidth = 280
        }, "报告客户");
        var selectedLead = _leads[0];
        var reports = await _services.Repository.GetCustomerAnalysisReportsAsync(selectedLead.Id, _lifetime.Token);
        var reportBox = Accessible(new ComboBox
        {
            ItemsSource = reports.Select(item => item.VersionLabel).ToList(),
            SelectedIndex = reports.Count > 0 ? 0 : -1,
            MinWidth = 240
        }, "报告版本");
        var currentReport = reports.FirstOrDefault();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        controls.Children.Add(customerBox);
        controls.Children.Add(reportBox);
        var generate = ActionButton("生成新报告", async () =>
        {
            selectedLead = _leads[Math.Clamp(customerBox.SelectedIndex, 0, _leads.Count - 1)];
            await GenerateCustomerReportAsync(selectedLead);
        }, primary: true);
        controls.Children.Add(generate);
        var exportWord = ActionButton("导出 Word", async () =>
        {
            if (currentReport is null) { await ShowMessageAsync("暂无报告", "请先生成或选择一份已完成报告。"); return; }
            await ExportCustomerReportAsync(currentReport, true);
        });
        var exportPdf = ActionButton("导出 PDF", async () =>
        {
            if (currentReport is null) { await ShowMessageAsync("暂无报告", "请先生成或选择一份已完成报告。"); return; }
            await ExportCustomerReportAsync(currentReport, false);
        });
        controls.Children.Add(exportWord);
        controls.Children.Add(exportPdf);
        customerBox.SelectionChanged += async (_, _) =>
        {
            if (customerBox.SelectedIndex < 0) return;
            selectedLead = _leads[customerBox.SelectedIndex];
            reports = await _services.Repository.GetCustomerAnalysisReportsAsync(selectedLead.Id, _lifetime.Token);
            reportBox.ItemsSource = reports.Select(item => item.VersionLabel).ToList();
            reportBox.SelectedIndex = reports.Count > 0 ? 0 : -1;
            currentReport = reports.FirstOrDefault();
        };
        reportBox.SelectionChanged += (_, _) =>
        {
            currentReport = reportBox.SelectedIndex >= 0 && reportBox.SelectedIndex < reports.Count
                ? reports[reportBox.SelectedIndex]
                : null;
        };
        page.Children.Add(controls);
        page.Children.Add(BuildReportSummary(currentReport, selectedLead));
        var customerRows = new StackPanel { Spacing = 0 };
        customerRows.Children.Add(TableHeader(
            ["客户", "等级", "阶段", "采购概率", "报告版本", "最近更新时间"],
            [1.5, .65, .9, .8, .9, 1.1]));
        foreach (var lead in _leads.Take(250))
        {
            var leadReports = await _services.Repository.GetCustomerAnalysisReportsAsync(lead.Id, _lifetime.Token);
            customerRows.Children.Add(TableRow(
                [
                    TextCell(lead.DisplayName, true, Fallback(lead.Company, lead.ProductInterest)),
                    BadgeCell($"{lead.Grade} · {lead.Score}", GradeBrush(lead.Grade)),
                    TextCell(lead.StageLabel),
                    TextCell($"{lead.PurchaseProbability}%"),
                    TextCell(leadReports.Count == 0 ? "尚未生成" : leadReports[0].VersionLabel),
                    TextCell(lead.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
                ],
                [1.5, .65, .9, .8, .9, 1.1]));
        }
        page.Children.Add(SectionCard("客户报告覆盖", $"{_leads.Count:N0} 位", customerRows));
        return page;
    }

    private static Control BuildReportSummary(CustomerAnalysisReport? report, Lead lead)
    {
        if (report is null)
            return EmptyState(
                "尚未生成报告",
                $"选择“{lead.DisplayName}”并点击“生成新报告”；系统会分五步生成并保留版本。");
        var content = report.Report;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(TitleText($"{report.CustomerName} · {report.VersionLabel}", 20));
        panel.Children.Add(BodyText(report.StatusLabel, report.Status == CustomerReportStatus.Succeeded ? Primary : Warning, 12));
        panel.Children.Add(BodyText(
            Fallback(content.ExecutiveSummary.OneLinePositioning, content.ManagementSummary),
            Ink,
            14));
        panel.Children.Add(BodyText(
            $"价值判断：{Fallback(content.ExecutiveSummary.OverallValueJudgment, "待生成")}\n" +
            $"当前建议：{Fallback(content.ExecutiveSummary.CurrentSalesRecommendation, "待生成")}\n" +
            $"成交概率：{content.OpportunityJudgment.DealProbability}% · AI 评分：{content.OpportunityJudgment.AiScore}/100\n" +
            $"数据覆盖：WhatsApp {report.SourceSnapshot.WhatsAppMessages.Count:N0} 条 · 邮件 {report.SourceSnapshot.EmailMessages.Count:N0} 条 · 自动化 {report.SourceSnapshot.CampaignTouches.Count:N0} 次"));
        return Card(panel);
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
}
