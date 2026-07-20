using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Pages;

public partial class DashboardView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    public DashboardView(AppServices services) { InitializeComponent(); _services = services; }

    public async Task RefreshAsync()
    {
        var data = await _services.Repository.GetDashboardAsync();
        TotalLeadsText.Text = data.TotalLeads.ToString();
        HighValueText.Text = (data.Grades.GetValueOrDefault("A") + data.Grades.GetValueOrDefault("B")).ToString();
        FollowUpsText.Text = data.PendingFollowUps.ToString(); ApprovedDraftsText.Text = data.ReadyDrafts.ToString();
        LastImportText.Text = data.LastImportText;
        SetGrade("A", GradeAText, GradeABar); SetGrade("B", GradeBText, GradeBBar); SetGrade("C", GradeCText, GradeCBar); SetGrade("D", GradeDText, GradeDBar);
        var maximum = Math.Max(1, data.Stages.Values.DefaultIfEmpty(0).Max());
        StageItems.ItemsSource = Enum.GetValues<LeadStage>().Select(stage => new StageMetric(Labels.Stage(stage), data.Stages.GetValueOrDefault(stage), data.Stages.GetValueOrDefault(stage) * 100d / maximum)).ToList();
        PriorityGrid.ItemsSource = data.PriorityLeads;
        return;
        void SetGrade(string grade, TextBlock text, Border bar) { var count = data.Grades.GetValueOrDefault(grade); text.Text = count.ToString(); bar.Height = 20 + (data.TotalLeads == 0 ? 0 : 100d * count / data.TotalLeads); }
    }

    private sealed record StageMetric(string Label, int Count, double Percent);
}
