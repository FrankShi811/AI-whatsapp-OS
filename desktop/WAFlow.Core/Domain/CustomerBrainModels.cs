using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntelligenceStatementNature
{
    Fact,
    Inference,
    Recommendation,
    InformationGap
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiRecommendationStatus
{
    Proposed,
    Accepted,
    Dismissed,
    InProgress,
    Completed,
    Failed,
    Superseded
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SalesActionStatus
{
    Planned,
    Approved,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerBrainRunStatus
{
    Queued,
    Collecting,
    Understanding,
    EvaluatingOpportunity,
    Recommending,
    Succeeded,
    RetryableFailed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerBrainDecisionStatus
{
    NotAnalyzed,
    Current,
    Stale,
    RetryableFailed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FollowUpTaskStatus
{
    Proposed,
    Open,
    InProgress,
    Completed,
    Dismissed,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FollowUpPriority
{
    Low,
    Normal,
    High,
    Urgent
}

public sealed class CustomerIntelligenceStatement
{
    public IntelligenceStatementNature Nature { get; set; }
    public string Topic { get; set; } = "";
    public string Text { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerIntelligenceCoverage
{
    public bool HasCrmData { get; set; }
    public bool HasWhatsAppHistory { get; set; }
    public bool HasEmailHistory { get; set; }
    public bool HasLeadAnalysis { get; set; }
    public bool HasCustomerReport { get; set; }
    public bool HasCampaignHistory { get; set; }

    [JsonIgnore]
    public int Percentage
    {
        get
        {
            var values = new[] { HasCrmData, HasWhatsAppHistory, HasEmailHistory, HasLeadAnalysis, HasCustomerReport, HasCampaignHistory };
            return (int)Math.Round(values.Count(value => value) / (double)values.Length * 100);
        }
    }
}

public sealed class CustomerIntelligenceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public int Version { get; set; } = 1;
    public string CustomerName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public List<string> BusinessModels { get; set; } = [];
    public List<string> PurchaseMotivations { get; set; } = [];
    public List<string> PainPoints { get; set; } = [];
    public List<string> OpportunitySignals { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public string NextBestAction { get; set; } = "";
    public double Confidence { get; set; }
    public int PurchaseProbability { get; set; }
    public LeadStage SuggestedStage { get; set; } = LeadStage.New;
    public CustomerBrainDecisionStatus DecisionStatus { get; set; } = CustomerBrainDecisionStatus.NotAnalyzed;
    public string DecisionSourceSnapshotHash { get; set; } = "";
    public string LastBrainRunId { get; set; } = "";
    public DateTimeOffset? LastBrainAnalyzedAt { get; set; }
    public string AiModel { get; set; } = "";
    public CustomerIntelligenceCoverage Coverage { get; set; } = new();
    public List<CustomerIntelligenceStatement> Statements { get; set; } = [];
    public string SourceSnapshotHash { get; set; } = "";
    public DateTimeOffset SourceCapturedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string VersionLabel => $"Brain V{Version}";
    [JsonIgnore] public string CoverageLabel => $"数据覆盖 {Coverage.Percentage}%";
    [JsonIgnore] public bool HasCurrentDecision =>
        DecisionStatus == CustomerBrainDecisionStatus.Current
        && !string.IsNullOrWhiteSpace(DecisionSourceSnapshotHash)
        && string.Equals(DecisionSourceSnapshotHash, SourceSnapshotHash, StringComparison.Ordinal);
}

public sealed class CustomerUnderstandingResult
{
    public string CustomerDna { get; set; } = "";
    public string ProfileSummary { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public List<string> BusinessModels { get; set; } = [];
    public List<string> PainPoints { get; set; } = [];
    public List<string> PurchaseMotivations { get; set; } = [];
    public List<string> InformationGaps { get; set; } = [];
    public List<CustomerIntelligenceStatement> Statements { get; set; } = [];
}

public sealed class CustomerOpportunityEvaluation
{
    public int PurchaseProbability { get; set; }
    public double Confidence { get; set; }
    public LeadStage SuggestedStage { get; set; } = LeadStage.New;
    public List<string> PositiveSignals { get; set; } = [];
    public List<string> RiskSignals { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public string Rationale { get; set; } = "";
}

public sealed class CustomerSalesRecommendation
{
    public string NextBestAction { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string SuggestedTalkTrack { get; set; } = "";
    public List<string> QuestionsToVerify { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public int DueInHours { get; set; } = 24;
    public FollowUpPriority Priority { get; set; } = FollowUpPriority.Normal;
}

public sealed class CustomerBrainRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public CustomerBrainRunStatus Status { get; set; } = CustomerBrainRunStatus.Queued;
    public string AiModel { get; set; } = "";
    public string SourceSnapshotHash { get; set; } = "";
    public string SourceSnapshotJson { get; set; } = "";
    public string UnderstandingJson { get; set; } = "";
    public string OpportunityJson { get; set; } = "";
    public string RecommendationJson { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AiRecommendationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationType { get; set; } = "next_best_action";
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string Rationale { get; set; } = "";
    public List<string> Evidence { get; set; } = [];
    public double Confidence { get; set; }
    public AiRecommendationStatus Status { get; set; } = AiRecommendationStatus.Proposed;
    public string SourceProfileId { get; set; } = "";
    public int SourceProfileVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerBehaviorEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceType { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SalesActionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string Description { get; set; } = "";
    public string Owner { get; set; } = "";
    public SalesActionStatus Status { get; set; } = SalesActionStatus.Planned;
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Outcome { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AiLearningFeedback
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool Helpful { get; set; }
    public string FeedbackSource { get; set; } = "human";
    public string Note { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class FollowUpTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public FollowUpPriority Priority { get; set; } = FollowUpPriority.Normal;
    public FollowUpTaskStatus Status { get; set; } = FollowUpTaskStatus.Proposed;
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.Now.AddDays(1);
    public string SourceType { get; set; } = "customer_brain";
    public string SourceId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CustomerEventLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class TodayBriefItem
{
    public string CustomerId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public FollowUpPriority Priority { get; set; } = FollowUpPriority.Normal;
    public FollowUpTaskStatus Status { get; set; } = FollowUpTaskStatus.Proposed;
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.Now;
    public int PurchaseProbability { get; set; }
    public double Confidence { get; set; }
    public LeadStage SuggestedStage { get; set; } = LeadStage.New;

    [JsonIgnore] public string PriorityLabel => Priority switch
    {
        FollowUpPriority.Urgent => "紧急",
        FollowUpPriority.High => "高优先",
        FollowUpPriority.Normal => "普通",
        _ => "低优先"
    };

    [JsonIgnore] public string DueLabel
    {
        get
        {
            var now = DateTimeOffset.Now;
            if (DueAt <= now) return $"已逾期 {Math.Max(1, (int)Math.Ceiling((now - DueAt).TotalHours))} 小时";
            if (DueAt.Date == now.Date) return $"今天 {DueAt:HH:mm}";
            if (DueAt.Date == now.Date.AddDays(1)) return $"明天 {DueAt:HH:mm}";
            return DueAt.ToString("MM-dd HH:mm");
        }
    }
}

public sealed class PersonalLearningSummary
{
    public int Proposed { get; set; }
    public int Accepted { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Dismissed { get; set; }
    public int FeedbackCount { get; set; }
    public int HelpfulFeedback { get; set; }

    [JsonIgnore] public double CompletionRate => Accepted == 0 ? 0 : Math.Round(100d * Completed / Accepted, 1);
    [JsonIgnore] public double HelpfulRate => FeedbackCount == 0 ? 0 : Math.Round(100d * HelpfulFeedback / FeedbackCount, 1);
}

public sealed class TodayBriefSnapshot
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public int OverdueCount { get; set; }
    public int DueTodayCount { get; set; }
    public int InProgressCount { get; set; }
    public List<TodayBriefItem> Items { get; set; } = [];
    public PersonalLearningSummary Learning { get; set; } = new();
}
