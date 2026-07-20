using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class CustomersView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly List<DataGridColumn> _customColumns = [];
    private bool _updatingCustomFilter;
    public event EventHandler? ImportRequested;
    public event EventHandler? DataChanged;

    public CustomersView(AppServices services)
    {
        InitializeComponent(); _services = services;
        GradeFilter.ItemsSource = new[] { "全部等级", "A", "B", "C", "D" }; GradeFilter.SelectedIndex = 0;
        StageFilter.ItemsSource = new[] { new StageOption("全部阶段", null) }.Concat(Enum.GetValues<LeadStage>().Select(x => new StageOption(Labels.Stage(x), x))).ToList(); StageFilter.SelectedIndex = 0;
        CustomFieldFilter.ItemsSource = new[] { new DimensionOption("全部表格维度", null) }; CustomFieldFilter.DisplayMemberPath = nameof(DimensionOption.Label); CustomFieldFilter.SelectedIndex = 0;
    }

    public async Task RefreshAsync()
    {
        var grade = GradeFilter.SelectedIndex <= 0 ? null : GradeFilter.SelectedItem as string;
        var stage = (StageFilter.SelectedItem as StageOption)?.Value;
        var leads = await _services.Repository.GetLeadsAsync(SearchBox.Text, grade, stage);
        var tag = TagFilterBox.Text.Trim(); var owner = OwnerFilterBox.Text.Trim();
        if (tag.Length > 0) leads = leads.Where(l => l.Tags.Any(x => x.Contains(tag, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (owner.Length > 0) leads = leads.Where(l => l.Owner.Contains(owner, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var dimensions = OrderedDimensions(leads);
        UpdateCustomFieldFilter(dimensions);
        var selectedDimension = (CustomFieldFilter.SelectedItem as DimensionOption)?.Key;
        var customValue = CustomValueFilterBox.Text.Trim();
        if (customValue.Length > 0)
        {
            leads = selectedDimension is null
                ? leads.Where(l => l.CustomFields.Values.Any(value => value.Contains(customValue, StringComparison.CurrentCultureIgnoreCase))).ToList()
                : leads.Where(l => TryGetCustomValue(l, selectedDimension, out var value) && value.Contains(customValue, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        RenderCustomColumns(dimensions);
        CustomerGrid.ItemsSource = leads;
        ListStatsText.Text = $"{leads.Count:N0} 位客户 · {dimensions.Count:N0} 个原表维度 · 横向滚动查看全部列";
        EditButton.IsEnabled = CustomerGrid.SelectedItem is Lead;
    }

    private void UpdateCustomFieldFilter(IReadOnlyList<string> dimensions)
    {
        var selected = (CustomFieldFilter.SelectedItem as DimensionOption)?.Key;
        _updatingCustomFilter = true;
        var options = new[] { new DimensionOption("全部表格维度", null) }
            .Concat(dimensions.Select(value => new DimensionOption(CompactHeader(value), value))).ToList();
        CustomFieldFilter.ItemsSource = options;
        CustomFieldFilter.SelectedItem = selected is null
            ? options[0]
            : options.FirstOrDefault(option => option.Key?.Equals(selected, StringComparison.CurrentCultureIgnoreCase) == true) ?? options[0];
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
                Header = new TextBlock
                {
                    Text = CompactHeader(dimension), ToolTip = dimension, TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150
                },
                Width = new DataGridLength(165),
                Binding = new Binding(nameof(Lead.CustomFields)) { Converter = CustomFieldValueConverter.Instance, ConverterParameter = dimension },
                ElementStyle = (Style)FindResource("CustomerCellText")
            };
            _customColumns.Add(column);
            CustomerGrid.Columns.Add(column);
        }
    }

    private static List<string> OrderedDimensions(IEnumerable<Lead> leads)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var lead in leads)
            foreach (var key in lead.CustomFields.Keys)
                if (seen.Add(key)) result.Add(key);
        return result;
    }

    private static string CompactHeader(string header)
    {
        var firstLine = header.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? header.Trim();
        return firstLine.Length <= 22 ? firstLine : firstLine[..21] + "…";
    }

    private static bool TryGetCustomValue(Lead lead, string key, out string value)
    {
        var match = lead.CustomFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.CurrentCultureIgnoreCase));
        value = match.Value ?? "";
        return match.Key is not null;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private void CustomerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => EditButton.IsEnabled = CustomerGrid.SelectedItem is Lead;
    private async void CustomerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (CustomerGrid.SelectedItem is Lead) await EditSelectedAsync(); }
    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();
    private async Task EditSelectedAsync()
    {
        if (CustomerGrid.SelectedItem is not Lead selected) return;
        var current = await _services.Repository.GetLeadAsync(selected.Id);
        if (current is null) { await RefreshAsync(); return; }
        var window = new CustomerEditWindow(_services, current) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_updatingCustomFilter) await RefreshAsync(); }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RefreshAsync(); }
    private async void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); TagFilterBox.Clear(); OwnerFilterBox.Clear(); CustomValueFilterBox.Clear(); GradeFilter.SelectedIndex = 0; StageFilter.SelectedIndex = 0; CustomFieldFilter.SelectedIndex = 0; await RefreshAsync(); }
    private sealed record StageOption(string Label, LeadStage? Value);
    private sealed record DimensionOption(string Label, string? Key);

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
