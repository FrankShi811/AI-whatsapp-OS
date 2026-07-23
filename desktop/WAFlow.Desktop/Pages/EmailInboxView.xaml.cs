using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class EmailInboxView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly ObservableCollection<EmailAccount> _accounts = [];
    private readonly ObservableCollection<EmailConversation> _conversations = [];
    private readonly ObservableCollection<EmailMessage> _messages = [];
    private EmailConversation? _conversation;
    private Lead? _lead;
    private bool _loading;
    private bool _customerDrawerExpanded = true;

    public event EventHandler? DataChanged;

    public EmailInboxView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        AccountBox.ItemsSource = _accounts; ConversationList.ItemsSource = _conversations; MessageList.ItemsSource = _messages;
        StageBox.ItemsSource = Enum.GetValues<LeadStage>().Select(stage => new StageChoice(Labels.Stage(stage), stage)).ToList();
    }

    private void ToggleCustomerDrawer_Click(object sender, RoutedEventArgs e)
    {
        _customerDrawerExpanded = !_customerDrawerExpanded;
        CustomerDrawerColumn.Width = new GridLength(_customerDrawerExpanded ? 360 : 44);
        CustomerDrawerBorder.Visibility = _customerDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        CustomerDrawerCollapsedRail.Visibility = _customerDrawerExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    public async Task RefreshAsync()
    {
        var accountId = (AccountBox.SelectedItem as EmailAccount)?.Id;
        _loading = true;
        try
        {
            var accounts = await _services.Repository.GetEmailAccountsAsync();
            _accounts.Clear(); foreach (var account in accounts) _accounts.Add(account);
            AccountBox.SelectedItem = _accounts.FirstOrDefault(item => item.Id == accountId) ?? _accounts.FirstOrDefault();
            await RefreshConversationsAsync();
        }
        finally { _loading = false; }
    }

    private async Task RefreshConversationsAsync()
    {
        var selectedId = _conversation?.Id;
        _conversations.Clear();
        if (AccountBox.SelectedItem is not EmailAccount account) { ClearConversation(); return; }
        foreach (var conversation in await _services.Repository.GetEmailConversationsAsync(account.Id)) _conversations.Add(conversation);
        ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == selectedId);
        ApplySearch();
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (new EmailAccountWindow(_services) { Owner = Window.GetWindow(this) }.ShowDialog() == true) await RefreshAsync();
    }

    private async void ManageAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not EmailAccount account) { AddAccount_Click(sender, e); return; }
        if (new EmailAccountWindow(_services, account) { Owner = Window.GetWindow(this) }.ShowDialog() == true) await RefreshAsync();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not EmailAccount account) { MessageBox.Show("请先连接邮件账号。", "邮件 Inbox", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            SyncButton.IsEnabled = false; SyncButton.Content = "正在同步…";
            var count = await _services.Email.SyncInboxAsync(account.Id, 500);
            await RefreshAsync(); DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show($"邮件同步完成，已处理 {count} 封收件箱邮件。", "同步完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "同步失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SyncButton.IsEnabled = true; SyncButton.Content = "同步收件箱"; }
    }

    private async void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) await RefreshConversationsAsync();
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not EmailConversation conversation) { ClearConversation(); return; }
        _conversation = conversation;
        if (conversation.UnreadCount > 0)
        {
            conversation.UnreadCount = 0;
            await _services.Repository.UpsertEmailConversationAsync(conversation);
        }
        ConversationTitle.Text = conversation.DisplayName; ConversationSubtitle.Text = $"{conversation.PeerEmail} · {conversation.Subject}";
        _messages.Clear(); foreach (var message in await _services.Repository.GetEmailMessagesAsync(conversation.Id)) _messages.Add(message);
        SubjectBox.Text = string.IsNullOrWhiteSpace(conversation.Subject) ? "" : conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : $"Re: {conversation.Subject}";
        _lead = !string.IsNullOrWhiteSpace(conversation.LeadId) ? await _services.Repository.GetLeadAsync(conversation.LeadId) : await _services.Repository.GetLeadByEmailAsync(conversation.PeerEmail);
        PopulateCustomer();
        await Dispatcher.InvokeAsync(() => MessageScroll.ScrollToEnd());
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation is null || AccountBox.SelectedItem is not EmailAccount account) return;
        try
        {
            SendButton.IsEnabled = false;
            await _services.Email.SendAsync(account.Id, _conversation.PeerEmail, SubjectBox.Text, ComposerBox.Text, _lead?.Id);
            ComposerBox.Clear(); await RefreshConversationsAsync();
            ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == _conversation.Id);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "邮件发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SendButton.IsEnabled = true; }
    }

    private async void SaveCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation is null) return;
        try
        {
            _lead ??= new Lead { Name = NameBox.Text.Trim(), Email = CustomerEmailBox.Text.Trim(), Grade = "D", Score = 0, Stage = LeadStage.New };
            _lead.Name = NameBox.Text.Trim(); _lead.Email = CustomerEmailBox.Text.Trim(); _lead.Company = CompanyBox.Text.Trim();
            _lead.Country = CountryBox.Text.Trim(); _lead.Owner = OwnerBox.Text.Trim();
            var selectedStage = (StageBox.SelectedItem as StageChoice)?.Value ?? LeadStage.New;
            if (selectedStage != _lead.Stage)
            {
                _lead.Stage = selectedStage;
                _lead.StageManuallyLocked = true;
                _lead.StageSource = "user";
                _lead.StageManuallyUpdatedAt = DateTimeOffset.Now;
            }
            _lead.Tags = TagsBox.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            _lead.LatestMessage = NotesBox.Text.Trim();
            await _services.Repository.UpsertLeadAsync(_lead);
            _conversation.LeadId = _lead.Id; _conversation.PeerName = _lead.DisplayName;
            await _services.Repository.UpsertEmailConversationAsync(_conversation);
            LinkStateText.Text = $"已关联：{_lead.Grade} 级 · {Labels.Stage(_lead.Stage)}";
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("客户资料已同步到客户列表、商机智能和自动化触达。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void PopulateCustomer()
    {
        NameBox.Text = _lead?.Name ?? _conversation?.PeerName ?? ""; CustomerEmailBox.Text = _lead?.Email ?? _conversation?.PeerEmail ?? "";
        CompanyBox.Text = _lead?.Company ?? ""; CountryBox.Text = _lead?.Country ?? ""; OwnerBox.Text = _lead?.Owner ?? "";
        TagsBox.Text = _lead is null ? "" : string.Join(", ", _lead.Tags); NotesBox.Text = _lead?.LatestMessage ?? "";
        StageBox.SelectedItem = StageBox.Items.Cast<StageChoice>().First(item => item.Value == (_lead?.Stage ?? LeadStage.New));
        LinkStateText.Text = _lead is null ? "未关联客户 · 保存时将创建" : $"已关联：{_lead.Grade} 级 · {Labels.Stage(_lead.Stage)}";
    }

    private void ClearConversation()
    {
        _conversation = null; _lead = null; _messages.Clear(); ConversationTitle.Text = "选择邮件会话"; ConversationSubtitle.Text = ""; SubjectBox.Clear(); ComposerBox.Clear();
        NameBox.Clear(); CustomerEmailBox.Clear(); CompanyBox.Clear(); CountryBox.Clear(); OwnerBox.Clear(); TagsBox.Clear(); NotesBox.Clear(); LinkStateText.Text = "按邮箱自动匹配客户";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();
    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_conversations).Filter = item => item is EmailConversation conversation &&
            (query.Length == 0 || string.Join(' ', conversation.DisplayName, conversation.PeerEmail, conversation.Subject, conversation.LastMessage).Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private sealed record StageChoice(string Label, LeadStage Value);
}
