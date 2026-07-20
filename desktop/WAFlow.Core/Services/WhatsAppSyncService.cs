using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class WhatsAppSyncService
{
    private readonly LocalRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler<WhatsAppMessage>? MessageSynchronized;
    public event EventHandler<string>? SyncError;

    public WhatsAppSyncService(LocalRepository repository, WhatsAppConnectionManager bridge)
    {
        _repository = repository;
        bridge.EventReceived += (_, e) => _ = HandleAsync(e);
    }

    private async Task HandleAsync(WhatsAppBridgeEvent e)
    {
        if (e.Name is not ("message" or "message_status")) return;
        await _gate.WaitAsync();
        try
        {
            if (e.Name == "message") await IngestMessageAsync(e);
            else await IngestStatusAsync(e);
        }
        catch (Exception error) { SyncError?.Invoke(this, error.Message); }
        finally { _gate.Release(); }
    }

    private async Task IngestMessageAsync(WhatsAppBridgeEvent e)
    {
        var accountId = string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId;
        var phone = Text(e.Data, "phone");
        var providerId = Text(e.Data, "id");
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(providerId)) return;
        var fromMe = Bool(e.Data, "fromMe");
        var timestamp = DateTimeOffset.TryParse(Text(e.Data, "timestamp"), out var parsed) ? parsed : DateTimeOffset.Now;
        var lead = await _repository.GetLeadByPhoneAsync(phone);
        var conversationId = $"{accountId}:{phone}";
        var conversation = await _repository.GetWhatsAppConversationAsync(accountId, phone) ?? new WhatsAppConversation
        {
            Id = conversationId,
            AccountId = accountId,
            Phone = phone,
            DisplayName = string.IsNullOrWhiteSpace(Text(e.Data, "pushName")) ? $"+{phone}" : Text(e.Data, "pushName")
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
            Kind = Text(e.Data, "kind"),
            Body = Text(e.Data, "text"),
            PushName = Text(e.Data, "pushName"),
            Timestamp = timestamp,
            Source = Text(e.Data, "source")
        };
        var inserted = await _repository.UpsertWhatsAppMessageAsync(message);
        if (timestamp >= conversation.LastMessageAt)
        {
            conversation.LastMessageAt = timestamp;
            conversation.LastMessage = string.IsNullOrWhiteSpace(message.Body) ? $"[{message.Kind}]" : message.Body;
        }
        if (inserted && !fromMe) conversation.UnreadCount++;
        await _repository.UpsertWhatsAppConversationAsync(conversation);
        if (lead is not null && inserted)
        {
            lead.LastContactAt = timestamp;
            if (!fromMe) lead.LatestMessage = message.Body;
            await _repository.UpsertLeadAsync(lead);
            await _repository.LogEventAsync(fromMe ? "whatsapp_message_sent" : "whatsapp_message_received", lead.Id, null, $"message_id={providerId}; account={accountId}");
        }
        if (inserted) MessageSynchronized?.Invoke(this, message);
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
        await _repository.UpdateWhatsAppMessageStatusAsync(string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId, providerId, status);
    }

    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
