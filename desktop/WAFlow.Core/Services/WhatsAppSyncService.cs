using System.Text.Json;
using System.Threading.Channels;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record WhatsAppSyncProgress(
    string AccountId,
    string State,
    string Phase,
    int? Progress,
    int Contacts,
    int Chats,
    int Messages,
    bool ExistingSession,
    string Error = "");

public sealed class WhatsAppSyncService
{
    private readonly LocalRepository _repository;
    private readonly Channel<WhatsAppBridgeEvent> _events = Channel.CreateUnbounded<WhatsAppBridgeEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public event EventHandler<WhatsAppMessage>? MessageSynchronized;
    public event EventHandler<WhatsAppSyncProgress>? SynchronizationChanged;
    public event EventHandler<string>? SyncError;

    public WhatsAppSyncService(LocalRepository repository, WhatsAppConnectionManager bridge)
    {
        _repository = repository;
        bridge.EventReceived += (_, e) => _events.Writer.TryWrite(e);
        _ = Task.Run(ProcessEventsAsync);
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync())
        {
            try { await HandleAsync(item); }
            catch (Exception error) { SyncError?.Invoke(this, error.Message); }
        }
    }

    private async Task HandleAsync(WhatsAppBridgeEvent e)
    {
        var accountId = string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId;
        switch (e.Name)
        {
            case "message":
                await IngestMessageAsync(accountId, e.Data);
                return;
            case "message_status":
                await IngestStatusAsync(e);
                return;
            case "contacts_upsert":
                foreach (var item in Items(e.Data)) await IngestContactAsync(accountId, item);
                RaiseDataChanged(accountId, "contacts");
                return;
            case "chats_upsert":
                foreach (var item in Items(e.Data)) await IngestChatAsync(accountId, item);
                RaiseDataChanged(accountId, "chats");
                return;
            case "messages_history":
                foreach (var item in Items(e.Data)) await IngestMessageAsync(accountId, item);
                RaiseDataChanged(accountId, "messages");
                return;
            case "sync_status":
                SynchronizationChanged?.Invoke(this, ParseProgress(accountId, e.Data));
                return;
        }
    }

    private async Task IngestContactAsync(string accountId, JsonElement data)
    {
        var jid = Text(data, "jid");
        var sourceJid = Text(data, "sourceJid");
        if (string.IsNullOrWhiteSpace(jid)) jid = sourceJid;
        if (string.IsNullOrWhiteSpace(jid)) return;
        var phone = Digits(Text(data, "phone"));
        var displayName = FirstText(data, "displayName", "savedName", "notifyName", "verifiedName", "username");
        if (string.IsNullOrWhiteSpace(displayName)) displayName = string.IsNullOrWhiteSpace(phone) ? jid : $"+{phone}";
        var contact = new WhatsAppContact
        {
            Id = $"{accountId}:{(string.IsNullOrWhiteSpace(sourceJid) ? jid : sourceJid)}",
            AccountId = accountId,
            Jid = jid,
            SourceJid = sourceJid,
            Phone = phone,
            DisplayName = displayName,
            SavedName = Text(data, "savedName"),
            NotifyName = Text(data, "notifyName"),
            VerifiedName = Text(data, "verifiedName"),
            Username = Text(data, "username"),
            Source = Text(data, "source")
        };
        await _repository.UpsertWhatsAppContactAsync(contact);
        if (string.IsNullOrWhiteSpace(phone)) return;
        var lead = await _repository.GetLeadByPhoneAsync(phone);
        var conversation = await _repository.GetWhatsAppConversationAsync(accountId, phone) ?? new WhatsAppConversation
        {
            Id = $"{accountId}:{phone}", AccountId = accountId, Phone = phone
        };
        if (lead is not null)
        {
            conversation.LeadId = lead.Id;
            conversation.DisplayName = lead.DisplayName;
        }
        else if (!string.IsNullOrWhiteSpace(displayName) && (string.IsNullOrWhiteSpace(conversation.DisplayName) || conversation.DisplayName == $"+{phone}"))
        {
            conversation.DisplayName = displayName;
        }
        await _repository.UpsertWhatsAppConversationAsync(conversation);
    }

    private async Task IngestChatAsync(string accountId, JsonElement data)
    {
        var phone = Digits(Text(data, "phone"));
        if (string.IsNullOrWhiteSpace(phone)) return;
        var lead = await _repository.GetLeadByPhoneAsync(phone);
        var conversation = await _repository.GetWhatsAppConversationAsync(accountId, phone) ?? new WhatsAppConversation
        {
            Id = $"{accountId}:{phone}", AccountId = accountId, Phone = phone
        };
        var displayName = Text(data, "displayName");
        if (lead is not null)
        {
            conversation.LeadId = lead.Id;
            conversation.DisplayName = lead.DisplayName;
        }
        else if (!string.IsNullOrWhiteSpace(displayName) && (string.IsNullOrWhiteSpace(conversation.DisplayName) || conversation.DisplayName == $"+{phone}"))
        {
            conversation.DisplayName = displayName;
        }
        var lastMessage = Text(data, "lastMessage");
        if (DateTimeOffset.TryParse(Text(data, "lastMessageAt"), out var lastAt) && lastAt >= conversation.LastMessageAt)
        {
            conversation.LastMessageAt = lastAt;
            if (!string.IsNullOrWhiteSpace(lastMessage)) conversation.LastMessage = lastMessage;
        }
        if (data.TryGetProperty("unreadCount", out var unread) && unread.ValueKind == JsonValueKind.Number && unread.TryGetInt32(out var unreadCount))
            conversation.UnreadCount = Math.Max(0, unreadCount);
        if (string.IsNullOrWhiteSpace(conversation.DisplayName)) conversation.DisplayName = $"+{phone}";
        await _repository.UpsertWhatsAppConversationAsync(conversation);
    }

    private async Task IngestMessageAsync(string accountId, JsonElement data)
    {
        var phone = Digits(Text(data, "phone"));
        var providerId = Text(data, "id");
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(providerId)) return;
        var fromMe = Bool(data, "fromMe");
        var timestamp = DateTimeOffset.TryParse(Text(data, "timestamp"), out var parsed) ? parsed : DateTimeOffset.Now;
        var source = Text(data, "source");
        var historical = source.StartsWith("history:", StringComparison.OrdinalIgnoreCase);
        var lead = await _repository.GetLeadByPhoneAsync(phone);
        var conversationId = $"{accountId}:{phone}";
        var conversation = await _repository.GetWhatsAppConversationAsync(accountId, phone) ?? new WhatsAppConversation
        {
            Id = conversationId,
            AccountId = accountId,
            Phone = phone,
            DisplayName = string.IsNullOrWhiteSpace(Text(data, "pushName")) ? $"+{phone}" : Text(data, "pushName")
        };
        if (lead is not null)
        {
            conversation.LeadId = lead.Id;
            conversation.DisplayName = lead.DisplayName;
        }
        var message = new WhatsAppMessage
        {
            Id = $"{accountId}:{providerId}",
            ProviderMessageId = providerId,
            AccountId = accountId,
            ConversationId = conversationId,
            LeadId = lead?.Id ?? "",
            Phone = phone,
            Direction = fromMe ? WhatsAppMessageDirection.Outgoing : WhatsAppMessageDirection.Incoming,
            Status = fromMe ? WhatsAppMessageStatus.Sent : WhatsAppMessageStatus.Received,
            Kind = Text(data, "kind"),
            Body = Text(data, "text"),
            PushName = Text(data, "pushName"),
            Timestamp = timestamp,
            Source = source
        };
        var inserted = await _repository.UpsertWhatsAppMessageAsync(message);
        if (timestamp >= conversation.LastMessageAt)
        {
            conversation.LastMessageAt = timestamp;
            conversation.LastMessage = string.IsNullOrWhiteSpace(message.Body) ? $"[{message.Kind}]" : message.Body;
        }
        if (inserted && !fromMe && !historical) conversation.UnreadCount++;
        await _repository.UpsertWhatsAppConversationAsync(conversation);
        if (lead is not null && inserted)
        {
            LeadConnectionStatus.ApplyFromMessage(lead, message);
            await _repository.UpsertLeadAsync(lead);
            await _repository.LogEventAsync(fromMe ? "whatsapp_message_sent" : "whatsapp_message_received", lead.Id, null, $"message_id={providerId}; account={accountId}");
        }
        if (inserted && !historical) MessageSynchronized?.Invoke(this, message);
    }

    private async Task IngestStatusAsync(WhatsAppBridgeEvent e)
    {
        var providerId = Text(e.Data, "id");
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var numeric = e.Data.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var value) ? value : -1;
        var status = numeric switch
        {
            <= 0 => WhatsAppMessageStatus.Failed,
            1 => WhatsAppMessageStatus.Pending,
            2 => WhatsAppMessageStatus.Sent,
            3 => WhatsAppMessageStatus.Delivered,
            >= 4 => WhatsAppMessageStatus.Read
        };
        var message = await _repository.UpdateWhatsAppMessageStatusAsync(string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId, providerId, status);
        if (message is null || string.IsNullOrWhiteSpace(message.LeadId) || message.Direction != WhatsAppMessageDirection.Outgoing) return;
        var lead = await _repository.GetLeadAsync(message.LeadId);
        if (lead is null || !LeadConnectionStatus.ApplyFromMessage(lead, message)) return;
        await _repository.UpsertLeadAsync(lead);
        MessageSynchronized?.Invoke(this, message);
    }

    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private static IEnumerable<JsonElement> Items(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
    private static string FirstText(JsonElement data, params string[] names)
    {
        foreach (var name in names) if (Text(data, name) is { Length: > 0 } value) return value;
        return "";
    }

    private void RaiseDataChanged(string accountId, string phase) =>
        SynchronizationChanged?.Invoke(this, new WhatsAppSyncProgress(accountId, "data", phase, null, 0, 0, 0, false));

    private static WhatsAppSyncProgress ParseProgress(string accountId, JsonElement data) => new(
        accountId,
        Text(data, "state"),
        Text(data, "phase"),
        data.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Number && progress.TryGetInt32(out var numeric) ? numeric : null,
        Int(data, "contacts"),
        Int(data, "chats"),
        Int(data, "messages"),
        Bool(data, "existingSession"),
        Text(data, "error"));

    private static int Int(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) ? numeric : 0;
}
