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
        var states = await _repository.GetAgentStatesAsync(cancellationToken: cancellationToken);
        var handoffs = await _repository.GetOpenHumanHandoffsAsync(cancellationToken);
        var sourcingRequests = await _repository.GetLatestSourcingRequestsAsync(cancellationToken);
        var sourceAccountIds = states.Select(item => item.AccountId)
            .Concat(handoffs.Select(item => item.AccountId))
            .Concat(sourcingRequests.SelectMany(item => item.Fields.Values.Select(field => field.SourceAccountId)))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var conversationsById = new Dictionary<string, WhatsAppConversation>(StringComparer.OrdinalIgnoreCase);
        foreach (var accountId in sourceAccountIds)
        {
            foreach (var conversation in await _repository.GetWhatsAppConversationsAsync(accountId, cancellationToken))
                conversationsById.TryAdd(conversation.Id, conversation);
        }
        var profileCache = new Dictionary<string, CustomerIntelligenceProfile?>(StringComparer.Ordinal);
        var identityCache = new Dictionary<string, GlobalCustomerIdentity?>(StringComparer.Ordinal);

        async Task<CustomerIntelligenceProfile?> GetProfileAsync(string customerId)
        {
            if (string.IsNullOrWhiteSpace(customerId)) return null;
            if (profileCache.TryGetValue(customerId, out var cached)) return cached;
            var profile = await _repository.GetCustomerIntelligenceProfileAsync(customerId, cancellationToken);
            profileCache[customerId] = profile;
            return profile;
        }

        async Task<string> ResolveCustomerNameAsync(string customerId, string conversationId = "")
        {
            if (leadsById.TryGetValue(customerId, out var lead) && ResolveLeadCustomerName(lead, customerId) is { } leadName)
                return leadName;

            var profile = await GetProfileAsync(customerId);
            if (IsReadableCustomerName(profile?.CustomerName, customerId)) return profile!.CustomerName.Trim();

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                if (!identityCache.TryGetValue(customerId, out var identity))
                {
                    identity = await _repository.GetGlobalCustomerIdentityAsync(customerId, cancellationToken);
                    identityCache[customerId] = identity;
                }
                if (IsReadableCustomerName(identity?.CanonicalName, customerId)) return identity!.CanonicalName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(conversationId) &&
                conversationsById.TryGetValue(conversationId, out var conversation))
            {
                if (IsReadableCustomerName(conversation.DisplayName, customerId)) return conversation.DisplayName.Trim();
                var digits = new string(conversation.Phone.Where(char.IsDigit).ToArray());
                if (digits.Length >= 4) return $"WhatsApp 客户 · 尾号 {digits[^4..]}";
            }

            return "未命名客户";
        }

        var items = new List<TodayBriefItem>();
        foreach (var task in activeTasks.Take(20))
        {
            leadsById.TryGetValue(task.CustomerId, out var lead);
            var profile = await GetProfileAsync(task.CustomerId);
            items.Add(new TodayBriefItem
            {
                CustomerId = task.CustomerId,
                CustomerName = await ResolveCustomerNameAsync(task.CustomerId),
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

        var identityPending = states
            .Where(item => item.Mode == ConversationAgentMode.IdentityResolutionRequired)
            .GroupBy(
                item => string.IsNullOrWhiteSpace(item.CustomerId)
                    ? $"conversation:{item.AccountId}:{item.ConversationId}"
                    : $"customer:{item.CustomerId}",
                StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        var sourcingComplete = sourcingRequests
            .Where(item => item.Status == SourcingRequestStatus.Complete)
            .ToList();
        var crossAccount = states
            .Where(item => !string.IsNullOrWhiteSpace(item.CustomerId))
            .GroupBy(item => item.CustomerId, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First()).ToList();

        foreach (var state in identityPending.Take(8))
            items.Insert(0, BuildSpecialItem(state.CustomerId, await ResolveCustomerNameAsync(state.CustomerId, state.ConversationId),
                "identity", "核对 WhatsApp 昵称、号码与 CRM 资料，确认正确客户后再恢复自动回复",
                "当前客户身份存在歧义；确认前所有自动回复保持关闭。", FollowUpPriority.Urgent,
                state.AccountId, state.ConversationId, now));
        foreach (var handoff in handoffs.Take(8))
            items.Insert(0, BuildSpecialItem(handoff.CustomerId, await ResolveCustomerNameAsync(handoff.CustomerId, handoff.ConversationId),
                "handoff", "打开对应 WhatsApp 会话，完成人工处理并记录结果",
                handoff.Reason, FollowUpPriority.Urgent, handoff.AccountId, handoff.ConversationId, now));
        foreach (var sourcing in sourcingComplete.Take(8))
        {
            var source = sourcing.Fields.Values.OrderByDescending(item => item.ObservedAt).FirstOrDefault();
            items.Add(BuildSpecialItem(sourcing.CustomerId, await ResolveCustomerNameAsync(sourcing.CustomerId, source?.SourceConversationId ?? ""),
                "sourcing_complete", "复核五项采购信息，确认无误后提交采购需求",
                "图片、数量、目标价、目的地和运输偏好已收齐。", FollowUpPriority.High,
                source?.SourceAccountId ?? "", source?.SourceConversationId ?? "", now));
        }
        foreach (var state in crossAccount.Take(8))
            items.Add(BuildSpecialItem(state.CustomerId, await ResolveCustomerNameAsync(state.CustomerId, state.ConversationId),
                "cross_account", "指定本轮主跟进账号，并检查其他账号是否存在重复触达",
                "该客户出现在多个 WhatsApp 账号中，需要统一本轮跟进责任。", FollowUpPriority.High,
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
        string customerId, string customerName, string category, string action, string reason, FollowUpPriority priority,
        string accountId, string conversationId, DateTimeOffset dueAt)
    {
        return new TodayBriefItem
        {
            CustomerId = customerId,
            CustomerName = customerName,
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

    private static bool IsReadableCustomerName(string? value, string customerId)
    {
        var candidate = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Equals(customerId, StringComparison.OrdinalIgnoreCase)) return false;
        return candidate.Length < 24 || !candidate.All(Uri.IsHexDigit);
    }

    private static string? ResolveLeadCustomerName(Lead lead, string customerId)
    {
        var candidates = new List<string?> { lead.Name };
        candidates.AddRange(lead.CustomFields
            .Where(item =>
            {
                var key = item.Key.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
                return key.Contains("nickname", StringComparison.Ordinal)
                       || key.Contains("buyername", StringComparison.Ordinal)
                       || key.Contains("customername", StringComparison.Ordinal)
                       || key.Contains("买家昵称", StringComparison.Ordinal)
                       || key.Contains("买家姓名", StringComparison.Ordinal)
                       || key.Contains("客户姓名", StringComparison.Ordinal);
            })
            .Select(item => item.Value));
        candidates.Add(lead.Company);
        return candidates.Select(item => item?.Trim())
            .FirstOrDefault(item => IsReadableCustomerName(item, customerId));
    }

    private static int PriorityRank(FollowUpPriority priority) => priority switch
    {
        FollowUpPriority.Urgent => 4,
        FollowUpPriority.High => 3,
        FollowUpPriority.Normal => 2,
        _ => 1
    };
}
