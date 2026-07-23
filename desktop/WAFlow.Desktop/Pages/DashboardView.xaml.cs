using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Pages;

public partial class DashboardView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    public event EventHandler<string>? NavigateRequested;
    public DashboardView(AppServices services) { InitializeComponent(); _services = services; }

    public async Task RefreshAsync()
    {
        var data = await _services.Repository.GetDashboardAsync();
        TotalLeadsText.Text = data.TotalLeads.ToString();
        HighValueText.Text = (data.Grades.GetValueOrDefault("A") + data.Grades.GetValueOrDefault("B")).ToString();
        FollowUpsText.Text = data.PendingFollowUps.ToString(); ActiveCampaignsText.Text = data.ActiveCampaigns.ToString();
        LastImportText.Text = data.LastImportText;
        GradeDonut.SetValues(data.Grades.GetValueOrDefault("A"), data.Grades.GetValueOrDefault("B"), data.Grades.GetValueOrDefault("C"), data.Grades.GetValueOrDefault("D"));
        var coverage = data.TotalLeads == 0 ? 0 : 100d * data.AnalyzedLeads / data.TotalLeads;
        AnalysisCoverageText.Text = $"{coverage:0}%";
        AnalysisCoverageBar.Value = coverage;
        AnalyzedLeadsText.Text = $"{data.AnalyzedLeads} / {data.TotalLeads}";
        AnalysisQueueText.Text = data.QueuedAnalyses > 0 ? $"{data.QueuedAnalyses} 个客户正在等待或分析中" : data.FailedAnalyses > 0 ? $"{data.FailedAnalyses} 个分析可重试" : "AI 队列已清空";
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
        TodayBriefSummaryText.Text = $"逾期 {brief.OverdueCount} · 今日到期 {brief.DueTodayCount} · 执行中 {brief.InProgressCount}";
        TodayBriefItems.ItemsSource = brief.Items.Take(6).ToList();
        LearningCompletionText.Text = brief.Learning.Accepted == 0
            ? "完成率 —"
            : $"完成率 {brief.Learning.CompletionRate:0.#}%";
        LearningHelpfulText.Text = brief.Learning.FeedbackCount == 0
            ? "有效反馈 —"
            : $"有效反馈 {brief.Learning.HelpfulRate:0.#}%";
        LearningDetailText.Text =
            $"已接受 {brief.Learning.Accepted} · 已完成 {brief.Learning.Completed} · 失败 {brief.Learning.Failed} · 忽略 {brief.Learning.Dismissed}";
        return;
        void SetGrade(string grade, TextBlock text, Border bar) { var count = data.Grades.GetValueOrDefault(grade); text.Text = count.ToString(); bar.Height = 20 + (data.TotalLeads == 0 ? 0 : 100d * count / data.TotalLeads); }
    }

    private void Action_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) NavigateRequested?.Invoke(this, page);
    }

    private sealed record StageMetric(string Label, int Count, double Percent);
}
