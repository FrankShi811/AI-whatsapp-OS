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
        page.Children.Add(PageLead(
            "每一列，都是客户上下文",
            "保留上传表格的全部动态维度；搜索、筛选、编辑与 WhatsApp 侧栏使用同一份客户数据。",
            "UNIFIED CUSTOMER GRAPH"));

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var search = Accessible(new TextBox
        {
            Text = _customerSearch,
            Watermark = "搜索所有核心字段与原表维度",
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
        var editSelected = ActionButton("编辑客户", async () =>
        {
            var item = _leads.FirstOrDefault(lead => _selectedCustomerIds.Contains(lead.Id));
            if (item is null)
            {
                await ShowMessageAsync("请选择客户", "先在客户清单中勾选一位客户。");
                return;
            }
            await ShowLeadEditorAsync(item);
        });
        editSelected.IsEnabled = _selectedCustomerIds.Count == 1;
        actions.Children.Add(editSelected);
        var deleteSelected = ActionButton("删除所选", async () =>
        {
            var selected = _leads.Where(item => _selectedCustomerIds.Contains(item.Id)).ToList();
            if (selected.Count == 0) return;
            if (!await ConfirmAsync("删除所选客户", $"将删除 {selected.Count:N0} 位客户及其本地分析、草稿和关联记录。此操作不能撤销。", "确认删除")) return;
            foreach (var item in selected)
                await _services.Repository.DeleteLeadAsync(item.Id, _lifetime.Token);
            _selectedCustomerIds.Clear();
            await RenderCurrentPageAsync();
        }, danger: true);
        deleteSelected.IsEnabled = _selectedCustomerIds.Count > 0;
        actions.Children.Add(deleteSelected);
        actions.Children.Add(ActionButton("＋  导入客户数据", async () => await ImportCustomersAsync(), primary: true));
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);

        var dimensions = CustomerDimensionCatalog.Build(_leads)
            .Where(item => !CustomerDimensionCatalog.IsPrimaryCategoryPreference(item))
            .ToList();
        var categoryValues = _leads.Select(CustomerDimensionCatalog.ResolvePrimaryCategoryPreference)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var filterRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.05*,1.15*,1.25*,1.35*,Auto"),
            ColumnSpacing = 9
        };
        var gradeFilter = Accessible(new ComboBox
        {
            ItemsSource = new[] { "全部等级", "A级", "B级", "C级", "D级" },
            SelectedIndex = _customerGradeFilter == "全部" ? 0 : Math.Max(1, Array.IndexOf(new[] { "A", "B", "C", "D" }, _customerGradeFilter) + 1)
        }, "客户等级筛选");
        var stageValues = Enum.GetValues<LeadStage>().ToList();
        var stageFilter = Accessible(new ComboBox
        {
            ItemsSource = new[] { "全部阶段" }.Concat(stageValues.Select(Labels.Stage)).ToList(),
            SelectedIndex = _customerStageFilter is null ? 0 : stageValues.IndexOf(_customerStageFilter.Value) + 1
        }, "客户阶段筛选");
        var categoryFilter = Accessible(new ComboBox
        {
            ItemsSource = new[] { "全部一级品类" }.Concat(categoryValues).ToList(),
            SelectedIndex = string.IsNullOrWhiteSpace(_customerCategoryFilter)
                ? 0
                : Math.Max(0, categoryValues.FindIndex(value => value.Equals(_customerCategoryFilter, StringComparison.CurrentCultureIgnoreCase)) + 1)
        }, "一级品类偏好筛选");
        var dimensionFilter = Accessible(new ComboBox
        {
            ItemsSource = new[] { "原表维度列" }.Concat(dimensions.Select(item => item.Label)).ToList(),
            SelectedIndex = string.IsNullOrWhiteSpace(_customerDimensionKey)
                ? 0
                : Math.Max(0, dimensions.FindIndex(item => item.Key.Equals(_customerDimensionKey, StringComparison.OrdinalIgnoreCase)) + 1)
        }, "显示原表维度列");
        filterRow.Children.Add(gradeFilter);
        Grid.SetColumn(stageFilter, 1);
        filterRow.Children.Add(stageFilter);
        Grid.SetColumn(categoryFilter, 2);
        filterRow.Children.Add(categoryFilter);
        Grid.SetColumn(dimensionFilter, 3);
        filterRow.Children.Add(dimensionFilter);
        var clearFilters = ActionButton("清除筛选", async () =>
        {
            _customerSearch = "";
            _customerGradeFilter = "全部";
            _customerStageFilter = null;
            _customerCategoryFilter = "";
            _customerDimensionKey = "";
            _customerPage = 1;
            await RenderCurrentPageAsync();
        });
        Grid.SetColumn(clearFilters, 4);
        filterRow.Children.Add(clearFilters);
        gradeFilter.SelectionChanged += async (_, _) =>
        {
            _customerGradeFilter = gradeFilter.SelectedIndex <= 0 ? "全部" : new[] { "A", "B", "C", "D" }[gradeFilter.SelectedIndex - 1];
            _customerPage = 1;
            await RenderCurrentPageAsync();
        };
        stageFilter.SelectionChanged += async (_, _) =>
        {
            _customerStageFilter = stageFilter.SelectedIndex <= 0 ? null : stageValues[stageFilter.SelectedIndex - 1];
            _customerPage = 1;
            await RenderCurrentPageAsync();
        };
        categoryFilter.SelectionChanged += async (_, _) =>
        {
            _customerCategoryFilter = categoryFilter.SelectedIndex <= 0 ? "" : categoryValues[categoryFilter.SelectedIndex - 1];
            _customerPage = 1;
            await RenderCurrentPageAsync();
        };
        dimensionFilter.SelectionChanged += async (_, _) =>
        {
            _customerDimensionKey = dimensionFilter.SelectedIndex <= 0 ? "" : dimensions[dimensionFilter.SelectedIndex - 1].Key;
            await RenderCurrentPageAsync();
        };
        page.Children.Add(filterRow);

        var filtered = (string.IsNullOrWhiteSpace(_customerSearch)
            ? _leads
            : _leads.Where(item =>
                string.Join(' ', new[]
                {
                    item.DisplayName, item.BuyerId, item.Company, item.Country, item.PhoneE164,
                    item.Email, item.ProductInterest, item.Owner, item.TagsLabel, item.CustomFieldsLabel
                }).Contains(_customerSearch, StringComparison.CurrentCultureIgnoreCase)).ToList())
            .Where(item => _customerGradeFilter == "全部" || item.Grade.Equals(_customerGradeFilter, StringComparison.OrdinalIgnoreCase))
            .Where(item => _customerStageFilter is null || item.Stage == _customerStageFilter)
            .Where(item => string.IsNullOrWhiteSpace(_customerCategoryFilter) ||
                           CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(item).Equals(_customerCategoryFilter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)_customerPageSize));
        _customerPage = Math.Clamp(_customerPage, 1, pages);
        var visible = filtered.Skip((_customerPage - 1) * _customerPageSize).Take(_customerPageSize).ToList();

        var shownDimension = dimensions.FirstOrDefault(item => item.Key.Equals(_customerDimensionKey, StringComparison.OrdinalIgnoreCase));
        var labels = new List<string>
        {
            "", "客户", "Buyer ID", "公司", "邮箱", "国家", "WhatsApp", "WhatsApp 状态",
            "标签", "负责人", "等级", "阶段", "一级品类偏好"
        };
        var widths = new List<double> { .36, 1.35, 1.25, 1.05, 1.25, .7, 1.05, .8, 1, .8, .55, .75, 1.15 };
        if (shownDimension is not null)
        {
            labels.Add(shownDimension.Label);
            widths.Add(1.2);
        }
        var rows = new StackPanel { Spacing = 0, MinWidth = 1420 };
        rows.Children.Add(TableHeader(labels, widths));
        foreach (var item in visible)
        {
            var selection = Accessible(new CheckBox
            {
                IsChecked = _selectedCustomerIds.Contains(item.Id),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }, $"选择客户 {item.DisplayName}");
            selection.Click += async (_, _) =>
            {
                if (selection.IsChecked == true) _selectedCustomerIds.Add(item.Id);
                else _selectedCustomerIds.Remove(item.Id);
                await RenderCurrentPageAsync();
            };
            var cells = new List<Control>
            {
                selection,
                TextCell(item.DisplayName, true, item.ProductInterest),
                TextCell(Fallback(item.BuyerId, "—")),
                TextCell(Fallback(item.Company, "—")),
                TextCell(Fallback(item.Email, "—")),
                TextCell(Fallback(item.Country, "—")),
                TextCell(Fallback(item.PhoneE164, "—")),
                TextCell(item.PhoneState),
                TextCell(item.TagsLabel),
                TextCell(Fallback(item.Owner, "—")),
                BadgeCell(item.Grade, GradeBrush(item.Grade)),
                TextCell(item.StageLabel),
                TextCell(Fallback(CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(item), "—"))
            };
            if (shownDimension is not null)
                cells.Add(TextCell(Fallback(CustomerDimensionCatalog.ResolveValue(item.CustomFields, shownDimension), "—")));
            var row = TableRow(cells, widths);
            row.DoubleTapped += async (_, _) => await ShowLeadEditorAsync(item);
            rows.Children.Add(row);
        }
        if (visible.Count == 0)
            rows.Children.Add(EmptyState(
                "没有匹配客户",
                string.IsNullOrWhiteSpace(_customerSearch)
                    ? "导入原始客户表或新建客户。"
                    : "更换关键词或清除搜索后重试。"));
        var listHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 9 };
        var selectAll = new CheckBox
        {
            Content = "全选本页",
            IsChecked = visible.Count > 0 && visible.All(item => _selectedCustomerIds.Contains(item.Id))
        };
        selectAll.Click += async (_, _) =>
        {
            foreach (var item in visible)
            {
                if (selectAll.IsChecked == true) _selectedCustomerIds.Add(item.Id);
                else _selectedCustomerIds.Remove(item.Id);
            }
            await RenderCurrentPageAsync();
        };
        listHeader.Children.Add(selectAll);
        var selectedText = BodyText($"已选 {_selectedCustomerIds.Count:N0} 位", Muted, 11);
        selectedText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(selectedText, 1);
        listHeader.Children.Add(selectedText);
        var stats = BodyText($"CRM DATA · {filtered.Count:N0} 位", Primary, 11);
        stats.HorizontalAlignment = HorizontalAlignment.Right;
        stats.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(stats, 2);
        listHeader.Children.Add(stats);
        var clearSelection = ActionButton("取消选择", async () =>
        {
            _selectedCustomerIds.Clear();
            await RenderCurrentPageAsync();
        });
        clearSelection.IsEnabled = _selectedCustomerIds.Count > 0;
        Grid.SetColumn(clearSelection, 3);
        listHeader.Children.Add(clearSelection);
        var listPanel = new StackPanel { Spacing = 11 };
        listPanel.Children.Add(listHeader);
        listPanel.Children.Add(new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        page.Children.Add(SectionCard("客户清单", $"{filtered.Count:N0} 位", listPanel));

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
        var language = new TextBox { Text = lead.PreferredLanguage };
        var amount = new TextBox { Text = lead.EstimatedOrderValue > 0 ? lead.EstimatedOrderValue.ToString("0.##") : "" };
        var currency = new TextBox { Text = lead.Currency };
        var sourceBox = new TextBox { Text = lead.Source };
        var tags = new TextBox { Text = string.Join(", ", lead.Tags) };
        var stage = new ComboBox { ItemsSource = Enum.GetValues<LeadStage>(), SelectedItem = lead.Stage };
        var stageLock = new CheckBox { Content = "阶段已人工锁定（AI 不再覆盖）", IsChecked = lead.StageManuallyLocked };
        var optIn = new CheckBox { Content = "已同意 WhatsApp 营销联系", IsChecked = lead.WhatsAppOptIn };
        var optedOut = new CheckBox { Content = "已退订", IsChecked = lead.OptedOut };
        var notes = new TextBox
        {
            Text = lead.LatestMessage,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 84
        };
        var dimensions = new TextBox
        {
            Text = string.Join("\n", lead.CustomFields.Select(item => $"{item.Key}={item.Value}")),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150
        };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(TitleText(source is null ? "新建客户" : $"编辑 · {lead.DisplayName}", 24));
        panel.Children.Add(BodyText("系统字段用于搜索、评分和 WhatsApp；原表维度逐列保留，可逐项修改。"));
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 10
        };
        var fields = new Control[]
        {
            Field("客户姓名 / 昵称", name), Field("Buyer ID", buyerId),
            Field("公司", company), Field("国家 / 地区", country),
            Field("WhatsApp 国际号码", phone, "必须包含国家区号，不自动猜测。"), Field("邮箱", email),
            Field("关注产品", product), Field("负责人", owner),
            Field("销售语言", language), Field("阶段", stage),
            Field("预计订单额", amount), Field("币种", currency),
            Field("来源", sourceBox), Field("标签", tags, "多个标签用逗号分隔。")
        };
        for (var index = 0; index < fields.Length; index++)
        {
            Grid.SetRow(fields[index], index / 2);
            Grid.SetColumn(fields[index], index % 2);
            form.Children.Add(fields[index]);
        }
        panel.Children.Add(form);
        panel.Children.Add(stageLock);
        var permission = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        permission.Children.Add(optIn);
        permission.Children.Add(optedOut);
        panel.Children.Add(Field("联系权限", permission));
        panel.Children.Add(Field("备注 / 最新沟通", notes));

        if (source is not null)
        {
            var brain = await _services.CustomerBrain.GetAsync(source.Id, _lifetime.Token)
                        ?? await _services.CustomerBrain.RefreshAsync(source.Id, _lifetime.Token);
            var brainPanel = new StackPanel { Spacing = 8 };
            var brainStatus = BodyText(
                $"购买概率 {brain.PurchaseProbability}% · AI 置信度 {brain.Confidence:P0} · 数据覆盖 {brain.Coverage.Percentage}% · 建议阶段 {Labels.Stage(brain.SuggestedStage)}",
                Ink,
                12);
            brainPanel.Children.Add(brainStatus);
            brainPanel.Children.Add(BodyText($"客户理解：{Fallback(brain.Summary, "等待 Customer Brain 整合 CRM、WhatsApp、邮件、触达和历史分析。")}", Ink, 12));
            brainPanel.Children.Add(BodyText($"下一步最佳行动：{Fallback(brain.NextBestAction, "等待分析")}", Primary, 12));
            var brainActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            brainActions.Children.Add(ActionButton("刷新数据", async () =>
            {
                var refreshed = await _services.CustomerBrain.RefreshAsync(source.Id, _lifetime.Token);
                brainStatus.Text = $"购买概率 {refreshed.PurchaseProbability}% · AI 置信度 {refreshed.Confidence:P0} · 数据覆盖 {refreshed.Coverage.Percentage}% · 建议阶段 {Labels.Stage(refreshed.SuggestedStage)}";
            }));
            brainActions.Children.Add(ActionButton("AI 分析并生成行动", async () =>
            {
                var analyzed = await _services.CustomerBrain.AnalyzeAsync(source.Id, _lifetime.Token);
                brainStatus.Text = $"购买概率 {analyzed.PurchaseProbability}% · AI 置信度 {analyzed.Confidence:P0} · 数据覆盖 {analyzed.Coverage.Percentage}% · 建议阶段 {Labels.Stage(analyzed.SuggestedStage)}";
            }, primary: true));
            brainPanel.Children.Add(brainActions);
            panel.Children.Add(SectionCard("Customer 360 · 个人 AI 销售员工", "CUSTOMER BRAIN", brainPanel));
        }

        panel.Children.Add(Field(
            "原表全部维度",
            dimensions,
            "每行 key=value。修改后保存，客户列表中的对应单元格会同步更新。"));
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = DialogWindow(source is null ? "新建客户" : "编辑客户", new ScrollViewer { Content = panel }, 860, 840);
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
                lead.PreferredLanguage = language.Text?.Trim() ?? "";
                lead.EstimatedOrderValue = decimal.TryParse(amount.Text, out var parsedAmount) ? parsedAmount : 0;
                lead.Currency = currency.Text?.Trim() ?? "USD";
                lead.Source = sourceBox.Text?.Trim() ?? "";
                lead.Stage = stage.SelectedItem is LeadStage selectedStage ? selectedStage : LeadStage.New;
                lead.StageManuallyLocked = stageLock.IsChecked == true;
                lead.StageSource = lead.StageManuallyLocked ? "human" : lead.StageSource;
                if (lead.StageManuallyLocked) lead.StageManuallyUpdatedAt = DateTimeOffset.Now;
                lead.Tags = (tags.Text ?? "").Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                lead.WhatsAppOptIn = optIn.IsChecked == true;
                if (lead.WhatsAppOptIn && lead.WhatsAppOptInAt is null) lead.WhatsAppOptInAt = DateTimeOffset.Now;
                lead.OptedOut = optedOut.IsChecked == true;
                lead.LatestMessage = notes.Text?.Trim() ?? "";
                lead.CustomFields = (dimensions.Text ?? "")
                    .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(line =>
                    {
                        var separator = line.IndexOf('=');
                        return separator <= 0
                            ? (Key: "", Value: "")
                            : (Key: line[..separator].Trim(), Value: line[(separator + 1)..].Trim());
                    })
                    .Where(item => item.Key.Length > 0)
                    .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
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
            "把客户数据变成下一步行动",
            "分数不是结论：每次 AI 判断同时展示置信度、行为信号、证据、风险与建议。",
            "AI LEAD INTELLIGENCE"));
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var search = Accessible(new TextBox
        {
            Text = _leadSearch,
            Watermark = "搜索客户、市场或号码",
            MinWidth = 300
        }, "搜索商机");
        search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _leadSearch = search.Text?.Trim() ?? "";
            await RenderCurrentPageAsync();
        };
        filters.Children.Add(search);
        var grades = Accessible(new ComboBox
        {
            ItemsSource = new[] { "全部等级", "A级", "B级", "C级", "D级" },
            SelectedIndex = _leadGradeFilter == "全部"
                ? 0
                : Math.Max(1, Array.IndexOf(new[] { "A", "B", "C", "D" }, _leadGradeFilter) + 1),
            MinWidth = 120
        }, "商机等级筛选");
        grades.SelectionChanged += async (_, _) =>
        {
            _leadGradeFilter = grades.SelectedIndex <= 0 ? "全部" : new[] { "A", "B", "C", "D" }[grades.SelectedIndex - 1];
            await RenderCurrentPageAsync();
        };
        filters.Children.Add(grades);
        filters.Children.Add(ActionButton("↻  刷新", async () => await RenderCurrentPageAsync()));
        toolbar.Children.Add(filters);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        Grid.SetColumn(actions, 1);
        actions.Children.Add(ActionButton("＋  导入客户", async () => await ImportCustomersAsync()));
        var bulk = ActionButton("✦  AI 分析 / 重试全部", async () => await RunBulkAnalysisAsync(), primary: true);
        actions.Children.Add(bulk);
        var cancel = ActionButton("停止分析", () =>
        {
            _bulkAnalysisCancellation?.Cancel();
            _operationStatus = "正在安全停止当前批量分析…";
            return Task.CompletedTask;
        });
        cancel.IsEnabled = _bulkAnalysisCancellation is not null;
        actions.Children.Add(cancel);
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);

        if (_bulkAnalysisCancellation is not null)
        {
            var progress = new StackPanel { Spacing = 6 };
            progress.Children.Add(BodyText(_operationStatus, ResourceBrush("AiAccent", "#6659B8"), 12));
            progress.Children.Add(new ProgressBar { IsIndeterminate = true });
            page.Children.Add(Card(progress, new Thickness(14), ResourceBrush("AiSurface", "#F4F1FF")));
        }

        var visible = _leads
            .Where(item => string.IsNullOrWhiteSpace(_leadSearch) ||
                           string.Join(' ', item.DisplayName, item.Company, item.Country, item.PhoneE164)
                               .Contains(_leadSearch, StringComparison.CurrentCultureIgnoreCase))
            .Where(item => _leadGradeFilter == "全部" || item.Grade.Equals(_leadGradeFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(500)
            .ToList();
        var selected = visible.FirstOrDefault(item => item.Id.Equals(_selectedLeadId, StringComparison.OrdinalIgnoreCase))
                       ?? visible.FirstOrDefault();
        _selectedLeadId = selected?.Id ?? "";

        var list = new StackPanel { Spacing = 5 };
        list.Children.Add(TitleText("商机队列", 18));
        list.Children.Add(BodyText("按 AI 优先级决策", Muted, 11));
        foreach (var item in visible)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = item.Id == _selectedLeadId ? PrimarySoft : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10)
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(TextCell(item.DisplayName, item.Id == _selectedLeadId, $"{Fallback(item.Country, "—")} · {item.StageLabel} · {item.AnalysisStateLabel}"));
            var score = BadgeCell($"{item.Grade} · {item.Score}", GradeBrush(item.Grade));
            Grid.SetColumn(score, 1);
            row.Children.Add(score);
            button.Content = row;
            button.Click += async (_, _) =>
            {
                _selectedLeadId = item.Id;
                await RenderCurrentPageAsync();
            };
            list.Children.Add(button);
        }
        if (visible.Count == 0)
            list.Children.Add(EmptyState("没有匹配商机", "调整搜索或等级筛选后重试。"));

        var decision = new StackPanel { Spacing = 13 };
        if (selected is null)
        {
            decision.Children.Add(EmptyState("选择一个商机", "选择客户查看 AI 决策层。"));
        }
        else
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            var identity = new StackPanel { Spacing = 4 };
            identity.Children.Add(BodyText("AI DECISION BRIEF", ResourceBrush("AiAccent", "#6659B8"), 11));
            identity.Children.Add(TitleText(selected.DisplayName, 25));
            identity.Children.Add(BodyText($"{Fallback(selected.Company, "—")} · {Fallback(selected.Country, "—")}", Muted, 11));
            header.Children.Add(identity);
            var score = TitleText($"{selected.Score} / 100 · {selected.Grade}", 24);
            score.Foreground = GradeBrush(selected.Grade);
            Grid.SetColumn(score, 1);
            header.Children.Add(score);
            decision.Children.Add(header);
            decision.Children.Add(MetricGrid(
                ("AI 置信度", $"{selected.AnalysisConfidence:P0}", "结构化结果可信度", "#6659B8"),
                ("阶段", selected.StageLabel, "当前 CRM 阶段", "#087A5E"),
                ("预估订单", selected.AmountLabel, "客户资料", "#4E8CF7"),
                ("购买概率", $"{selected.PurchaseProbability}%", "AI 商业判断", "#8A5A00")));
            decision.Children.Add(SectionCard(
                "AI 客户画像",
                $"CUSTOMER BRAIN · {selected.AnalysisStateLabel}",
                BodyText(Fallback(selected.ProfileSummary, "尚未分析"), Ink, 13)));
            decision.Children.Add(SectionCard(
                "NEXT BEST ACTION",
                "下一步",
                BodyText(Fallback(selected.NextAction, "补充客户资料"), Primary, 14)));
            var factors = new StackPanel { Spacing = 8 };
            foreach (var factor in selected.ScoreFactors)
                factors.Children.Add(TextCell($"{factor.Key} · {factor.Score}/{factor.MaxScore}", true, $"{factor.Rationale}\n证据：{string.Join("；", factor.Evidence)}"));
            if (factors.Children.Count == 0) factors.Children.Add(BodyText("暂无六维评分证据。", Muted, 11));
            decision.Children.Add(SectionCard("六维商机雷达与评分证据", "画像分布", factors));
            var risks = new StackPanel { Spacing = 6 };
            foreach (var risk in selected.Risks) risks.Children.Add(BodyText($"• {risk}", Warning, 11));
            if (risks.Children.Count == 0) risks.Children.Add(BodyText(Fallback(selected.RiskWarning, "尚无已识别风险。"), Muted, 11));
            decision.Children.Add(SectionCard("风险与人工复核", "HUMAN REVIEW", risks));
            decision.Children.Add(ActionButton(
                selected.AnalysisStatus == AnalysisStatus.RetryableFailed ? "重试当前商机" : "分析当前商机",
                async () => await AnalyzeLeadAsync(selected),
                primary: true));
        }

        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*"), ColumnSpacing = 14 };
        workspace.Children.Add(Card(new ScrollViewer
        {
            Content = list,
            MaxHeight = 680,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, new Thickness(12)));
        var decisionCard = Card(decision);
        Grid.SetColumn(decisionCard, 1);
        workspace.Children.Add(decisionCard);
        page.Children.Add(workspace);
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
        if (_bulkAnalysisCancellation is not null) return;
        _bulkAnalysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var progress = new Progress<LeadBulkAnalysisProgress>(value =>
        {
            _operationStatus = $"{value.Message} · {value.Completed:N0}/{value.Total:N0}";
            PageSubtitle.Text = _operationStatus;
        });
        try
        {
            await RenderCurrentPageAsync();
            var result = await _services.LeadAutomation.AnalyzeAllLeadsAsync(progress, _bulkAnalysisCancellation.Token);
            await ShowMessageAsync(
                "批量分析完成",
                $"共 {result.Total:N0} 位客户，成功 {result.Succeeded:N0}，失败 {result.Failed:N0}。");
        }
        catch (OperationCanceledException) { _operationStatus = "批量分析已安全停止，可继续重试未完成客户。"; }
        catch (Exception error)
        {
            await ShowMessageAsync("批量分析已停止", error.Message);
        }
        finally
        {
            _bulkAnalysisCancellation.Dispose();
            _bulkAnalysisCancellation = null;
            await RenderCurrentPageAsync();
        }
    }
}
