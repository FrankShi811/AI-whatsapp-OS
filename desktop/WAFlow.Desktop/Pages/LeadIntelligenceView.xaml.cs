using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class LeadIntelligenceView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private List<Lead> _leads = [];
    private List<Lead> _visibleLeads = [];
    private CancellationTokenSource? _bulkCancellation;
    private LeadBulkAnalysisProgress? _lastBulkProgress;
    private bool _decisionDrawerExpanded = true;
    private int _customerBrainRefreshGeneration;
    private int _currentPage = 1;
    private int _pageSize = 30;
    public event EventHandler? ImportRequested;
    public event EventHandler? DataChanged;

    public LeadIntelligenceView(AppServices services)
    {
        InitializeComponent(); _services = services;
        GradeFilter.ItemsSource = new[] { "全部", "A", "B", "C", "D" }; GradeFilter.SelectedIndex = 0;
        PageSizeBox.ItemsSource = new[] { new PageSizeOption("10 条/页", 10), new PageSizeOption("30 条/页", 30), new PageSizeOption("50 条/页", 50) };
        PageSizeBox.SelectedIndex = 1;
    }

    public async Task RefreshAsync()
    {
        var selectedId = (LeadGrid.SelectedItem as Lead)?.Id;
        _leads = await _services.Repository.GetLeadsAsync(SearchBox.Text, GradeFilter.SelectedItem as string);
        await RefreshAiRouteAsync();
        ApplyPagination(selectedId);
        var selectedLead = LeadGrid.SelectedItem as Lead;
        UpdateInspector(selectedLead);
        await UpdateCustomerBrainAsync(selectedLead);
    }

    private void ApplyPagination(string? preferredLeadId = null)
    {
        var total = _leads.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
        _currentPage = Math.Clamp(_currentPage, 1, totalPages);
        var startIndex = (_currentPage - 1) * _pageSize;
        _visibleLeads = _leads.Skip(startIndex).Take(_pageSize).ToList();

        LeadGrid.ItemsSource = null;
        LeadGrid.ItemsSource = _visibleLeads;
        LeadGrid.SelectedItem = _visibleLeads.FirstOrDefault(lead =>
            lead.Id.Equals(preferredLeadId, StringComparison.OrdinalIgnoreCase)) ?? _visibleLeads.FirstOrDefault();

        var first = total == 0 ? 0 : startIndex + 1;
        var last = total == 0 ? 0 : startIndex + _visibleLeads.Count;
        PageRangeText.Text = total == 0 ? "暂无商机" : $"显示第 {first:N0}–{last:N0} 位，共 {total:N0} 位";
        PageStatusText.Text = $"第 {_currentPage:N0} / {totalPages:N0} 页";
        PreviousPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < totalPages;
    }

    public async Task RefreshAiRouteAsync()
    {
        var allLeads = await _services.Repository.GetLeadsAsync();
        var execution = await _services.DeepSeek.ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence);
        UpdateBulkAnalyzeButtonIdleContent(
            execution.ProviderId,
            execution.Model,
            execution.ReasoningEffort,
            allLeads.Count(lead => lead.AnalysisStatus == AnalysisStatus.RetryableFailed));
    }

    private void UpdateBulkAnalyzeButtonIdleContent(
        string providerId,
        string model,
        string reasoningEffort,
        int retryableCount)
    {
        BulkAnalyzeButton.Content = retryableCount > 0
            ? $"使用 {model} 重试失败 {retryableCount}"
            : $"使用 {model} 分析全部";
        BulkAnalyzeButton.ToolTip =
            $"商机智能实际路由：{providerId} · {model} · 推理深度 {reasoningEffort}";
    }

    private void UpdateBulkAnalyzeButtonRunningContent(int completed, int total)
    {
        BulkAnalyzeButton.Content = $"正在分析 {Math.Min(completed, total)} / {total}";
    }

    private void UpdateInspector(Lead? lead)
    {
        if (lead is null)
        {
            LeadNameText.Text = "选择一个商机"; CompanyText.Text = ""; GradeText.Text = "—"; ScoreText.Text = "0"; StageText.Text = "—"; AmountText.Text = "—";
            BaseScoreText.Text = "0 / 100"; BehaviorScoreText.Text = "0";
            ProfileText.Text = "尚未选择客户"; AnalysisMetaText.Text = ""; CustomerBrainMetaText.Text = "CUSTOMER BRAIN · 等待选择客户"; SignalItems.ItemsSource = null; NextActionText.Text = "—"; FactorItems.ItemsSource = null; RiskItems.ItemsSource = null; AnalysisErrorText.Text = "";
            ConfidenceText.Text = "0%"; ConfidenceBar.Value = 0; ScoreRing.SetScore(0, "D", 0); RadarChart.SetValues([]); return;
        }
        LeadNameText.Text = lead.DisplayName; CompanyText.Text = $"{lead.Company} · {lead.Country}"; GradeText.Text = $"{lead.Grade}级"; ScoreText.Text = lead.Score.ToString();
        StageText.Text = lead.StageLabel; AmountText.Text = lead.AmountLabel; ProfileText.Text = lead.ProfileSummary; NextActionText.Text = lead.NextAction;
        BaseScoreText.Text = $"{lead.BaseProfileScore} / 100";
        BehaviorScoreText.Text = $"{lead.BehaviorSignalScore:+#;-#;0} / ±20";
        ConfidenceText.Text = $"{lead.AnalysisConfidence:P0}";
        ConfidenceBar.Value = Math.Clamp(lead.AnalysisConfidence * 100, 0, 100);
        ScoreRing.SetScore(lead.Score, lead.Grade, lead.AnalysisConfidence);
        var trigger = lead.AnalysisTrigger == "whatsapp_reply" ? "WhatsApp 新回复自动触发" : lead.AnalysisTrigger == "manual" ? "人工触发" : "尚未触发";
        var analyzedAt = lead.LastAnalyzedAt is null ? "尚未完成 AI 分析" : $"最近完成 {lead.LastAnalyzedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";
        var contract = lead.HasCurrentAiScore ? $"V{lead.AnalysisContractVersion}" : "等待 V2";
        AnalysisMetaText.Text = $"{contract} · {trigger} · {analyzedAt} · {lead.AnalysisStateLabel}";
        SignalItems.ItemsSource = lead.BehaviorSignals.Count > 0
            ? lead.BehaviorSignals.Select(signal => $"{signal.Signal} {signal.Score:+#;-#;0} · {signal.Evidence}").ToList()
            : new[] { "尚无经 AI 验证的 WhatsApp 行为信号" };
        var labels = new Dictionary<string, string> { ["paid_marketing_willingness"]="付费营销意愿", ["supply_stability"]="供应链稳定性", ["ecommerce_foundation"]="电商基础", ["private_traffic"]="私域 / 流量", ["existing_sales"]="已有销售能力", ["materials_readiness"]="素材准备度" };
        var factorByKey = lead.ScoreFactors.ToDictionary(factor => factor.Key, StringComparer.OrdinalIgnoreCase);
        FactorItems.ItemsSource = LeadScoringLabel.Order.Select(key =>
        {
            factorByKey.TryGetValue(key, out var factor);
            return new FactorMetric(labels[key], lead.ScoreBreakdown.GetValueOrDefault(key), WAFlow.Core.Services.LeadScoringService.Weights[key], factor?.Rationale ?? "等待 AI 分析", factor is null ? "尚无证据" : string.Join("；", factor.Evidence));
        }).ToList();
        RadarChart.SetValues(LeadScoringLabel.Order.Select(key => (double)lead.ScoreBreakdown.GetValueOrDefault(key) / LeadScoringService.Weights[key]));
        RiskItems.ItemsSource = lead.Risks.Count > 0 ? lead.Risks : !lead.PhoneValid ? new[] { "号码无效，禁止打开 WhatsApp。" } : lead.AiScoreApplied ? new[] { "AI 分析结论仍需人工核对。" } : new[] { "当前 D 级是未分析初始值，不代表低价值客户。" };
        AnalysisErrorText.Text = lead.AnalysisError;
        GradeBadge.Background = (System.Windows.Media.Brush)FindResource(lead.Grade is "A" or "B" ? "SuccessSoft" : lead.Grade == "C" ? "WarningSoft" : "DangerSoft");
    }

    private async Task UpdateCustomerBrainAsync(Lead? lead)
    {
        var generation = ++_customerBrainRefreshGeneration;
        if (lead is null)
        {
            CustomerBrainMetaText.Text = "CUSTOMER BRAIN · 等待选择客户";
            return;
        }

        CustomerBrainMetaText.Text = "CUSTOMER BRAIN · 正在整合 CRM、会话、触达与分析证据…";
        try
        {
            var brain = await _services.CustomerBrain.RefreshAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || (LeadGrid.SelectedItem as Lead)?.Id != lead.Id) return;

            var facts = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Fact);
            var inferences = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Inference);
            var recommendations = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Recommendation);
            var gaps = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.InformationGap);
            CustomerBrainMetaText.Text =
                $"CUSTOMER BRAIN V{brain.Version} · 覆盖 {brain.Coverage.Percentage}% · 事实 {facts} · AI 判断 {inferences} · 建议 {recommendations} · 缺口 {gaps}";
            if (!string.IsNullOrWhiteSpace(brain.Summary)) ProfileText.Text = brain.Summary;
            if (!string.IsNullOrWhiteSpace(brain.NextBestAction)) NextActionText.Text = brain.NextBestAction;
            if (brain.Risks.Count > 0) RiskItems.ItemsSource = brain.Risks;
        }
        catch (Exception error)
        {
            if (generation != _customerBrainRefreshGeneration || (LeadGrid.SelectedItem as Lead)?.Id != lead.Id) return;
            CustomerBrainMetaText.Text = $"CUSTOMER BRAIN · 暂未物化：{error.Message}";
        }
    }

    private async void BulkAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_bulkCancellation is not null) return;
        var allLeads = await _services.Repository.GetLeadsAsync();
        if (allLeads.Count == 0)
        {
            MessageBox.Show("商机智能列表中没有可分析的客户。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_services.DeepSeek.HasApiKey())
        {
            MessageBox.Show("请先在左侧“设置”中配置 API Key 并选择模型。", "无法开始批量分析", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _bulkCancellation = new CancellationTokenSource();
        _lastBulkProgress = null;
        BulkAnalyzeButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        CancelBulkButton.IsEnabled = true;
        CancelBulkButton.Visibility = Visibility.Visible;
        BulkProgressPanel.Visibility = Visibility.Visible;
        BulkProgressBar.Maximum = Math.Max(1, allLeads.Count);
        BulkProgressBar.Value = 0;
        BulkProgressText.Text = $"准备分析 0 / {allLeads.Count}";
        UpdateBulkAnalyzeButtonRunningContent(0, allLeads.Count);
        var progress = new Progress<LeadBulkAnalysisProgress>(UpdateBulkProgress);
        (string Message, string Title, MessageBoxImage Icon)? outcome = null;
        try
        {
            var result = await _services.LeadAutomation.AnalyzeAllLeadsAsync(progress, _bulkCancellation.Token);
            DataChanged?.Invoke(this, EventArgs.Empty);
            outcome = (
                $"批量分析完成。\n\n总数：{result.Total}\n成功：{result.Succeeded}\n失败：{result.Failed}",
                "AI Sales OS",
                result.Failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
            var state = _lastBulkProgress;
            outcome = (
                $"批量分析已停止。\n\n已完成：{state?.Completed ?? 0} / {state?.Total ?? allLeads.Count}\n成功：{state?.Succeeded ?? 0}\n失败：{state?.Failed ?? 0}\n停止位置：{state?.CurrentLeadName ?? "—"}",
                "AI Sales OS",
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            outcome = (error.Message, "批量分析无法继续", MessageBoxImage.Warning);
        }
        finally
        {
            _bulkCancellation.Dispose();
            _bulkCancellation = null;
            BulkAnalyzeButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
            CancelBulkButton.Visibility = Visibility.Collapsed;
            await RefreshAsync();
        }
        if (outcome is { } resultDialog)
            MessageBox.Show(resultDialog.Message, resultDialog.Title, MessageBoxButton.OK, resultDialog.Icon);
    }

    private void CancelBulk_Click(object sender, RoutedEventArgs e)
    {
        CancelBulkButton.IsEnabled = false;
        BulkProgressText.Text = "正在安全停止当前 AI 请求…";
        _bulkCancellation?.Cancel();
    }

    private void UpdateBulkProgress(LeadBulkAnalysisProgress progress)
    {
        if (_bulkCancellation is null) return;
        _lastBulkProgress = progress;
        BulkProgressBar.Maximum = Math.Max(1, progress.Total);
        BulkProgressBar.Value = Math.Min(progress.Completed, progress.Total);
        BulkProgressText.Text = $"{progress.Message} · {progress.Completed}/{progress.Total} · 成功 {progress.Succeeded} · 失败 {progress.Failed}";
        UpdateBulkAnalyzeButtonRunningContent(progress.Completed, progress.Total);
        CancelBulkButton.IsEnabled = progress.State is not "cancelled";
    }

    private async void LeadGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var lead = LeadGrid.SelectedItem as Lead;
        UpdateInspector(lead);
        await UpdateCustomerBrainAsync(lead);
    }
    private void ToggleDecisionDrawer_Click(object sender, RoutedEventArgs e)
    {
        _decisionDrawerExpanded = !_decisionDrawerExpanded;
        DecisionSidebarColumn.Width = new GridLength(_decisionDrawerExpanded ? 430 : 40);
        DecisionSidebarBorder.Visibility = _decisionDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        DecisionDrawerCollapsedRail.Visibility = _decisionDrawerExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void GradeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _currentPage = 1;
        await RefreshAsync();
    }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _currentPage = 1;
        await RefreshAsync();
    }
    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is not PageSizeOption option || _pageSize == option.Value) return;
        _pageSize = option.Value;
        _currentPage = 1;
        if (IsLoaded) ApplyPagination();
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1) return;
        _currentPage--;
        ApplyPagination();
    }
    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_leads.Count / (double)_pageSize));
        if (_currentPage >= totalPages) return;
        _currentPage++;
        ApplyPagination();
    }

    private sealed record FactorMetric(string Label, int Score, int Max, string Reason, string Evidence) { public double Percent => Max == 0 ? 0 : 100d * Score / Max; public string Value => $"{Score}/{Max}"; }
    private sealed record PageSizeOption(string Label, int Value);
    private static class LeadScoringLabel { public static readonly string[] Order = ["paid_marketing_willingness","supply_stability","ecommerce_foundation","private_traffic","existing_sales","materials_readiness"]; }
}
