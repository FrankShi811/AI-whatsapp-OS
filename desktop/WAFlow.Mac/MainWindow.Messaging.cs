using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Mac;

public sealed partial class MainWindow
{
    private async Task<Control> BuildWhatsAppAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "WhatsApp Conversation Command Center",
            "会话、客户上下文、AI 商机判断与销售动作在同一工作区完成。",
            "LIVE SALES CONVERSATIONS"));
        var accounts = await _services.Repository.GetWhatsAppAccountsAsync(_lifetime.Token);
        if (accounts.Count == 0)
        {
            accounts.Add(new WhatsAppAccount { Id = "primary", Name = "个人号 1" });
            await _services.Repository.SaveWhatsAppAccountsAsync(accounts, _lifetime.Token);
        }
        var active = accounts.FirstOrDefault(item =>
                         item.Id.Equals(_activeWhatsAppAccountId, StringComparison.OrdinalIgnoreCase))
                     ?? accounts[0];
        _activeWhatsAppAccountId = active.Id;
        _services.WhatsApp.SetActiveAccount(active.Id);
        _whatsAppState = _services.WhatsApp.ConnectionStateFor(active.Id) switch
        {
            "connected" => "connected",
            "connecting" => "connecting",
            "logged_out" => "logged_out",
            _ => _whatsAppState == "waiting_qr" ? "waiting_qr" : "disconnected"
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        var accountBox = Accessible(new ComboBox
        {
            ItemsSource = accounts.Select(item => item.DisplayLabel).ToList(),
            SelectedIndex = accounts.IndexOf(active),
            MinWidth = 220
        }, "WhatsApp 账号");
        accountBox.SelectionChanged += async (_, _) =>
        {
            if (accountBox.SelectedIndex < 0 || accountBox.SelectedIndex >= accounts.Count) return;
            _activeWhatsAppAccountId = accounts[accountBox.SelectedIndex].Id;
            _selectedWhatsAppConversationId = "";
            _whatsAppQrDataUrl = "";
            _services.WhatsApp.SetActiveAccount(_activeWhatsAppAccountId);
            await RenderCurrentPageAsync();
        };
        toolbar.Children.Add(accountBox);
        var ip = await _services.PublicIp.CheckAsync(active.Id, _lifetime.Token);
        var stateText = BodyText(
            $"公网 IP：{Fallback(ip.State.CurrentIp, ip.Error.Length > 0 ? ip.Error : "检测中…")} · {ip.State.LocationLabel} · " +
            $"{WhatsAppStateLabel(_whatsAppState)} · {Fallback(active.LinkedPhone, "尚未关联手机号")} · {_operationStatus}",
            _whatsAppState == "connected" ? Primary : Muted,
            12);
        stateText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(stateText, 1);
        toolbar.Children.Add(stateText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        Grid.SetColumn(actions, 2);
        actions.Children.Add(ActionButton("添加账号", async () => await AddWhatsAppAccountAsync(accounts)));
        actions.Children.Add(ActionButton("连接 / 二维码", async () => await ConnectWhatsAppAsync(), primary: _whatsAppState != "connected"));
        actions.Children.Add(ActionButton("同步", async () => await SyncWhatsAppAsync()));
        actions.Children.Add(ActionButton("断开", async () => await DisconnectWhatsAppAsync()));
        actions.Children.Add(ActionButton("退出账号", async () => await LogoutWhatsAppAsync(), danger: true));
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);

        if (_whatsAppState == "waiting_qr" && !string.IsNullOrWhiteSpace(_whatsAppQrDataUrl))
        {
            var image = new Image
            {
                Source = DecodeDataUrl(_whatsAppQrDataUrl),
                Width = 260,
                Height = 260,
                Stretch = Stretch.Uniform
            };
            var qrPanel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
            qrPanel.Children.Add(TitleText("使用手机 WhatsApp 扫码", 20));
            qrPanel.Children.Add(BodyText("手机 WhatsApp → 设置 → 已关联设备 → 关联设备。二维码会自动刷新。"));
            qrPanel.Children.Add(image);
            page.Children.Add(Card(qrPanel, new Thickness(24), Brush.Parse("#F8FBFA")));
        }

        var allConversations = await _services.Repository.GetWhatsAppConversationsAsync(active.Id, _lifetime.Token);
        var conversations = allConversations
            .Where(item => string.IsNullOrWhiteSpace(_whatsAppSearch) ||
                           string.Join(' ', item.DisplayName, item.Phone, item.LastMessage)
                               .Contains(_whatsAppSearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var selected = conversations.FirstOrDefault(item =>
                           item.Id.Equals(_selectedWhatsAppConversationId, StringComparison.OrdinalIgnoreCase))
                       ?? conversations.FirstOrDefault();
        _selectedWhatsAppConversationId = selected?.Id ?? "";
        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*,390"), ColumnSpacing = 14 };
        var conversationPanel = new StackPanel { Spacing = 7 };
        var conversationHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        conversationHeader.Children.Add(TitleText($"会话与联系人 · {allConversations.Count:N0}", 17));
        var group = ActionButton("新建群组", async () => await CreateWhatsAppGroupAsync(), primary: false);
        group.IsEnabled = _whatsAppState == "connected";
        Grid.SetColumn(group, 1);
        conversationHeader.Children.Add(group);
        conversationPanel.Children.Add(conversationHeader);
        var search = Accessible(new TextBox
        {
            Text = _whatsAppSearch,
            Watermark = "输入姓名、WhatsApp 名称或号码，实时搜索"
        }, "搜索 WhatsApp 会话");
        search.KeyUp += async (_, _) =>
        {
            var value = search.Text?.Trim() ?? "";
            if (value == _whatsAppSearch) return;
            _whatsAppSearch = value;
            await RenderCurrentPageAsync();
        };
        conversationPanel.Children.Add(search);
        foreach (var conversation in conversations.Take(300))
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = conversation.Id == _selectedWhatsAppConversationId ? PrimarySoft : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10)
            };
            var item = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            item.Children.Add(TextCell(
                Fallback(conversation.DisplayName, conversation.IsGroup ? "WhatsApp 群组" : "+" + conversation.Phone),
                conversation.Id == _selectedWhatsAppConversationId,
                conversation.LastMessage));
            if (conversation.UnreadCount > 0)
            {
                var unread = BadgeCell(conversation.UnreadCount.ToString(), PrimarySoft);
                Grid.SetColumn(unread, 1);
                item.Children.Add(unread);
            }
            button.Content = item;
            button.Click += async (_, _) =>
            {
                _selectedWhatsAppConversationId = conversation.Id;
                await _services.Repository.MarkWhatsAppConversationReadAsync(conversation.Id, _lifetime.Token);
                await RenderCurrentPageAsync();
            };
            conversationPanel.Children.Add(button);
        }
        if (conversations.Count == 0)
            conversationPanel.Children.Add(EmptyState(
                "暂无 WhatsApp 会话",
                _whatsAppState == "connected"
                    ? "点击“同步”从手机获取联系人和历史消息。"
                    : "点击“连接 / 二维码”扫码登录。"));
        var conversationScroll = new ScrollViewer
        {
            Content = conversationPanel,
            MaxHeight = 660,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        workspace.Children.Add(Card(conversationScroll, new Thickness(12)));

        var composer = Accessible(new TextBox
        {
            Watermark = selected?.IsGroup == true
                ? "当前群组暂只读；请在手机 WhatsApp 中发送。"
                : "Enter 发送，⌘Enter 换行",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            IsEnabled = _whatsAppState == "connected" && selected is { IsGroup: false }
        }, "WhatsApp 消息内容");
        var chat = await BuildWhatsAppConversationAsync(selected, composer);
        Grid.SetColumn(chat, 1);
        workspace.Children.Add(chat);
        var intelligence = await BuildWhatsAppIntelligenceAsync(selected, composer);
        Grid.SetColumn(intelligence, 2);
        workspace.Children.Add(intelligence);
        page.Children.Add(workspace);
        return page;
    }

    private async Task<Control> BuildWhatsAppConversationAsync(WhatsAppConversation? conversation, TextBox composer)
    {
        if (conversation is null)
            return Card(EmptyState("选择会话", "登录并同步后，在左侧选择客户或群组。"), new Thickness(14));
        var panel = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(TextCell(
            Fallback(conversation.DisplayName, "+" + conversation.Phone),
            true,
            conversation.IsGroup ? "WhatsApp 群组" : Fallback(conversation.Phone, conversation.Jid)));
        var pin = ActionButton(conversation.IsPinned ? "取消置顶" : "置顶", async () =>
        {
            await _services.WhatsApp.SetChatPinnedAsync(
                conversation.AccountId,
                conversation.Phone,
                !conversation.IsPinned,
                _lifetime.Token);
            await RenderCurrentPageAsync();
        });
        pin.IsEnabled = _whatsAppState == "connected" && !conversation.IsGroup;
        Grid.SetColumn(pin, 1);
        header.Children.Add(pin);
        panel.Children.Add(header);

        var messages = await _services.Repository.GetWhatsAppMessagesAsync(conversation.Id, 300, _lifetime.Token);
        var messagePanel = new StackPanel { Spacing = 8 };
        WhatsAppMessage? replyTo = null;
        var replyPreview = new Border
        {
            IsVisible = false,
            Background = PrimarySoft,
            BorderBrush = Primary,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7)
        };
        var replyPreviewText = BodyText("", Ink, 11);
        replyPreview.Child = replyPreviewText;
        Button? cancelReply = null;
        foreach (var message in messages.TakeLast(200))
        {
            var outgoing = message.Direction == WhatsAppMessageDirection.Outgoing;
            var body = message.IsRevoked
                ? "此消息已撤回"
                : string.IsNullOrWhiteSpace(message.Body)
                    ? $"[{Fallback(message.Kind, "媒体")}] {message.FileName}"
                    : message.Body;
            var bubble = new StackPanel { Spacing = 4 };
            if (conversation.IsGroup && !outgoing && !string.IsNullOrWhiteSpace(message.ParticipantName))
                bubble.Children.Add(BodyText(message.ParticipantName, Primary, 10));
            bubble.Children.Add(BodyText(body, Ink, 13));
            bubble.Children.Add(BodyText(
                $"{message.Timestamp.LocalDateTime:MM-dd HH:mm} · {message.Status}",
                Muted,
                9));
            if (!message.IsRevoked && !conversation.IsGroup)
            {
                var messageActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var reply = ActionButton("回复", () =>
                {
                    replyTo = message;
                    replyPreviewText.Text = $"回复：{Fallback(message.Body, message.FileName)}";
                    replyPreview.IsVisible = true;
                    if (cancelReply is not null) cancelReply.IsVisible = true;
                    composer.Focus();
                    return Task.CompletedTask;
                });
                reply.IsEnabled = composer.IsEnabled;
                messageActions.Children.Add(reply);
                if (!string.IsNullOrWhiteSpace(message.MediaPath) && File.Exists(message.MediaPath))
                    messageActions.Children.Add(ActionButton("打开媒体", () =>
                    {
                        Process.Start(new ProcessStartInfo { FileName = message.MediaPath, UseShellExecute = true });
                        return Task.CompletedTask;
                    }));
                if (outgoing && !string.IsNullOrWhiteSpace(message.ProviderMessageId))
                    messageActions.Children.Add(ActionButton("撤回", async () =>
                    {
                        if (!await ConfirmAsync("撤回 WhatsApp 消息", "WhatsApp 是否允许撤回取决于服务器时间限制；确认尝试撤回？", "确认撤回")) return;
                        await _services.WhatsApp.RevokeMessageAsync(
                            conversation.AccountId,
                            conversation.Phone,
                            message.ProviderMessageId,
                            _lifetime.Token);
                        await _services.Repository.MarkWhatsAppMessageRevokedAsync(
                            conversation.AccountId,
                            message.ProviderMessageId,
                            DateTimeOffset.Now,
                            _lifetime.Token);
                        await RenderCurrentPageAsync();
                    }, danger: true));
                bubble.Children.Add(messageActions);
            }
            messagePanel.Children.Add(new Border
            {
                Background = outgoing ? Brush.Parse("#DCF7EE") : Surface,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11, 8),
                HorizontalAlignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 680,
                Child = bubble
            });
        }
        if (messages.Count == 0)
            messagePanel.Children.Add(EmptyState("暂无消息", "同步历史或从下方发送第一条消息。"));
        panel.Children.Add(new ScrollViewer
        {
            Content = messagePanel,
            MaxHeight = 460,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });

        panel.Children.Add(replyPreview);
        panel.Children.Add(composer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var ai = ActionButton("AI 回复建议", async () =>
        {
            try
            {
                var lead = string.IsNullOrWhiteSpace(conversation.LeadId)
                    ? null
                    : await _services.Repository.GetLeadAsync(conversation.LeadId, _lifetime.Token);
                var result = await _services.ConversationAssistant.AnalyzeAsync(conversation.Id, lead, _lifetime.Token);
                composer.Text = result.ReplyText;
                await ShowMessageAsync(
                    "AI 建议已填入输入框",
                    $"客户意图：{result.CustomerIntent}\n建议动作：{result.RecommendedNextAction}\n置信度：{result.Confidence:P0}\n\n请人工核对后再发送。");
            }
            catch (Exception error) { await ShowMessageAsync("AI 回复生成失败", error.Message); }
        });
        ai.IsEnabled = composer.IsEnabled;
        var media = ActionButton("发送媒体", async () =>
        {
            var path = await PickOpenFileAsync("选择要发送的文件", "*.*");
            if (string.IsNullOrWhiteSpace(path)) return;
            if (replyTo is null)
                await _services.WhatsApp.SendMediaAsync(
                    conversation.AccountId,
                    conversation.Phone,
                    path,
                    composer.Text ?? "",
                    _lifetime.Token);
            else
                await _services.WhatsApp.SendReplyMediaAsync(
                    conversation.AccountId,
                    conversation.Phone,
                    path,
                    composer.Text ?? "",
                    replyTo.ProviderMessageId,
                    Fallback(replyTo.Body, replyTo.FileName),
                    replyTo.Direction == WhatsAppMessageDirection.Outgoing,
                    _lifetime.Token);
            composer.Text = "";
            replyTo = null;
            replyPreview.IsVisible = false;
            if (cancelReply is not null) cancelReply.IsVisible = false;
        });
        media.IsEnabled = composer.IsEnabled;
        async Task SendCurrentAsync()
        {
            if (string.IsNullOrWhiteSpace(composer.Text))
            {
                await ShowMessageAsync("无法发送", "请输入消息内容。");
                return;
            }
            if (replyTo is null)
                await _services.WhatsApp.SendTextAsync(
                    conversation.AccountId,
                    conversation.Phone,
                    composer.Text.Trim(),
                    _lifetime.Token);
            else
                await _services.WhatsApp.SendReplyTextAsync(
                    conversation.AccountId,
                    conversation.Phone,
                    composer.Text.Trim(),
                    replyTo.ProviderMessageId,
                    Fallback(replyTo.Body, replyTo.FileName),
                    replyTo.Direction == WhatsAppMessageDirection.Outgoing,
                    _lifetime.Token);
            composer.Text = "";
            replyTo = null;
            replyPreview.IsVisible = false;
            if (cancelReply is not null) cancelReply.IsVisible = false;
            await Task.Delay(300, _lifetime.Token);
            await RenderCurrentPageAsync();
        }
        composer.KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) ||
                e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
            e.Handled = true;
            await SendCurrentAsync();
        };
        cancelReply = ActionButton("取消回复", () =>
        {
            replyTo = null;
            replyPreview.IsVisible = false;
            if (cancelReply is not null) cancelReply.IsVisible = false;
            return Task.CompletedTask;
        });
        cancelReply.IsVisible = false;
        var send = ActionButton("发送消息", SendCurrentAsync, primary: true);
        send.IsEnabled = composer.IsEnabled;
        actions.Children.Add(ai);
        actions.Children.Add(cancelReply);
        actions.Children.Add(media);
        actions.Children.Add(send);
        panel.Children.Add(actions);
        return Card(panel);
    }

    private async Task<Control> BuildWhatsAppIntelligenceAsync(
        WhatsAppConversation? conversation,
        TextBox composer)
    {
        if (conversation is null)
            return Card(EmptyState("Customer Intelligence", "选择会话后关联客户并显示 AI 销售上下文。"), new Thickness(14));

        var context = await _services.CustomerSuccessAgent.GetContextAsync(
            conversation.AccountId,
            conversation.Id,
            _lifetime.Token);
        var lead = context?.Customer ??
                   (!string.IsNullOrWhiteSpace(conversation.LeadId)
                       ? await _services.Repository.GetLeadAsync(conversation.LeadId, _lifetime.Token)
                       : null);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(TitleText("Customer Intelligence", 18));
        panel.Children.Add(BodyText(
            lead is null ? "选择会话后关联客户" : $"已关联：{lead.DisplayName} · {lead.StageLabel}",
            lead is null ? Muted : Primary,
            11));

        var state = context?.AgentState ?? new ConversationAgentState
        {
            CustomerId = lead?.Id ?? "",
            AccountId = conversation.AccountId,
            ConversationId = conversation.Id,
            Mode = ConversationAgentMode.SuggestOnly
        };
        var agent = new StackPanel { Spacing = 9 };
        agent.Children.Add(BodyText("DHgate Customer Success Agent", ResourceBrush("AiAccent", "#6659B8"), 11));
        agent.Children.Add(BodyText(
            lead is null ? "等待会话身份" : $"{lead.DisplayName} · {CustomerSuccessAgentLabels.Mode(state.Mode)}",
            Ink,
            12));
        var allowedModes = new[]
        {
            ConversationAgentMode.AutoOff,
            ConversationAgentMode.SuggestOnly,
            ConversationAgentMode.CopilotActive,
            ConversationAgentMode.AutoActive
        };
        var mode = Accessible(new ComboBox
        {
            ItemsSource = allowedModes.Select(CustomerSuccessAgentLabels.Mode).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(allowedModes, state.Mode))
        }, "客户成功 Agent 模式");
        var apply = ActionButton("应用", async () =>
        {
            if (lead is null) throw new InvalidOperationException("当前会话尚未关联客户。");
            var selectedMode = allowedModes[Math.Clamp(mode.SelectedIndex, 0, allowedModes.Length - 1)];
            if (selectedMode == ConversationAgentMode.AutoActive &&
                !await ConfirmAsync(
                    "开启自动回复",
                    "自动回复只会在身份、账号锁和安全校验全部通过时发送；高风险问题会转人工。确认开启当前客户的自动回复？",
                    "确认开启"))
                return;
            await _services.CustomerSuccessAgent.SetModeAsync(
                lead.Id,
                conversation.AccountId,
                conversation.Id,
                selectedMode,
                cancellationToken: _lifetime.Token);
            await RenderCurrentPageAsync();
        });
        apply.IsEnabled = lead is not null;
        var modeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        modeRow.Children.Add(mode);
        Grid.SetColumn(apply, 1);
        modeRow.Children.Add(apply);
        agent.Children.Add(modeRow);
        agent.Children.Add(BodyText(CustomerSuccessAgentLabels.ModeHeadline(state.Mode), Ink, 11));
        agent.Children.Add(BodyText(CustomerSuccessAgentLabels.ModeTrigger(state.Mode), Muted, 10));
        agent.Children.Add(BodyText(CustomerSuccessAgentLabels.ModeOutput(state.Mode), Muted, 10));
        agent.Children.Add(BodyText(CustomerSuccessAgentLabels.ModeSend(state.Mode), Muted, 10));
        agent.Children.Add(BodyText(
            Fallback(state.StateReason, CustomerSuccessAgentLabels.ModeStateReason(state.Mode)),
            Muted,
            10));

        var lastRun = new StackPanel { Spacing = 6 };
        lastRun.Children.Add(BodyText("最近一次 Agent 产出", Ink, 12));
        lastRun.Children.Add(BodyText(
            state.LastRunAt is null ? "尚无产出" : $"{state.LastRunStatus} · {state.LastRunAt:MM-dd HH:mm}",
            state.LastRunError.Length > 0 ? Danger : Primary,
            10));
        lastRun.Children.Add(BodyText($"客户原话：{Fallback(state.LastSourcePreview, "等待生成")}", Ink, 10));
        lastRun.Children.Add(BodyText(Fallback(state.LastGeneratedReply, "生成的建议回复会显示在这里。"), Ink, 11));
        lastRun.Children.Add(BodyText($"分析摘要：{Fallback(state.LastRunSummary, "—")}", Muted, 10));
        lastRun.Children.Add(BodyText($"下一步：{Fallback(state.LastRecommendedAction, "—")}", Muted, 10));
        var agentActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var generate = ActionButton("立即生成建议", async () =>
        {
            var result = await _services.CustomerSuccessAgent.AnalyzeAsync(
                conversation.AccountId,
                conversation.Id,
                conversation.Phone,
                conversation.DisplayName,
                conversation.Jid,
                cancellationToken: _lifetime.Token);
            if (result.Decision is null)
                throw new InvalidOperationException(Fallback(result.BlockReason, "当前身份或安全状态不允许生成。"));
            composer.Text = result.Decision.ReplyText;
            await ShowMessageAsync(
                "Agent 建议已填入输入框",
                $"分析摘要：{result.Decision.ChineseSummary}\n下一步：{result.Decision.RecommendedNextAction}\n置信度：{result.Decision.Confidence:P0}\n\n请人工核对后发送。");
            await RenderCurrentPageAsync();
        }, primary: true);
        generate.IsEnabled = lead is not null && state.Mode != ConversationAgentMode.AutoOff;
        var use = ActionButton("填入输入框", () =>
        {
            composer.Text = state.LastGeneratedReply;
            return Task.CompletedTask;
        });
        use.IsEnabled = !string.IsNullOrWhiteSpace(state.LastGeneratedReply) && composer.IsEnabled;
        agentActions.Children.Add(generate);
        agentActions.Children.Add(use);
        lastRun.Children.Add(agentActions);
        agent.Children.Add(Card(lastRun, new Thickness(12), ResourceBrush("AiSurface", "#F4F1FF")));
        panel.Children.Add(SectionCard("AI 销售协作", CustomerSuccessAgentLabels.Mode(state.Mode), agent));

        var sourcing = context?.SourcingRequest;
        var sourcingPanel = new StackPanel { Spacing = 7 };
        sourcingPanel.Children.Add(TitleText($"{sourcing?.Completeness ?? 0}%", 22));
        sourcingPanel.Children.Add(new ProgressBar { Maximum = 100, Value = sourcing?.Completeness ?? 0 });
        sourcingPanel.Children.Add(BodyText(
            sourcing is null ? "尚未建立采购需求" : $"状态：{sourcing.Status} · V{sourcing.Version}",
            Ink,
            11));
        sourcingPanel.Children.Add(BodyText(
            sourcing is null || sourcing.MissingFields.Count > 0
                ? $"缺失：{string.Join("、", (sourcing?.MissingFields ?? Enum.GetValues<SourcingFieldKey>()).Select(SourcingFieldLabel))}"
                : "五项采购需求已完整",
            sourcing?.Conflicts.Count > 0 ? Warning : Muted,
            10));
        if (sourcing?.Conflicts.Count > 0)
            sourcingPanel.Children.Add(BodyText($"冲突：{string.Join("；", sourcing.Conflicts.Where(item => !item.IsResolved).Select(item => SourcingFieldLabel(item.Field)))}", Warning, 10));
        panel.Children.Add(SectionCard("采购需求五要素", "SOURCING", sourcingPanel));

        if (context?.OpenHandoff is { } handoff)
        {
            var handoffPanel = new StackPanel { Spacing = 7 };
            handoffPanel.Children.Add(BodyText(handoff.Reason, Danger, 11));
            handoffPanel.Children.Add(BodyText($"客户原话：{handoff.OriginalMessage}", Ink, 10));
            handoffPanel.Children.Add(BodyText($"中文辅助：{handoff.ChineseAssistTranslation}", Muted, 10));
            handoffPanel.Children.Add(BodyText($"暂停消息：{handoff.PausedMessageCount}", Muted, 10));
            var handoffActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            handoffActions.Children.Add(ActionButton("人工接管", async () =>
            {
                await _services.CustomerSuccessAgent.TakeOverAsync(context.CustomerId, "macOS 用户", _lifetime.Token);
                await RenderCurrentPageAsync();
            }, danger: true));
            handoffActions.Children.Add(ActionButton("标记解决", async () =>
            {
                await _services.CustomerSuccessAgent.ResolveHandoffAsync(context.CustomerId, "macOS 客户端人工确认已处理", _lifetime.Token);
                await RenderCurrentPageAsync();
            }));
            handoffActions.Children.Add(ActionButton("复核并恢复", async () =>
            {
                await _services.CustomerSuccessAgent.ResumeAsync(
                    context.CustomerId,
                    conversation.AccountId,
                    conversation.Id,
                    ConversationAgentMode.SuggestOnly,
                    _lifetime.Token);
                await RenderCurrentPageAsync();
            }, primary: true));
            handoffPanel.Children.Add(handoffActions);
            panel.Children.Add(SectionCard("需要人工处理", "HUMAN REQUIRED", handoffPanel));
        }

        var brain = context?.Brain;
        var brainPanel = new StackPanel { Spacing = 7 };
        brainPanel.Children.Add(BodyText(
            brain is null ? "CUSTOMER BRAIN · 等待客户上下文" : $"CUSTOMER BRAIN · {brain.CoverageLabel}",
            ResourceBrush("AiAccent", "#6659B8"),
            10));
        brainPanel.Children.Add(TitleText(
            brain is null ? "等待 AI 分析" : $"{brain.Confidence:P0} 置信度",
            18));
        brainPanel.Children.Add(new ProgressBar { Maximum = 100, Value = (brain?.Confidence ?? 0) * 100 });
        brainPanel.Children.Add(BodyText(
            brain?.Summary ?? lead?.ProfileSummary ?? "选择已关联客户后显示画像",
            Ink,
            10));
        brainPanel.Children.Add(BodyText(
            $"下一步：{brain?.NextBestAction ?? lead?.NextAction ?? "等待客户上下文"}",
            Primary,
            10));
        if (lead is not null)
            brainPanel.Children.Add(ActionButton("编辑并同步客户资料", async () => await ShowLeadEditorAsync(lead)));
        panel.Children.Add(SectionCard("AI SALES BRIEF", "CUSTOMER BRAIN", brainPanel));

        var knowledge = brain?.KnowledgeReferences ?? [];
        var knowledgePanel = new StackPanel { Spacing = 6 };
        knowledgePanel.Children.Add(BodyText(
            knowledge.Count == 0 ? "尚未执行知识检索" : $"本次建议参考 {knowledge.Count:N0} 条已批准知识",
            Muted,
            10));
        foreach (var hit in knowledge.Take(5))
            knowledgePanel.Children.Add(TextCell(hit.CitationLabel, true, hit.Content));
        panel.Children.Add(SectionCard("本次建议参考", "RAG SOURCES", knowledgePanel));

        return Card(new ScrollViewer
        {
            Content = panel,
            MaxHeight = 720,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, new Thickness(12));
    }

    private static string SourcingFieldLabel(SourcingFieldKey value) => value switch
    {
        SourcingFieldKey.ProductImage => "产品图片/链接",
        SourcingFieldKey.Quantity => "数量",
        SourcingFieldKey.TargetPrice => "目标价",
        SourcingFieldKey.Destination => "目的地",
        SourcingFieldKey.ShippingPreference => "运输偏好",
        _ => value.ToString()
    };

    private async Task AddWhatsAppAccountAsync(List<WhatsAppAccount> accounts)
    {
        var next = new WhatsAppAccount
        {
            Id = $"personal_{Guid.NewGuid():N}"[..29],
            Name = $"个人号 {accounts.Count + 1}"
        };
        accounts.Add(next);
        await _services.Repository.SaveWhatsAppAccountsAsync(accounts, _lifetime.Token);
        _activeWhatsAppAccountId = next.Id;
        _selectedWhatsAppConversationId = "";
        await RenderCurrentPageAsync();
    }

    private async Task ConnectWhatsAppAsync()
    {
        try
        {
            _whatsAppState = "connecting";
            _operationStatus = "正在启动本机 WhatsApp Bridge…";
            await _services.WhatsApp.ConnectAsync(_activeWhatsAppAccountId, _lifetime.Token);
        }
        catch (Exception error)
        {
            _whatsAppState = "disconnected";
            await ShowMessageAsync("WhatsApp 连接失败", error.Message);
        }
        await RenderCurrentPageAsync();
    }

    private async Task SyncWhatsAppAsync()
    {
        if (!_services.WhatsApp.IsConnectedFor(_activeWhatsAppAccountId))
        {
            await ShowMessageAsync("尚未连接", "请先扫码连接当前 WhatsApp 账号。");
            return;
        }
        _operationStatus = "正在请求 WhatsApp 同步…";
        await _services.WhatsApp.SyncNowAsync(_activeWhatsAppAccountId, _lifetime.Token);
    }

    private async Task DisconnectWhatsAppAsync()
    {
        await _services.Campaigns.PauseAccountAsync(
            _activeWhatsAppAccountId,
            "用户在 macOS 客户端手动断开 WhatsApp。",
            _lifetime.Token);
        await _services.WhatsApp.DisconnectAsync(_activeWhatsAppAccountId, _lifetime.Token);
        _whatsAppState = "disconnected";
        await RenderCurrentPageAsync();
    }

    private async Task LogoutWhatsAppAsync()
    {
        if (!await ConfirmAsync(
                "退出 WhatsApp 账号",
                "退出会删除本机加密登录会话，下次需要重新扫码；已同步的联系人和消息仍保存在本机。",
                "确认退出"))
            return;
        await _services.Campaigns.PauseAccountAsync(
            _activeWhatsAppAccountId,
            "用户退出 WhatsApp，活动任务已暂停。",
            _lifetime.Token);
        await _services.WhatsApp.LogoutAsync(_activeWhatsAppAccountId, _lifetime.Token);
        _whatsAppState = "logged_out";
        _whatsAppQrDataUrl = "";
        await RenderCurrentPageAsync();
    }

    private async Task CreateWhatsAppGroupAsync()
    {
        if (!_services.WhatsApp.IsConnectedFor(_activeWhatsAppAccountId))
        {
            await ShowMessageAsync("无法建群", "请先连接当前 WhatsApp 账号。");
            return;
        }
        var subject = new TextBox { Watermark = "群组名称（1–100 个字符）" };
        var phones = new TextBox
        {
            Watermark = "每行一个国际号码，例如 +14155552671",
            AcceptsReturn = true,
            MinHeight = 180
        };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(TitleText("建立 WhatsApp 群组", 23));
        panel.Children.Add(Field("群组名称", subject));
        panel.Children.Add(Field("成员号码", phones, "至少 1 位，系统会校验国际号码格式。"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = DialogWindow("建立 WhatsApp 群组", panel, 620, 540);
        var cancel = new Button { Content = "取消" };
        var create = new Button { Content = "建立群组" };
        create.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(false);
        create.Click += (_, _) => dialog.Close(true);
        row.Children.Add(cancel);
        row.Children.Add(create);
        panel.Children.Add(row);
        if (!await dialog.ShowDialog<bool>(this)) return;
        var members = (phones.Text ?? "")
            .Split(['\r', '\n', ',', '，', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => PhoneNormalizer.Normalize(value, null))
            .Where(value => value.Valid)
            .Select(value => value.E164)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (string.IsNullOrWhiteSpace(subject.Text) || members.Count == 0)
        {
            await ShowMessageAsync("无法建立群组", "请填写有效群名称和至少一个国际号码。");
            return;
        }
        var result = await _services.WhatsApp.CreateGroupAsync(
            _activeWhatsAppAccountId,
            new WhatsAppGroupCreateRequest(subject.Text.Trim(), members),
            _lifetime.Token);
        await ShowMessageAsync("群组已建立", $"“{result.Subject}”已建立，成员 {result.ParticipantCount:N0} 位。");
        await SyncWhatsAppAsync();
    }

    private async Task<Control> BuildEmailAsync()
    {
        var page = PageStack();
        page.Children.Add(PageLead(
            "邮件 Inbox",
            "连接邮箱、收发邮件、保存历史，并通过邮箱地址与客户列表和商机智能共享同一客户资料。",
            "CUSTOMER EMAIL INTELLIGENCE"));
        var accounts = await _services.Repository.GetEmailAccountsAsync(_lifetime.Token);
        if (accounts.Count > 0 &&
            !accounts.Any(item => item.Id.Equals(_activeEmailAccountId, StringComparison.OrdinalIgnoreCase)))
            _activeEmailAccountId = accounts[0].Id;
        var active = accounts.FirstOrDefault(item =>
            item.Id.Equals(_activeEmailAccountId, StringComparison.OrdinalIgnoreCase));

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        var accountBox = Accessible(new ComboBox
        {
            ItemsSource = accounts.Select(item => item.DisplayLabel).ToList(),
            SelectedIndex = active is null ? -1 : accounts.IndexOf(active),
            MinWidth = 260
        }, "邮箱账号");
        accountBox.SelectionChanged += async (_, _) =>
        {
            if (accountBox.SelectedIndex < 0 || accountBox.SelectedIndex >= accounts.Count) return;
            _activeEmailAccountId = accounts[accountBox.SelectedIndex].Id;
            _selectedEmailConversationId = "";
            await RenderCurrentPageAsync();
        };
        toolbar.Children.Add(accountBox);
        var status = BodyText(
            active is null
                ? "连接 Gmail、Outlook、Yahoo、iCloud 或自定义企业邮箱"
                : $"{active.StatusLabel} · {Fallback(active.LastError, active.LastSyncAt is null ? "尚未同步" : $"上次同步 {active.LastSyncAt:MM-dd HH:mm}")}",
            active?.Status == EmailConnectionStatus.Error ? Danger : Muted,
            12);
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 1);
        toolbar.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(actions, 2);
        actions.Children.Add(ActionButton("连接邮箱", async () => await ShowEmailAccountEditorAsync(null), primary: active is null));
        var manage = ActionButton("管理账号", async () => await ShowEmailAccountEditorAsync(active));
        manage.IsEnabled = active is not null;
        actions.Children.Add(manage);
        var sync = ActionButton("立即同步", async () => await SyncEmailAsync(active));
        sync.IsEnabled = active is not null;
        actions.Children.Add(sync);
        toolbar.Children.Add(actions);
        page.Children.Add(toolbar);

        if (active is null)
        {
            page.Children.Add(EmptyState(
                "尚未连接邮箱",
                "点击“连接邮箱”，填写邮箱地址和应用专用密码。Gmail / Yahoo / iCloud 会自动带入服务器参数。"));
            return page;
        }

        var allConversations = await _services.Repository.GetEmailConversationsAsync(active.Id, _lifetime.Token);
        var conversations = allConversations
            .Where(item => string.IsNullOrWhiteSpace(_emailSearch) ||
                           string.Join(' ', item.DisplayName, item.PeerEmail, item.Subject, item.LastMessage)
                               .Contains(_emailSearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var composingNew = _selectedEmailConversationId == "__new__";
        var selected = composingNew
            ? null
            : conversations.FirstOrDefault(item =>
                  item.Id.Equals(_selectedEmailConversationId, StringComparison.OrdinalIgnoreCase))
              ?? conversations.FirstOrDefault();
        if (!composingNew) _selectedEmailConversationId = selected?.Id ?? "";
        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*,390"), ColumnSpacing = 14 };
        var list = new StackPanel { Spacing = 7 };
        var listHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        listHeader.Children.Add(TitleText($"邮件会话 · {allConversations.Count:N0}", 17));
        var newEmail = ActionButton("＋", async () =>
        {
            _selectedEmailConversationId = "__new__";
            await RenderCurrentPageAsync();
        }, minWidth: 42);
        AutomationProperties.SetName(newEmail, "新建邮件");
        Grid.SetColumn(newEmail, 1);
        listHeader.Children.Add(newEmail);
        list.Children.Add(listHeader);
        var search = Accessible(new TextBox
        {
            Text = _emailSearch,
            Watermark = "搜索姓名、邮箱、主题或正文"
        }, "搜索邮件会话");
        search.KeyUp += async (_, _) =>
        {
            var value = search.Text?.Trim() ?? "";
            if (value == _emailSearch) return;
            _emailSearch = value;
            await RenderCurrentPageAsync();
        };
        list.Children.Add(search);
        foreach (var conversation in conversations.Take(300))
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = conversation.Id == _selectedEmailConversationId ? PrimarySoft : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10)
            };
            button.Content = TextCell(
                conversation.DisplayName,
                conversation.Id == _selectedEmailConversationId,
                $"{conversation.Subject} · {conversation.LastMessage}");
            button.Click += async (_, _) =>
            {
                _selectedEmailConversationId = conversation.Id;
                await _services.Repository.MarkEmailConversationReadAsync(conversation.Id, _lifetime.Token);
                await RenderCurrentPageAsync();
            };
            list.Children.Add(button);
        }
        if (conversations.Count == 0)
            list.Children.Add(EmptyState("暂无邮件会话", "点击“立即同步”，或在右侧发送新邮件。"));
        workspace.Children.Add(Card(new ScrollViewer
        {
            Content = list,
            MaxHeight = 660,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, new Thickness(12)));
        var detail = await BuildEmailConversationAsync(active, selected);
        Grid.SetColumn(detail, 1);
        workspace.Children.Add(detail);
        var intelligence = await BuildEmailIntelligenceAsync(selected);
        Grid.SetColumn(intelligence, 2);
        workspace.Children.Add(intelligence);
        page.Children.Add(workspace);
        return page;
    }

    private async Task<Control> BuildEmailConversationAsync(EmailAccount account, EmailConversation? conversation)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(TitleText(conversation is null ? "新建邮件" : conversation.DisplayName, 18));
        var messages = conversation is null
            ? []
            : await _services.Repository.GetEmailMessagesAsync(conversation.Id, 200, _lifetime.Token);
        if (messages.Count > 0)
        {
            var messagePanel = new StackPanel { Spacing = 8 };
            foreach (var message in messages.TakeLast(100))
            {
                var outgoing = message.Direction == EmailMessageDirection.Outgoing;
                var content = new StackPanel { Spacing = 5 };
                content.Children.Add(BodyText(
                    $"{(outgoing ? "发给" : "来自")} {Fallback(outgoing ? string.Join(", ", message.ToAddresses) : message.FromAddress, "未知邮箱")} · {message.TimeLabel}",
                    Muted,
                    10));
                content.Children.Add(TextCell(message.Subject, true, message.TextBody));
                messagePanel.Children.Add(new Border
                {
                    Background = outgoing ? Brush.Parse("#E6F7F1") : Surface,
                    BorderBrush = Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12),
                    Child = content
                });
            }
            panel.Children.Add(new ScrollViewer
            {
                Content = messagePanel,
                MaxHeight = 330,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            });
        }
        var recipient = new TextBox { Text = conversation?.PeerEmail ?? "", Watermark = "customer@example.com" };
        var subject = new TextBox
        {
            Text = conversation is null
                ? ""
                : conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                    ? conversation.Subject
                    : "Re: " + conversation.Subject
        };
        var body = new TextBox
        {
            Watermark = "输入邮件正文，或告诉 AI 想表达什么。",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150
        };
        panel.Children.Add(Field("收件人", recipient));
        panel.Children.Add(Field("主题", subject));
        panel.Children.Add(Field("正文 / AI 写信意图", body));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("AI 生成草稿", async () =>
        {
            try
            {
                var lead = string.IsNullOrWhiteSpace(conversation?.LeadId)
                    ? await _services.Repository.GetLeadByEmailAsync(recipient.Text ?? "", _lifetime.Token)
                    : await _services.Repository.GetLeadAsync(conversation!.LeadId, _lifetime.Token);
                var result = await _services.EmailAssistant.AnalyzeAsync(
                    account.Id,
                    conversation?.Id,
                    recipient.Text ?? "",
                    lead,
                    body.Text ?? "",
                    subject.Text ?? "",
                    body.Text ?? "",
                    _lifetime.Token);
                subject.Text = result.Subject;
                body.Text = result.Body;
                await ShowMessageAsync(
                    "AI 邮件草稿已生成",
                    $"客户意图：{result.CustomerIntent}\n下一步：{result.RecommendedNextAction}\n置信度：{result.Confidence:P0}\n\n草稿不会自动发送，请人工核对。");
            }
            catch (Exception error) { await ShowMessageAsync("AI 草稿生成失败", error.Message); }
        }));
        actions.Children.Add(ActionButton("发送邮件", async () =>
        {
            try
            {
                var lead = await _services.Repository.GetLeadByEmailAsync(recipient.Text ?? "", _lifetime.Token);
                await _services.Email.SendAsync(
                    account.Id,
                    recipient.Text ?? "",
                    subject.Text ?? "",
                    body.Text ?? "",
                    lead?.Id,
                    cancellationToken: _lifetime.Token);
                body.Text = "";
                await ShowMessageAsync("邮件已发送", $"邮件已通过 {account.EmailAddress} 发送。");
                await RenderCurrentPageAsync();
            }
            catch (Exception error) { await ShowMessageAsync("邮件发送失败", error.Message); }
        }, primary: true));
        panel.Children.Add(actions);
        return Card(panel);
    }

    private async Task<Control> BuildEmailIntelligenceAsync(EmailConversation? conversation)
    {
        var lead = conversation is null
            ? null
            : !string.IsNullOrWhiteSpace(conversation.LeadId)
                ? await _services.Repository.GetLeadAsync(conversation.LeadId, _lifetime.Token)
                : await _services.Repository.GetLeadByEmailAsync(conversation.PeerEmail, _lifetime.Token);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(TitleText("Customer Intelligence", 18));
        panel.Children.Add(BodyText(
            lead is null ? "按邮箱自动匹配客户" : $"已关联：{lead.DisplayName} · {lead.Email}",
            lead is null ? Muted : Primary,
            11));

        var brain = lead is null
            ? null
            : await _services.CustomerBrain.GetAsync(lead.Id, _lifetime.Token)
              ?? await _services.CustomerBrain.RefreshAsync(lead.Id, _lifetime.Token);
        var brief = new StackPanel { Spacing = 7 };
        brief.Children.Add(BodyText(
            brain is null ? "CUSTOMER BRAIN · 等待客户上下文" : $"CUSTOMER BRAIN · {brain.CoverageLabel}",
            ResourceBrush("AiAccent", "#6659B8"),
            10));
        brief.Children.Add(TitleText(brain is null ? "等待关联客户" : $"{brain.Confidence:P0} 资料置信度", 18));
        brief.Children.Add(new ProgressBar { Maximum = 100, Value = (brain?.Confidence ?? 0) * 100 });
        brief.Children.Add(BodyText(
            brain?.Summary ?? lead?.ProfileSummary ?? "选择会话或填写收件邮箱后显示客户画像",
            Ink,
            10));
        brief.Children.Add(BodyText(
            $"下一步：{brain?.NextBestAction ?? lead?.NextAction ?? "等待客户上下文"}",
            Primary,
            10));
        panel.Children.Add(SectionCard("AI SALES BRIEF", "CUSTOMER BRAIN", brief));

        var linked = new StackPanel { Spacing = 8 };
        linked.Children.Add(BodyText(
            "保存后，客户列表、商机智能、Customer Brain、客户分析和邮件自动化任务都会读取这份资料。",
            Muted,
            10));
        if (lead is null)
        {
            linked.Children.Add(EmptyState(
                "尚未关联 CRM 客户",
                conversation is null ? "新邮件发送后会按收件邮箱匹配客户。" : "可在客户列表中新建或补充相同邮箱。"));
        }
        else
        {
            linked.Children.Add(TextCell(lead.DisplayName, true, $"{lead.Company} · {lead.Country} · {lead.StageLabel}"));
            linked.Children.Add(BodyText($"负责人：{Fallback(lead.Owner, "—")} · 标签：{Fallback(lead.TagsLabel, "—")}", Ink, 10));
            linked.Children.Add(BodyText($"备注 / 最近情况：{Fallback(lead.LatestMessage, "—")}", Ink, 10));
            linked.Children.Add(ActionButton("编辑并同步客户资料", async () => await ShowLeadEditorAsync(lead), primary: true));
        }
        panel.Children.Add(SectionCard("邮件与 CRM 已联动", "CRM LIVE SYNC", linked));
        return Card(new ScrollViewer
        {
            Content = panel,
            MaxHeight = 720,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, new Thickness(12));
    }

    private async Task SyncEmailAsync(EmailAccount? account)
    {
        if (account is null) return;
        try
        {
            _operationStatus = $"正在同步 {account.EmailAddress}…";
            var imported = await _services.Email.SyncInboxAsync(account.Id, 500, _lifetime.Token);
            _operationStatus = $"已同步 {imported:N0} 封新邮件";
            await RenderCurrentPageAsync();
        }
        catch (Exception error) { await ShowMessageAsync("邮件同步失败", error.Message); }
    }

    private async Task ShowEmailAccountEditorAsync(EmailAccount? source)
    {
        var account = source ?? new EmailAccount();
        var providerDefinitions = EmailService.ProviderPresets.ToList();
        var provider = new ComboBox
        {
            ItemsSource = providerDefinitions.Select(item => item.Label).ToList(),
            SelectedIndex = Math.Max(0, providerDefinitions.FindIndex(item => item.Provider == account.Provider))
        };
        var displayName = new TextBox { Text = account.DisplayName };
        var email = new TextBox { Text = account.EmailAddress };
        var user = new TextBox { Text = account.UserName };
        var password = new TextBox { PasswordChar = '●', Watermark = source is null ? "应用专用密码 / 客户端授权码" : "留空表示继续使用钥匙串中的密码" };
        var imapHost = new TextBox { Text = account.ImapHost };
        var imapPort = new TextBox { Text = account.ImapPort.ToString() };
        var smtpHost = new TextBox { Text = account.SmtpHost };
        var smtpPort = new TextBox { Text = account.SmtpPort.ToString() };
        var imapSsl = new CheckBox { Content = "IMAP 使用 SSL / TLS", IsChecked = account.ImapUseSsl };
        var smtpSsl = new CheckBox { Content = "SMTP 使用 SSL / TLS", IsChecked = account.SmtpUseSsl };
        var guideTitle = TitleText("", 17);
        var guideBadge = BodyText("", Primary, 11);
        var guideSummary = BodyText("", Ink, 12);
        var guideSteps = BodyText("", Muted, 11);
        var compatibility = BodyText("", Warning, 11);
        var setup = new Button();
        var help = new Button();
        EmailProviderGuide CurrentGuide() =>
            EmailService.Guide(providerDefinitions[Math.Clamp(provider.SelectedIndex, 0, providerDefinitions.Count - 1)].Provider);
        void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        setup.Click += (_, _) => OpenUrl(CurrentGuide().SetupUrl);
        help.Click += (_, _) => OpenUrl(CurrentGuide().HelpUrl);
        void ApplyPreset()
        {
            var preset = providerDefinitions[Math.Clamp(provider.SelectedIndex, 0, providerDefinitions.Count - 1)];
            var guide = EmailService.Guide(preset.Provider);
            account.Provider = preset.Provider;
            if (preset.Provider != EmailProviderKind.Custom)
            {
                imapHost.Text = preset.ImapHost;
                imapPort.Text = preset.ImapPort.ToString();
                smtpHost.Text = preset.SmtpHost;
                smtpPort.Text = preset.SmtpPort.ToString();
            }
            if (string.IsNullOrWhiteSpace(user.Text)) user.Text = email.Text;
            email.Watermark = guide.EmailHint;
            user.Watermark = guide.UserNameHint;
            password.Watermark = source is null ? guide.PasswordHint : $"留空继续使用钥匙串密码；{guide.PasswordHint}";
            guideTitle.Text = guide.Title;
            guideBadge.Text = guide.Badge;
            guideSummary.Text = guide.Summary;
            guideSteps.Text = string.Join("\n", guide.Steps.Select((step, index) => $"{index + 1}. {step}"));
            compatibility.Text = guide.CompatibilityNote;
            setup.Content = guide.SetupButtonLabel;
            setup.IsVisible = !string.IsNullOrWhiteSpace(guide.SetupUrl);
            help.Content = guide.HelpButtonLabel;
        }
        provider.SelectionChanged += (_, _) => ApplyPreset();
        if (source is null) ApplyPreset();
        var panel = new StackPanel { Spacing = 11, Margin = new Thickness(24) };
        panel.Children.Add(TitleText(source is null ? "连接邮箱" : $"管理 · {account.EmailAddress}", 23));
        var guideActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        guideActions.Children.Add(setup);
        guideActions.Children.Add(help);
        panel.Children.Add(Card(new StackPanel
        {
            Spacing = 7,
            Children = { guideTitle, guideBadge, guideSummary, guideSteps, guideActions, compatibility }
        }, background: PrimarySoft));
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 9
        };
        var fields = new Control[]
        {
            Field("服务商", provider), Field("显示名称", displayName),
            Field("邮箱地址", email), Field("登录用户名", user),
            Field("密码 / 应用密码", password), Field("IMAP 主机", imapHost),
            Field("IMAP 端口", imapPort), Field("SMTP 主机", smtpHost),
            Field("SMTP 端口", smtpPort)
        };
        for (var index = 0; index < fields.Length; index++)
        {
            Grid.SetRow(fields[index], index / 2);
            Grid.SetColumn(fields[index], index % 2);
            form.Children.Add(fields[index]);
        }
        panel.Children.Add(form);
        var sslRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        sslRow.Children.Add(imapSsl);
        sslRow.Children.Add(smtpSsl);
        panel.Children.Add(Field("高级服务器加密", sslRow, "993 / 465 通常使用直接 TLS；587 通常由邮件库自动协商 STARTTLS。"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = DialogWindow(source is null ? "连接邮箱" : "管理邮箱", new ScrollViewer { Content = panel }, 760, 720);
        if (source is not null)
            buttons.Children.Add(ActionButton("删除账号", async () =>
            {
                if (!await ConfirmAsync("删除邮箱账号", "将删除本机账号配置和钥匙串密码，已同步的邮件历史也会从本机数据库移除。", "确认删除")) return;
                await _services.Email.DeleteAccountAsync(account, _lifetime.Token);
                _activeEmailAccountId = "";
                dialog.Close(true);
            }, danger: true));
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => dialog.Close(false);
        var save = new Button { Content = "测试连接并保存" };
        save.Classes.Add("primary");
        save.Click += async (_, _) =>
        {
            try
            {
                ApplyPreset();
                account.DisplayName = displayName.Text?.Trim() ?? "";
                account.EmailAddress = email.Text?.Trim() ?? "";
                account.UserName = string.IsNullOrWhiteSpace(user.Text) ? account.EmailAddress : user.Text.Trim();
                account.ImapHost = imapHost.Text?.Trim() ?? "";
                account.ImapPort = int.TryParse(imapPort.Text, out var imap) ? imap : 993;
                account.SmtpHost = smtpHost.Text?.Trim() ?? "";
                account.SmtpPort = int.TryParse(smtpPort.Text, out var smtp) ? smtp : 465;
                account.ImapUseSsl = imapSsl.IsChecked == true;
                account.SmtpUseSsl = smtpSsl.IsChecked == true;
                await _services.Email.SaveAndTestAccountAsync(account, password.Text ?? "", _lifetime.Token);
                _activeEmailAccountId = account.Id;
                dialog.Close(true);
            }
            catch (Exception error) { await ShowMessageAsync("邮箱连接失败", error.Message); }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        if (await dialog.ShowDialog<bool>(this)) await RenderCurrentPageAsync();
    }
}
