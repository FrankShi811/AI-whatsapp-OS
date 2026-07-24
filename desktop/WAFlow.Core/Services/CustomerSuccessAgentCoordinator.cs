using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class CustomerSuccessAgentCoordinator : IDisposable
{
    private readonly LocalRepository _repository;
    private readonly WhatsAppSyncService _sync;
    private readonly WhatsAppConnectionManager _connections;
    private readonly CustomerSuccessAgentService _agent;
    private readonly CancellationTokenSource _shutdown = new();

    public CustomerSuccessAgentCoordinator(
        LocalRepository repository,
        WhatsAppSyncService sync,
        WhatsAppConnectionManager connections,
        CustomerSuccessAgentService agent)
    {
        _repository = repository;
        _sync = sync;
        _connections = connections;
        _agent = agent;
        _sync.MessageSynchronized += OnMessageSynchronized;
    }

    private void OnMessageSynchronized(object? sender, WhatsAppMessage message)
    {
        if (message.Direction != WhatsAppMessageDirection.Incoming || message.IsStatusUpdate ||
            message.IsRevoked || string.IsNullOrWhiteSpace(message.Body)) return;
        _ = HandleAsync(message, _shutdown.Token);
    }

    private async Task HandleAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var conversation = (await _repository.GetWhatsAppConversationsAsync(message.AccountId, cancellationToken))
                .FirstOrDefault(item => item.Id == message.ConversationId);
            if (conversation is null) return;
            var state = await _repository.GetConversationAgentStateAsync(message.AccountId, message.ConversationId, cancellationToken);
            if (state?.Mode != ConversationAgentMode.AutoActive &&
                state?.Mode is not ConversationAgentMode.HumanRequired and not ConversationAgentMode.HumanActive and not ConversationAgentMode.ResumeReview)
                return;
            var result = await _agent.AnalyzeAsync(
                message.AccountId, message.ConversationId, conversation.Phone, conversation.DisplayName,
                sourceMessageId: message.Id, cancellationToken: cancellationToken);
            if (result.Decision is null) return;

            var shouldSendHolding = result.Handoff is not null &&
                                    string.IsNullOrWhiteSpace(result.AgentState?.LastHoldingReplyMessageId);
            if (!result.AutoReplyAllowed && !shouldSendHolding) return;
            var response = await _connections.SendTextAsync(message.AccountId, conversation.Phone, result.Decision.ReplyText, cancellationToken);
            var providerMessageId = ReadProviderId(response);
            if (string.IsNullOrWhiteSpace(providerMessageId))
                throw new InvalidOperationException("WhatsApp 未返回服务端消息 ID，AI 回复未确认发出。");
            if (!ReadBool(response, "targetVerified"))
                throw new InvalidOperationException("WhatsApp 未确认目标联系人，AI 回复未发出。");
            var providerStatus = ReadNumericStatus(response);
            var confirmedByServer = providerStatus is >= 2 and <= 4;
            if (shouldSendHolding && result.AgentState is not null)
            {
                result.AgentState.LastHoldingReplyMessageId = providerMessageId;
                await _repository.UpsertConversationAgentStateAsync(result.AgentState, cancellationToken);
            }
            await _repository.LogEventAsync(
                confirmedByServer
                    ? shouldSendHolding ? "customer_success_holding_reply_sent" : "customer_success_auto_reply_sent"
                    : shouldSendHolding ? "customer_success_holding_reply_pending" : "customer_success_auto_reply_pending",
                result.Context?.CustomerId, null,
                Json.Serialize(new { message.AccountId, message.ConversationId, sourceMessageId = message.Id, providerMessageId, providerStatus, targetVerified = true }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            await _repository.SaveAgentTurnLogAsync(new AgentTurnLog
            {
                AccountId = message.AccountId,
                ConversationId = message.ConversationId,
                SourceMessageId = message.Id,
                Error = ex.Message,
                Decision = "auto_reply_failed"
            }, CancellationToken.None);
        }
    }

    private static string ReadProviderId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        foreach (var name in new[] { "messageId", "id", "providerMessageId" })
            if (value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String)
                return item.GetString() ?? "";
        return "";
    }

    private static bool ReadBool(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(name, out var item) &&
               item.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               item.GetBoolean();
    }

    private static int ReadNumericStatus(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("status", out var item) &&
               item.ValueKind == JsonValueKind.Number &&
               item.TryGetInt32(out var numeric)
            ? numeric
            : 1;
    }

    public void Dispose()
    {
        _sync.MessageSynchronized -= OnMessageSynchronized;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
