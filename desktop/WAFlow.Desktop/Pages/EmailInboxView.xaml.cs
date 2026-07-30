using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;
using WAFlow.Desktop.Collections;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class EmailInboxView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly BatchObservableCollection<EmailAccount> _accounts = [];
    private readonly BatchObservableCollection<EmailConversationItem> _conversations = [];
    private readonly BatchObservableCollection<EmailMessage> _messages = [];
    private EmailConversation? _conversation;
    private Lead? _lead;
    private EmailAssistantResult? _emailDraft;
    private bool _loading;
    private bool _isNewEmail;
    private bool _aiAssisting;
    private bool _suppressSelectionChanged;
    private bool _customerDrawerExpanded = true;
    private int _customerBrainRefreshGeneration;
    private readonly object _refreshLock = new();
    private Task _activeRefresh = Task.CompletedTask;
    private bool _refreshRequestedAgain;
    private CancellationTokenSource? _synchronizationRefreshDebounce;

    public event EventHandler? DataChanged;

    public EmailInboxView(AppServices services)
    {
        _services = services;
        InitializeComponent();
        AccountBox.ItemsSource = _accounts; ConversationList.ItemsSource = _conversations; MessageList.ItemsSource = _messages;
        StageBox.ItemsSource = Enum.GetValues<LeadStage>().Select(stage => new StageChoice(Labels.Stage(stage), stage)).ToList();
        _services.Email.SynchronizationChanged += Email_SynchronizationChanged;
    }

    private void ToggleCustomerDrawer_Click(object sender, RoutedEventArgs e)
    {
        _customerDrawerExpanded = !_customerDrawerExpanded;
        CustomerDrawerColumn.Width = new GridLength(_customerDrawerExpanded ? 380 : 44);
        CustomerDrawerBorder.Visibility = _customerDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        CustomerDrawerCollapsedRail.Visibility = _customerDrawerExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    public Task RefreshAsync()
    {
        lock (_refreshLock)
        {
            if (!_activeRefresh.IsCompleted)
            {
                _refreshRequestedAgain = true;
                return _activeRefresh;
            }

            _activeRefresh = RefreshLoopAsync();
            return _activeRefresh;
        }
    }

    private async Task RefreshLoopAsync()
    {
        do
        {
            lock (_refreshLock) _refreshRequestedAgain = false;
            await RefreshCoreAsync();
        }
        while (IsVisible && ReadRefreshRequestedAgain());
    }

    private bool ReadRefreshRequestedAgain()
    {
        lock (_refreshLock) return _refreshRequestedAgain;
    }

    private async Task RefreshCoreAsync()
    {
        var accountId = (AccountBox.SelectedItem as EmailAccount)?.Id;
        var selectedConversationId = _conversation?.Id;
        var snapshot = await Task.Run(async () =>
        {
            var accounts = await _services.Repository.GetEmailAccountsAsync();
            var selectedAccount = accounts.FirstOrDefault(item => item.Id == accountId)
                ?? accounts.FirstOrDefault();
            var conversations = selectedAccount is null
                ? []
                : (await _services.Repository.GetEmailConversationsAsync(selectedAccount.Id))
                    .Select(item => new EmailConversationItem(item))
                    .ToList();
            return new EmailInboxSnapshot(accounts, selectedAccount?.Id, conversations);
        });

        _loading = true;
        _suppressSelectionChanged = true;
        try
        {
            _accounts.ReplaceAll(snapshot.Accounts);
            AccountBox.SelectedItem = _accounts.FirstOrDefault(item => item.Id == snapshot.SelectedAccountId);
            _conversations.ReplaceAll(snapshot.Conversations);
            if (!_isNewEmail)
                ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == selectedConversationId);
            ApplySearch();
            await UpdateEmailAssistantModelAsync();
            UpdateComposerState();
        }
        finally
        {
            _suppressSelectionChanged = false;
            _loading = false;
        }
    }

    private async Task RefreshConversationsAsync()
    {
        var selectedId = _conversation?.Id;
        var preserveNewEmail = _isNewEmail;
        _suppressSelectionChanged = true;
        try
        {
            if (AccountBox.SelectedItem is not EmailAccount account)
            {
                _conversations.ReplaceAll([]);
                if (!preserveNewEmail) ClearConversation();
                return;
            }
            var conversations = await Task.Run(async () =>
                (await _services.Repository.GetEmailConversationsAsync(account.Id))
                    .Select(item => new EmailConversationItem(item))
                    .ToList());
            _conversations.ReplaceAll(conversations);
            if (!preserveNewEmail)
                ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == selectedId);
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
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

    private void NewEmail_Click(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not EmailAccount account)
        {
            MessageBox.Show("请先连接并选择一个发件邮箱。", "新建邮件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _suppressSelectionChanged = true;
        ConversationList.SelectedItem = null;
        _suppressSelectionChanged = false;
        _isNewEmail = true;
        _conversation = null;
        _lead = null;
        _messages.Clear();
        _emailDraft = null;
        ConversationTitle.Text = "新建邮件";
        ConversationSubtitle.Text = $"发件账号：{account.EmailAddress}";
        RecipientBox.IsReadOnly = false;
        RecipientBox.Clear();
        SubjectBox.Clear();
        ComposerBox.Clear();
        EmailAiInstructionBox.Clear();
        PopulateCustomer();
        ResetEmailAssistantResult();
        ResetCustomerIntelligenceSummary();
        UpdateComposerState();
        RecipientBox.Focus();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not EmailAccount account) { MessageBox.Show("请先连接邮件账号。", "邮件 Inbox", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            SyncButton.IsEnabled = false; SyncButton.Content = "正在同步…";
            var count = await _services.Email.SyncInboxAsync(account.Id, 500);
            await RefreshAsync(); DataChanged?.Invoke(this, EventArgs.Empty);
            SyncButton.Content = $"已同步 {count} 封";
            await Task.Delay(900);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"{error.Message}\n\n程序会继续在后台重连；无需删除或重新添加邮箱账号。",
                "邮件暂时无法同步",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally { SyncButton.IsEnabled = true; SyncButton.Content = "同步收件箱"; }
    }

    private async void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        await RefreshConversationsAsync();
        if (_isNewEmail && AccountBox.SelectedItem is EmailAccount account)
            ConversationSubtitle.Text = $"发件账号：{account.EmailAddress}";
        await UpdateEmailAssistantModelAsync();
        UpdateComposerState();
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (ConversationList.SelectedItem is not EmailConversationItem item) { ClearConversation(); return; }
        var conversation = item.Conversation;
        _isNewEmail = false;
        _conversation = conversation;
        _emailDraft = null;
        if (IsVisible && item.Unread > 0)
        {
            item.MarkRead(DateTimeOffset.Now);
            await _services.Repository.MarkEmailConversationReadAsync(conversation.Id);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        ConversationTitle.Text = conversation.DisplayName; ConversationSubtitle.Text = $"{conversation.PeerEmail} · {conversation.Subject}";
        var messages = await Task.Run(async () =>
            await _services.Repository.GetEmailMessagesAsync(conversation.Id));
        _messages.ReplaceAll(messages);
        RecipientBox.Text = conversation.PeerEmail;
        RecipientBox.IsReadOnly = true;
        SubjectBox.Text = string.IsNullOrWhiteSpace(conversation.Subject) ? "" : conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : $"Re: {conversation.Subject}";
        _lead = !string.IsNullOrWhiteSpace(conversation.LeadId) ? await _services.Repository.GetLeadAsync(conversation.LeadId) : await _services.Repository.GetLeadByEmailAsync(conversation.PeerEmail);
        PopulateCustomer();
        ResetEmailAssistantResult();
        UpdateLeadIntelligenceSummary(_lead);
        await UpdateCustomerBrainSummaryAsync(_lead);
        UpdateComposerState();
        await Dispatcher.InvokeAsync(() => MessageScroll.ScrollToEnd());
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not EmailAccount account)
        {
            MessageBox.Show("请先连接并选择一个发件邮箱。", "邮件发送", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            SendButton.IsEnabled = false; SendButton.Content = "发送中…";
            await ResolveRecipientContextAsync();
            var replyTo = _isNewEmail
                ? null
                : _messages.LastOrDefault(message => message.Direction == EmailMessageDirection.Incoming)?.ProviderMessageId;
            var sent = await _services.Email.SendAsync(
                account.Id,
                RecipientBox.Text,
                SubjectBox.Text,
                ComposerBox.Text,
                _lead?.Id,
                replyTo);
            _isNewEmail = false;
            ComposerBox.Clear();
            await RefreshConversationsAsync();
            ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == sent.ConversationId);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "邮件发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SendButton.Content = "发送邮件"; UpdateComposerState(); }
    }

    private async void SaveCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation is null && string.IsNullOrWhiteSpace(RecipientBox.Text))
        {
            MessageBox.Show("请先选择邮件会话或填写收件邮箱。", "客户资料", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var email = string.IsNullOrWhiteSpace(CustomerEmailBox.Text) ? RecipientBox.Text.Trim() : CustomerEmailBox.Text.Trim();
            _lead ??= new Lead
            {
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? email : NameBox.Text.Trim(),
                Email = email,
                Grade = "D",
                Score = 0,
                Stage = LeadStage.New,
                Source = "邮件 Inbox"
            };
            _lead.Name = NameBox.Text.Trim(); _lead.Email = CustomerEmailBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_lead.Name)) _lead.Name = email;
            if (string.IsNullOrWhiteSpace(_lead.Email)) _lead.Email = email;
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
            _lead.ManualNotes = NotesBox.Text.Trim();
            await _services.Repository.UpsertLeadAsync(_lead);
            if (_conversation is not null)
            {
                _conversation.LeadId = _lead.Id; _conversation.PeerName = _lead.DisplayName;
                await _services.Repository.UpsertEmailConversationAsync(_conversation);
            }
            LinkStateText.Text = $"已关联：{_lead.Grade} 级 · {Labels.Stage(_lead.Stage)}";
            UpdateLeadIntelligenceSummary(_lead);
            await UpdateCustomerBrainSummaryAsync(_lead);
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("客户资料已同步到客户列表、商机智能和自动化触达。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void PopulateCustomer()
    {
        NameBox.Text = _lead?.Name ?? _conversation?.PeerName ?? "";
        CustomerEmailBox.Text = _lead?.Email ?? _conversation?.PeerEmail ?? RecipientBox.Text.Trim();
        CountryBox.Text = _lead?.Country ?? ""; OwnerBox.Text = _lead?.Owner ?? "";
        TagsBox.Text = _lead is null ? "" : string.Join(", ", _lead.Tags); NotesBox.Text = _lead?.ManualNotes ?? "";
        StageBox.SelectedItem = StageBox.Items.Cast<StageChoice>().First(item => item.Value == (_lead?.Stage ?? LeadStage.New));
        LinkStateText.Text = _lead is null ? "未关联客户 · 保存时将创建" : $"已关联：{_lead.Grade} 级 · {Labels.Stage(_lead.Stage)}";
    }

    private void ClearConversation()
    {
        _isNewEmail = false; _conversation = null; _lead = null; _emailDraft = null; _messages.Clear(); ConversationTitle.Text = "选择邮件会话"; ConversationSubtitle.Text = "";
        RecipientBox.Clear(); RecipientBox.IsReadOnly = true; SubjectBox.Clear(); ComposerBox.Clear(); EmailAiInstructionBox.Clear();
        NameBox.Clear(); CustomerEmailBox.Clear(); CountryBox.Clear(); OwnerBox.Clear(); TagsBox.Clear(); NotesBox.Clear(); LinkStateText.Text = "按邮箱自动匹配客户";
        ResetEmailAssistantResult();
        ResetCustomerIntelligenceSummary();
        UpdateComposerState();
    }

    private async void RecipientBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isNewEmail) return;
        await ResolveRecipientContextAsync();
        UpdateComposerState();
    }

    private void RecipientBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateComposerState();
    }

    private async Task ResolveRecipientContextAsync()
    {
        var email = RecipientBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(email)) return;
        _lead = await _services.Repository.GetLeadByEmailAsync(email);
        PopulateCustomer();
        UpdateLeadIntelligenceSummary(_lead);
        await UpdateCustomerBrainSummaryAsync(_lead);
    }

    private async void GenerateEmailDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_aiAssisting || AccountBox.SelectedItem is not EmailAccount account) return;
        try
        {
            _aiAssisting = true;
            GenerateEmailDraftButton.Content = "正在生成…";
            ComposerAiButton.Content = "分析中";
            UpdateComposerState();
            await ResolveRecipientContextAsync();
            _emailDraft = await _services.EmailAssistant.AnalyzeAsync(
                account.Id,
                _conversation?.Id,
                RecipientBox.Text,
                _lead,
                EmailAiInstructionBox.Text,
                SubjectBox.Text,
                ComposerBox.Text);
            ShowEmailAssistantResult(_emailDraft);
            UseEmailDraftButton.IsEnabled = true;
        }
        catch (DeepSeekException error)
        {
            MessageBox.Show(EmailAssistantErrorMessage(error), "AI 邮件助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "AI 邮件助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _aiAssisting = false;
            GenerateEmailDraftButton.Content = _emailDraft is null ? "立即生成草稿" : "重新生成草稿";
            ComposerAiButton.Content = "✦ AI 写信";
            UpdateComposerState();
        }
    }

    private void UseEmailDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_emailDraft is null) return;
        SubjectBox.Text = _emailDraft.Subject;
        ComposerBox.Text = _emailDraft.Body;
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
        ComposerBox.Focus();
        EmailAiStatusText.Text = "草稿已填入 · 请核对后手动发送";
    }

    private void ShowEmailAssistantResult(EmailAssistantResult result)
    {
        EmailAiStatusText.Text = $"已生成 · {result.Language}";
        EmailAiConfidenceText.Text = $"置信度 {result.Confidence:P0}";
        EmailAiSubjectText.Text = $"主题：{result.Subject}";
        EmailAiBodyText.Text = result.Body;
        EmailAiSummaryText.Text = $"上下文摘要：{result.ContextSummary}\n客户意向：{result.CustomerIntent}" +
                                  (result.Risks.Count == 0 ? "" : $"\n风险：{string.Join("；", result.Risks)}");
        EmailAiNextActionText.Text = $"下一步：{result.RecommendedNextAction}";
        EmailAiKnowledgeText.Text = result.KnowledgeCitations.Count == 0
            ? "知识参考：本次未使用知识库资料"
            : $"知识参考：{string.Join("；", result.KnowledgeCitations.Select(item => $"{item.DocumentTitle} {item.Locator}").Distinct())}";
        EmailAiModelText.Text = result.Model;
    }

    private void ResetEmailAssistantResult()
    {
        _emailDraft = null;
        EmailAiStatusText.Text = "尚无产出";
        EmailAiConfidenceText.Text = "—";
        EmailAiSubjectText.Text = "主题：等待生成";
        EmailAiBodyText.Text = "生成的邮件正文会显示在这里。";
        EmailAiSummaryText.Text = "上下文摘要：—";
        EmailAiNextActionText.Text = "下一步：—";
        EmailAiKnowledgeText.Text = "知识参考：尚未生成";
        UseEmailDraftButton.IsEnabled = false;
        GenerateEmailDraftButton.Content = "立即生成草稿";
    }

    private async Task UpdateEmailAssistantModelAsync()
    {
        if (!_services.DeepSeek.HasApiKey(AiModuleKeys.EmailInbox))
        {
            EmailAiModelText.Text = "未配置邮件 AI";
            return;
        }
        try
        {
            EmailAiModelText.Text = await _services.DeepSeek.GetSelectedModelAsync(AiModuleKeys.EmailInbox);
        }
        catch
        {
            EmailAiModelText.Text = "邮件 AI 配置异常";
        }
    }

    private void UpdateComposerState()
    {
        var hasAccount = AccountBox.SelectedItem is EmailAccount;
        var hasRecipient = !string.IsNullOrWhiteSpace(RecipientBox.Text);
        var aiReady = hasAccount && hasRecipient && _services.DeepSeek.HasApiKey(AiModuleKeys.EmailInbox) && !_aiAssisting;
        ComposerAiButton.IsEnabled = aiReady;
        GenerateEmailDraftButton.IsEnabled = aiReady;
        UseEmailDraftButton.IsEnabled = !_aiAssisting && _emailDraft is not null;
        SendButton.IsEnabled = hasAccount && hasRecipient && !_aiAssisting;
    }

    private void UpdateLeadIntelligenceSummary(Lead? lead)
    {
        var current = lead is { HasCurrentAiScore: true };
        var score = current ? lead!.Score : 0;
        var grade = current ? lead!.Grade : "D";
        var confidence = current ? lead!.AnalysisConfidence : 0;
        AiSidebarScoreRing.SetScore(score, grade, confidence);
        AiSidebarConfidenceBar.Value = Math.Clamp(confidence * 100, 0, 100);
        AiSidebarBrainMetaText.Text = lead is null ? "CUSTOMER BRAIN · 等待客户上下文" : "CUSTOMER BRAIN · 正在整合跨渠道证据…";
        AiSidebarConfidenceText.Text = current ? $"AI 置信度 {confidence:P0}" : lead is null ? "等待关联客户" : $"{lead.AnalysisStateLabel} · D / 0";
        AiSidebarProfileText.Text = current && !string.IsNullOrWhiteSpace(lead!.ProfileSummary)
            ? lead.ProfileSummary
            : lead is null ? "选择会话或填写收件邮箱后显示客户画像" : "尚无经过验证的 AI 客户画像";
        AiSidebarNextActionText.Text = current && !string.IsNullOrWhiteSpace(lead!.NextAction)
            ? $"下一步：{lead.NextAction}"
            : "下一步：等待 AI 分析或人工判断";
    }

    private async Task UpdateCustomerBrainSummaryAsync(Lead? lead)
    {
        var generation = ++_customerBrainRefreshGeneration;
        if (lead is null)
        {
            ResetCustomerIntelligenceSummary();
            return;
        }
        try
        {
            var brain = await _services.CustomerBrain.RefreshAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || _lead?.Id != lead.Id) return;
            var facts = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Fact);
            var inferences = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Inference);
            var gaps = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.InformationGap);
            AiSidebarBrainMetaText.Text = $"BRAIN V{brain.Version} · 覆盖 {brain.Coverage.Percentage}% · 事实 {facts} / 判断 {inferences} / 缺口 {gaps}";
            if (!string.IsNullOrWhiteSpace(brain.Summary)) AiSidebarProfileText.Text = brain.Summary;
            if (!string.IsNullOrWhiteSpace(brain.NextBestAction)) AiSidebarNextActionText.Text = $"下一步：{brain.NextBestAction}";
            RenderConversationContext(brain.ConversationContext, loading: true);
            brain = await _services.CustomerBrain.UpdateConversationContextAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || _lead?.Id != lead.Id) return;
            RenderConversationContext(brain.ConversationContext);
        }
        catch (Exception error)
        {
            if (generation != _customerBrainRefreshGeneration || _lead?.Id != lead.Id) return;
            AiSidebarBrainMetaText.Text = $"CUSTOMER BRAIN · 暂不可用：{error.Message}";
            var profile = await _services.CustomerBrain.GetAsync(lead.Id);
            RenderConversationContext(profile?.ConversationContext);
        }
    }

    private void ResetCustomerIntelligenceSummary()
    {
        ++_customerBrainRefreshGeneration;
        AiSidebarScoreRing.SetScore(0, "D", 0);
        AiSidebarConfidenceBar.Value = 0;
        AiSidebarBrainMetaText.Text = "CUSTOMER BRAIN · 等待客户上下文";
        AiSidebarConfidenceText.Text = "等待关联客户";
        AiSidebarProfileText.Text = "选择会话或填写收件邮箱后显示客户画像";
        AiSidebarNextActionText.Text = "下一步：等待客户上下文";
        RenderConversationContext(null);
    }

    private async void RefreshAiContext_Click(object sender, RoutedEventArgs e)
    {
        if (_lead is null) return;
        RefreshAiContextButton.IsEnabled = false;
        AiContextStatusText.Text = "正在重新读取全部 WhatsApp 与邮件历史…";
        try
        {
            var profile = await _services.CustomerBrain.UpdateConversationContextAsync(_lead.Id, force: true);
            if (_lead?.Id == profile.CustomerId) RenderConversationContext(profile.ConversationContext);
        }
        catch (Exception error)
        {
            AiContextStatusText.Text = "更新失败，可重试";
            AiContextMetaText.Text = error.Message;
        }
        finally
        {
            RefreshAiContextButton.IsEnabled = _lead is not null;
        }
    }

    private void RenderConversationContext(CustomerConversationContext? context, bool loading = false)
    {
        RefreshAiContextButton.IsEnabled = _lead is not null && !loading;
        if (_lead is null)
        {
            AiContextStatusText.Text = "等待关联客户";
            AiContextSummaryText.Text = "关联客户后，将综合 WhatsApp 与邮件历史生成态度、性格、语气和当前关系摘要。";
            AiContextMetaText.Text = "客户原文是证据；人工备注与 AI 推断会分开标注。";
            return;
        }
        if (loading && context is not { Status: CustomerContextStatus.Current })
        {
            AiContextStatusText.Text = "正在检查新增上下文…";
            AiContextSummaryText.Text = context?.HasContent == true ? BuildContextText(context) : "正在读取该客户的跨渠道历史。";
            AiContextMetaText.Text = "仅在消息或人工备注发生变化时调用模型。";
            return;
        }
        context ??= new CustomerConversationContext();
        AiContextStatusText.Text = context.Status switch
        {
            CustomerContextStatus.Current => "已更新",
            CustomerContextStatus.Stale => "有新增内容，等待更新",
            CustomerContextStatus.Generating => "正在生成…",
            CustomerContextStatus.NotConfigured => "等待配置 Customer Brain 模型",
            CustomerContextStatus.RetryableFailed => "更新失败，可重试",
            _ => "尚无可总结的沟通"
        };
        AiContextSummaryText.Text = context.HasContent
            ? BuildContextText(context)
            : context.Status == CustomerContextStatus.NotConfigured
                ? "配置模型后，将自动总结该客户的 WhatsApp 与邮件历史。"
                : "该客户尚无可用于总结的 WhatsApp、邮件或人工备注。";
        AiContextMetaText.Text = context.UpdatedAt is null
            ? string.IsNullOrWhiteSpace(context.Error) ? "客户原文是证据；人工备注与 AI 推断会分开标注。" : context.Error
            : $"WhatsApp {context.WhatsAppMessageCount} 条 · 邮件 {context.EmailMessageCount} 条 · {context.AiModel} · {context.UpdatedAt:MM-dd HH:mm}";
    }

    private static string BuildContextText(CustomerConversationContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Overview)) parts.Add(context.Overview);
        if (context.AttitudesAndInterests.Count > 0) parts.Add($"态度与偏好：{string.Join("；", context.AttitudesAndInterests.Take(3))}");
        if (context.PersonalityTraits.Count > 0) parts.Add($"性格倾向：{string.Join("；", context.PersonalityTraits.Take(3))}");
        if (context.CommunicationStyle.Count > 0) parts.Add($"沟通语气：{string.Join("；", context.CommunicationStyle.Take(3))}");
        if (context.ConcernsAndObjections.Count > 0) parts.Add($"关注与异议：{string.Join("；", context.ConcernsAndObjections.Take(3))}");
        if (context.PurchaseSignals.Count > 0) parts.Add($"购买信号：{string.Join("；", context.PurchaseSignals.Take(3))}");
        if (!string.IsNullOrWhiteSpace(context.RelationshipState)) parts.Add($"当前关系：{context.RelationshipState}");
        if (!string.IsNullOrWhiteSpace(context.RecommendedApproach)) parts.Add($"建议沟通：{context.RecommendedApproach}");
        return string.Join(Environment.NewLine, parts);
    }

    private static string EmailAssistantErrorMessage(DeepSeekException error) => error.Code switch
    {
        "provider_not_configured" or "model_not_selected" or "provider_unauthorized" => error.Message,
        "provider_timeout" or "provider_unavailable" or "provider_rate_limited" =>
            $"{error.Message}\n\n草稿没有发送或写入，请稍后重新生成。",
        _ => "本次邮件草稿没有生成完成，系统没有发送邮件或修改客户资料。\n\n请检查写信意图后重新尝试。"
    };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void Email_SynchronizationChanged(object? sender, EmailSynchronizationState state)
    {
        if (state.Imported <= 0 || !IsVisible) return;
        var debounce = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _synchronizationRefreshDebounce, debounce);
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, debounce.Token);
                await Dispatcher
                    .InvokeAsync(async () =>
                    {
                        if (!IsVisible) return;
                        await RefreshAsync();
                        if (_lead is not null) await UpdateCustomerBrainSummaryAsync(_lead);
                    })
                    .Task
                    .Unwrap();
            }
            catch (OperationCanceledException) when (debounce.IsCancellationRequested)
            {
                // A newer synchronization event owns the pending refresh.
            }
            finally
            {
                if (ReferenceEquals(
                        Interlocked.CompareExchange(ref _synchronizationRefreshDebounce, null, debounce),
                        debounce))
                    debounce.Dispose();
            }
        });
    }

    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_conversations).Filter = item => item is EmailConversationItem conversation &&
            (query.Length == 0 || string.Join(' ', conversation.DisplayName, conversation.PeerEmail, conversation.Subject, conversation.LastMessage).Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private sealed record StageChoice(string Label, LeadStage Value);
    private sealed record EmailInboxSnapshot(
        IReadOnlyList<EmailAccount> Accounts,
        string? SelectedAccountId,
        IReadOnlyList<EmailConversationItem> Conversations);

    private sealed class EmailConversationItem(EmailConversation conversation) : INotifyPropertyChanged
    {
        public EmailConversation Conversation { get; } = conversation;
        public string Id => Conversation.Id;
        public string DisplayName => Conversation.DisplayName;
        public string PeerEmail => Conversation.PeerEmail;
        public string Subject => Conversation.Subject;
        public string LastMessage => Conversation.LastMessage;
        public string LastTimeLabel => Conversation.LastTimeLabel;
        public int Unread => Conversation.UnreadCount;
        public string UnreadLabel => Unread > 99 ? "99+" : Unread.ToString();
        public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;

        public void MarkRead(DateTimeOffset readAt)
        {
            Conversation.UnreadCount = 0;
            if (Conversation.LastReadAt is null || readAt > Conversation.LastReadAt)
                Conversation.LastReadAt = readAt;
            OnPropertyChanged(nameof(Unread));
            OnPropertyChanged(nameof(UnreadLabel));
            OnPropertyChanged(nameof(UnreadVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
