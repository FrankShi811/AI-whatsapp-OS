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
            if (shouldSendHolding && result.AgentState is not null)
            {
                result.AgentState.LastHoldingReplyMessageId = string.IsNullOrWhiteSpace(providerMessageId)
                    ? $"holding-{Guid.NewGuid():N}" : providerMessageId;
                await _repository.UpsertConversationAgentStateAsync(result.AgentState, cancellationToken);
            }
            await _repository.LogEventAsync(
                shouldSendHolding ? "customer_success_holding_reply_sent" : "customer_success_auto_reply_sent",
                result.Context?.CustomerId, null,
                Json.Serialize(new { message.AccountId, message.ConversationId, sourceMessageId = message.Id, providerMessageId }),
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

    public void Dispose()
    {
        _sync.MessageSynchronized -= OnMessageSynchronized;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
