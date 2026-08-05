using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

/// <summary>Edits WhatsApp labels for one conversation and synchronizes them with the
/// WhatsApp server (phone app). All mutations go through the bridge so the phone
/// reflects changes immediately; the bridge's labels.edit / labels.association
/// events persist the same state locally.</summary>
public partial class LabelManagerWindow : Window
{
    private readonly AppServices _services;
    private readonly string _accountId;
    private readonly string _phone;
    private readonly string _displayName;
    private readonly ObservableCollection<LabelItem> _assigned = [];
    private readonly ObservableCollection<LabelItem> _all = [];
    private int _selectedColor;

    public LabelManagerWindow(AppServices services, string accountId, string phone, string displayName)
    {
        InitializeComponent();
        _services = services;
        _accountId = accountId;
        _phone = phone;
        _displayName = displayName;
        TitleText.Text = $"标签 · {displayName}";
        AssignedList.ItemsSource = _assigned;
        AllLabelsList.ItemsSource = _all;
        NewLabelColorCombo.ItemsSource = LabelPalette.Names;
        NewLabelColorCombo.SelectedIndex = 0;
        _selectedColor = 0;
        Loaded += async (_, _) => await LoadAsync();
    }

    private static class LabelPalette
    {
        public static readonly string[] Names =
        [
            "红", "橙", "黄", "绿", "青", "蓝", "紫", "粉", "棕", "灰",
            "红2", "橙2", "黄2", "绿2", "青2", "蓝2", "紫2", "粉2", "棕2", "灰2"
        ];
    }

    private async Task LoadAsync()
    {
        try
        {
            var labels = await _services.Repository.GetWhatsAppLabelsAsync(_accountId);
            var assignedIds = await _services.Repository.GetWhatsAppChatLabelIdsAsync(_accountId, _phone);
            var assigned = assignedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _assigned.Clear();
            _all.Clear();
            foreach (var label in labels)
            {
                var item = new LabelItem(label, assigned.Contains(label.Id));
                _all.Add(item);
                if (item.Assigned) _assigned.Add(item);
            }
            EmptyHint.Visibility = _assigned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error)
        {
            StatusText.Text = $"加载标签失败：{error.Message}";
        }
    }

    private async Task ToggleAsync(LabelItem item, bool add)
    {
        try
        {
            if (!_services.WhatsApp.IsConnectedFor(_accountId))
            {
                StatusText.Text = "请先连接 WhatsApp，再同步标签。";
                return;
            }
            StatusText.Text = add ? $"正在添加「{item.Name}」并同步到手机…" : $"正在移除「{item.Name}」并同步到手机…";
            await _services.WhatsApp.SetChatLabelAsync(_accountId, _phone, item.Id, add);
            await _services.Repository.SetWhatsAppChatLabelAsync(_accountId, _phone, item.Id, add);
            item.Assigned = add;
            if (add)
            {
                if (!_assigned.Contains(item)) _assigned.Add(item);
            }
            else
            {
                var existing = _assigned.FirstOrDefault(candidate => candidate.Id == item.Id);
                if (existing is not null) _assigned.Remove(existing);
            }
            EmptyHint.Visibility = _assigned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"已同步到手机：{(add ? "添加" : "移除")}「{item.Name}」";
        }
        catch (Exception error)
        {
            StatusText.Text = $"同步失败：{error.Message}";
        }
    }

    private async void AssignedLabel_Remove(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: LabelItem item }) await ToggleAsync(item, add: false);
    }

    private async void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: LabelItem item }) await ToggleAsync(item, add: true);
    }

    private async void RemoveLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: LabelItem item }) await ToggleAsync(item, add: false);
    }

    private async void DeleteLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: LabelItem item }) return;
        if (MessageBox.Show($"确定从 WhatsApp 删除标签「{item.Name}」吗？所有使用该标签的客户都会失去此标签。",
                "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            if (!_services.WhatsApp.IsConnectedFor(_accountId))
            {
                StatusText.Text = "请先连接 WhatsApp，再同步标签。";
                return;
            }
            StatusText.Text = $"正在删除「{item.Name}」并同步到手机…";
            await _services.WhatsApp.UpsertLabelAsync(_accountId, new WhatsAppLabel { Id = item.Id, Name = item.Name, Color = item.Color, Deleted = true });
            await _services.Repository.UpsertWhatsAppLabelAsync(new WhatsAppLabel { Id = item.Id, AccountId = _accountId, Name = item.Name, Color = item.Color, Deleted = true });
            _all.Remove(item);
            var assigned = _assigned.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (assigned is not null) _assigned.Remove(assigned);
            EmptyHint.Visibility = _assigned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"已删除「{item.Name}」并同步到手机";
        }
        catch (Exception error)
        {
            StatusText.Text = $"删除失败：{error.Message}";
        }
    }

    private void NewLabelColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NewLabelColorCombo.SelectedIndex >= 0) _selectedColor = NewLabelColorCombo.SelectedIndex;
    }

    private async void CreateLabel_Click(object sender, RoutedEventArgs e)
    {
        var name = NewLabelNameBox.Text.Trim();
        if (name.Length == 0) return;
        if (name.Length > 100)
        {
            StatusText.Text = "标签名称不能超过 100 个字符。";
            return;
        }
        try
        {
            if (!_services.WhatsApp.IsConnectedFor(_accountId))
            {
                StatusText.Text = "请先连接 WhatsApp，再同步标签。";
                return;
            }
            StatusText.Text = $"正在创建「{name}」并同步到手机…";
            var label = new WhatsAppLabel
            {
                Id = Guid.NewGuid().ToString("N"),
                AccountId = _accountId,
                Name = name,
                Color = _selectedColor,
                Deleted = false
            };
            await _services.WhatsApp.UpsertLabelAsync(_accountId, label);
            await _services.Repository.UpsertWhatsAppLabelAsync(label);
            var item = new LabelItem(label, assigned: false);
            _all.Add(item);
            await ToggleAsync(item, add: true);
            NewLabelNameBox.Text = "";
        }
        catch (Exception error)
        {
            StatusText.Text = $"创建失败：{error.Message}";
        }
    }

    public sealed class LabelItem : INotifyPropertyChanged
    {
        private bool _assigned;
        public LabelItem(WhatsAppLabel label, bool assigned)
        {
            Id = label.Id;
            Name = label.Name;
            Color = label.Color;
            _assigned = assigned;
        }
        public string Id { get; }
        public string Name { get; }
        public int Color { get; }
        public bool Assigned { get => _assigned; set { if (_assigned != value) { _assigned = value; OnPropertyChanged(nameof(Assigned)); OnPropertyChanged(nameof(AddVisibility)); } } }
        public Visibility AddVisibility => Assigned ? Visibility.Collapsed : Visibility.Visible;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
