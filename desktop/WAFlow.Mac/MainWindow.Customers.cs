using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Core.Services;

namespace WAFlow.Mac;

public sealed partial class MainWindow
{
    private Task<Control> BuildCustomersAsync()
    {
        var page = PageStack();
        var lead = PageLead(
            "统一客户资产",
            "Buyer ID 优先识别同一客户；Excel / CSV 全量导入、编辑、搜索、分页和删除都直接操作本机数据库。");
        page.Children.Add(lead);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var search = Accessible(new TextBox
        {
            Text = _customerSearch,
            Watermark = "搜索姓名、Buyer ID、电话、邮箱或自定义维度",
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left
        }, "搜索客户");
        search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _customerSearch = search.Text?.Trim() ?? "";
            _customerPage = 1;
            await RenderCurrentPageAsync();
        };
        toolbar.Children.Add(search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        Grid.SetColumn(actions, 1);
        actions.Children.Add(ActionButton("清除搜索", async () =>
        {
            _customerSearch = "";
            _customerPage = 1;
            await RenderCurrentPageAsync();
        }));
        actions.Children.Add(ActionButton("导入 Excel / CSV", async () => await ImportCustomersAsync()));
        actions.Children.Add(ActionButton("新建客户", async () => await ShowLeadEditorAsync(null), primary: true));
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);

