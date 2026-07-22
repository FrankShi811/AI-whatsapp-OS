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
    private CancellationTokenSource? _bulkCancellation;
    private LeadBulkAnalysisProgress? _lastBulkProgress;
    private bool _decisionDrawerExpanded = true;
    public event EventHandler? ImportRequested;
    public event EventHandler? DataChanged;

    public LeadIntelligenceView(AppServices services)
    {
        InitializeComponent(); _services = services;
        GradeFilter.ItemsSource = new[] { "全部", "A", "B", "C", "D" }; GradeFilter.SelectedIndex = 0;
    }

    public async Task RefreshAsync()
    {
        var selectedId = (LeadGrid.SelectedItem as Lead)?.Id;
        _leads = await _services.Repository.GetLeadsAsync(SearchBox.Text, GradeFilter.SelectedItem as string);
        var settings = await _services.Repository.GetAppSettingsAsync();
        BulkAnalyzeButton.Content = $"使用 {settings.DeepSeekModel} 分析 / 重试全部";
        LeadGrid.ItemsSource = _leads;
        LeadGrid.SelectedItem = _leads.FirstOrDefault(x => x.Id == selectedId) ?? _leads.FirstOrDefault();
        UpdateInspector(LeadGrid.SelectedItem as Lead);
    }

    private void UpdateInspector(Lead? lead)
    {
        if (lead is null)
        {
            LeadNameText.Text = "选择一个商机"; CompanyText.Text = ""; GradeText.Text = "—"; ScoreText.Text = "0"; StageText.Text = "—"; AmountText.Text = "—";
            BaseScoreText.Text = "0 / 100"; BehaviorScoreText.Text = "0";
            ProfileText.Text = "尚未选择客户"; AnalysisMetaText.Text = ""; SignalItems.ItemsSource = null; NextActionText.Text = "—"; FactorItems.ItemsSource = null; RiskItems.ItemsSource = null; AnalysisErrorText.Text = "";
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
            MessageBox.Show("请先在“API 对接”中配置 API Key 并选择模型。", "无法开始批量分析", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var settings = await _services.Repository.GetAppSettingsAsync();
        var confirmation = MessageBox.Show(
            $"将使用 {settings.DeepSeekModel} 对全部 {allLeads.Count} 位客户逐一进行 AI 分析或重试。\n\n" +
            $"预计产生 {allLeads.Count} 次 AI 请求；每条结果独立保存，失败客户保持 D / 0 并可再次重试。是否继续？",
            "确认批量分析全部客户",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

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
        var progress = new Progress<LeadBulkAnalysisProgress>(UpdateBulkProgress);
        try
        {
            var result = await _services.LeadAutomation.AnalyzeAllLeadsAsync(progress, _bulkCancellation.Token);
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show(
                $"批量分析完成。\n\n总数：{result.Total}\n成功：{result.Succeeded}\n失败：{result.Failed}",
                "AI Sales OS",
                MessageBoxButton.OK,
                result.Failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
            var state = _lastBulkProgress;
            MessageBox.Show(
                $"批量分析已停止。\n\n已完成：{state?.Completed ?? 0} / {state?.Total ?? allLeads.Count}\n成功：{state?.Succeeded ?? 0}\n失败：{state?.Failed ?? 0}\n停止位置：{state?.CurrentLeadName ?? "—"}",
                "AI Sales OS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "批量分析无法继续", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshAsync();
        }
        finally
        {
            _bulkCancellation.Dispose();
            _bulkCancellation = null;
            BulkAnalyzeButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
            CancelBulkButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelBulk_Click(object sender, RoutedEventArgs e)
    {
        CancelBulkButton.IsEnabled = false;
        BulkProgressText.Text = "正在安全停止当前 AI 请求…";
        _bulkCancellation?.Cancel();
    }

    private void UpdateBulkProgress(LeadBulkAnalysisProgress progress)
    {
        _lastBulkProgress = progress;
        BulkProgressBar.Maximum = Math.Max(1, progress.Total);
        BulkProgressBar.Value = Math.Min(progress.Completed, progress.Total);
        BulkProgressText.Text = $"{progress.Message} · {progress.Completed}/{progress.Total} · 成功 {progress.Succeeded} · 失败 {progress.Failed}";
        CancelBulkButton.IsEnabled = progress.State is not "cancelled";
    }

    private void LeadGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInspector(LeadGrid.SelectedItem as Lead);
    private void ToggleDecisionDrawer_Click(object sender, RoutedEventArgs e)
    {
        _decisionDrawerExpanded = !_decisionDrawerExpanded;
        DecisionSidebarColumn.Width = new GridLength(_decisionDrawerExpanded ? 430 : 40);
        DecisionSidebarBorder.Visibility = _decisionDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        DecisionDrawerCollapsedRail.Visibility = _decisionDrawerExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void GradeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RefreshAsync(); }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RefreshAsync(); }

    private sealed record FactorMetric(string Label, int Score, int Max, string Reason, string Evidence) { public double Percent => Max == 0 ? 0 : 100d * Score / Max; public string Value => $"{Score}/{Max}"; }
    private static class LeadScoringLabel { public static readonly string[] Order = ["paid_marketing_willingness","supply_stability","ecommerce_foundation","private_traffic","existing_sales","materials_readiness"]; }
}
