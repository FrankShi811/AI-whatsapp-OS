using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class WhatsAppInboxView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ConversationItem> _conversations = [];
    private readonly ObservableCollection<WhatsAppAccount> _accounts = [];
    private readonly List<Lead> _leads = [];
    private Lead? _currentLead;
    private bool _connected;
    private bool _switchingAccount;
    private bool _existingSession;
    private bool _refreshScheduled;
    private bool _refreshAgain;
    private int _persistedConversationCount;
    private int _contactCount;
    private readonly HashSet<string> _automaticSyncRequested = new(StringComparer.OrdinalIgnoreCase);

    private string CurrentAccountId => (AccountCombo.SelectedItem as WhatsAppAccount)?.Id ?? "primary";

    public event EventHandler? DataChanged;

    public WhatsAppInboxView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        ConversationList.ItemsSource = _conversations;
        AccountCombo.ItemsSource = _accounts;
        StageCombo.ItemsSource = Enum.GetValues<LeadStage>().Select(x => new StageOption(Labels.Stage(x), x)).ToList();
        _services.WhatsApp.EventReceived += WhatsApp_EventReceived;
        _services.WhatsAppSync.MessageSynchronized += (_, _) => Dispatcher.InvokeAsync(() => DataChanged?.Invoke(this, EventArgs.Empty));
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSync_SynchronizationChanged;
    }

    public async Task RefreshAsync()
    {
        var selectedAccountId = (AccountCombo.SelectedItem as WhatsAppAccount)?.Id ?? _services.WhatsApp.ActiveAccountId;
        var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
        _switchingAccount = true;
        _accounts.Clear(); foreach (var account in accounts) _accounts.Add(account);
        AccountCombo.SelectedItem = _accounts.FirstOrDefault(x => x.Id == selectedAccountId) ?? _accounts.First();
        _switchingAccount = false;
        _services.WhatsApp.SetActiveAccount(CurrentAccountId);
        _connected = _services.WhatsApp.IsConnectedFor(CurrentAccountId);
        _leads.Clear();
        _leads.AddRange(await _services.Repository.GetLeadsAsync());
        var persisted = await _services.Repository.GetWhatsAppConversationsAsync(CurrentAccountId);
        var contacts = await _services.Repository.GetWhatsAppContactsAsync(CurrentAccountId);
        var selectedId = (ConversationList.SelectedItem as ConversationItem)?.Id;
        var refreshed = new Dictionary<string, ConversationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in persisted)
        {
            var conversation = new ConversationItem(saved.AccountId, saved.Phone, saved.DisplayName, "");
            conversation.DisplayName = saved.DisplayName; conversation.LastMessage = saved.LastMessage; conversation.LastAt = saved.LastMessageAt; conversation.Unread = saved.UnreadCount;
            refreshed[conversation.Id] = conversation;
        }
        foreach (var contact in contacts)
        {
            var itemId = string.IsNullOrWhiteSpace(contact.Phone) ? contact.Id : $"{contact.AccountId}:{contact.Phone}";
            if (!refreshed.TryGetValue(itemId, out var conversation))
            {
                conversation = new ConversationItem(contact.AccountId, contact.Phone, contact.DisplayName, contact.Jid) { LastMessage = "WhatsApp 联系人" };
                refreshed[itemId] = conversation;
            }
            else
            {
                conversation.Jid = contact.Jid;
                if (!string.IsNullOrWhiteSpace(contact.DisplayName) && (string.IsNullOrWhiteSpace(conversation.DisplayName) || conversation.DisplayName == $"+{conversation.Phone}")) conversation.DisplayName = contact.DisplayName;
            }
        }
        var ordered = refreshed.Values.OrderByDescending(x => x.LastAt).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _conversations.Clear(); foreach (var item in ordered) _conversations.Add(item);
        _persistedConversationCount = persisted.Count;
        _contactCount = contacts.Count;
        ConversationCountText.Text = $"{_persistedConversationCount} 会话 · {_contactCount} 联系人";
        ApplyConversationFilter();
        ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == selectedId);
        UpdateConnectionControls();
    }

    private async void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingAccount || AccountCombo.SelectedItem is not WhatsAppAccount) return;
        _services.WhatsApp.SetActiveAccount(CurrentAccountId); _conversations.Clear(); ConversationList.SelectedItem = null; ClearLead();
        await RefreshAsync();
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
            var account = new WhatsAppAccount { Id = $"personal_{Guid.NewGuid():N}"[..29], Name = $"个人号 {accounts.Count + 1}" };
            accounts.Add(account); await _services.Repository.SaveWhatsAppAccountsAsync(accounts);
            await RefreshAsync(); AccountCombo.SelectedItem = _accounts.First(x => x.Id == account.Id);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "添加账号失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SetConnectionText("正在启动本地桥…", false);
        ConnectButton.IsEnabled = false;
        try
        {
            await _services.WhatsApp.ConnectAsync(CurrentAccountId);
        }
        catch (Exception error)
        {
            SetConnectionText("连接失败", false);
            MessageBox.Show(error.Message, "WhatsApp 连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _services.Campaigns.PauseAccountAsync(CurrentAccountId, "用户手动断开 WhatsApp，活动 Campaign 已暂停。");
            await _services.WhatsApp.DisconnectAsync();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("退出后将删除本机登录会话，需要重新扫码；已经同步到 AI Sales OS 的联系人和消息仍会保留。是否继续？", "退出 WhatsApp 账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _services.Campaigns.PauseAccountAsync(CurrentAccountId, "用户退出 WhatsApp，活动 Campaign 已暂停。");
            await _services.WhatsApp.LogoutAsync(); _automaticSyncRequested.Remove(CurrentAccountId); _conversations.Clear(); ClearLead();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void WhatsApp_EventReceived(object? sender, WhatsAppBridgeEvent e) => Dispatcher.InvokeAsync(() => HandleBridgeEvent(e));

    private void WhatsAppSync_SynchronizationChanged(object? sender, WhatsAppSyncProgress progress) => Dispatcher.InvokeAsync(() =>
    {
        if (!progress.AccountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
        if (progress.State == "data")
        {
            ScheduleRefresh();
            return;
        }
        SyncStatusText.Text = progress.State switch
        {
            "syncing" => $"正在同步 {PhaseLabel(progress.Phase)}{(progress.Progress is null ? "" : $" {progress.Progress}%")}",
            "complete" => progress.Messages > 0 || progress.Contacts > 0 || progress.Chats > 0
                ? $"已同步 {progress.Chats} 会话 / {progress.Contacts} 联系人 / {progress.Messages} 消息"
                : _existingSession ? "已同步最新变更；首次历史需重新扫码获取" : "同步完成",
            "paused" => "已保存手机提供的历史，传输现已暂停",
            "failed" => $"同步失败：{progress.Error}",
            _ => SyncStatusText.Text
        };
        if (progress.State is "complete" or "paused") ScheduleRefresh();
    });

    private async void ScheduleRefresh()
    {
        if (_refreshScheduled) { _refreshAgain = true; return; }
        _refreshScheduled = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await Task.Delay(250);
                await RefreshAsync();
            }
            while (_refreshAgain);
        }
        finally { _refreshScheduled = false; }
    }

    private void HandleBridgeEvent(WhatsAppBridgeEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.AccountId) && !e.AccountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Name == "qr" && e.Data.TryGetProperty("dataUrl", out var dataUrl))
        {
            QrImage.Source = DecodeDataUrl(dataUrl.GetString() ?? "");
            QrHintText.Text = "请使用手机 WhatsApp → 设置 → 已关联设备扫描二维码。二维码会定期刷新。";
            QrPanel.Visibility = Visibility.Visible; MessageList.Visibility = Visibility.Collapsed;
            SetConnectionText("等待扫码", false);
            return;
        }
        if (e.Name == "connection")
        {
            var connection = e.Data.TryGetProperty("state", out var state) ? state.GetString() ?? "disconnected" : "disconnected";
            _connected = connection == "connected";
            _existingSession = Bool(e.Data, "existingSession");
            SetConnectionText(connection switch { "connected" => "已连接", "connecting" => "连接中", "logged_out" => "已退出", _ => "已断开" }, _connected);
            DisconnectButton.IsEnabled = _connected || connection == "connecting";
            LogoutButton.IsEnabled = _connected;
            ComposerBox.IsEnabled = _connected && ConversationList.SelectedItem is not null;
            SendButton.IsEnabled = ComposerBox.IsEnabled;
            SyncButton.IsEnabled = _connected;
            if (_connected)
            {
                QrPanel.Visibility = Visibility.Collapsed;
                MessageList.Visibility = Visibility.Visible;
                _ = SaveLinkedAccountAsync(e);
                SyncStatusText.Text = _existingSession ? "正在获取最新变更；旧历史缺失时需重新扫码一次" : "正在接收首次历史与联系人…";
                if (_existingSession && _automaticSyncRequested.Add(CurrentAccountId)) _ = StartSyncAsync(showError: false);
            }
            return;
        }
        if (e.Name != "message") return;
        var phone = Text(e.Data, "phone");
        if (string.IsNullOrWhiteSpace(phone)) return;
        var messageId = Text(e.Data, "id");
        var text = Text(e.Data, "text");
        var fromMe = Bool(e.Data, "fromMe");
        var displayName = Text(e.Data, "pushName");
        var timestamp = DateTimeOffset.TryParse(Text(e.Data, "timestamp"), out var parsed) ? parsed : DateTimeOffset.Now;
        var conversation = _conversations.FirstOrDefault(x => x.Phone == phone);
        if (conversation is null)
        {
            conversation = new ConversationItem(string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId, phone, string.IsNullOrWhiteSpace(displayName) ? $"+{phone}" : displayName, Text(e.Data, "jid"));
            _conversations.Insert(0, conversation);
        }
        if (!conversation.Messages.Any(x => x.Id == messageId)) conversation.Messages.Add(new MessageItem(messageId, text, timestamp, fromMe));
        conversation.LastMessage = string.IsNullOrWhiteSpace(text) ? "[媒体消息]" : text;
        conversation.LastAt = timestamp;
        if (!fromMe && ConversationList.SelectedItem != conversation) conversation.Unread++;
        _conversations.Remove(conversation); _conversations.Insert(0, conversation);
        ApplyConversationFilter();
        if (ConversationList.SelectedItem == conversation) ScrollMessages(conversation);
    }

    private async Task SaveLinkedAccountAsync(WhatsAppBridgeEvent e)
    {
        try
        {
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
            var account = accounts.FirstOrDefault(x => x.Id == CurrentAccountId); if (account is null) return;
            var user = Text(e.Data, "user"); var name = Text(e.Data, "name");
            var phone = new string(user.Split(':')[0].Where(char.IsDigit).ToArray());
            if (phone.Length > 0) account.LinkedPhone = "+" + phone;
            if (!string.IsNullOrWhiteSpace(name) && account.Name.StartsWith("个人号 ", StringComparison.Ordinal)) account.Name = name;
            await _services.Repository.SaveWhatsAppAccountsAsync(accounts);
        }
        catch { }
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation)
        {
            ChatTitleText.Text = "选择会话"; ChatNumberText.Text = "连接后会同步个人会话"; MessageList.ItemsSource = null; ClearLead(); return;
        }
        conversation.Unread = 0;
        if (!string.IsNullOrWhiteSpace(conversation.Phone)) await _services.Repository.MarkWhatsAppConversationReadAsync(conversation.Id);
        ChatTitleText.Text = conversation.DisplayName;
        ChatNumberText.Text = string.IsNullOrWhiteSpace(conversation.Phone) ? "WhatsApp 尚未提供该联系人的电话号码" : $"+{conversation.Phone}";
        var persistedMessages = string.IsNullOrWhiteSpace(conversation.Phone) ? [] : await _services.Repository.GetWhatsAppMessagesAsync(conversation.Id, 2000);
        foreach (var message in persistedMessages)
            if (!conversation.Messages.Any(x => x.Id == message.ProviderMessageId)) conversation.Messages.Add(new MessageItem(message.ProviderMessageId, message.Body, message.Timestamp, message.Direction == WhatsAppMessageDirection.Outgoing));
        MessageList.ItemsSource = conversation.Messages;
        if (_connected) { QrPanel.Visibility = Visibility.Collapsed; MessageList.Visibility = Visibility.Visible; }
        ComposerBox.IsEnabled = _connected && !string.IsNullOrWhiteSpace(conversation.Phone);
        SendButton.IsEnabled = ComposerBox.IsEnabled;
        SaveLeadButton.IsEnabled = !string.IsNullOrWhiteSpace(conversation.Phone);
        await LoadLeadAsync(conversation);
        ScrollMessages(conversation);
    }

    private async Task LoadLeadAsync(ConversationItem conversation)
    {
        _currentLead = string.IsNullOrWhiteSpace(conversation.Phone) ? null : FindLead(conversation.Phone);
        LeadLinkStateText.Text = _currentLead is null ? "未关联客户；保存时将创建" : $"已关联：{_currentLead.Grade} 级 · {Labels.Stage(_currentLead.Stage)}";
        NameBox.Text = _currentLead?.Name ?? conversation.DisplayName;
        CompanyBox.Text = _currentLead?.Company ?? "";
        OwnerBox.Text = _currentLead?.Owner ?? "";
        TagsBox.Text = _currentLead is null ? "" : string.Join(", ", _currentLead.Tags);
        OptInCheck.IsChecked = _currentLead?.WhatsAppOptIn == true;
        OptInSourceBox.Text = _currentLead?.WhatsAppOptInSource ?? "";
        OptedOutCheck.IsChecked = _currentLead?.OptedOut == true;
        NotesBox.Text = _currentLead?.LatestMessage ?? "";
        CustomFieldsBox.Text = _currentLead is null ? "" : string.Join(Environment.NewLine, _currentLead.CustomFields.Select(x => $"{x.Key}={x.Value}"));
        StageCombo.SelectedItem = (StageCombo.ItemsSource as IEnumerable<StageOption>)?.FirstOrDefault(x => x.Value == (_currentLead?.Stage ?? LeadStage.New));
        await Task.CompletedTask;
    }

    private async void SaveLead_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation) return;
        if (string.IsNullOrWhiteSpace(conversation.Phone)) { MessageBox.Show("WhatsApp 尚未向关联设备提供该联系人的电话号码，暂时不能创建客户。", "WhatsApp Inbox", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var lead = _currentLead ?? new Lead { PhoneE164 = "+" + conversation.Phone, PhoneValid = true, Source = "WhatsApp QR session" };
            lead.Name = NameBox.Text.Trim(); lead.Company = CompanyBox.Text.Trim(); lead.Owner = OwnerBox.Text.Trim(); lead.LatestMessage = NotesBox.Text.Trim();
            lead.Stage = (StageCombo.SelectedItem as StageOption)?.Value ?? LeadStage.New;
            lead.Tags = TagsBox.Text.Split([',','，',';','；','|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            var wasOptedIn = lead.WhatsAppOptIn;
            lead.WhatsAppOptIn = OptInCheck.IsChecked == true;
            lead.WhatsAppOptInSource = OptInSourceBox.Text.Trim();
            if (!wasOptedIn && lead.WhatsAppOptIn) lead.WhatsAppOptInAt = DateTimeOffset.Now;
            if (!lead.WhatsAppOptIn) lead.WhatsAppOptInAt = null;
            lead.OptedOut = OptedOutCheck.IsChecked == true;
            lead.CustomFields = ParseCustomFields(CustomFieldsBox.Text);
            await _services.Repository.UpsertLeadAsync(lead);
            await _services.Repository.LogEventAsync("whatsapp_customer_sidebar_saved", lead.Id, null, "客户侧栏人工保存");
            _currentLead = lead;
            if (!_leads.Any(x => x.Id == lead.Id)) _leads.Add(lead);
            conversation.DisplayName = lead.DisplayName;
            LeadLinkStateText.Text = $"已关联：{lead.Grade} 级 · {Labels.Stage(lead.Stage)}";
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("客户资料已同步到 AI Sales OS。", "WhatsApp Inbox", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation || string.IsNullOrWhiteSpace(ComposerBox.Text)) return;
        if (string.IsNullOrWhiteSpace(conversation.Phone)) { MessageBox.Show("该联系人的电话号码尚未同步，暂时不能发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_currentLead?.OptedOut == true) { MessageBox.Show("客户已退订，禁止发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var text = ComposerBox.Text.Trim(); SendButton.IsEnabled = false;
        try
        {
            var result = await _services.WhatsApp.SendTextAsync(conversation.Phone, text);
            var id = result.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            if (!conversation.Messages.Any(x => x.Id == id)) conversation.Messages.Add(new MessageItem(id, text, DateTimeOffset.Now, true));
            conversation.LastMessage = text; conversation.LastAt = DateTimeOffset.Now; ComposerBox.Clear(); ScrollMessages(conversation);
            if (_currentLead is not null)
            {
                _currentLead.LastContactAt = DateTimeOffset.Now;
                await _services.Repository.UpsertLeadAsync(_currentLead);
                await _services.Repository.LogEventAsync("whatsapp_message_sent", _currentLead.Id, null, $"message_id={id}");
            }
        }
        catch (Exception error) { MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SendButton.IsEnabled = _connected; }
    }

    private void ConversationSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyConversationFilter();

    private void ApplyConversationFilter()
    {
        var query = ConversationSearchBox.Text.Trim();
        ConversationList.ItemsSource = query.Length == 0
            ? _conversations
            : _conversations.Where(x => x.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Jid.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        ConversationCountText.Text = query.Length == 0 ? $"{_persistedConversationCount} 会话 · {_contactCount} 联系人" : $"找到 {ConversationList.Items.Count} 个";
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await StartSyncAsync(showError: true);

    private async Task StartSyncAsync(bool showError)
    {
        if (!_connected) return;
        SyncButton.IsEnabled = false;
        SyncStatusText.Text = "正在同步联系人、会话和最新变更…";
        try { await _services.WhatsApp.SyncNowAsync(); }
        catch (Exception error)
        {
            SyncStatusText.Text = "同步启动失败";
            if (showError) MessageBox.Show(error.Message, "WhatsApp 同步失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SyncButton.IsEnabled = _connected; }
    }

    private Lead? FindLead(string phone)
    {
        var digits = Digits(phone);
        return _leads.FirstOrDefault(x => Digits(x.PhoneE164) == digits);
    }

    private static Dictionary<string, string> ParseCustomFields(string text)
    {
        var output = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var line in text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim(); var value = line[(separator + 1)..].Trim();
            if (key.Length > 0) output[key] = value;
        }
        return output;
    }

    private void ClearLead()
    {
        _currentLead = null; LeadLinkStateText.Text = "选择会话后关联客户"; NameBox.Clear(); CompanyBox.Clear(); OwnerBox.Clear(); TagsBox.Clear(); OptInCheck.IsChecked = false; OptInSourceBox.Clear(); OptedOutCheck.IsChecked = false; NotesBox.Clear(); CustomFieldsBox.Clear(); SaveLeadButton.IsEnabled = false;
    }

    private void ScrollMessages(ConversationItem conversation)
    {
        MessageList.ItemsSource = conversation.Messages;
        if (conversation.Messages.LastOrDefault() is { } last) MessageList.ScrollIntoView(last);
    }

    private void SetConnectionText(string text, bool connected)
    {
        ConnectionStateText.Text = text;
        ConnectionStateText.Foreground = new SolidColorBrush(connected ? Color.FromRgb(15,112,79) : Color.FromRgb(138,97,16));
    }

    private void UpdateConnectionControls()
    {
        var state = _services.WhatsApp.ConnectionStateFor(CurrentAccountId);
        SetConnectionText(state switch { "connected" => "已连接", "connecting" => "连接中", "logged_out" => "已退出", _ => "未连接" }, state == "connected");
        DisconnectButton.IsEnabled = state is "connected" or "connecting"; LogoutButton.IsEnabled = state == "connected";
        SyncButton.IsEnabled = state == "connected";
    }

    private static BitmapImage? DecodeDataUrl(string dataUrl)
    {
        var marker = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var bytes = Convert.FromBase64String(dataUrl[(marker + 7)..]);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private static string PhaseLabel(string phase) => phase switch
    {
        "initial_bootstrap" => "基础会话",
        "full" => "完整历史",
        "recent" => "近期历史",
        "push_name" => "联系人名称",
        "non_blocking_data" => "联系人资料",
        "app_state" => "联系人与会话变更",
        _ => "WhatsApp 数据"
    };
    private sealed record StageOption(string Label, LeadStage Value);

    private sealed class ConversationItem(string accountId, string phone, string displayName, string jid) : INotifyPropertyChanged
    {
        private string _displayName = displayName; private string _lastMessage = ""; private DateTimeOffset _lastAt; private int _unread;
        public string AccountId { get; } = accountId; public string Phone { get; } = phone; public string Jid { get; set; } = jid; public string Id => string.IsNullOrWhiteSpace(Phone) ? $"{AccountId}:{Jid}" : $"{AccountId}:{Phone}"; public ObservableCollection<MessageItem> Messages { get; } = [];
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }
        public DateTimeOffset LastAt { get => _lastAt; set { if (Set(ref _lastAt, value)) OnPropertyChanged(nameof(LastTimeLabel)); } }
        public string LastTimeLabel => LastAt == default ? "" : LastAt.LocalDateTime.ToString("MM-dd HH:mm");
        public int Unread { get => _unread; set { if (Set(ref _unread, value)) OnPropertyChanged(nameof(UnreadVisibility)); } }
        public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(property); return true; }
        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed record MessageItem(string Id, string Text, DateTimeOffset Timestamp, bool FromMe)
    {
        public string TimeLabel => Timestamp.LocalDateTime.ToString("MM-dd HH:mm");
        public HorizontalAlignment Alignment => FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBrush => new SolidColorBrush(FromMe ? Color.FromRgb(220,248,233) : Colors.White);
    }
}
