using System.Collections.ObjectModel;
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
    private ObservableCollection<MappingRow> _mapping = [];
    private List<ImportPreviewRow> _preview = [];
    private int _step;

    public ImportWindow(AppServices services)
    {
        InitializeComponent(); _services = services;
        TargetColumn.ItemsSource = Enum.GetValues<ImportField>().Select(value => new TargetOption(FieldLabel(value), value)).ToList();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title="选择商机表", Filter="Excel / CSV (*.xlsx;*.csv)|*.xlsx;*.csv", Multiselect=false };
        if (dialog.ShowDialog(this) == true) FilePathText.Text = dialog.FileName;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        NextButton.IsEnabled = false;
        try
        {
            if (_step == 0)
            {
                var filePath = FilePathText.Text;
                if (string.IsNullOrWhiteSpace(filePath)) { MessageBox.Show("请先选择文件。", "AI Sales OS"); return; }
                ShowProgress("正在后台解析文件…", 0, indeterminate:true);
                _parsed = await Task.Run(() => _services.Imports.Parse(filePath)); SheetCombo.ItemsSource = _parsed.Sheets; SheetCombo.SelectedIndex = 0;
                ShowStep(1);
            }
            else if (_step == 1)
            {
                MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true); MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);
                if (SheetCombo.SelectedItem is not ImportSheet sheet) return;
                var mapping = _mapping.Select(row => new MappingRow { Header = row.Header, Sample = row.Sample, Target = row.Target }).ToList();
                var progress = CreateProgress();
                _preview = await Task.Run(() => _services.Imports.BuildPreviewAsync(sheet, mapping, progress)); PreviewGrid.ItemsSource = _preview;
                PreviewStatsText.Text = $"{_preview.Count} 行 · {_preview.Count(x=>x.IsDuplicate)} 重复 · {_preview.Count(x=>!x.PhoneValid)} 号码风险";
                ShowStep(2);
            }
            else
            {
                if (_parsed is null) return;
                var fileName = Path.GetFileName(_parsed.FilePath);
                var preview = _preview.ToList();
                var allowStageChange = AllowStageCheck.IsChecked == true;
                var allowOwnerChange = AllowOwnerCheck.IsChecked == true;
                var progress = CreateProgress();
                var result = await Task.Run(() => _services.Imports.CommitAsync(fileName, preview, allowStageChange, allowOwnerChange, progress));
                MessageBox.Show($"导入完成\n新建 {result.Created} · 更新 {result.Updated} · 号码风险 {result.InvalidPhones} · 失败 {result.Failed}", "AI Sales OS", MessageBoxButton.OK, result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                DialogResult = true;
            }
        }
        catch (Exception error) { MessageBox.Show(error.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { NextButton.IsEnabled = true; HideProgress(); }
    }

    private void SheetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetCombo.SelectedItem is not ImportSheet sheet) return;
        _mapping = new ObservableCollection<MappingRow>(_services.Imports.SuggestMapping(sheet)); MappingGrid.ItemsSource = _mapping;
        MappingHintText.Text = $"工作表 {sheet.Name} · {sheet.Rows.Count:N0} 行 · {sheet.Headers.Count} 列 · 已清理 {sheet.SanitizedFormulaCount} 个公式 / 注入值";
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (_step > 0) ShowStep(_step - 1); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowStep(int step)
    {
        _step = step; SelectPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed; MappingPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed; PreviewPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible; NextButton.Content = step switch { 0 => "解析并继续", 1 => "生成重复预览", _ => "确认并导入" };
        Step1Circle.Background = Brush(step >= 0); Step2Circle.Background = Brush(step >= 1); Step3Circle.Background = Brush(step >= 2);
        static System.Windows.Media.Brush Brush(bool active) => new System.Windows.Media.SolidColorBrush(active ? System.Windows.Media.Color.FromRgb(15,143,104) : System.Windows.Media.Color.FromRgb(203,215,209));
    }

    private static string FieldLabel(ImportField value) => value switch
    {
        ImportField.Ignore=>"不导入", ImportField.Custom=>"自定义维度（保留原表头）", ImportField.Name=>"客户姓名", ImportField.Company=>"公司名称", ImportField.Country=>"国家 / 地区", ImportField.WhatsApp=>"WhatsApp 号码",
        ImportField.Email=>"邮箱", ImportField.ProductInterest=>"意向产品", ImportField.EstimatedOrderValue=>"预计订单额", ImportField.CompanyScale=>"公司规模", ImportField.PurchasePower=>"采购能力",
        ImportField.ExplicitDemand=>"明确需求", ImportField.Source=>"来源", ImportField.Owner=>"负责人", ImportField.Stage=>"商机阶段", ImportField.Tags=>"标签", ImportField.Notes=>"备注", _=>value.ToString()
    };

    private IProgress<ImportProgress> CreateProgress() => new Progress<ImportProgress>(value => ShowProgress(value.Label, value.Percent));

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

    private sealed record TargetOption(string Label, ImportField Value);
}
