using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class CreateWhatsAppGroupWindow : Window
{
    private readonly ObservableCollection<GroupMemberItem> _allMembers;
    public WhatsAppGroupCreateRequest? Request { get; private set; }

    public CreateWhatsAppGroupWindow(IEnumerable<GroupMemberCandidate> candidates)
    {
        InitializeComponent();
        _allMembers = new ObservableCollection<GroupMemberItem>(candidates
            .Where(candidate => PhoneNormalizer.Normalize(candidate.Phone, null).Valid)
            .GroupBy(candidate => PhoneNormalizer.Normalize(candidate.Phone, null).E164, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(candidate => new GroupMemberItem(candidate.DisplayName, PhoneNormalizer.Normalize(candidate.Phone, null).E164, candidate.SourceLabel)));
        MemberGrid.ItemsSource = _allMembers;
        UpdateState();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        MemberGrid.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _allMembers
            : _allMembers.Where(member => member.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || member.Phone.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void AddManualMember_Click(object sender, RoutedEventArgs e)
    {
        var normalized = PhoneNormalizer.Normalize(ManualPhoneBox.Text, null);
        if (!normalized.Valid)
        {
            MessageBox.Show("请输入包含国家区号的有效 WhatsApp 号码（8–15 位数字）。", "号码无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var existing = _allMembers.FirstOrDefault(member => member.Phone.Equals(normalized.E164, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new GroupMemberItem(normalized.E164, normalized.E164, "手动添加") { IsSelected = true };
            _allMembers.Insert(0, existing);
        }
        else existing.IsSelected = true;
        ManualPhoneBox.Clear(); SearchBox.Clear(); MemberGrid.ItemsSource = _allMembers; UpdateState();
    }

    private void MemberCheck_Click(object sender, RoutedEventArgs e) => UpdateState();
    private void SubjectBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var member in _allMembers) member.IsSelected = false;
        MemberGrid.Items.Refresh(); UpdateState();
    }

    private void UpdateState()
    {
        if (SelectionText is null || CreateButton is null || SubjectBox is null) return;
        var selected = _allMembers.Count(member => member.IsSelected);
        SelectionText.Text = $"已选 {selected:N0} 位";
        CreateButton.IsEnabled = SubjectBox.Text.Trim().Length is >= 1 and <= 100 && selected is >= 1 and <= 256;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Request = WhatsAppGroupCreateRequest.CreateValidated(SubjectBox.Text, _allMembers.Where(member => member.IsSelected).Select(member => member.Phone));
            DialogResult = true;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "无法建立群组", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public sealed record GroupMemberCandidate(string DisplayName, string Phone, string SourceLabel);

    private sealed class GroupMemberItem(string displayName, string phone, string sourceLabel) : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string DisplayName { get; } = displayName;
        public string Phone { get; } = phone;
        public string SourceLabel { get; } = sourceLabel;
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
