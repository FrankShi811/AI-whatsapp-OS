using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class DashboardView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private bool _unreadDigestRefreshRunning;
    private bool _unreadDigestRefreshPending;
    private bool _unreadDigestForcePending;
    private int _unreadDigestDelayMilliseconds;
    private bool _bulkProgressSubscribed;
    public event EventHandler<string>? NavigateRequested;
    public DashboardView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Loaded += DashboardView_Loaded;
        Unloaded += DashboardView_Unloaded;
    }

    private void DashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_bulkProgressSubscribed)
        {
            _services.LeadAutomation.BulkAnalysisProgressChanged += LeadAutomation_BulkAnalysisProgressChanged;
            _bulkProgressSubscribed = true;
        }

        if (_services.LeadAutomation.IsBulkAnalysisRunning)
        {
            var progress = _services.LeadAutomation.CurrentBulkProgress;
            if (progress is not null) ApplyAnalysisCoverage(progress);
        }
    }

    private void DashboardView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_bulkProgressSubscribed) return;
        _services.LeadAutomation.BulkAnalysisProgressChanged -= LeadAutomation_BulkAnalysisProgressChanged;
        _bulkProgressSubscribed = false;
    }

    private void LeadAutomation_BulkAnalysisProgressChanged(object? sender, LeadBulkAnalysisProgress progress)
    {
        _ = Dispatcher.InvokeAsync(() => ApplyAnalysisCoverage(progress));
    }

    public async Task RefreshAsync()
    {
        var data = await _services.Repository.GetDashboardAsync();
        TotalLeadsText.Text = data.TotalLeads.ToString();
        HighValueText.Text = (data.Grades.GetValueOrDefault("A") + data.Grades.GetValueOrDefault("B")).ToString();
        FollowUpsText.Text = data.PendingFollowUps.ToString(); ActiveCampaignsText.Text = data.ActiveCampaigns.ToString();
        LastImportText.Text = data.LastImportText;
        GradeDonut.SetValues(data.Grades.GetValueOrDefault("A"), data.Grades.GetValueOrDefault("B"), data.Grades.GetValueOrDefault("C"), data.Grades.GetValueOrDefault("D"));
        if (_services.LeadAutomation.IsBulkAnalysisRunning)
        {
            var activeBulkProgress = _services.LeadAutomation.CurrentBulkProgress;
            if (activeBulkProgress is not null)
            {
                ApplyAnalysisCoverage(activeBulkProgress);
            }
            else
            {
                ApplyAnalysisCoverage(0, data.TotalLeads, $"正在准备分析 0 / {data.TotalLeads:N0}");
            }
        }
        else
        {
            ApplyAnalysisCoverage(
                data.AnalyzedLeads,
                data.TotalLeads,
                data.QueuedAnalyses > 0
                    ? $"{data.QueuedAnalyses} 个客户正在等待或分析中"
                    : data.FailedAnalyses > 0
                        ? $"{data.FailedAnalyses} 个分析可重试"
                        : "AI 队列已清空");
        }
        CampaignSentText.Text = data.CampaignSent.ToString();
        CampaignQueuedText.Text = data.CampaignQueued.ToString();
        CampaignFailedText.Text = data.CampaignFailed.ToString();
        var attempts = data.CampaignSent + data.CampaignFailed;
        CampaignQualityText.Text = attempts == 0 ? "暂无发送历史；建立任务后将在这里看到执行质量。" : $"发送到位率 {(100d * data.CampaignSent / attempts):0.0}% · 共尝试 {attempts} 条";
        CampaignSafetyText.Text = data.SafetyStoppedCampaigns > 0 ? $"{data.SafetyStoppedCampaigns} 个任务被 IP 安全阀停止" : "排期、运行或暂停";
        SetGrade("A", GradeAText, GradeABar); SetGrade("B", GradeBText, GradeBBar); SetGrade("C", GradeCText, GradeCBar); SetGrade("D", GradeDText, GradeDBar);
        var maximum = Math.Max(1, data.Stages.Values.DefaultIfEmpty(0).Max());
        StageItems.ItemsSource = Enum.GetValues<LeadStage>().Select(stage => new StageMetric(Labels.Stage(stage), data.Stages.GetValueOrDefault(stage), data.Stages.GetValueOrDefault(stage) * 100d / maximum)).ToList();
        PriorityGrid.ItemsSource = data.PriorityLeads;
        var brief = await _services.TodayBrief.GetAsync();
        TodayBriefSummaryText.Text = brief.Items.Count == 0
            ? "今天暂无待处理行动；新客户回复、人工接管或 AI 建议会自动进入这里。"
            : $"待处理 {brief.Items.Count} 项｜逾期 {brief.OverdueCount}｜今天到期 {brief.DueTodayCount}｜人工接管 {brief.HumanHandoffCount}｜知识审核 {brief.KnowledgeReviewCount}｜知识冲突 {brief.KnowledgeConflictCount}｜候选审批 {brief.KnowledgeCandidateCount}";
        TodayBriefItems.ItemsSource = brief.Items.Take(6).ToList();
        LearningCompletionText.Text = brief.Learning.Accepted == 0
            ? "完成率 —"
            : $"完成率 {brief.Learning.CompletionRate:0.#}%";
        LearningHelpfulText.Text = brief.Learning.FeedbackCount == 0
            ? "有效反馈 —"
            : $"有效反馈 {brief.Learning.HelpfulRate:0.#}%";
        LearningOutcomeText.Text = brief.Learning.Executed == 0
            ? "真实结果尚未形成"
            : $"真实回复 {brief.Learning.ResponseRate:0.#}% · 阶段推进 {brief.Learning.ProgressionRate:0.#}% · 成交观察 {brief.Learning.DealRate:0.#}%";
        LearningDetailText.Text =
            $"已接受 {brief.Learning.Accepted} · 已执行 {brief.Learning.Executed} · 观察中 {brief.Learning.AwaitingOutcome} · 复购 {brief.Learning.RepeatPurchases}";
        LearningStrategyText.Text = brief.Learning.StrategyReview;
        QueueUnreadDigestRefresh();
        return;
        void SetGrade(string grade, TextBlock text, Border bar) { var count = data.Grades.GetValueOrDefault(grade); text.Text = count.ToString(); bar.Height = 20 + (data.TotalLeads == 0 ? 0 : 100d * count / data.TotalLeads); }
    }

    public void NotifyUnreadChanged()
    {
        if (IsVisible) QueueUnreadDigestRefresh(delayMilliseconds: 350);
    }

    private void QueueUnreadDigestRefresh(bool forceRefresh = false, int delayMilliseconds = 0)
    {
        _unreadDigestRefreshPending = true;
        _unreadDigestForcePending |= forceRefresh;
        _unreadDigestDelayMilliseconds = Math.Max(_unreadDigestDelayMilliseconds, delayMilliseconds);
        if (_unreadDigestRefreshRunning) return;
        _ = RunUnreadDigestRefreshLoopAsync();
    }

    private async Task RunUnreadDigestRefreshLoopAsync()
    {
        _unreadDigestRefreshRunning = true;
        RefreshUnreadDigestButton.IsEnabled = false;
        try
        {
            do
            {
                var forceRefresh = _unreadDigestForcePending;
                var delay = _unreadDigestDelayMilliseconds;
                _unreadDigestRefreshPending = false;
                _unreadDigestForcePending = false;
                _unreadDigestDelayMilliseconds = 0;
                if (delay > 0) await Task.Delay(delay);
                await RefreshUnreadDigestAsync(forceRefresh);
            }
            while (_unreadDigestRefreshPending);
        }
        finally
        {
            _unreadDigestRefreshRunning = false;
            RefreshUnreadDigestButton.IsEnabled = true;
        }
    }

    private async Task RefreshUnreadDigestAsync(bool forceRefresh)
    {
        try
        {
            UnreadDigestEmptyText.Visibility = Visibility.Collapsed;

            var digest = await _services.DashboardUnreadDigest.GetAsync(forceRefresh);
            UnreadDigestItems.ItemsSource = digest.Items.Take(6).ToList();
            UnreadDigestEmptyText.Text = "暂无未读客户消息。";
            UnreadDigestEmptyText.Visibility = digest.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            UnreadDigestItems.ItemsSource = null;
            UnreadDigestEmptyText.Text = "未读消息暂时无法整理，后台会自动重试。";
            UnreadDigestEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void RefreshUnreadDigest_Click(object sender, RoutedEventArgs e)
    {
        QueueUnreadDigestRefresh(forceRefresh: true);
    }

    private void OpenUnreadChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string channel }) return;
        NavigateRequested?.Invoke(this, channel.Equals("email", StringComparison.OrdinalIgnoreCase) ? "email" : "inbox");
    }

    private void Action_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) NavigateRequested?.Invoke(this, page);
    }

    private void ApplyAnalysisCoverage(LeadBulkAnalysisProgress progress)
    {
        var completed = Math.Min(Math.Max(0, progress.Completed), Math.Max(0, progress.Total));
        var state = progress.State == "completed" ? "已分析" : "正在分析";
        ApplyAnalysisCoverage(completed, progress.Total, $"{state} {completed:N0} / {progress.Total:N0}");
    }

    private void ApplyAnalysisCoverage(int completed, int total, string queueText)
    {
        total = Math.Max(0, total);
        completed = Math.Clamp(completed, 0, total);
        var coverage = total == 0 ? 0 : 100d * completed / total;
        AnalysisCoverageText.Text = $"{coverage:0}%";
        AnalysisCoverageBar.Value = coverage;
        AnalyzedLeadsText.Text = $"{completed:N0} / {total:N0}";
        AnalysisQueueText.Text = queueText;
    }

    private sealed record StageMetric(string Label, int Count, double Percent);
}