        var filtered = string.IsNullOrWhiteSpace(_customerSearch)
            ? _leads
            : _leads.Where(item =>
                string.Join(' ', new[]
                {
                    item.DisplayName, item.BuyerId, item.Company, item.Country, item.PhoneE164,
                    item.Email, item.ProductInterest, item.Owner, item.TagsLabel, item.CustomFieldsLabel
                }).Contains(_customerSearch, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)_customerPageSize));
        _customerPage = Math.Clamp(_customerPage, 1, pages);
        var visible = filtered.Skip((_customerPage - 1) * _customerPageSize).Take(_customerPageSize).ToList();

        var rows = new StackPanel { Spacing = 0 };
        rows.Children.Add(TableHeader(
            ["客户", "统一身份", "公司 / 产品", "阶段", "AI 等级", "操作"],
            [1.45, 1.45, 1.5, .85, .7, 1.1]));
        foreach (var item in visible)
        {
            var rowActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowActions.Children.Add(ActionButton("编辑", async () => await ShowLeadEditorAsync(item), minWidth: 58));
            rowActions.Children.Add(ActionButton("删除", async () => await DeleteLeadAsync(item), danger: true, minWidth: 58));
            rows.Children.Add(TableRow(
                [
                    TextCell(item.DisplayName, true, Fallback(item.PhoneE164, item.Email)),
                    TextCell(Fallback(item.BuyerId, "待补充 Buyer ID"), false, item.Owner),
                    TextCell(Fallback(item.Company, "—"), false, item.ProductInterest),
                    TextCell(item.StageLabel),
                    BadgeCell($"{item.Grade} · {item.Score}", GradeBrush(item.Grade)),
                    rowActions
                ],
                [1.45, 1.45, 1.5, .85, .7, 1.1]));
        }
        if (visible.Count == 0)
            rows.Children.Add(EmptyState(
                "没有匹配客户",
                string.IsNullOrWhiteSpace(_customerSearch)
                    ? "导入原始客户表或新建客户。"
                    : "更换关键词或清除搜索后重试。"));
        page.Children.Add(SectionCard("客户清单", $"{filtered.Count:N0} 位", rows));

        var pagination = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        var previous = ActionButton("上一页", async () =>
        {
            _customerPage = Math.Max(1, _customerPage - 1);
            await RenderCurrentPageAsync();
        });
        previous.IsEnabled = _customerPage > 1;
        pagination.Children.Add(previous);
        var status = BodyText($"第 {_customerPage:N0} / {pages:N0} 页 · 共 {filtered.Count:N0} 位客户", Ink, 12);
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 1);
        pagination.Children.Add(status);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(right, 2);
        var size = Accessible(new ComboBox
        {
            ItemsSource = new[] { 10, 30 },
            SelectedItem = _customerPageSize,
            MinWidth = 110
        }, "每页客户数量");
        size.SelectionChanged += async (_, _) =>
        {
            if (size.SelectedItem is not int value || value == _customerPageSize) return;
            _customerPageSize = value;
            _customerPage = 1;
            await RenderCurrentPageAsync();
        };
        right.Children.Add(size);
        var next = ActionButton("下一页", async () =>
        {
            _customerPage = Math.Min(pages, _customerPage + 1);
            await RenderCurrentPageAsync();
        });
        next.IsEnabled = _customerPage < pages;
        right.Children.Add(next);
        pagination.Children.Add(right);
        page.Children.Add(pagination);
        return Task.FromResult<Control>(page);
    }

    private async Task ImportCustomersAsync()
    {
        var path = await PickOpenFileAsync("选择客户 Excel / CSV", "*.xlsx", "*.csv");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            _operationStatus = "正在解析客户表…";
            var parsed = _services.Imports.Parse(path);
            var sheet = parsed.Sheets.FirstOrDefault(item =>
                            item.Name.Equals(parsed.PreferredSheetName, StringComparison.OrdinalIgnoreCase))
                        ?? parsed.Sheets[0];
            var mapping = _services.Imports.SuggestMapping(sheet);
            var preview = await _services.Imports.BuildPreviewAsync(
                sheet,
                mapping,
                cancellationToken: _lifetime.Token);
            var valid = preview.Count(item => item.Errors.Count == 0);
            var newRows = preview.Count(item => item.Errors.Count == 0 && !item.IsDuplicate);
            var updates = preview.Count(item => item.Errors.Count == 0 && item.IsDuplicate);
            var nameMapping = mapping.FirstOrDefault(item => item.Target == ImportField.Name)?.Header ?? "自动回退";
            var buyerMapping = mapping.FirstOrDefault(item => item.Target == ImportField.BuyerId)?.Header ?? "未识别";
            var phoneMapping = mapping.FirstOrDefault(item => item.Target == ImportField.WhatsApp)?.Header ?? "未识别";
            var confirmed = await ConfirmAsync(
                "确认导入客户表",
                $"工作表：{sheet.Name}\n" +
                $"数据行：{sheet.Rows.Count:N0}，可提交：{valid:N0}\n" +
                $"预计新建：{newRows:N0}，更新：{updates:N0}\n\n" +
                $"姓名映射：{nameMapping}\nBuyer ID 映射：{buyerMapping}\nWhatsApp 映射：{phoneMapping}\n\n" +
                "所有源列都会按原表头保存在客户自定义维度中；不会只取前 10 行。",
                "开始导入");
            if (!confirmed) return;
            var result = await _services.Imports.CommitAsync(
                Path.GetFileName(path),
                preview,
                allowStageChange: true,
                allowOwnerChange: true,
                cancellationToken: _lifetime.Token);
            await ShowMessageAsync(
                "导入完成",
                $"共处理 {result.Total:N0} 行：新建 {result.Created:N0}，更新 {result.Updated:N0}，" +
                $"号码风险 {result.InvalidPhones:N0}，待 WhatsApp 检测 {result.PendingWhatsAppChecks:N0}，失败 {result.Failed:N0}。");
            _customerPage = 1;
            await RenderCurrentPageAsync();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("导入失败", error.Message);
        }
    }

    private async Task ShowLeadEditorAsync(Lead? source)
    {
        var lead = source ?? new Lead();
        var name = new TextBox { Text = lead.Name, Watermark = "客户姓名或昵称" };
        var buyerId = new TextBox { Text = lead.BuyerId, Watermark = "DHgate Buyer ID / 统一客户标识" };
        var company = new TextBox { Text = lead.Company };
        var country = new TextBox { Text = lead.Country };
        var phone = new TextBox { Text = lead.PhoneE164, Watermark = "+8613800000000" };
        var email = new TextBox { Text = lead.Email };
        var product = new TextBox { Text = lead.ProductInterest };
        var owner = new TextBox { Text = lead.Owner };
        var tags = new TextBox { Text = string.Join(", ", lead.Tags) };
        var stage = new ComboBox { ItemsSource = Enum.GetValues<LeadStage>(), SelectedItem = lead.Stage };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(TitleText(source is null ? "新建客户" : $"编辑 · {lead.DisplayName}", 24));
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 10
        };
        var fields = new Control[]
        {
            Field("客户姓名 / 昵称", name), Field("Buyer ID", buyerId),
            Field("公司", company), Field("国家 / 地区", country),
            Field("WhatsApp 国际号码", phone, "必须包含国家区号，不自动猜测。"), Field("邮箱", email),
            Field("关注产品", product), Field("负责人", owner),
            Field("阶段", stage), Field("标签", tags, "多个标签用逗号分隔。")
        };
        for (var index = 0; index < fields.Length; index++)
        {
            Grid.SetRow(fields[index], index / 2);
            Grid.SetColumn(fields[index], index % 2);
            form.Children.Add(fields[index]);
        }
        panel.Children.Add(form);
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = DialogWindow(source is null ? "新建客户" : "编辑客户", new ScrollViewer { Content = panel }, 760, 690);
        var cancel = new Button { Content = "取消" };
        var save = new Button { Content = "保存客户" };
        save.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(false);
        save.Click += async (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text) && string.IsNullOrWhiteSpace(company.Text))
                    throw new InvalidOperationException("客户姓名和公司至少填写一项。");
                var normalized = PhoneNormalizer.Normalize(phone.Text, country.Text);
                var previousPhone = lead.PhoneE164;
                lead.Name = name.Text?.Trim() ?? "";
                lead.BuyerId = buyerId.Text?.Trim() ?? "";
                lead.Company = company.Text?.Trim() ?? "";
                lead.Country = country.Text?.Trim() ?? "";
                lead.PhoneE164 = normalized.E164.Length > 0 ? normalized.E164 : phone.Text?.Trim() ?? "";
                lead.PhoneValid = normalized.Valid;
                lead.Email = email.Text?.Trim() ?? "";
                lead.ProductInterest = product.Text?.Trim() ?? "";
                lead.Owner = owner.Text?.Trim() ?? "";
                lead.Stage = stage.SelectedItem is LeadStage selectedStage ? selectedStage : LeadStage.New;
                lead.Tags = (tags.Text ?? "").Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                lead.UpdatedAt = DateTimeOffset.Now;
                if (!previousPhone.Equals(lead.PhoneE164, StringComparison.Ordinal))
                    lead.QueueWhatsAppRegistrationCheck();
                await _services.Repository.UpsertLeadAsync(lead, _lifetime.Token);
                dialog.Close(true);
            }
            catch (Exception error)
            {
                await ShowMessageAsync("无法保存客户", error.Message);
            }
        };
        buttonRow.Children.Add(cancel);
        buttonRow.Children.Add(save);
        panel.Children.Add(buttonRow);
        if (await dialog.ShowDialog<bool>(this))
            await RenderCurrentPageAsync();
    }

    private async Task DeleteLeadAsync(Lead lead)
    {
        if (!await ConfirmAsync(
                "删除客户",
                $"将删除“{lead.DisplayName}”及其本地分析、草稿和关联记录。此操作不能撤销。",
                "确认删除"))
            return;
        await _services.Repository.DeleteLeadAsync(lead.Id, _lifetime.Token);
        await RenderCurrentPageAsync();
    }

    private Task<Control> BuildLeadIntelligenceAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "商机决策队列",
            "基于 CRM 与真实沟通证据生成等级、采购概率、风险和下一步动作；批量任务支持中断续跑。"));
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        toolbar.Children.Add(BodyText(_operationStatus, Muted, 12));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        Grid.SetColumn(actions, 1);
        actions.Children.Add(ActionButton("导入客户", async () => await ImportCustomersAsync()));
        actions.Children.Add(ActionButton("批量运行 AI 分析", async () => await RunBulkAnalysisAsync(), primary: true));
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);
        page.Children.Add(MetricGrid(
            ("已分析", _dashboard.AnalyzedLeads.ToString("N0"), "具备当前 AI 合约结果", "#087A5E"),
            ("等待分析", _dashboard.QueuedAnalyses.ToString("N0"), "正在排队或处理中", "#0B74C8"),
            ("可重试", _dashboard.FailedAnalyses.ToString("N0"), "失败客户可断点重试", "#B42318"),
            ("A级客户", _dashboard.Grades.GetValueOrDefault("A").ToString("N0"), "建议优先推进", "#7A5AF8")));

        var rows = new StackPanel { Spacing = 0 };
        rows.Children.Add(TableHeader(
            ["客户", "评分", "阶段", "画像摘要", "下一步动作", "操作"],
            [1.15, .65, .75, 1.8, 1.8, .8]));
        foreach (var item in _leads.OrderByDescending(item => item.Score).ThenByDescending(item => item.UpdatedAt).Take(250))
        {
            var analyze = ActionButton(
                item.AnalysisStatus == AnalysisStatus.RetryableFailed ? "重试" : "分析",
                async () => await AnalyzeLeadAsync(item),
                primary: item.AnalysisStatus != AnalysisStatus.Succeeded);
            rows.Children.Add(TableRow(
                [
                    TextCell(item.DisplayName, true, Fallback(item.Company, item.ProductInterest)),
                    BadgeCell($"{item.Grade} · {item.Score}", GradeBrush(item.Grade)),
                    TextCell(item.StageLabel, false, item.AnalysisStateLabel),
                    TextCell(Fallback(item.ProfileSummary, "等待 AI 分析")),
                    TextCell(Fallback(item.NextAction, "补充客户资料")),
                    analyze
                ],
                [1.15, .65, .75, 1.8, 1.8, .8]));
        }
        if (_leads.Count == 0)
            rows.Children.Add(EmptyState("尚无客户", "先导入客户表，再运行商机智能分析。"));
        page.Children.Add(SectionCard("客户决策清单", $"{_leads.Count:N0} 位", rows));
        return Task.FromResult<Control>(page);
    }

    private async Task AnalyzeLeadAsync(Lead lead)
    {
        try
        {
            _operationStatus = $"正在分析：{lead.DisplayName}";
            await _services.DeepSeek.AnalyzeLeadAsync(lead, _lifetime.Token);
            _operationStatus = $"已完成：{lead.DisplayName}";
            await RenderCurrentPageAsync();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("AI 分析失败", error.Message);
        }
    }

    private async Task RunBulkAnalysisAsync()
    {
        var progress = new Progress<LeadBulkAnalysisProgress>(value =>
        {
            _operationStatus = $"{value.Message} · {value.Completed:N0}/{value.Total:N0}";
            PageSubtitle.Text = _operationStatus;
        });
        try
        {
            var result = await _services.LeadAutomation.AnalyzeAllLeadsAsync(progress, _lifetime.Token);
            await ShowMessageAsync(
                "批量分析完成",
                $"共 {result.Total:N0} 位客户，成功 {result.Succeeded:N0}，失败 {result.Failed:N0}。");
            await RenderCurrentPageAsync();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("批量分析已停止", error.Message);
        }
    }
}
