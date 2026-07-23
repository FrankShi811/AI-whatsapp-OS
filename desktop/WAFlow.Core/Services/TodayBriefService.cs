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

        var learning = await _learning.RefreshAsync(cancellationToken);

        return new TodayBriefSnapshot
        {
            GeneratedAt = now,
            OverdueCount = activeTasks.Count(item => item.DueAt < now),
            DueTodayCount = activeTasks.Count(item => item.DueAt.Date == now.Date),
            InProgressCount = activeTasks.Count(item => item.Status == FollowUpTaskStatus.InProgress),
            Items = items,
            Learning = learning
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
