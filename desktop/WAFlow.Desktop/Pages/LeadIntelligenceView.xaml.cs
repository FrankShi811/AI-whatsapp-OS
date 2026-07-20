using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Pages;

public partial class LeadIntelligenceView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private List<Lead> _leads = [];
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
        LeadGrid.ItemsSource = _leads;
        LeadGrid.SelectedItem = _leads.FirstOrDefault(x => x.Id == selectedId) ?? _leads.FirstOrDefault();
        UpdateInspector(LeadGrid.SelectedItem as Lead);
    }

    private void UpdateInspector(Lead? lead)
    {
        AnalyzeButton.IsEnabled = lead is not null;
        if (lead is null)
        {
            LeadNameText.Text = "选择一个商机"; CompanyText.Text = ""; GradeText.Text = "—"; StageText.Text = "—"; AmountText.Text = "—";
            ProfileText.Text = "尚未选择客户"; NextActionText.Text = "—"; FactorItems.ItemsSource = null; RiskItems.ItemsSource = null; AnalysisErrorText.Text = ""; return;
        }
        LeadNameText.Text = lead.DisplayName; CompanyText.Text = $"{lead.Company} · {lead.Country}"; GradeText.Text = lead.Grade;
        StageText.Text = lead.StageLabel; AmountText.Text = lead.AmountLabel; ProfileText.Text = lead.ProfileSummary; NextActionText.Text = lead.NextAction;
        var labels = new Dictionary<string, string> { ["marketValue"]="市场价值", ["companyScale"]="公司规模", ["productFit"]="产品匹配", ["purchasePower"]="采购能力", ["replyEngagement"]="回复积极度", ["recency"]="活跃时间", ["explicitDemand"]="明确需求", ["registeredOrConsulted"]="注册 / 咨询" };
        FactorItems.ItemsSource = LeadScoringLabel.Order.Select(key => new FactorMetric(labels[key], lead.ScoreBreakdown.GetValueOrDefault(key), WAFlow.Core.Services.LeadScoringService.Weights[key])).ToList();
        RiskItems.ItemsSource = lead.Risks.Count > 0 ? lead.Risks : lead.PhoneValid ? new[] { "AI 分析结论仍需人工核对。" } : new[] { "号码无效，禁止打开 WhatsApp。" };
        AnalysisErrorText.Text = lead.AnalysisError;
        GradeBadge.Background = (System.Windows.Media.Brush)FindResource(lead.Grade is "A" or "B" ? "SuccessSoft" : lead.Grade == "C" ? "WarningSoft" : "DangerSoft");
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (LeadGrid.SelectedItem is not Lead lead) return;
        var profile = await _services.Repository.GetSalesProfileAsync();
        if (profile is null) { MessageBox.Show("请先完成企业销售资料设置。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        AnalyzeButton.IsEnabled = false; AnalyzeButton.Content = "DeepSeek 分析中…";
        try
        {
            await _services.DeepSeek.AnalyzeLeadAsync(lead, profile);
            await RefreshAsync(); DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("分析已完成，评分、证据和下一步动作已保存。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "DeepSeek 分析失败（可重试）", MessageBoxButton.OK, MessageBoxImage.Warning); await RefreshAsync(); }
        finally { AnalyzeButton.Content = "使用 DeepSeek 分析 / 重试"; AnalyzeButton.IsEnabled = true; }
    }

    private void LeadGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInspector(LeadGrid.SelectedItem as Lead);
    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void GradeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RefreshAsync(); }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RefreshAsync(); }

    private sealed record FactorMetric(string Label, int Score, int Max) { public double Percent => Max == 0 ? 0 : 100d * Score / Max; public string Value => $"{Score}/{Max}"; }
    private static class LeadScoringLabel { public static readonly string[] Order = ["marketValue","companyScale","productFit","purchasePower","replyEngagement","recency","explicitDemand","registeredOrConsulted"]; }
}
