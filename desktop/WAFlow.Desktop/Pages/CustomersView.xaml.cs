using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Pages;

public partial class CustomersView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly List<DataGridColumn> _customColumns = [];
    private bool _updatingCustomFilter;
    public event EventHandler? ImportRequested;

    public CustomersView(AppServices services)
    {
        InitializeComponent(); _services = services;
        GradeFilter.ItemsSource = new[] { "全部等级", "A", "B", "C", "D" }; GradeFilter.SelectedIndex = 0;
        StageFilter.ItemsSource = new[] { new StageOption("全部阶段", null) }.Concat(Enum.GetValues<LeadStage>().Select(x => new StageOption(Labels.Stage(x), x))).ToList(); StageFilter.SelectedIndex = 0;
        CustomFieldFilter.ItemsSource = new[] { "全部自定义维度" }; CustomFieldFilter.SelectedIndex = 0;
    }

    public async Task RefreshAsync()
    {
        var grade = GradeFilter.SelectedIndex <= 0 ? null : GradeFilter.SelectedItem as string;
        var stage = (StageFilter.SelectedItem as StageOption)?.Value;
        var leads = await _services.Repository.GetLeadsAsync(SearchBox.Text, grade, stage);
        var tag = TagFilterBox.Text.Trim(); var owner = OwnerFilterBox.Text.Trim();
        if (tag.Length > 0) leads = leads.Where(l => l.Tags.Any(x => x.Contains(tag, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (owner.Length > 0) leads = leads.Where(l => l.Owner.Contains(owner, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var dimensions = leads.SelectMany(l => l.CustomFields.Keys).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        UpdateCustomFieldFilter(dimensions);
        var selectedDimension = CustomFieldFilter.SelectedIndex <= 0 ? null : CustomFieldFilter.SelectedItem as string;
        var customValue = CustomValueFilterBox.Text.Trim();
        if (customValue.Length > 0)
        {
            leads = selectedDimension is null
                ? leads.Where(l => l.CustomFields.Values.Any(value => value.Contains(customValue, StringComparison.CurrentCultureIgnoreCase))).ToList()
                : leads.Where(l => TryGetCustomValue(l, selectedDimension, out var value) && value.Contains(customValue, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        RenderCustomColumns(dimensions);
        CustomerGrid.ItemsSource = leads;
    }

    private void UpdateCustomFieldFilter(IReadOnlyList<string> dimensions)
    {
        var selected = CustomFieldFilter.SelectedItem as string;
        _updatingCustomFilter = true;
        CustomFieldFilter.ItemsSource = new[] { "全部自定义维度" }.Concat(dimensions).ToList();
        CustomFieldFilter.SelectedItem = selected is not null && dimensions.Contains(selected, StringComparer.CurrentCultureIgnoreCase) ? dimensions.First(x => x.Equals(selected, StringComparison.CurrentCultureIgnoreCase)) : "全部自定义维度";
        _updatingCustomFilter = false;
    }

    private void RenderCustomColumns(IEnumerable<string> dimensions)
    {
        foreach (var column in _customColumns) CustomerGrid.Columns.Remove(column);
        _customColumns.Clear();
        foreach (var dimension in dimensions)
        {
            var column = new DataGridTextColumn
            {
                Header = dimension,
                Width = new DataGridLength(125),
                Binding = new Binding(nameof(Lead.CustomFields)) { Converter = CustomFieldValueConverter.Instance, ConverterParameter = dimension }
            };
            _customColumns.Add(column);
            CustomerGrid.Columns.Add(column);
        }
    }

    private static bool TryGetCustomValue(Lead lead, string key, out string value)
    {
        var match = lead.CustomFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.CurrentCultureIgnoreCase));
        value = match.Value ?? "";
        return match.Key is not null;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_updatingCustomFilter) await RefreshAsync(); }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RefreshAsync(); }
    private async void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); TagFilterBox.Clear(); OwnerFilterBox.Clear(); CustomValueFilterBox.Clear(); GradeFilter.SelectedIndex = 0; StageFilter.SelectedIndex = 0; CustomFieldFilter.SelectedIndex = 0; await RefreshAsync(); }
    private sealed record StageOption(string Label, LeadStage? Value);

    private sealed class CustomFieldValueConverter : IValueConverter
    {
        public static readonly CustomFieldValueConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not IReadOnlyDictionary<string, string> fields || parameter is not string key) return "";
            return fields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.CurrentCultureIgnoreCase)).Value ?? "";
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
}
