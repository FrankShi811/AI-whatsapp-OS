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
    public string AiModel { get; set; } = "";
    public CustomerIntelligenceCoverage Coverage { get; set; } = new();
    public List<CustomerIntelligenceStatement> Statements { get; set; } = [];
    public string SourceSnapshotHash { get; set; } = "";
    public DateTimeOffset SourceCapturedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string VersionLabel => $"Brain V{Version}";
    [JsonIgnore] public string CoverageLabel => $"数据覆盖 {Coverage.Percentage}%";
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
