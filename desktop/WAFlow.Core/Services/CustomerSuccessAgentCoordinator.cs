using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record CustomerSuccessAgentRunCompletedEvent(
    string AccountId,
    string ConversationId,
    CustomerSuccessRunStatus Status);

public sealed class CustomerSuccessAgentCoordinator : IDisposable
{
    private readonly LocalRepository _repository;
    private readonly WhatsAppSyncService _sync;
    private readonly WhatsAppConnectionManager _connections;
    private readonly CustomerSuccessAgentService _agent;
    private readonly CancellationTokenSource _shutdown = new();

    public event EventHandler<CustomerSuccessAgentRunCompletedEvent>? RunCompleted;

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
        if (message.IsGroup) return;
        if (message.Direction == WhatsAppMessageDirection.Outgoing && !message.IsRevoked)
        {
            _ = ReconcileOutgoingStatusAsync(message, _shutdown.Token);
            return;
        }
        if (message.Direction != WhatsAppMessageDirection.Incoming || message.IsStatusUpdate ||
            message.IsRevoked || string.IsNullOrWhiteSpace(message.Body)) return;
        _ = HandleAsync(message, _shutdown.Token);
    }

    private async Task ReconcileOutgoingStatusAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _repository.GetConversationAgentStateAsync(
                message.AccountId, message.ConversationId, cancellationToken);
            if (state is null ||
                string.IsNullOrWhiteSpace(state.LastProviderMessageId) ||
                !state.LastProviderMessageId.Equals(message.ProviderMessageId, StringComparison.OrdinalIgnoreCase))
                return;

            if (message.Status == WhatsAppMessageStatus.Failed)
            {
                var status = state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                    ? CustomerSuccessRunStatus.HumanRequired
                    : CustomerSuccessRunStatus.Failed;
                await _agent.UpdateRunOutcomeAsync(
                    message.AccountId, message.ConversationId, status,
                    state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                        ? "高风险问题仍由人工处理；占位回复发送失败。"
                        : "WhatsApp 后续回执确认自动回复发送失败。",
                    state.LastProviderMessageId,
                    message.FailureReason,
                    cancellationToken);
                RaiseRunCompleted(message, status);
                return;
            }

            if (message.Status is not WhatsAppMessageStatus.Sent and
                not WhatsAppMessageStatus.Delivered and
                not WhatsAppMessageStatus.Read)
                return;
            var reconciledStatus = state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                ? CustomerSuccessRunStatus.HumanRequired
                : CustomerSuccessRunStatus.AutoReplySent;
            var detail = state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                ? $"高风险问题仍由人工处理；占位回复状态：{message.Status}。"
                : $"WhatsApp 后续回执已确认自动回复状态：{message.Status}。";
            await _agent.UpdateRunOutcomeAsync(
                message.AccountId, message.ConversationId, reconciledStatus, detail,
                state.LastProviderMessageId, cancellationToken: cancellationToken);
            RaiseRunCompleted(message, reconciledStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A late receipt must never interrupt the primary WhatsApp sync loop.
        }
    }

    private async Task HandleAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var conversation = (await _repository.GetWhatsAppConversationsAsync(message.AccountId, cancellationToken))
                .FirstOrDefault(item => item.Id == message.ConversationId);
            if (conversation is null) return;
            var state = await _repository.GetConversationAgentStateAsync(message.AccountId, message.ConversationId, cancellationToken);
            if (state?.Mode is not ConversationAgentMode.CopilotActive and not ConversationAgentMode.AutoActive &&
                state?.Mode is not ConversationAgentMode.HumanRequired and not ConversationAgentMode.HumanActive and not ConversationAgentMode.ResumeReview)
                return;
            var requestedMode = state.Mode;
            var result = await _agent.AnalyzeAsync(
                message.AccountId, message.ConversationId, conversation.Phone, conversation.DisplayName,
                sourceMessageId: message.Id,
                trigger: CustomerSuccessRunTrigger.IncomingAutomation,
                cancellationToken: cancellationToken);
            if (result.Decision is null)
            {
                var status = requestedMode is ConversationAgentMode.HumanRequired or ConversationAgentMode.HumanActive or ConversationAgentMode.ResumeReview
                    ? CustomerSuccessRunStatus.HumanRequired
                    : CustomerSuccessRunStatus.Blocked;
                await _agent.UpdateRunOutcomeAsync(
                    message.AccountId, message.ConversationId, status,
                    string.IsNullOrWhiteSpace(result.BlockReason) ? "本轮没有生成回复。" : result.BlockReason,
                    cancellationToken: cancellationToken);
                RaiseRunCompleted(message, status);
                return;
            }

            if (result.Handoff is not null && requestedMode != ConversationAgentMode.AutoActive)
            {
                await _agent.UpdateRunOutcomeAsync(
                    message.AccountId, message.ConversationId, CustomerSuccessRunStatus.HumanRequired,
                    "检测到高风险问题，协作草稿未发送，已转人工处理。",
                    cancellationToken: cancellationToken);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.HumanRequired);
                return;
            }

            if (requestedMode == ConversationAgentMode.CopilotActive)
            {
                RaiseRunCompleted(message, CustomerSuccessRunStatus.CopilotDraftReady);
                return;
            }

            var shouldSendHolding = requestedMode == ConversationAgentMode.AutoActive &&
                                    result.Handoff is not null &&
                                    string.IsNullOrWhiteSpace(result.AgentState?.LastHoldingReplyMessageId);
            if (!result.AutoReplyAllowed && !shouldSendHolding)
            {
                var detail = string.IsNullOrWhiteSpace(result.BlockReason)
                    ? "自动回复未通过账号锁或安全校验，消息未发送。"
                    : result.BlockReason;
                await _agent.UpdateRunOutcomeAsync(
                    message.AccountId, message.ConversationId, CustomerSuccessRunStatus.Blocked, detail,
                    cancellationToken: cancellationToken);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.Blocked);
                return;
            }
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
            if (result.Decision.KnowledgeCitations.Count > 0)
            {
                foreach (var citation in result.Decision.KnowledgeCitations)
                {
                    await _repository.SaveKnowledgeUsageOutcomeAsync(new KnowledgeUsageOutcome
                    {
                        Id = $"{providerMessageId}:{citation.ChunkId}",
                        RetrievalLogId = result.Decision.KnowledgeRetrievalId,
                        ChunkId = citation.ChunkId,
                        CustomerId = result.Context?.CustomerId ?? "",
                        SourceMessageId = providerMessageId,
                        ActuallySent = confirmedByServer,
                        ObservationNote = confirmedByServer
                            ? "知识辅助回复已由 WhatsApp 服务端确认；后续回复和阶段结果需另行观察。"
                            : "已取得消息 ID，但服务端状态尚未确认；不计入真实发送样本。"
                    }, cancellationToken);
                }
            }
            var runStatus = shouldSendHolding
                ? CustomerSuccessRunStatus.HumanRequired
                : confirmedByServer
                    ? CustomerSuccessRunStatus.AutoReplySent
                    : CustomerSuccessRunStatus.AutoReplyPending;
            var runDetail = shouldSendHolding
                ? confirmedByServer
                    ? "高风险问题已转人工，占位回复已由 WhatsApp 服务端确认。"
                    : "高风险问题已转人工，占位回复已提交，等待 WhatsApp 服务端确认。"
                : confirmedByServer
                    ? "自动回复已通过目标校验，并由 WhatsApp 服务端确认。"
                    : "自动回复已取得消息 ID，等待 WhatsApp 服务端状态确认。";
            await _agent.UpdateRunOutcomeAsync(
                message.AccountId, message.ConversationId, runStatus, runDetail, providerMessageId,
                cancellationToken: cancellationToken);
            await _repository.LogEventAsync(
                confirmedByServer
                    ? shouldSendHolding ? "customer_success_holding_reply_sent" : "customer_success_auto_reply_sent"
                    : shouldSendHolding ? "customer_success_holding_reply_pending" : "customer_success_auto_reply_pending",
                result.Context?.CustomerId, null,
                Json.Serialize(new { message.AccountId, message.ConversationId, sourceMessageId = message.Id, providerMessageId, providerStatus, targetVerified = true }),
                cancellationToken);
            RaiseRunCompleted(message, runStatus);
        }
        catch (Exception ex)
        {
            await _agent.UpdateRunOutcomeAsync(
                message.AccountId, message.ConversationId, CustomerSuccessRunStatus.Failed,
                "Agent 处理失败，未自动发送消息。", error: ex.Message,
                cancellationToken: CancellationToken.None);
            await _repository.SaveAgentTurnLogAsync(new AgentTurnLog
            {
                AccountId = message.AccountId,
                ConversationId = message.ConversationId,
                SourceMessageId = message.Id,
                Error = ex.Message,
                Decision = "auto_reply_failed"
            }, CancellationToken.None);
            RaiseRunCompleted(message, CustomerSuccessRunStatus.Failed);
        }
    }

    private void RaiseRunCompleted(WhatsAppMessage message, CustomerSuccessRunStatus status) =>
        RunCompleted?.Invoke(this, new CustomerSuccessAgentRunCompletedEvent(message.AccountId, message.ConversationId, status));

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
