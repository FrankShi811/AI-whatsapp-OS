using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Imports;

namespace WAFlow.Desktop.Windows;

public partial class ImportWindow : Window
{
    private readonly AppServices _services;
    private ParsedImport? _parsed;
    private int _step;

    public ImportWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title="选择客户表", Filter="Excel / CSV (*.xlsx;*.csv)|*.xlsx;*.csv", Multiselect=false };
        if (dialog.ShowDialog(this) == true) FilePathText.Text = dialog.FileName;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            if (_step == 0)
            {
                var filePath = FilePathText.Text;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    MessageBox.Show("请先选择文件。", "AI Sales OS");
                    return;
                }

                ShowProgress("正在后台解析文件…", 0, indeterminate:true);
                _parsed = await Task.Run(() => _services.Imports.Parse(filePath));
                SheetCombo.ItemsSource = _parsed.Sheets;
                SheetCombo.SelectedItem = _parsed.Sheets.FirstOrDefault(sheet => sheet.Name.Equals(_parsed.PreferredSheetName, StringComparison.OrdinalIgnoreCase)) ?? _parsed.Sheets[0];
                ShowStep(1);
                return;
            }

            if (_parsed is null || SheetCombo.SelectedItem is not ImportSheet selectedSheet) return;
            var sheet = selectedSheet;
            var mapping = _services.Imports.SuggestMapping(sheet)
                .Select(row => new MappingRow { Header = row.Header, Sample = row.Sample, Target = row.Target })
                .ToList();
            var fileName = Path.GetFileName(_parsed.FilePath);
            var progress = CreateProgress();
            var outcome = await Task.Run(async () =>
            {
                var preview = await _services.Imports.BuildPreviewAsync(sheet, mapping, progress);
                var commit = await _services.Imports.CommitAsync(fileName, preview, allowStageChange:true, allowOwnerChange:true, progress);
                var demosRemoved = commit.Created + commit.Updated > 0
                    ? await _services.Repository.RemoveDemoLeadsIfRealDataExistsAsync()
                    : 0;
                return (Commit: commit, DemosRemoved: demosRemoved);
            });
            var result = outcome.Commit;
            var cleanupText = outcome.DemosRemoved > 0 ? $"\n\u5df2\u81ea\u52a8\u6e05\u7406 {outcome.DemosRemoved} \u6761\u6f14\u793a\u5ba2\u6237\u3002" : "";

            MessageBox.Show(
                $"\u5bfc\u5165\u5b8c\u6210\n\u5904\u7406 {result.Total:N0} \u884c \u00b7 \u65b0\u5efa {result.Created:N0} \u00b7 \u66f4\u65b0 {result.Updated:N0}\n\u53f7\u7801\u98ce\u9669 {result.InvalidPhones:N0} \u00b7 \u5931\u8d25 {result.Failed:N0}\n\n\u539f\u5de5\u4f5c\u8868\u7684 {sheet.Headers.Count} \u5217\u5df2\u5168\u90e8\u4fdd\u7559\u4e3a\u5ba2\u6237\u7ef4\u5ea6\u3002{cleanupText}",
                "AI Sales OS", MessageBoxButton.OK, result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            HideProgress();
        }
    }

    private void SheetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetCombo.SelectedItem is not ImportSheet sheet) return;
        SheetStatsText.Text = $"{sheet.Name} · {sheet.Rows.Count:N0} 行 × {sheet.Headers.Count:N0} 列";
        ColumnSummaryText.Text = string.Join("　·　", sheet.Headers.Select(CompactHeader));
    }

    private static string CompactHeader(string header)
    {
        var firstLine = header.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? header.Trim();
        return firstLine.Length <= 32 ? firstLine : firstLine[..31] + "…";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) ShowStep(0);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowStep(int step)
    {
        _step = step;
        SelectPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        SheetPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step == 0 ? "解析文件" : "直接导入全部列";
        Step1Circle.Background = Brush(step >= 0);
        Step2Circle.Background = Brush(step >= 1);
        static System.Windows.Media.Brush Brush(bool active) => new System.Windows.Media.SolidColorBrush(active ? System.Windows.Media.Color.FromRgb(15,143,104) : System.Windows.Media.Color.FromRgb(203,215,209));
    }

    private IProgress<ImportProgress> CreateProgress() => new Progress<ImportProgress>(value => ShowProgress(value.Label, value.Percent));

    private void SetBusy(bool busy)
    {
        NextButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        SheetCombo.IsEnabled = !busy;
    }

    private void ShowProgress(string text, int percent, bool indeterminate = false)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ImportProgressText.Text = text;
        ImportProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate) ImportProgressBar.Value = percent;
    }

    private void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        ImportProgressBar.IsIndeterminate = false;
        ImportProgressBar.Value = 0;
    }
}
