using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class CustomerEditWindow : Window
{
    private readonly AppServices _services;
    private readonly Lead _lead;
    private readonly LeadStage _originalStage;
    private readonly ObservableCollection<EditableDimension> _dimensions;

    public CustomerEditWindow(AppServices services, Lead lead)
    {
        InitializeComponent();
        _services = services;
        _lead = lead;
        _originalStage = lead.Stage;
        _dimensions = new ObservableCollection<EditableDimension>(lead.CustomFields.Select(pair => new EditableDimension(pair.Key, pair.Value)));
        OriginalDimensions.ItemsSource = _dimensions;

        NameBox.Text = lead.Name;
        CompanyBox.Text = lead.Company;
        CountryBox.Text = lead.Country;
        PhoneBox.Text = lead.PhoneE164;
        EmailBox.Text = lead.Email;
        OwnerBox.Text = lead.Owner;
        LanguageBox.Text = lead.PreferredLanguage;
        ProductBox.Text = lead.ProductInterest;
        AmountBox.Text = lead.EstimatedOrderValue == 0 ? "" : lead.EstimatedOrderValue.ToString(CultureInfo.CurrentCulture);
        CurrencyBox.Text = lead.Currency;
        SourceBox.Text = lead.Source;
        TagsBox.Text = string.Join("，", lead.Tags);
        NotesBox.Text = lead.LatestMessage;
        OptInCheck.IsChecked = lead.WhatsAppOptIn;
        OptedOutCheck.IsChecked = lead.OptedOut;

        AiScoreText.Text = lead.Score.ToString();
        AiGradeText.Text = $"{lead.Grade}级";
        AiBaseScoreText.Text = $"{lead.BaseProfileScore} / 100";
        AiBehaviorScoreText.Text = $"{lead.BehaviorSignalScore:+#;-#;0} / ±20";
        AiPurchaseProbabilityText.Text = $"{lead.PurchaseProbability}%";
        AiScoreStateText.Text = lead.HasCurrentAiScore
            ? $"Lead Intelligence V{lead.AnalysisContractVersion} · {lead.AnalysisStateLabel} · {lead.ProfileSummary}"
            : $"等待 Lead Intelligence V2 分析 · {lead.AnalysisStateLabel}";
        AiNextActionText.Text = lead.NextAction;
        AiRiskText.Text = string.IsNullOrWhiteSpace(lead.RiskWarning)
            ? lead.Risks.FirstOrDefault() ?? "当前 D 级是未分析初始值，不代表低价值客户。"
            : lead.RiskWarning;
        AiReasonItems.ItemsSource = lead.ScoreFactors.Count == 0
            ? new[] { "尚无 AI 评分原因与证据" }
            : lead.ScoreFactors.Select(factor => $"{factor.Rationale}（{factor.Score}/{factor.MaxScore}）— {string.Join("；", factor.Evidence)}").ToList();

        StageBox.ItemsSource = Enum.GetValues<LeadStage>().Select(stage => new StageOption(Labels.Stage(stage), stage)).ToList();
        StageBox.SelectedItem = ((IEnumerable<StageOption>)StageBox.ItemsSource).First(option => option.Value == lead.Stage);
        StageLockCheck.IsChecked = lead.StageManuallyLocked;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadCustomerBrainAsync(refresh: true);

    private async void RefreshBrain_Click(object sender, RoutedEventArgs e) => await LoadCustomerBrainAsync(refresh: true);

    private async void AnalyzeBrain_Click(object sender, RoutedEventArgs e)
    {
        SetBrainButtonsEnabled(false);
        BrainStatusText.Text = "Customer Brain 正在分阶段理解客户、评估机会并生成下一步行动…";
        try
        {
            await _services.CustomerBrain.AnalyzeAsync(_lead.Id);
            await LoadCustomerBrainAsync(refresh: false);
            MessageBox.Show("Customer Brain 分析完成。购买概率、建议阶段、AI 建议和跟进任务已经更新。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            BrainStatusText.Text = "分析失败，可在保留现有客户资料和上一次有效结论的前提下重试。";
            MessageBox.Show(error.Message, "Customer Brain 分析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBrainButtonsEnabled(true);
        }
    }

    private async Task LoadCustomerBrainAsync(bool refresh)
    {
        SetBrainButtonsEnabled(false);
        try
        {
            var brain = refresh
                ? await _services.CustomerBrain.RefreshAsync(_lead.Id)
                : await _services.CustomerBrain.GetAsync(_lead.Id) ?? await _services.CustomerBrain.RefreshAsync(_lead.Id);
            BrainProbabilityText.Text = $"{brain.PurchaseProbability}%";
            BrainConfidenceText.Text = $"{brain.Confidence:P0}";
            BrainCoverageText.Text = $"{brain.Coverage.Percentage}%";
            BrainStageText.Text = Labels.Stage(brain.SuggestedStage);
            BrainSummaryText.Text = brain.Summary;
            BrainNextActionText.Text = brain.NextBestAction;
            BrainStatusText.Text = brain.DecisionStatus switch
            {
                CustomerBrainDecisionStatus.Current =>
                    $"{brain.VersionLabel} · {brain.AiModel} · 结论与当前数据一致 · {brain.LastBrainAnalyzedAt:yyyy-MM-dd HH:mm}",
                CustomerBrainDecisionStatus.Stale =>
                    $"{brain.VersionLabel} · 客户数据已经变化，上一次 AI 结论保留但已标记过期，请重新分析。",
                CustomerBrainDecisionStatus.RetryableFailed =>
                    $"{brain.VersionLabel} · 上一次 AI 分析失败，原始资料未改变，可安全重试。",
                _ => $"{brain.VersionLabel} · 已整合客户资料，尚未运行 Customer Brain AI 决策。"
            };

            var recommendations = await _services.Repository.GetAiRecommendationHistoryAsync(_lead.Id);
            BrainRecommendationItems.ItemsSource = recommendations.Count == 0
                ? new[] { "暂无 AI 建议历史" }
                : recommendations.Take(5).Select(item => $"{item.Action} · {RecommendationStatusLabel(item.Status)}").ToList();

            var tasks = await _services.Repository.GetFollowUpTasksAsync(_lead.Id);
            BrainTaskItems.ItemsSource = tasks.Count == 0
                ? new[] { "暂无跟进任务" }
                : tasks.Take(5).Select(item => $"{item.DueAt:MM-dd HH:mm} · {item.Title} · {TaskStatusLabel(item.Status)}").ToList();

            var events = await _services.Repository.GetCustomerEventsAsync(_lead.Id);
            if (events.Count > 0)
            {
                BrainEventItems.ItemsSource = events.Take(6)
                    .Select(item => $"{item.OccurredAt:MM-dd HH:mm} · {item.Title}")
                    .ToList();
            }
            else
            {
                var timeline = await _services.Repository.GetCustomerBehaviorTimelineAsync(_lead.Id);
                BrainEventItems.ItemsSource = timeline.Count == 0
                    ? new[] { "暂无客户互动轨迹" }
                    : timeline.Take(6).Select(item => $"{item.OccurredAt:MM-dd HH:mm} · {item.Channel} · {item.Summary}").ToList();
            }
        }
        catch (Exception error)
        {
            BrainStatusText.Text = $"Customer 360 暂时无法加载：{error.Message}";
        }
        finally
        {
            SetBrainButtonsEnabled(true);
        }
    }

    private void SetBrainButtonsEnabled(bool enabled)
    {
        RefreshBrainButton.IsEnabled = enabled;
        AnalyzeBrainButton.IsEnabled = enabled;
    }

    private void AddDimension_Click(object sender, RoutedEventArgs e)
    {
        var header = NewDimensionNameBox.Text.Trim();
        if (header.Length == 0)
        {
            MessageBox.Show("请输入新维度名称。", "AI Sales OS");
            return;
        }
        if (_dimensions.Any(item => item.Header.Equals(header, StringComparison.CurrentCultureIgnoreCase)))
        {
            MessageBox.Show("这个维度已经存在，可直接修改右侧的值。", "AI Sales OS");
            return;
        }
        _dimensions.Add(new EditableDimension(header, NewDimensionValueBox.Text));
        NewDimensionNameBox.Clear();
        NewDimensionValueBox.Clear();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var name = NameBox.Text.Trim();
            var company = CompanyBox.Text.Trim();
            if (name.Length == 0 && company.Length == 0)
            {
                MessageBox.Show("客户名称和公司不能同时为空。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _lead.Name = name;
            _lead.Company = company;
            _lead.Country = CountryBox.Text.Trim();
            var rawPhone = PhoneBox.Text.Trim();
            var normalized = PhoneNormalizer.Normalize(rawPhone, _lead.Country);
            _lead.PhoneE164 = rawPhone.Length == 0 ? "" : normalized.E164.Length > 0 ? normalized.E164 : rawPhone;
            _lead.PhoneValid = normalized.Valid;
            _lead.Email = EmailBox.Text.Trim();
            _lead.Owner = OwnerBox.Text.Trim();
            _lead.PreferredLanguage = string.IsNullOrWhiteSpace(LanguageBox.Text) ? "en" : LanguageBox.Text.Trim();
            _lead.ProductInterest = ProductBox.Text.Trim();
            _lead.Currency = string.IsNullOrWhiteSpace(CurrencyBox.Text) ? "USD" : CurrencyBox.Text.Trim().ToUpperInvariant();
            _lead.EstimatedOrderValue = ParseAmount(AmountBox.Text);
            _lead.Source = SourceBox.Text.Trim();
            _lead.Tags = TagsBox.Text.Split([',','，',';','；','|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            _lead.LatestMessage = NotesBox.Text.Trim();
            _lead.WhatsAppOptIn = OptInCheck.IsChecked == true;
            _lead.OptedOut = OptedOutCheck.IsChecked == true;
            if (StageBox.SelectedItem is StageOption stage) _lead.Stage = stage.Value;
            var stageChangedByUser = _originalStage != _lead.Stage;
            _lead.StageManuallyLocked = stageChangedByUser || StageLockCheck.IsChecked == true;
            _lead.StageSource = _lead.StageManuallyLocked ? "user" : "ai";
            _lead.StageManuallyUpdatedAt = stageChangedByUser ? DateTimeOffset.Now : _lead.StageManuallyUpdatedAt;
            _lead.CustomFields = _dimensions.ToDictionary(item => item.Header, item => item.Value ?? "", StringComparer.OrdinalIgnoreCase);

            if (!_lead.AiScoreApplied)
            {
                LeadScoringService.ResetToAiBaseline(_lead);
            }
            await _services.Repository.UpsertLeadAsync(_lead);
            await _services.Repository.LogEventAsync("customer_edited", _lead.Id, null, $"edited core fields and {_lead.CustomFields.Count} table dimensions");
            await _services.Repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
            {
                CustomerId = _lead.Id,
                EventType = "customer_edited",
                Title = "客户资料已人工更新",
                Detail = $"已更新系统字段和 {_lead.CustomFields.Count} 个原表维度。",
                SourceType = "customer_edit",
                SourceId = Guid.NewGuid().ToString("N"),
                OccurredAt = DateTimeOffset.Now
            });
            if (_originalStage != _lead.Stage)
            {
                await _services.Repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
                {
                    CustomerId = _lead.Id,
                    EventType = "stage_changed",
                    Title = $"商机阶段：{Labels.Stage(_originalStage)} → {Labels.Stage(_lead.Stage)}",
                    Detail = "由用户在客户资料中手动调整。",
                    SourceType = "customer_edit",
                    SourceId = Guid.NewGuid().ToString("N"),
                    OccurredAt = DateTimeOffset.Now
                });
            }
            await _services.CustomerBrain.RefreshAsync(_lead.Id);
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var displayName = string.IsNullOrWhiteSpace(_lead.DisplayName) ? _lead.Id : _lead.DisplayName;
        var message = $"\u786e\u5b9a\u5220\u9664\u5ba2\u6237 \u201c{displayName}\u201d \u5417\uff1f\n\n\u5ba2\u6237\u8d44\u6599\u3001AI \u5206\u6790\u3001\u8349\u7a3f\u548c\u672a\u53d1\u9001\u7684 Campaign \u4efb\u52a1\u5c06\u88ab\u5220\u9664\uff1bWhatsApp \u4f1a\u8bdd\u4e0e\u6d88\u606f\u5386\u53f2\u4f1a\u4fdd\u7559\uff0c\u4f46\u4e0d\u518d\u5173\u8054\u8be5\u5ba2\u6237\u3002";
        if (MessageBox.Show(message, "AI Sales OS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        DeleteButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        try
        {
            if (!await _services.Repository.DeleteLeadAsync(_lead.Id))
            {
                MessageBox.Show("\u8be5\u5ba2\u6237\u5df2\u4e0d\u5b58\u5728\u3002", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "\u5220\u9664\u5931\u8d25", MessageBoxButton.OK, MessageBoxImage.Warning);
            DeleteButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private static decimal ParseAmount(string text)
    {
        var value = text.Trim().Replace(",", "");
        if (value.Length == 0) return 0;
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out amount))
            return Math.Max(0, amount);
        throw new InvalidDataException("预计订单额必须是数字。");
    }

    private static string RecommendationStatusLabel(AiRecommendationStatus status) => status switch
    {
        AiRecommendationStatus.Proposed => "待确认",
        AiRecommendationStatus.Accepted => "已接受",
        AiRecommendationStatus.InProgress => "进行中",
        AiRecommendationStatus.Completed => "已完成",
        AiRecommendationStatus.Dismissed => "已忽略",
        AiRecommendationStatus.Failed => "执行失败",
        AiRecommendationStatus.Superseded => "已被新建议替代",
        _ => status.ToString()
    };

    private static string TaskStatusLabel(FollowUpTaskStatus status) => status switch
    {
        FollowUpTaskStatus.Proposed => "待确认",
        FollowUpTaskStatus.Open => "待处理",
        FollowUpTaskStatus.InProgress => "进行中",
        FollowUpTaskStatus.Completed => "已完成",
        FollowUpTaskStatus.Dismissed => "已忽略",
        FollowUpTaskStatus.Failed => "执行失败",
        _ => status.ToString()
    };

    private sealed record StageOption(string Label, LeadStage Value);

    private sealed class EditableDimension(string header, string value)
    {
        public string Header { get; } = header;
        public string Value { get; set; } = value;
    }
}
