using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class CustomerActionLifecycleService
{
    private readonly LocalRepository _repository;

    public CustomerActionLifecycleService(LocalRepository repository)
    {
        _repository = repository;
    }

    public Task AcceptAsync(string customerId, string recommendationId, CancellationToken cancellationToken = default) =>
        TransitionAsync(customerId, recommendationId, AiRecommendationStatus.Accepted, "recommendation_accepted", "AI 建议已接受", cancellationToken: cancellationToken);

    public Task StartAsync(string customerId, string recommendationId, CancellationToken cancellationToken = default) =>
        TransitionAsync(customerId, recommendationId, AiRecommendationStatus.InProgress, "recommendation_started", "AI 建议已开始执行", cancellationToken: cancellationToken);

    public Task CompleteAsync(string customerId, string recommendationId, string outcome, CancellationToken cancellationToken = default) =>
        TransitionAsync(customerId, recommendationId, AiRecommendationStatus.Completed, "recommendation_completed", "AI 建议已完成", outcome, true, cancellationToken);

    public Task FailAsync(string customerId, string recommendationId, string outcome, CancellationToken cancellationToken = default) =>
        TransitionAsync(customerId, recommendationId, AiRecommendationStatus.Failed, "recommendation_failed", "AI 建议执行失败", outcome, false, cancellationToken);

    public Task DismissAsync(string customerId, string recommendationId, string note, CancellationToken cancellationToken = default) =>
        TransitionAsync(customerId, recommendationId, AiRecommendationStatus.Dismissed, "recommendation_dismissed", "AI 建议已忽略", note, false, cancellationToken);

    public async Task DeferAsync(
        string customerId,
        string recommendationId,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(customerId, recommendationId, cancellationToken);
        var now = DateTimeOffset.Now;
        var dueAt = now.Add(delay <= TimeSpan.Zero ? TimeSpan.FromHours(24) : delay);
        CaptureBaseline(state.Action, state.Lead, now);

        state.Recommendation.Status = AiRecommendationStatus.Accepted;
        state.Recommendation.UpdatedAt = now;
        await _repository.SaveAiRecommendationAsync(state.Recommendation, cancellationToken);

        state.Task.Status = FollowUpTaskStatus.Open;
        state.Task.DueAt = dueAt;
        state.Task.UpdatedAt = now;
        await _repository.UpsertFollowUpTaskAsync(state.Task, cancellationToken);

        state.Action.Status = SalesActionStatus.Approved;
        state.Action.DueAt = dueAt;
        state.Action.UpdatedAt = now;
        await _repository.SaveSalesActionAsync(state.Action, cancellationToken);

        await SaveEventAsync(
            customerId,
            recommendationId,
            "recommendation_deferred",
            "AI 建议已延期",
            $"新的计划时间：{dueAt:yyyy-MM-dd HH:mm}",
            cancellationToken);
    }

    public Task RecordExecutionEventAsync(
        string customerId,
        string title,
        string detail,
        string sourceId,
        CancellationToken cancellationToken = default) =>
        SaveEventAsync(customerId, sourceId, "ai_assistant_action_executed", title, detail, cancellationToken);

    public async Task<bool> RecordMessageExecutionAsync(
        string customerId,
        string channel,
        string content,
        string sourceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var recommendation = (await _repository.GetAiRecommendationHistoryAsync(customerId, cancellationToken))
            .Where(item => item.Status is AiRecommendationStatus.Accepted or AiRecommendationStatus.InProgress)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (recommendation is null)
        {
            await SaveEventAsync(
                customerId,
                sourceId,
                "ai_assistant_action_executed",
                "AI 会话助理回复已发送",
                $"{channel} 已接受消息，但当前没有已接受或执行中的 Customer Brain 建议。",
                cancellationToken);
            return false;
        }

        var state = await LoadStateAsync(customerId, recommendation.Id, cancellationToken);
        CaptureBaseline(state.Action, state.Lead, occurredAt);

        state.Recommendation.Status = AiRecommendationStatus.InProgress;
        state.Recommendation.UpdatedAt = occurredAt;
        await _repository.SaveAiRecommendationAsync(state.Recommendation, cancellationToken);

        state.Task.Status = FollowUpTaskStatus.InProgress;
        state.Task.UpdatedAt = occurredAt;
        await _repository.UpsertFollowUpTaskAsync(state.Task, cancellationToken);

        state.Action.Status = SalesActionStatus.InProgress;
        state.Action.ExecutedAt = occurredAt;
        state.Action.ExecutionChannel = channel.Trim();
        state.Action.ExecutedContent = content.Trim();
        state.Action.ExecutedSourceId = sourceId.Trim();
        state.Action.UpdatedAt = occurredAt;
        await _repository.SaveSalesActionAsync(state.Action, cancellationToken);

        await SaveEventAsync(
            customerId,
            sourceId,
            "sales_action_message_executed",
            "Customer Brain 建议已执行",
            $"{channel} 消息已发送并关联建议：{recommendation.Action}",
            cancellationToken);
        return true;
    }

    private async Task TransitionAsync(
        string customerId,
        string recommendationId,
        AiRecommendationStatus recommendationStatus,
        string eventType,
        string eventTitle,
        string outcome = "",
        bool? helpful = null,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(customerId, recommendationId, cancellationToken);
        var now = DateTimeOffset.Now;
        if (recommendationStatus is AiRecommendationStatus.Accepted
            or AiRecommendationStatus.InProgress
            or AiRecommendationStatus.Completed
            or AiRecommendationStatus.Failed)
            CaptureBaseline(state.Action, state.Lead, now);

        state.Recommendation.Status = recommendationStatus;
        state.Recommendation.UpdatedAt = now;
        await _repository.SaveAiRecommendationAsync(state.Recommendation, cancellationToken);

        state.Task.Status = recommendationStatus switch
        {
            AiRecommendationStatus.InProgress => FollowUpTaskStatus.InProgress,
            AiRecommendationStatus.Completed => FollowUpTaskStatus.Completed,
            AiRecommendationStatus.Failed => FollowUpTaskStatus.Failed,
            AiRecommendationStatus.Dismissed => FollowUpTaskStatus.Dismissed,
            _ => FollowUpTaskStatus.Open
        };
        state.Task.Outcome = outcome;
        state.Task.CompletedAt = recommendationStatus is AiRecommendationStatus.Completed
            or AiRecommendationStatus.Failed
            or AiRecommendationStatus.Dismissed ? now : null;
        state.Task.UpdatedAt = now;
        await _repository.UpsertFollowUpTaskAsync(state.Task, cancellationToken);

        state.Action.Status = recommendationStatus switch
        {
            AiRecommendationStatus.Accepted => SalesActionStatus.Approved,
            AiRecommendationStatus.InProgress => SalesActionStatus.InProgress,
            AiRecommendationStatus.Completed => SalesActionStatus.Completed,
            AiRecommendationStatus.Failed => SalesActionStatus.Failed,
            AiRecommendationStatus.Dismissed => SalesActionStatus.Cancelled,
            _ => SalesActionStatus.Planned
        };
        state.Action.Outcome = outcome;
        if (state.Action.ExecutedAt is null
            && (recommendationStatus is AiRecommendationStatus.Completed or AiRecommendationStatus.Failed))
        {
            state.Action.ExecutedAt = now;
            state.Action.ExecutionChannel = "manual_action";
            state.Action.ExecutedContent = state.Action.Description;
            state.Action.ExecutedSourceId = recommendationId;
        }
        state.Action.CompletedAt = recommendationStatus is AiRecommendationStatus.Completed
            or AiRecommendationStatus.Failed
            or AiRecommendationStatus.Dismissed ? now : null;
        state.Action.UpdatedAt = now;
        await _repository.SaveSalesActionAsync(state.Action, cancellationToken);

        if (helpful is not null)
        {
            await _repository.SaveAiLearningFeedbackAsync(new AiLearningFeedback
            {
                Id = $"feedback-{recommendationId}",
                CustomerId = customerId,
                RecommendationId = recommendationId,
                ActionId = state.Action.Id,
                Outcome = outcome,
                Helpful = helpful.Value,
                FeedbackSource = "human",
                Note = outcome,
                CreatedAt = now
            }, cancellationToken);
        }

        await SaveEventAsync(
            customerId,
            recommendationId,
            eventType,
            eventTitle,
            string.IsNullOrWhiteSpace(outcome) ? state.Recommendation.Action : outcome,
            cancellationToken);
    }

    private async Task<ActionState> LoadStateAsync(
        string customerId,
        string recommendationId,
        CancellationToken cancellationToken)
    {
        var recommendation = (await _repository.GetAiRecommendationHistoryAsync(customerId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, recommendationId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("找不到对应的 AI 建议，请刷新客户资料后重试。");
        var lead = await _repository.GetLeadAsync(customerId, cancellationToken);
        var task = (await _repository.GetFollowUpTasksAsync(customerId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.RecommendationId, recommendationId, StringComparison.Ordinal))
            ?? new FollowUpTask
            {
                CustomerId = customerId,
                RecommendationId = recommendationId,
                Title = recommendation.Title,
                Reason = recommendation.Rationale,
                DueAt = DateTimeOffset.Now.AddHours(24),
                SourceType = "customer_brain",
                SourceId = recommendationId
            };
        var action = (await _repository.GetSalesActionsAsync(customerId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.RecommendationId, recommendationId, StringComparison.Ordinal))
            ?? new SalesActionRecord
            {
                CustomerId = customerId,
                RecommendationId = recommendationId,
                ActionType = recommendation.RecommendationType,
                Description = recommendation.Action,
                Owner = lead?.Owner ?? "",
                DueAt = task.DueAt
            };
        return new ActionState(recommendation, task, action, lead);
    }

    private static void CaptureBaseline(SalesActionRecord action, Lead? lead, DateTimeOffset capturedAt)
    {
        if (action.BaselineStage is not null || lead is null) return;
        action.BaselineStage = lead.Stage;
        action.BaselineCapturedAt = capturedAt;
    }

    private async Task SaveEventAsync(
        string customerId,
        string sourceId,
        string eventType,
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
        {
            CustomerId = customerId,
            EventType = eventType,
            Title = title,
            Detail = detail,
            SourceType = "customer_brain_action",
            SourceId = sourceId,
            OccurredAt = DateTimeOffset.Now
        }, cancellationToken);
    }

    private sealed record ActionState(
        AiRecommendationRecord Recommendation,
        FollowUpTask Task,
        SalesActionRecord Action,
        Lead? Lead);
}
