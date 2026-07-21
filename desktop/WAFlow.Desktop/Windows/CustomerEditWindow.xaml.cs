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
    private readonly ObservableCollection<EditableDimension> _dimensions;

    public CustomerEditWindow(AppServices services, Lead lead)
    {
        InitializeComponent();
        _services = services;
        _lead = lead;
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
            _lead.CustomFields = _dimensions.ToDictionary(item => item.Header, item => item.Value ?? "", StringComparer.OrdinalIgnoreCase);

            if (!_lead.AiScoreApplied)
            {
                LeadScoringService.ResetToAiBaseline(_lead);
            }
            await _services.Repository.UpsertLeadAsync(_lead);
            await _services.Repository.LogEventAsync("customer_edited", _lead.Id, null, $"edited core fields and {_lead.CustomFields.Count} table dimensions");
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

    private sealed record StageOption(string Label, LeadStage Value);

    private sealed class EditableDimension(string header, string value)
    {
        public string Header { get; } = header;
        public string Value { get; set; } = value;
    }
}
