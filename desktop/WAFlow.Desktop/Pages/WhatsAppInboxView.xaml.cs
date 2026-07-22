using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;
using WAFlow.Desktop.Windows;

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
    private bool _initialLeadLinkCompleted;
    private bool _sending;
    private bool _aiAssisting;
    private string _attachmentPath = "";
    private MessageItem? _replyingTo;
    private string _composerConversationId = "";
    private int _persistedConversationCount;
    private int _contactCount;
    private readonly HashSet<string> _automaticSyncRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedIpChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _ipTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _checkingIp;

    private string CurrentAccountId => (AccountCombo.SelectedItem as WhatsAppAccount)?.Id ?? "primary";

    public event EventHandler? DataChanged;

    public WhatsAppInboxView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        // TextChanged may fire while InitializeComponent is still connecting named
        // controls. Wire it only after the complete visual tree is available.
        ComposerBox.TextChanged += ComposerBox_TextChanged;
        ConversationList.ItemsSource = _conversations;
        AccountCombo.ItemsSource = _accounts;
        StageCombo.ItemsSource = Enum.GetValues<LeadStage>().Select(x => new StageOption(Labels.Stage(x), x)).ToList();
        _services.WhatsApp.EventReceived += WhatsApp_EventReceived;
        _services.WhatsAppSync.MessageSynchronized += (_, _) => Dispatcher.InvokeAsync(() => DataChanged?.Invoke(this, EventArgs.Empty));
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSync_SynchronizationChanged;
        _ipTimer.Tick += async (_, _) => await RefreshPublicIpAsync();
        Loaded += async (_, _) => { _ipTimer.Start(); await RefreshPublicIpAsync(); };
        Unloaded += (_, _) => _ipTimer.Stop();
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
        if (!_initialLeadLinkCompleted)
        {
            await _services.Repository.SynchronizeLeadConnectionsFromInboxAsync(_leads);
            _leads.Clear();
            _leads.AddRange(await _services.Repository.GetLeadsAsync());
            _initialLeadLinkCompleted = true;
        }
        var persisted = await _services.Repository.GetWhatsAppConversationsAsync(CurrentAccountId);
        var contacts = await _services.Repository.GetWhatsAppContactsAsync(CurrentAccountId);
        var selectedId = (ConversationList.SelectedItem as ConversationItem)?.Id;
        var refreshed = new Dictionary<string, ConversationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in persisted)
        {
            var conversation = new ConversationItem(saved.AccountId, saved.Phone, saved.DisplayName, "");
            var linkedLead = FindLead(saved.Phone);
            conversation.LeadId = linkedLead?.Id ?? saved.LeadId;
            conversation.DisplayName = linkedLead is not null && !string.IsNullOrWhiteSpace(linkedLead.DisplayName) ? linkedLead.DisplayName : saved.DisplayName;
            conversation.LastMessage = saved.LastMessage; conversation.LastAt = saved.LastMessageAt; conversation.Unread = saved.UnreadCount;
            conversation.IsPinned = saved.IsPinned; conversation.PinnedAt = saved.PinnedAt;
            refreshed[conversation.Id] = conversation;
        }
        foreach (var contact in contacts)
        {
            var itemId = string.IsNullOrWhiteSpace(contact.Phone) ? contact.Id : $"{contact.AccountId}:{contact.Phone}";
            var linkedLead = string.IsNullOrWhiteSpace(contact.Phone) ? null : FindLead(contact.Phone);
            var contactName = linkedLead?.DisplayName;
            if (string.IsNullOrWhiteSpace(contactName)) contactName = BestContactName(contact);
            if (!refreshed.TryGetValue(itemId, out var conversation))
            {
                conversation = new ConversationItem(contact.AccountId, contact.Phone, contactName, contact.Jid) { LastMessage = "WhatsApp 联系人", LeadId = linkedLead?.Id ?? "" };
                refreshed[itemId] = conversation;
            }
            else
            {
                conversation.Jid = contact.Jid;
                if (linkedLead is not null) { conversation.LeadId = linkedLead.Id; conversation.DisplayName = linkedLead.DisplayName; }
                else if (!string.IsNullOrWhiteSpace(contactName)) conversation.DisplayName = contactName;
            }
        }
        var ordered = OrderConversations(refreshed.Values).ToList();
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
        await RefreshPublicIpAsync();
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

    private async void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.WhatsApp.IsConnectedFor(CurrentAccountId))
        {
            MessageBox.Show("请先连接当前 WhatsApp 账号。", "建立 WhatsApp 群组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var contacts = await _services.Repository.GetWhatsAppContactsAsync(CurrentAccountId);
            var candidates = contacts
                .Where(contact => PhoneNormalizer.Normalize(contact.Phone, null).Valid)
                .Select(contact => new CreateWhatsAppGroupWindow.GroupMemberCandidate(
                    string.IsNullOrWhiteSpace(contact.DisplayName) ? $"+{new string(contact.Phone.Where(char.IsDigit).ToArray())}" : contact.DisplayName,
                    contact.Phone,
                    "WhatsApp 联系人"))
                .ToList();
            var dialog = new CreateWhatsAppGroupWindow(candidates) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Request is null) return;

            CreateGroupButton.IsEnabled = false;
            var result = await _services.WhatsApp.CreateGroupAsync(CurrentAccountId, dialog.Request);
            await _services.Repository.LogEventAsync("whatsapp_group_created", null, null,
                $"account={CurrentAccountId};group={result.GroupJid};subject={result.Subject};participants={result.ParticipantCount}");
            try { await _services.WhatsApp.SyncNowAsync(); } catch { }
            MessageBox.Show($"群组“{result.Subject}”已建立，并已同步到手机 WhatsApp。\n\n成员：{result.ParticipantCount:N0} 位\n群组 ID：{result.GroupJid}", "WhatsApp 建群成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (WhatsAppBridgeException error)
        {
            var message = error.Code switch
            {
                "invalid_group_subject" => "群名称无效，请控制在 1–100 个字符。",
                "invalid_group_participants" or "invalid_group_participant_count" => "群成员无效，请重新选择具有国际区号的 WhatsApp 联系人。",
                "whatsapp_not_connected" => "WhatsApp 连接已经断开，请重新连接后再建群。",
                _ => error.Message
            };
            MessageBox.Show(message, "WhatsApp 建群失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "WhatsApp 建群失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { UpdateConnectionControls(); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SetConnectionText("正在启动本地桥…", false);
        ConnectButton.IsEnabled = false;
        _ = RefreshPublicIpAsync();
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

    private async Task RefreshPublicIpAsync()
    {
        if (_checkingIp) return;
        var accountId = CurrentAccountId;
        _checkingIp = true;
        try
        {
            var result = await _services.PublicIp.CheckAsync(accountId);
            if (!accountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                IpStatusText.Text = $"公网 IP：{result.Error} · 60 秒后重试";
                IpStatusDot.Foreground = (Brush)FindResource("Muted");
                IpStatusBorder.Background = new SolidColorBrush(Color.FromRgb(237, 244, 240));
                return;
            }
            var state = result.State;
            var location = string.IsNullOrWhiteSpace(state.LocationLabel) ? "位置未知" : state.LocationLabel;
            var recentlyChanged = !string.IsNullOrWhiteSpace(state.PreviousIp)
                && !state.PreviousIp.Equals(state.CurrentIp, StringComparison.OrdinalIgnoreCase)
                && state.ChangedAt >= DateTimeOffset.Now.AddHours(-24);
            IpStatusText.Text = recentlyChanged
                ? $"公网 IP 已变化：{state.PreviousIp} → {state.CurrentIp} · {location} · 每 60 秒监测"
                : $"公网 IP：{state.CurrentIp} · {location} · 每 60 秒监测";
            IpStatusDot.Foreground = new SolidColorBrush(recentlyChanged ? Color.FromRgb(183, 57, 57) : Color.FromRgb(15, 143, 104));
            IpStatusText.Foreground = new SolidColorBrush(recentlyChanged ? Color.FromRgb(168, 58, 47) : Color.FromRgb(69, 92, 81));
            IpStatusBorder.Background = (Brush)FindResource(recentlyChanged ? "DangerSoft" : "SuccessSoft");
            if (result.Changed)
            {
                var warningKey = $"{accountId}|{state.PreviousIp}|{state.CurrentIp}|{state.ChangedAt:O}";
                if (_warnedIpChanges.Add(warningKey))
                    MessageBox.Show($"检测到本机公网出口 IP 发生变化：\n{state.PreviousIp} → {state.CurrentIp}\n当前位置：{location}\n\nIP 变化不等于封号，但频繁跨地区切换、VPN/代理跳变可能增加异常登录风险。建议先暂停自动化并确认网络环境。", "WhatsApp 网络风险提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _checkingIp = false; }
    }

    private void UpdateVisibleMessageStatus(JsonElement data)
    {
        var id = Text(data, "id");
        if (string.IsNullOrWhiteSpace(id)) return;
        var numeric = data.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsed) ? parsed : -1;
        if (numeric < 0) return;
        var status = StatusFromNumeric(numeric);
        foreach (var conversation in _conversations)
        {
            var message = conversation.Messages.FirstOrDefault(item => item.Id == id);
            if (message is null) continue;
            message.UpdateStatus(status, ParseTime(data, "statusAt") ?? DateTimeOffset.Now, ParseTime(data, "deliveredAt"), ParseTime(data, "readAt"), Text(data, "failureReason"));
            break;
        }
    }

    private void UpdateVisibleMessageRevocation(JsonElement data)
    {
        var id = Text(data, "revokedMessageId");
        if (string.IsNullOrWhiteSpace(id)) return;
        var revokedAt = ParseTime(data, "timestamp") ?? DateTimeOffset.Now;
        foreach (var conversation in _conversations)
        {
            var message = conversation.Messages.FirstOrDefault(item => item.Id == id);
            if (message is null) continue;
            message.MarkRevoked(revokedAt);
            if (ReferenceEquals(_replyingTo, message)) ClearReply();
            if (ReferenceEquals(conversation.Messages.LastOrDefault(), message))
                conversation.LastMessage = message.FromMe ? "你撤回了一条消息" : "对方撤回了一条消息";
            break;
        }
    }

    private void HandleBridgeEvent(WhatsAppBridgeEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.AccountId) && !e.AccountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Name == "auth_recovery")
        {
            SetConnectionText("请重新扫码", false);
            QrHintText.Text = "旧登录凭据已损坏或密钥不匹配，软件已安全备份旧会话。请扫描新二维码重新登录。";
            MessageBox.Show("旧 WhatsApp 登录凭据无法解密，已安全备份并创建新会话。接下来请重新扫码；客户和历史消息数据库不会被删除。", "WhatsApp 会话已恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
            UpdateComposerState();
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
        if (e.Name == "message_status")
        {
            UpdateVisibleMessageStatus(e.Data);
            return;
        }
        if (e.Name == "message_revoked")
        {
            UpdateVisibleMessageRevocation(e.Data);
            return;
        }
        if (e.Name != "message") return;
        var phone = Text(e.Data, "phone");
        if (string.IsNullOrWhiteSpace(phone)) return;
        var messageId = Text(e.Data, "id");
        var text = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "text"));
        var fromMe = Bool(e.Data, "fromMe");
        var displayName = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "pushName"));
        var kind = Text(e.Data, "kind");
        var fileName = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "fileName"));
        var mimeType = Text(e.Data, "mimeType");
        var mediaPath = Text(e.Data, "mediaPath");
        var mediaDownloadError = Text(e.Data, "mediaDownloadError");
        var timestamp = DateTimeOffset.TryParse(Text(e.Data, "timestamp"), out var parsed) ? parsed : DateTimeOffset.Now;
        var conversation = _conversations.FirstOrDefault(x => x.Phone == phone);
        if (conversation is null)
        {
            var linkedLead = FindLead(phone);
            var preferredName = linkedLead?.DisplayName;
            if (string.IsNullOrWhiteSpace(preferredName)) preferredName = string.IsNullOrWhiteSpace(displayName) ? $"+{phone}" : displayName;
            conversation = new ConversationItem(string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId, phone, preferredName, Text(e.Data, "jid")) { LeadId = linkedLead?.Id ?? "" };
            _conversations.Insert(0, conversation);
        }
        if (!conversation.Messages.Any(x => x.Id == messageId))
            conversation.Messages.Add(new MessageItem(messageId, text, timestamp, fromMe, kind, fileName, mimeType, mediaPath, mediaDownloadError, ParseMessageStatus(e.Data, fromMe), ParseTime(e.Data, "statusAt"), ParseTime(e.Data, "deliveredAt"), ParseTime(e.Data, "readAt"), Text(e.Data, "failureReason"), Text(e.Data, "quotedMessageId"), WhatsAppTextEncodingRepair.Repair(Text(e.Data, "quotedText")), Bool(e.Data, "quotedFromMe"), Bool(e.Data, "isRevoked"), ParseTime(e.Data, "revokedAt")));
        conversation.LastMessage = MessagePreview(text, kind, fileName);
        conversation.LastAt = timestamp;
        if (!fromMe && ConversationList.SelectedItem != conversation) conversation.Unread++;
        ReorderConversations(conversation);
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
            _composerConversationId = "";
            ClearAttachment();
            ClearReply();
            ChatTitleText.Text = "选择会话"; ChatNumberText.Text = "连接后会同步个人会话"; MessageList.ItemsSource = null; ClearLead(); return;
        }
        if (!_composerConversationId.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase))
        {
            _composerConversationId = conversation.Id;
            ClearAttachment();
            ClearReply();
        }
        conversation.Unread = 0;
        if (!string.IsNullOrWhiteSpace(conversation.Phone)) await _services.Repository.MarkWhatsAppConversationReadAsync(conversation.Id);
        ChatTitleText.Text = conversation.DisplayName;
        ChatNumberText.Text = string.IsNullOrWhiteSpace(conversation.Phone) ? "WhatsApp 尚未提供该联系人的电话号码" : $"+{conversation.Phone}";
        var persistedMessages = string.IsNullOrWhiteSpace(conversation.Phone) ? [] : await _services.Repository.GetWhatsAppMessagesAsync(conversation.Id, 2000);
        foreach (var message in persistedMessages)
            if (!conversation.Messages.Any(x => x.Id == message.ProviderMessageId))
                conversation.Messages.Add(new MessageItem(message.ProviderMessageId, message.Body, message.Timestamp, message.Direction == WhatsAppMessageDirection.Outgoing, message.Kind, message.FileName, message.MimeType, message.MediaPath, message.MediaDownloadError, message.Status, message.StatusUpdatedAt, message.DeliveredAt, message.ReadAt, message.FailureReason, message.QuotedMessageId, message.QuotedText, message.QuotedFromMe, message.IsRevoked, message.RevokedAt));
        MessageList.ItemsSource = conversation.Messages;
        if (_connected) { QrPanel.Visibility = Visibility.Collapsed; MessageList.Visibility = Visibility.Visible; }
        SaveLeadButton.IsEnabled = !string.IsNullOrWhiteSpace(conversation.Phone);
        await LoadLeadAsync(conversation);
        UpdateComposerState();
        ScrollMessages(conversation);
    }

    private async Task LoadLeadAsync(ConversationItem conversation)
    {
        _currentLead = string.IsNullOrWhiteSpace(conversation.Phone) ? null : FindLead(conversation.Phone);
        LeadLinkStateText.Text = _currentLead is null ? "未关联客户；保存时将创建" : $"已关联：{_currentLead.Grade} 级 · {Labels.Stage(_currentLead.Stage)}";
        NameBox.Text = _currentLead?.Name ?? "";
        CompanyBox.Text = _currentLead?.Company ?? "";
        OwnerBox.Text = _currentLead?.Owner ?? "";
        TagsBox.Text = _currentLead is null ? "" : string.Join(", ", _currentLead.Tags);
        OptInCheck.IsChecked = _currentLead?.WhatsAppOptIn == true;
        OptInSourceBox.Text = _currentLead?.WhatsAppOptInSource ?? "";
        OptedOutCheck.IsChecked = _currentLead?.OptedOut == true;
        NotesBox.Text = _currentLead?.LatestMessage ?? "";
        CustomFieldsBox.Text = _currentLead is null ? "" : string.Join(Environment.NewLine, _currentLead.CustomFields.Select(x => $"{x.Key}={x.Value}"));
        StageCombo.SelectedItem = (StageCombo.ItemsSource as IEnumerable<StageOption>)?.FirstOrDefault(x => x.Value == (_currentLead?.Stage ?? LeadStage.New));
        UpdateLeadIntelligenceSummary(_currentLead);
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
            else
            {
                var index = _leads.FindIndex(x => x.Id == lead.Id);
                if (index >= 0) _leads[index] = lead;
            }
            await _services.Repository.SynchronizeLeadConnectionsFromInboxAsync([lead]);
            var latestReply = (await _services.Repository.GetWhatsAppMessagesForLeadAsync(lead, 40))
                .LastOrDefault(message => message.Direction == WhatsAppMessageDirection.Incoming && !string.IsNullOrWhiteSpace(message.Body));
            if (latestReply is not null && (!lead.AiScoreApplied || lead.LastAnalyzedAt is null || latestReply.Timestamp > lead.LastAnalyzedAt))
                await _services.LeadAutomation.QueueLeadForReplyAsync(latestReply);
            conversation.LeadId = lead.Id;
            conversation.DisplayName = lead.DisplayName;
            LeadLinkStateText.Text = $"已关联：{lead.Grade} 级 · {Labels.Stage(lead.Stage)}";
            UpdateLeadIntelligenceSummary(lead);
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("客户资料已同步到 AI Sales OS。", "WhatsApp Inbox", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void AiAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (_aiAssisting || ConversationList.SelectedItem is not ConversationItem conversation) return;
        if (string.IsNullOrWhiteSpace(conversation.Phone))
        {
            MessageBox.Show("WhatsApp 尚未提供该联系人的电话号码，AI 暂时不能安全关联客户资料。", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_services.DeepSeek.HasApiKey())
        {
            MessageBox.Show("请先点击右上角“API 对接”，填写 API Key 并选择工作模型。", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _aiAssisting = true;
        AiAssistantButton.Content = "分析中";
        UpdateComposerState();
        try
        {
            var result = await _services.ConversationAssistant.AnalyzeAsync(conversation.Id, _currentLead);
            var canSend = _connected && _currentLead?.OptedOut != true;
            var dialog = new AiConversationAssistantWindow(result, canSend) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Action == ConversationAssistantAction.Cancel) return;

            result.ReplyText = dialog.ReplyText;
            ComposerBox.Text = dialog.ReplyText;
            ComposerBox.CaretIndex = ComposerBox.Text.Length;
            if (dialog.Action == ConversationAssistantAction.FillComposer)
            {
                ComposerBox.Focus();
                return;
            }

            var lead = await _services.ConversationAssistant.ApplyAsync(
                _currentLead,
                conversation.Phone,
                conversation.DisplayName,
                result,
                dialog.SelectedUpdates);
            _currentLead = lead;
            var existingIndex = _leads.FindIndex(item => item.Id == lead.Id);
            if (existingIndex >= 0) _leads[existingIndex] = lead; else _leads.Add(lead);
            conversation.LeadId = lead.Id;
            conversation.DisplayName = lead.DisplayName;
            await _services.Repository.SynchronizeLeadConnectionsFromInboxAsync([lead]);
            await LoadLeadAsync(conversation);
            var latestReply = (await _services.Repository.GetWhatsAppMessagesForLeadAsync(lead, 80))
                .LastOrDefault(message => message.Direction == WhatsAppMessageDirection.Incoming && !string.IsNullOrWhiteSpace(message.Body));
            if (latestReply is not null)
                await _services.LeadAutomation.QueueLeadForReplyAsync(latestReply);
            DataChanged?.Invoke(this, EventArgs.Empty);
            var sent = await SendCurrentAsync("ai_conversation_assistant");
            if (!sent)
                MessageBox.Show("AI 提取的客户需求已经同步，但 WhatsApp 回复尚未得到成功回执。请先同步会话确认状态，不要重复发送。", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (DeepSeekException error)
        {
            MessageBox.Show($"{error.Message}\n\n错误类型：{error.Code}", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _aiAssisting = false;
            AiAssistantButton.Content = "✦ AI";
            UpdateComposerState();
        }
    }

    private void ReplyMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not MessageItem { FromMe: false, IsRevoked: false } message) return;
        _replyingTo = message;
        ReplyText.Text = message.DisplayText;
        ReplyPanel.Visibility = Visibility.Visible;
        ComposerBox.Focus();
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
    }

    private void ClearReply_Click(object sender, RoutedEventArgs e) => ClearReply();

    private void ClearReply()
    {
        _replyingTo = null;
        ReplyText.Text = "";
        ReplyPanel.Visibility = Visibility.Collapsed;
    }

    private async void RevokeMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not MessageItem message || !message.CanRevoke) return;
        if (ConversationList.SelectedItem is not ConversationItem conversation || !conversation.Messages.Contains(message)) return;
        if (!_connected)
        {
            MessageBox.Show("WhatsApp 当前未连接，无法撤回消息。", "撤回消息", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmed = MessageBox.Show(
            "确定要从自己和对方的设备上撤回这条消息吗？\n\nWhatsApp 可能因消息时限或设备状态拒绝撤回，软件只会在收到成功回执后更新本地状态。",
            "从双方设备撤回",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes) return;

        message.SetRevoking(true);
        try
        {
            await _services.WhatsApp.RevokeMessageAsync(conversation.AccountId, conversation.Phone, message.Id);
            var revokedAt = DateTimeOffset.Now;
            message.MarkRevoked(revokedAt);
            await _services.Repository.MarkWhatsAppMessageRevokedAsync(conversation.AccountId, message.Id, revokedAt);
            if (ReferenceEquals(_replyingTo, message)) ClearReply();
            if (ReferenceEquals(conversation.Messages.LastOrDefault(), message))
            {
                conversation.LastMessage = "你撤回了一条消息";
                var storedConversation = await _services.Repository.GetWhatsAppConversationAsync(conversation.AccountId, conversation.Phone);
                if (storedConversation is not null)
                {
                    storedConversation.LastMessage = conversation.LastMessage;
                    await _services.Repository.UpsertWhatsAppConversationAsync(storedConversation);
                }
            }
        }
        catch (TimeoutException)
        {
            MessageBox.Show("撤回请求已发出，但本机没有及时收到 WhatsApp 回执。消息暂不标记为已撤回，请先同步会话确认实际状态。", "撤回状态待确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show($"WhatsApp 未确认撤回：{error.Message}\n\n消息仍保留在本地；请检查是否超过 WhatsApp 允许的撤回时限。", "撤回失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { message.SetRevoking(false); }
    }

    private async void ComposerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var start = ComposerBox.SelectionStart;
            var length = ComposerBox.SelectionLength;
            ComposerBox.Text = ComposerBox.Text.Remove(start, length).Insert(start, Environment.NewLine);
            ComposerBox.CaretIndex = start + Environment.NewLine.Length;
            return;
        }
        await SendCurrentAsync();
    }

    private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposerState();

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要通过 WhatsApp 发送的文件",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "WhatsApp 支持的文件|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.mp4;*.3gp;*.mov;*.mp3;*.m4a;*.ogg;*.opus;*.wav;*.aac;*.pdf;*.txt;*.csv;*.json;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.zip;*.rar;*.7z|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var file = new FileInfo(dialog.FileName);
        if (file.Length <= 0 || file.Length > 100L * 1024 * 1024)
        {
            MessageBox.Show("附件大小必须大于 0 且不超过 100MB。", "WhatsApp 附件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _attachmentPath = file.FullName;
        AttachmentText.Text = $"{file.Name} · {file.Length / 1024d / 1024d:N1} MB";
        AttachmentPanel.Visibility = Visibility.Visible;
        UpdateComposerState();
    }

    private void ClearAttachment_Click(object sender, RoutedEventArgs e) => ClearAttachment();

    private void ClearAttachment()
    {
        _attachmentPath = "";
        AttachmentText.Text = "";
        AttachmentPanel.Visibility = Visibility.Collapsed;
        UpdateComposerState();
    }

    private async Task<bool> SendCurrentAsync(string origin = "human")
    {
        if (_sending || ConversationList.SelectedItem is not ConversationItem conversation) return false;
        var text = ComposerBox.Text.Trim();
        var attachmentPath = _attachmentPath;
        var reply = _replyingTo;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachmentPath)) return false;
        if (string.IsNullOrWhiteSpace(conversation.Phone)) { MessageBox.Show("该联系人的电话号码尚未同步，暂时不能发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (_currentLead?.OptedOut == true) { MessageBox.Show("客户已退订，禁止发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

        _sending = true;
        var accepted = false;
        var pendingId = $"local-{Guid.NewGuid():N}";
        var pendingTimestamp = DateTimeOffset.Now;
        var pendingKind = string.IsNullOrWhiteSpace(attachmentPath) ? "text" : KindFromFileName(attachmentPath);
        var pendingFileName = string.IsNullOrWhiteSpace(attachmentPath) ? "" : Path.GetFileName(attachmentPath);
        var pendingMessage = new MessageItem(pendingId, text, pendingTimestamp, true, pendingKind, pendingFileName, "", attachmentPath, "", WhatsAppMessageStatus.Pending, pendingTimestamp, null, null, "", reply?.Id ?? "", reply?.DisplayText ?? "", reply?.FromMe ?? false);
        conversation.Messages.Add(pendingMessage);
        conversation.LastMessage = MessagePreview(text, pendingKind, pendingFileName);
        conversation.LastAt = pendingTimestamp;
        ReorderConversations(conversation);
        ScrollMessages(conversation);
        UpdateComposerState();
        try
        {
            JsonElement result;
            if (string.IsNullOrWhiteSpace(attachmentPath))
                result = reply is null
                    ? await _services.WhatsApp.SendTextAsync(conversation.AccountId, conversation.Phone, text)
                    : await _services.WhatsApp.SendReplyTextAsync(conversation.AccountId, conversation.Phone, text, reply.Id, reply.DisplayText, reply.FromMe);
            else
                result = reply is null
                    ? await _services.WhatsApp.SendMediaAsync(conversation.AccountId, conversation.Phone, attachmentPath, text)
                    : await _services.WhatsApp.SendReplyMediaAsync(conversation.AccountId, conversation.Phone, attachmentPath, text, reply.Id, reply.DisplayText, reply.FromMe);
            var id = result.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var timestamp = result.TryGetProperty("timestamp", out var timestampElement) && DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp) ? parsedTimestamp : DateTimeOffset.Now;
            var kind = result.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? "text" : "text";
            var fileName = result.TryGetProperty("fileName", out var fileNameElement) ? fileNameElement.GetString() ?? "" : "";
            var mimeType = result.TryGetProperty("mimeType", out var mimeElement) ? mimeElement.GetString() ?? "" : "";
            var numericStatus = result.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsedStatus) ? parsedStatus : 1;
            var status = StatusFromNumeric(numericStatus);
            var existing = conversation.Messages.FirstOrDefault(item => item.Id == id && !ReferenceEquals(item, pendingMessage));
            if (existing is not null)
            {
                conversation.Messages.Remove(pendingMessage);
                pendingMessage = existing;
            }
            else pendingMessage.UpdateTransport(id, timestamp, kind, fileName);
            pendingMessage.UpdateStatus(status, DateTimeOffset.Now, status is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null, status == WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null, "");
            conversation.LastMessage = MessagePreview(text, kind, fileName);
            conversation.LastAt = timestamp;
            ComposerBox.Clear();
            ClearAttachment();
            ClearReply();

            var storedConversation = await _services.Repository.GetWhatsAppConversationAsync(conversation.AccountId, conversation.Phone) ?? new WhatsAppConversation
            {
                Id = conversation.Id, AccountId = conversation.AccountId, Phone = conversation.Phone
            };
            storedConversation.LeadId = _currentLead?.Id ?? conversation.LeadId;
            storedConversation.DisplayName = conversation.DisplayName;
            storedConversation.LastMessage = conversation.LastMessage;
            storedConversation.LastMessageAt = timestamp;
            storedConversation.IsPinned = conversation.IsPinned;
            storedConversation.PinnedAt = conversation.PinnedAt;
            await _services.Repository.UpsertWhatsAppConversationAsync(storedConversation);
            var storedMessage = new WhatsAppMessage
            {
                Id = $"{conversation.AccountId}:{id}", ProviderMessageId = id, AccountId = conversation.AccountId,
                ConversationId = conversation.Id, LeadId = _currentLead?.Id ?? conversation.LeadId, Phone = conversation.Phone,
                Direction = WhatsAppMessageDirection.Outgoing, Status = status, Kind = kind,
                Body = text, FileName = fileName, MimeType = mimeType, MediaPath = attachmentPath, Timestamp = timestamp,
                QuotedMessageId = reply?.Id ?? "", QuotedText = reply?.DisplayText ?? "", QuotedFromMe = reply?.FromMe ?? false,
                StatusUpdatedAt = DateTimeOffset.Now,
                DeliveredAt = status is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null,
                ReadAt = status == WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null,
                Source = origin == "ai_conversation_assistant" ? "desktop_ai" : "desktop"
            };
            await _services.Repository.UpsertWhatsAppMessageAsync(storedMessage);
            ReorderConversations(conversation);
            ScrollMessages(conversation);
            if (_currentLead is not null)
            {
                LeadConnectionStatus.ApplyFromMessage(_currentLead, storedMessage);
                await _services.Repository.UpsertLeadAsync(_currentLead);
                await _services.Repository.LogEventAsync("whatsapp_message_sent", _currentLead.Id, null, $"message_id={id}; kind={kind}; origin={origin}");
            }
            accepted = true;
        }
        catch (TimeoutException)
        {
            pendingMessage.UpdateStatus(WhatsAppMessageStatus.Pending, DateTimeOffset.Now, null, null, "等待 WhatsApp 回执，发送状态待确认");
            MessageBox.Show("手机端可能已经发送成功，但本机未及时收到确认。请先等待会话同步，不要立即重复发送。", "发送状态待确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            pendingMessage.UpdateStatus(WhatsAppMessageStatus.Failed, DateTimeOffset.Now, null, null, error.Message);
            MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _sending = false; UpdateComposerState(); }
        return accepted;
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

    private void ConversationList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ConversationList, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            item.IsSelected = true;
            if (item.DataContext is ConversationItem conversation)
            {
                var action = new MenuItem { Header = conversation.PinActionLabel, CommandParameter = conversation };
                action.Click += PinConversation_Click;
                item.ContextMenu = new ContextMenu();
                item.ContextMenu.Items.Add(action);
            }
        }
    }

    private async void PinConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: ConversationItem conversation }) return;
        if (!_connected)
        {
            MessageBox.Show("请先连接 WhatsApp，再同步置顶状态。", "WhatsApp 置顶", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pinned = !conversation.IsPinned;
        try
        {
            await _services.WhatsApp.SetChatPinnedAsync(conversation.AccountId, conversation.Phone, pinned);
            conversation.IsPinned = pinned;
            conversation.PinnedAt = pinned ? DateTimeOffset.Now : null;
            var stored = await _services.Repository.GetWhatsAppConversationAsync(conversation.AccountId, conversation.Phone) ?? new WhatsAppConversation
            {
                Id = conversation.Id, AccountId = conversation.AccountId, Phone = conversation.Phone, DisplayName = conversation.DisplayName
            };
            stored.IsPinned = conversation.IsPinned;
            stored.PinnedAt = conversation.PinnedAt;
            await _services.Repository.UpsertWhatsAppConversationAsync(stored);
            ReorderConversations(conversation);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "置顶同步失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
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

    private Lead? FindLead(string phone) => PhoneIdentity.FindUniqueLead(_leads, phone);

    private static string BestContactName(WhatsAppContact contact) => new[]
    {
        contact.SavedName, contact.DisplayName, contact.NotifyName, contact.VerifiedName, contact.Username,
        string.IsNullOrWhiteSpace(contact.Phone) ? contact.Jid : $"+{contact.Phone}"
    }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "WhatsApp 联系人";

    private static IOrderedEnumerable<ConversationItem> OrderConversations(IEnumerable<ConversationItem> source) => source
        .OrderByDescending(item => item.IsPinned)
        .ThenByDescending(item => item.IsPinned ? item.PinnedAt ?? item.LastAt : item.LastAt)
        .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    private void ReorderConversations(ConversationItem selected)
    {
        var ordered = OrderConversations(_conversations).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = _conversations.IndexOf(ordered[index]);
            if (current != index) _conversations.Move(current, index);
        }
        ApplyConversationFilter();
        ConversationList.SelectedItem = selected;
    }

    private static string MessagePreview(string text, string kind, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text;
        var type = kind switch { "image" => "图片", "video" => "视频", "audio" => "音频", "document" => "文件", "sticker" => "贴图", _ => "媒体消息" };
        return string.IsNullOrWhiteSpace(fileName) ? $"[{type}]" : $"[{type}] {fileName}";
    }

    private void UpdateComposerState()
    {
        var available = _connected && ConversationList.SelectedItem is ConversationItem { Phone.Length: > 0 } && !_sending;
        ComposerBox.IsEnabled = available;
        AttachButton.IsEnabled = available;
        SendButton.IsEnabled = available && (!string.IsNullOrWhiteSpace(ComposerBox.Text) || !string.IsNullOrWhiteSpace(_attachmentPath));
        AiAssistantButton.IsEnabled = ConversationList.SelectedItem is ConversationItem { Phone.Length: > 0 } && !_sending && !_aiAssisting;
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
        UpdateLeadIntelligenceSummary(null);
        UpdateComposerState();
    }

    private void UpdateLeadIntelligenceSummary(Lead? lead)
    {
        var current = lead is { HasCurrentAiScore: true };
        var score = current ? lead!.Score : 0;
        var grade = current ? lead!.Grade : "D";
        var confidence = current ? lead!.AnalysisConfidence : 0;
        AiSidebarScoreRing.SetScore(score, grade, confidence);
        AiSidebarConfidenceBar.Value = Math.Clamp(confidence * 100, 0, 100);
        AiSidebarConfidenceText.Text = current ? $"AI 置信度 {confidence:P0}" : lead is null ? "等待关联客户" : $"{lead.AnalysisStateLabel} · D / 0";
        AiSidebarProfileText.Text = current && !string.IsNullOrWhiteSpace(lead!.ProfileSummary) ? lead.ProfileSummary : lead is null ? "选择会话后显示对应客户画像" : "尚无经过验证的 AI 客户画像";
        AiSidebarNextActionText.Text = current && !string.IsNullOrWhiteSpace(lead!.NextAction) ? $"下一步：{lead.NextAction}" : "下一步：等待 AI 分析或人工判断";
    }

    private void ScrollMessages(ConversationItem conversation)
    {
        MessageList.ItemsSource = conversation.Messages;
        if (conversation.Messages.LastOrDefault() is { } last) MessageList.ScrollIntoView(last);
    }

    private void SetConnectionText(string text, bool connected)
    {
        ConnectionStateText.Text = text;
        ConnectionStateText.Foreground = (Brush)FindResource(connected ? "Success" : "Warning");
    }

    private void UpdateConnectionControls()
    {
        var state = _services.WhatsApp.ConnectionStateFor(CurrentAccountId);
        SetConnectionText(state switch { "connected" => "已连接", "connecting" => "连接中", "logged_out" => "已退出", _ => "未连接" }, state == "connected");
        DisconnectButton.IsEnabled = state is "connected" or "connecting"; LogoutButton.IsEnabled = state == "connected";
        SyncButton.IsEnabled = state == "connected";
        CreateGroupButton.IsEnabled = state == "connected";
        UpdateComposerState();
    }

    private static BitmapImage? DecodeDataUrl(string dataUrl)
    {
        var marker = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var bytes = Convert.FromBase64String(dataUrl[(marker + 7)..]);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private void OpenMedia_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MessageItem item } || string.IsNullOrWhiteSpace(item.MediaPath) || !File.Exists(item.MediaPath))
        {
            MessageBox.Show("媒体文件尚未下载到本机。连接 WhatsApp 后再次同步，可重新尝试下载。", "WhatsApp 媒体", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(item.MediaPath) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(error.Message, "无法打开媒体", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string KindFromFileName(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" => "image",
        ".gif" or ".mp4" or ".3gp" or ".mov" => "video",
        ".mp3" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".aac" => "audio",
        _ => "document"
    };

    private static WhatsAppMessageStatus ParseMessageStatus(JsonElement data, bool fromMe)
    {
        if (!fromMe) return WhatsAppMessageStatus.Received;
        if (ParseTime(data, "readAt") is not null) return WhatsAppMessageStatus.Read;
        if (ParseTime(data, "deliveredAt") is not null) return WhatsAppMessageStatus.Delivered;
        return data.TryGetProperty("status", out var value) && value.TryGetInt32(out var numeric) ? StatusFromNumeric(numeric) : WhatsAppMessageStatus.Sent;
    }
    private static WhatsAppMessageStatus StatusFromNumeric(int numeric) => numeric switch
    {
        <= 0 => WhatsAppMessageStatus.Failed,
        1 => WhatsAppMessageStatus.Pending,
        2 => WhatsAppMessageStatus.Sent,
        3 => WhatsAppMessageStatus.Delivered,
        >= 4 => WhatsAppMessageStatus.Read
    };
    private static DateTimeOffset? ParseTime(JsonElement data, string name) => DateTimeOffset.TryParse(Text(data, name), out var value) ? value : null;
    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
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
        private string _displayName = displayName; private string _lastMessage = ""; private DateTimeOffset _lastAt; private int _unread; private bool _isPinned; private DateTimeOffset? _pinnedAt;
        public string AccountId { get; } = accountId; public string Phone { get; } = phone; public string Jid { get; set; } = jid; public string LeadId { get; set; } = ""; public string Id => string.IsNullOrWhiteSpace(Phone) ? $"{AccountId}:{Jid}" : $"{AccountId}:{Phone}"; public ObservableCollection<MessageItem> Messages { get; } = [];
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }
        public DateTimeOffset LastAt { get => _lastAt; set { if (Set(ref _lastAt, value)) OnPropertyChanged(nameof(LastTimeLabel)); } }
        public string LastTimeLabel => LastAt == default ? "" : LastAt.LocalDateTime.ToString("MM-dd HH:mm");
        public int Unread { get => _unread; set { if (Set(ref _unread, value)) OnPropertyChanged(nameof(UnreadVisibility)); } }
        public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        public bool IsPinned { get => _isPinned; set { if (Set(ref _isPinned, value)) { OnPropertyChanged(nameof(PinnedVisibility)); OnPropertyChanged(nameof(PinActionLabel)); } } }
        public DateTimeOffset? PinnedAt { get => _pinnedAt; set => Set(ref _pinnedAt, value); }
        public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;
        public string PinActionLabel => IsPinned ? "取消置顶并同步到手机" : "置顶并同步到手机";
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(property); return true; }
        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class MessageItem : INotifyPropertyChanged
    {
        public MessageItem(
            string id,
            string text,
            DateTimeOffset timestamp,
            bool fromMe,
            string kind = "text",
            string fileName = "",
            string mimeType = "",
            string mediaPath = "",
            string mediaDownloadError = "",
            WhatsAppMessageStatus status = WhatsAppMessageStatus.Received,
            DateTimeOffset? statusUpdatedAt = null,
            DateTimeOffset? deliveredAt = null,
            DateTimeOffset? readAt = null,
            string failureReason = "",
            string quotedMessageId = "",
            string quotedText = "",
            bool quotedFromMe = false,
            bool isRevoked = false,
            DateTimeOffset? revokedAt = null)
        {
            Id = id; Text = text; Timestamp = timestamp; FromMe = fromMe; Kind = kind; FileName = fileName; MimeType = mimeType; MediaPath = mediaPath; MediaDownloadError = mediaDownloadError;
            Status = status; StatusUpdatedAt = statusUpdatedAt; DeliveredAt = deliveredAt; ReadAt = readAt; FailureReason = failureReason;
            QuotedMessageId = quotedMessageId; QuotedText = quotedText; QuotedFromMe = quotedFromMe; IsRevoked = isRevoked; RevokedAt = revokedAt;
            MediaPreview = LoadMediaPreview(kind, mediaPath);
        }

        public string Id { get; private set; }
        public string Text { get; }
        public DateTimeOffset Timestamp { get; private set; }
        public bool FromMe { get; }
        public string Kind { get; private set; }
        public string FileName { get; private set; }
        public string MimeType { get; }
        public string MediaPath { get; }
        public string MediaDownloadError { get; }
        public ImageSource? MediaPreview { get; }
        public WhatsAppMessageStatus Status { get; private set; }
        public DateTimeOffset? StatusUpdatedAt { get; private set; }
        public DateTimeOffset? DeliveredAt { get; private set; }
        public DateTimeOffset? ReadAt { get; private set; }
        public string FailureReason { get; private set; }
        public string QuotedMessageId { get; }
        public string QuotedText { get; }
        public bool QuotedFromMe { get; }
        public bool IsRevoked { get; private set; }
        public DateTimeOffset? RevokedAt { get; private set; }
        private bool IsRevoking { get; set; }
        public string DisplayText => IsRevoked ? (FromMe ? "你撤回了一条消息" : "对方撤回了一条消息") : MessagePreview(Text, Kind, FileName);
        public bool HasMedia => !IsRevoked && Kind is "image" or "video" or "audio" or "document" or "sticker";
        public bool HasDownloadedMedia => !string.IsNullOrWhiteSpace(MediaPath) && File.Exists(MediaPath);
        public string TextContent => IsRevoked ? DisplayText : !string.IsNullOrWhiteSpace(Text) ? Text : HasMedia && HasDownloadedMedia ? "" : DisplayText;
        public Visibility TextVisibility => string.IsNullOrWhiteSpace(TextContent) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ImageVisibility => HasDownloadedMedia && MediaPreview is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FileVisibility => HasDownloadedMedia && MediaPreview is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MediaMissingVisibility => HasMedia && !HasDownloadedMedia ? Visibility.Visible : Visibility.Collapsed;
        public string MediaActionLabel => Kind switch { "video" => $"▶ 打开视频 {FileName}", "audio" => $"♪ 播放音频 {FileName}", "document" => $"▣ 打开文件 {FileName}", _ => $"打开媒体 {FileName}" };
        public string MediaMissingText => string.IsNullOrWhiteSpace(MediaDownloadError) ? "媒体尚未下载；重新同步后会再次尝试。" : $"媒体下载失败：{MediaDownloadError}";
        public string TimeLabel => Timestamp.LocalDateTime.ToString("MM-dd HH:mm");
        public HorizontalAlignment Alignment => FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBrush => new SolidColorBrush(FromMe ? Color.FromRgb(220,248,233) : Colors.White);
        public Brush BubbleBorderBrush => new SolidColorBrush(FromMe ? Color.FromRgb(190,232,211) : Color.FromRgb(223,230,226));
        public Visibility QuoteVisibility => !IsRevoked && !string.IsNullOrWhiteSpace(QuotedMessageId) ? Visibility.Visible : Visibility.Collapsed;
        public string QuoteHeader => QuotedFromMe ? "你" : "对方";
        public string QuoteText => string.IsNullOrWhiteSpace(QuotedText) ? "[原消息]" : QuotedText;
        public Visibility ReplyMenuVisibility => !FromMe && !IsRevoked ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RevokeMenuVisibility => FromMe ? Visibility.Visible : Visibility.Collapsed;
        public bool CanRevoke => FromMe && !IsRevoked && !IsRevoking && !string.IsNullOrWhiteSpace(Id) && !Id.StartsWith("local-", StringComparison.OrdinalIgnoreCase) && Status is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
        public Visibility OutgoingStatusVisibility => FromMe && !IsRevoked ? Visibility.Visible : Visibility.Collapsed;
        public string ReceiptGlyph => !FromMe || IsRevoked ? "" : Status switch
        {
            WhatsAppMessageStatus.Pending => "…",
            WhatsAppMessageStatus.Sent => "✓",
            WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read => "✓✓",
            WhatsAppMessageStatus.Failed => "!",
            _ => ""
        };
        public Brush ReceiptBrush => new SolidColorBrush(Status switch
        {
            WhatsAppMessageStatus.Read => Color.FromRgb(31, 142, 213),
            WhatsAppMessageStatus.Failed => Color.FromRgb(183, 57, 57),
            WhatsAppMessageStatus.Delivered => Color.FromRgb(89, 105, 97),
            _ => Color.FromRgb(104, 118, 111)
        });
        public string StatusDetailLabel => !FromMe ? "" : IsRevoked ? $"已从双方设备撤回 · {At(RevokedAt ?? Timestamp)}" : Status switch
        {
            WhatsAppMessageStatus.Pending when !string.IsNullOrWhiteSpace(FailureReason) => $"状态待确认 · 发送 {At(Timestamp)}",
            WhatsAppMessageStatus.Pending => $"发送中 · {At(Timestamp)}",
            WhatsAppMessageStatus.Sent => $"发送 {At(Timestamp)} · 尚未送达",
            WhatsAppMessageStatus.Delivered => $"发送 {At(Timestamp)} · 送达 {At(DeliveredAt ?? StatusUpdatedAt)}",
            WhatsAppMessageStatus.Read => $"发送 {At(Timestamp)} · 送达 {At(DeliveredAt)} · 已读 {At(ReadAt ?? StatusUpdatedAt)}",
            WhatsAppMessageStatus.Failed => $"发送失败 {At(StatusUpdatedAt ?? Timestamp)}{(string.IsNullOrWhiteSpace(FailureReason) ? "" : $" · {ShortReason(FailureReason)}")}",
            _ => $"发送 {At(Timestamp)}"
        };

        public void UpdateTransport(string id, DateTimeOffset timestamp, string kind, string fileName)
        {
            Id = id; Timestamp = timestamp; Kind = kind; FileName = fileName;
            NotifyAll();
        }

        public void UpdateStatus(WhatsAppMessageStatus status, DateTimeOffset? statusAt, DateTimeOffset? deliveredAt, DateTimeOffset? readAt, string failureReason)
        {
            if (CanAdvance(Status, status)) Status = status;
            if (statusAt is not null && (StatusUpdatedAt is null || statusAt > StatusUpdatedAt)) StatusUpdatedAt = statusAt;
            if (deliveredAt is not null && (DeliveredAt is null || deliveredAt < DeliveredAt)) DeliveredAt = deliveredAt;
            if (readAt is not null && (ReadAt is null || readAt < ReadAt)) ReadAt = readAt;
            if (Status == WhatsAppMessageStatus.Read && DeliveredAt is null) DeliveredAt = ReadAt ?? StatusUpdatedAt;
            if (!string.IsNullOrWhiteSpace(failureReason)) FailureReason = failureReason;
            NotifyAll();
        }

        public void SetRevoking(bool value)
        {
            IsRevoking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRevoke)));
        }

        public void MarkRevoked(DateTimeOffset? revokedAt)
        {
            IsRevoked = true;
            RevokedAt ??= revokedAt ?? DateTimeOffset.Now;
            IsRevoking = false;
            NotifyAll();
        }

        private static bool CanAdvance(WhatsAppMessageStatus current, WhatsAppMessageStatus next)
        {
            if (current == next) return true;
            if (next == WhatsAppMessageStatus.Failed) return current == WhatsAppMessageStatus.Pending;
            if (current == WhatsAppMessageStatus.Failed) return next is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
            static int Rank(WhatsAppMessageStatus value) => value switch
            {
                WhatsAppMessageStatus.Pending => 0, WhatsAppMessageStatus.Sent => 1,
                WhatsAppMessageStatus.Delivered => 2, WhatsAppMessageStatus.Read => 3,
                WhatsAppMessageStatus.Received => 3, _ => -1
            };
            return Rank(next) >= Rank(current);
        }

        private static string At(DateTimeOffset? value) => value is null ? "--" : value.Value.LocalDateTime.ToString("MM-dd HH:mm");
        private static string ShortReason(string value) => value.Length <= 60 ? value : value[..60] + "…";
        private static ImageSource? LoadMediaPreview(string kind, string mediaPath)
        {
            if (kind is not ("image" or "sticker") || string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) return null;
            try
            {
                using var stream = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                return image;
            }
            catch { return null; }
        }
        private void NotifyAll()
        {
            foreach (var name in new[] { nameof(Id), nameof(DisplayText), nameof(TextContent), nameof(TextVisibility), nameof(HasMedia), nameof(ImageVisibility), nameof(FileVisibility), nameof(MediaMissingVisibility), nameof(TimeLabel), nameof(ReceiptGlyph), nameof(ReceiptBrush), nameof(StatusDetailLabel), nameof(OutgoingStatusVisibility), nameof(QuoteVisibility), nameof(ReplyMenuVisibility), nameof(RevokeMenuVisibility), nameof(CanRevoke), nameof(IsRevoked), nameof(RevokedAt) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
