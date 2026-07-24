using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class TodayBriefService
{
    private readonly LocalRepository _repository;
    private readonly PersonalSalesLearningService _learning;

    public TodayBriefService(LocalRepository repository, PersonalSalesLearningService? learning = null)
    {
        _repository = repository;
        _learning = learning ?? new PersonalSalesLearningService(repository);
    }

    public async Task<TodayBriefSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var leadsById = leads.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var tasks = await _repository.GetFollowUpTasksAsync(null, cancellationToken);
        var activeTasks = tasks
            .Where(item => item.Status is FollowUpTaskStatus.Proposed or FollowUpTaskStatus.Open or FollowUpTaskStatus.InProgress)
            .OrderByDescending(item => PriorityRank(item.Priority))
            .ThenBy(item => item.DueAt)
            .ToList();

        var items = new List<TodayBriefItem>();
        foreach (var task in activeTasks.Take(20))
        {
            leadsById.TryGetValue(task.CustomerId, out var lead);
            var profile = await _repository.GetCustomerIntelligenceProfileAsync(task.CustomerId, cancellationToken);
            items.Add(new TodayBriefItem
            {
                CustomerId = task.CustomerId,
                CustomerName = lead?.DisplayName ?? profile?.CustomerName ?? task.CustomerId,
                RecommendationId = task.RecommendationId,
                Action = task.Title,
                Reason = task.Reason,
                Priority = task.Priority,
                Status = task.Status,
                DueAt = task.DueAt,
                PurchaseProbability = profile?.PurchaseProbability ?? lead?.PurchaseProbability ?? 0,
                Confidence = profile?.Confidence ?? lead?.AnalysisConfidence ?? 0,
                SuggestedStage = profile?.SuggestedStage ?? lead?.Stage ?? LeadStage.New
            });
        }

        var states = await _repository.GetAgentStatesAsync(cancellationToken: cancellationToken);
        var identityPending = states
            .Where(item => item.Mode == ConversationAgentMode.IdentityResolutionRequired)
            .GroupBy(
                item => string.IsNullOrWhiteSpace(item.CustomerId)
                    ? $"conversation:{item.AccountId}:{item.ConversationId}"
                    : $"customer:{item.CustomerId}",
                StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        var handoffs = await _repository.GetOpenHumanHandoffsAsync(cancellationToken);
        var sourcingRequests = await _repository.GetLatestSourcingRequestsAsync(cancellationToken);
        var sourcingComplete = sourcingRequests
            .Where(item => item.Status == SourcingRequestStatus.Complete)
            .ToList();
        var crossAccount = states
            .Where(item => !string.IsNullOrWhiteSpace(item.CustomerId))
            .GroupBy(item => item.CustomerId, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First()).ToList();

        foreach (var state in identityPending.Take(8))
            items.Insert(0, BuildSpecialItem(state.CustomerId, "identity", "确认跨账号客户身份",
                "号码或 WhatsApp 身份存在歧义；确认前所有自动回复保持关闭。", FollowUpPriority.Urgent,
                state.AccountId, state.ConversationId, now));
        foreach (var handoff in handoffs.Take(8))
            items.Insert(0, BuildSpecialItem(handoff.CustomerId, "handoff", "处理人工接管事件",
                handoff.Reason, FollowUpPriority.Urgent, handoff.AccountId, handoff.ConversationId, now));
        foreach (var sourcing in sourcingComplete.Take(8))
            items.Add(BuildSpecialItem(sourcing.CustomerId, "sourcing_complete", "确认并提交完整采购需求",
                "图片、数量、目标价、目的地和运输偏好已收齐。", FollowUpPriority.High,
                sourcing.Fields.Values.OrderByDescending(item => item.ObservedAt)
                    .Select(item => item.SourceAccountId).FirstOrDefault() ?? "", "", now));
        foreach (var state in crossAccount.Take(8))
            items.Add(BuildSpecialItem(state.CustomerId, "cross_account", "复核跨账号连续跟进",
                "该客户出现在多个 WhatsApp 账号中，请确认本轮主跟进账号。", FollowUpPriority.High,
                state.AccountId, state.ConversationId, now));

        var learning = await _learning.RefreshAsync(cancellationToken);

        return new TodayBriefSnapshot
        {
            GeneratedAt = now,
            OverdueCount = activeTasks.Count(item => item.DueAt < now),
            DueTodayCount = activeTasks.Count(item => item.DueAt.Date == now.Date),
            InProgressCount = activeTasks.Count(item => item.Status == FollowUpTaskStatus.InProgress),
            IdentityPendingCount = identityPending.Count,
            HumanHandoffCount = handoffs.Count,
            SourcingCompleteCount = sourcingComplete.Count,
            CrossAccountFollowUpCount = crossAccount.Count,
            Items = items
                .OrderByDescending(item => PriorityRank(item.Priority))
                .ThenBy(item => item.DueAt)
                .Take(30).ToList(),
            Learning = learning
        };
    }

    private TodayBriefItem BuildSpecialItem(
        string customerId, string category, string action, string reason, FollowUpPriority priority,
        string accountId, string conversationId, DateTimeOffset dueAt)
    {
        return new TodayBriefItem
        {
            CustomerId = customerId,
            CustomerName = customerId,
            Category = category,
            Action = action,
            Reason = reason,
            Priority = priority,
            Status = FollowUpTaskStatus.Open,
            DueAt = dueAt,
            SourceAccountId = accountId,
            SourceConversationId = conversationId
        };
    }

    private static int PriorityRank(FollowUpPriority priority) => priority switch
    {
        FollowUpPriority.Urgent => 4,
        FollowUpPriority.High => 3,
        FollowUpPriority.Normal => 2,
        _ => 1
    };
}
