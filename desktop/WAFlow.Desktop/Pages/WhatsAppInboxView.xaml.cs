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
using Microsoft.Win32;
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
    private bool _initialLeadLinkCompleted;
    private bool _sending;
    private string _attachmentPath = "";
    private string _composerConversationId = "";
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
        if (e.Name != "message") return;
        var phone = Text(e.Data, "phone");
        if (string.IsNullOrWhiteSpace(phone)) return;
        var messageId = Text(e.Data, "id");
        var text = Text(e.Data, "text");
        var fromMe = Bool(e.Data, "fromMe");
        var displayName = Text(e.Data, "pushName");
        var kind = Text(e.Data, "kind");
        var fileName = Text(e.Data, "fileName");
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
        if (!conversation.Messages.Any(x => x.Id == messageId)) conversation.Messages.Add(new MessageItem(messageId, text, timestamp, fromMe, kind, fileName));
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
            ChatTitleText.Text = "选择会话"; ChatNumberText.Text = "连接后会同步个人会话"; MessageList.ItemsSource = null; ClearLead(); return;
        }
        if (!_composerConversationId.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase))
        {
            _composerConversationId = conversation.Id;
            ClearAttachment();
        }
        conversation.Unread = 0;
        if (!string.IsNullOrWhiteSpace(conversation.Phone)) await _services.Repository.MarkWhatsAppConversationReadAsync(conversation.Id);
        ChatTitleText.Text = conversation.DisplayName;
        ChatNumberText.Text = string.IsNullOrWhiteSpace(conversation.Phone) ? "WhatsApp 尚未提供该联系人的电话号码" : $"+{conversation.Phone}";
        var persistedMessages = string.IsNullOrWhiteSpace(conversation.Phone) ? [] : await _services.Repository.GetWhatsAppMessagesAsync(conversation.Id, 2000);
        foreach (var message in persistedMessages)
            if (!conversation.Messages.Any(x => x.Id == message.ProviderMessageId)) conversation.Messages.Add(new MessageItem(message.ProviderMessageId, message.Body, message.Timestamp, message.Direction == WhatsAppMessageDirection.Outgoing, message.Kind, message.FileName));
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
            conversation.LeadId = lead.Id;
            conversation.DisplayName = lead.DisplayName;
            LeadLinkStateText.Text = $"已关联：{lead.Grade} 级 · {Labels.Stage(lead.Stage)}";
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("客户资料已同步到 AI Sales OS。", "WhatsApp Inbox", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

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

    private async Task SendCurrentAsync()
    {
        if (_sending || ConversationList.SelectedItem is not ConversationItem conversation) return;
        var text = ComposerBox.Text.Trim();
        var attachmentPath = _attachmentPath;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachmentPath)) return;
        if (string.IsNullOrWhiteSpace(conversation.Phone)) { MessageBox.Show("该联系人的电话号码尚未同步，暂时不能发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_currentLead?.OptedOut == true) { MessageBox.Show("客户已退订，禁止发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        _sending = true;
        UpdateComposerState();
        try
        {
            var result = string.IsNullOrWhiteSpace(attachmentPath)
                ? await _services.WhatsApp.SendTextAsync(conversation.Phone, text)
                : await _services.WhatsApp.SendMediaAsync(conversation.Phone, attachmentPath, text);
            var id = result.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var timestamp = result.TryGetProperty("timestamp", out var timestampElement) && DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp) ? parsedTimestamp : DateTimeOffset.Now;
            var kind = result.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? "text" : "text";
            var fileName = result.TryGetProperty("fileName", out var fileNameElement) ? fileNameElement.GetString() ?? "" : "";
            var mimeType = result.TryGetProperty("mimeType", out var mimeElement) ? mimeElement.GetString() ?? "" : "";
            if (!conversation.Messages.Any(x => x.Id == id)) conversation.Messages.Add(new MessageItem(id, text, timestamp, true, kind, fileName));
            conversation.LastMessage = MessagePreview(text, kind, fileName);
            conversation.LastAt = timestamp;
            ComposerBox.Clear();
            ClearAttachment();

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
                Direction = WhatsAppMessageDirection.Outgoing, Status = WhatsAppMessageStatus.Sent, Kind = kind,
                Body = text, FileName = fileName, MimeType = mimeType, Timestamp = timestamp, Source = "desktop"
            };
            await _services.Repository.UpsertWhatsAppMessageAsync(storedMessage);
            ReorderConversations(conversation);
            ScrollMessages(conversation);
            if (_currentLead is not null)
            {
                LeadConnectionStatus.ApplyFromMessage(_currentLead, storedMessage);
                await _services.Repository.UpsertLeadAsync(_currentLead);
                await _services.Repository.LogEventAsync("whatsapp_message_sent", _currentLead.Id, null, $"message_id={id}; kind={kind}");
            }
        }
        catch (TimeoutException)
        {
            MessageBox.Show("手机端可能已经发送成功，但本机未及时收到确认。请先等待会话同步，不要立即重复发送。", "发送状态待确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _sending = false; UpdateComposerState(); }
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
            item.IsSelected = true;
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
        UpdateComposerState();
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

    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
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

    private sealed record MessageItem(string Id, string Text, DateTimeOffset Timestamp, bool FromMe, string Kind = "text", string FileName = "")
    {
        public string DisplayText => MessagePreview(Text, Kind, FileName);
        public string TimeLabel => Timestamp.LocalDateTime.ToString("MM-dd HH:mm");
        public HorizontalAlignment Alignment => FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBrush => new SolidColorBrush(FromMe ? Color.FromRgb(220,248,233) : Colors.White);
        public Brush BubbleBorderBrush => new SolidColorBrush(FromMe ? Color.FromRgb(190,232,211) : Color.FromRgb(223,230,226));
    }
}
