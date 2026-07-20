using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeadStage { New, Contacted, Interested, Negotiation, Waiting, Customer, Lost }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisStatus { NotRun, Running, Succeeded, RetryableFailed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DraftStatus { Draft, Approved, Superseded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignStatus { Draft, Scheduled, Running, Paused, Completed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignRecipientStatus { Queued, Sending, Sent, Skipped, Failed, Cancelled }

public sealed class SalesProfile
{
    public string CompanyName { get; set; } = "";
    public List<string> Products { get; set; } = [];
    public List<string> Advantages { get; set; } = [];
    public string DefaultLanguage { get; set; } = "en";
    public List<string> TargetMarkets { get; set; } = [];
}

public sealed class Lead
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Country { get; set; } = "";
    public string PhoneE164 { get; set; } = "";
    public bool PhoneValid { get; set; }
    public bool OptedOut { get; set; }
    public bool WhatsAppOptIn { get; set; }
    public DateTimeOffset? WhatsAppOptInAt { get; set; }
    public string WhatsAppOptInSource { get; set; } = "";
    public string Email { get; set; } = "";
    public string PreferredLanguage { get; set; } = "en";
    public string ProductInterest { get; set; } = "";
    public decimal EstimatedOrderValue { get; set; }
    public string Currency { get; set; } = "USD";
    public double CompanyScale { get; set; }
    public double PurchasePower { get; set; }
    public bool ExplicitDemand { get; set; }
    public bool RegisteredOrConsulted { get; set; }
    public string Source { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, string> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Owner { get; set; } = "";
    public LeadStage Stage { get; set; } = LeadStage.New;
    public int Score { get; set; }
    public string Grade { get; set; } = "D";
    public Dictionary<string, int> ScoreBreakdown { get; set; } = [];
    public List<string> ScoreReasons { get; set; } = [];
    public string ProfileSummary { get; set; } = "等待 DeepSeek 分析";
    public string CustomerSegment { get; set; } = "未分析";
    public string NextAction { get; set; } = "补充客户信息后进行分析";
    public List<string> Risks { get; set; } = [];
    public List<AnalysisEvidence> Evidence { get; set; } = [];
    public double AnalysisConfidence { get; set; }
    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.NotRun;
    public string AnalysisError { get; set; } = "";
    public string LatestMessage { get; set; } = "";
    public DateTimeOffset? LastContactAt { get; set; }
    public DateTimeOffset? NextFollowUpAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Company : Name;
    [JsonIgnore] public string StageLabel => Labels.Stage(Stage);
    [JsonIgnore] public string AmountLabel => EstimatedOrderValue <= 0 ? "—" : $"{Currency} {EstimatedOrderValue:N0}";
    [JsonIgnore] public string PhoneState => PhoneValid ? "有效" : "风险";
    [JsonIgnore] public string TagsLabel => string.Join(" · ", Tags);
    [JsonIgnore] public string CustomFieldsLabel => string.Join(" · ", CustomFields.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => $"{x.Key}: {x.Value}"));
    [JsonIgnore] public string AnalysisStateLabel => Labels.Analysis(AnalysisStatus);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WhatsAppMessageDirection { Incoming, Outgoing }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WhatsAppMessageStatus { Pending, Sent, Delivered, Read, Failed, Received }

public sealed class WhatsAppConversation
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "primary";
    public string Phone { get; set; } = "";
    public string LeadId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string LastMessage { get; set; } = "";
    public DateTimeOffset LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string LastTimeLabel => LastMessageAt == default ? "" : LastMessageAt.LocalDateTime.ToString("MM-dd HH:mm");
}

public sealed class WhatsAppMessage
{
    public string Id { get; set; } = "";
    public string ProviderMessageId { get; set; } = "";
    public string AccountId { get; set; } = "primary";
    public string ConversationId { get; set; } = "";
    public string LeadId { get; set; } = "";
    public string Phone { get; set; } = "";
    public WhatsAppMessageDirection Direction { get; set; }
    public WhatsAppMessageStatus Status { get; set; }
    public string Kind { get; set; } = "text";
    public string Body { get; set; } = "";
    public string PushName { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Source { get; set; } = "notify";
}

public sealed class WhatsAppAccount
{
    public string Id { get; set; } = "primary";
    public string Name { get; set; } = "个人号 1";
    public string LinkedPhone { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(LinkedPhone) ? Name : $"{Name} · {LinkedPhone}";
}

public sealed class WhatsAppCampaign
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "primary";
    public string Name { get; set; } = "";
    public string GradeFilter { get; set; } = "全部";
    public LeadStage? StageFilter { get; set; }
    public string TagFilter { get; set; } = "";
    public string OwnerFilter { get; set; } = "";
    public string MessageTemplate { get; set; } = "Hi {name}, I'd like to follow up about {product}.";
    public DateTimeOffset StartsAt { get; set; } = DateTimeOffset.Now.AddMinutes(5);
    public int IntervalMinutes { get; set; } = 5;
    public int DailyLimit { get; set; } = 50;
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public string PauseReason { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string StatusLabel => Status switch
    {
        CampaignStatus.Scheduled => "已排期", CampaignStatus.Running => "发送中", CampaignStatus.Paused => "已暂停",
        CampaignStatus.Completed => "已完成", CampaignStatus.Cancelled => "已取消", _ => "草稿"
    };
    [JsonIgnore] public string ScheduleLabel => StartsAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public bool IsEditable => Status == CampaignStatus.Draft;
}

public sealed class CampaignRecipient
{
    public string Id { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string LeadId { get; set; } = "";
    public string AccountId { get; set; } = "primary";
    public string Phone { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RenderedMessage { get; set; } = "";
    public CampaignRecipientStatus Status { get; set; } = CampaignRecipientStatus.Queued;
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string ProviderMessageId { get; set; } = "";
    public string LastError { get; set; } = "";
    public string SkipReason { get; set; } = "";
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string StatusLabel => Status switch
    {
        CampaignRecipientStatus.Queued => "等待发送", CampaignRecipientStatus.Sending => "正在发送",
        CampaignRecipientStatus.Sent => "已发送", CampaignRecipientStatus.Skipped => "已跳过",
        CampaignRecipientStatus.Failed => "失败", CampaignRecipientStatus.Cancelled => "已取消", _ => Status.ToString()
    };
    [JsonIgnore] public string ScheduledLabel => ScheduledAt.LocalDateTime.ToString("MM-dd HH:mm");
}

public sealed class AnalysisEvidence
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string Interpretation { get; set; } = "";
}

public sealed class LeadFactor
{
    public string Key { get; set; } = "";
    public int Score { get; set; }
    public int MaxScore { get; set; }
    public string Rationale { get; set; } = "";
}

public sealed class LeadAnalysis
{
    public int Score { get; set; }
    public string Grade { get; set; } = "D";
    public List<LeadFactor> Factors { get; set; } = [];
    public LeadStage Stage { get; set; }
    public double Confidence { get; set; }
    public List<AnalysisEvidence> Evidence { get; set; } = [];
    public string ProfileSummary { get; set; } = "";
    public string CustomerSegment { get; set; } = "";
    public string NextAction { get; set; } = "";
    public List<string> Risks { get; set; } = [];
}

public sealed class OutreachDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LeadId { get; set; } = "";
    public string LeadName { get; set; } = "";
    public string Purpose { get; set; } = "first_contact";
    public string Language { get; set; } = "en";
    public string Body { get; set; } = "";
    public List<string> Rationale { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public DraftStatus Status { get; set; } = DraftStatus.Draft;
    public string Provider { get; set; } = "deepseek";
    public string Model { get; set; } = "deepseek-chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = "";

    [JsonIgnore] public string StatusLabel => Status switch { DraftStatus.Approved => "已确认", DraftStatus.Superseded => "已替代", _ => "草稿" };
    [JsonIgnore] public string CreatedLabel => CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
}

public sealed class DashboardSnapshot
{
    public int TotalLeads { get; set; }
    public Dictionary<string, int> Grades { get; set; } = new() { ["A"] = 0, ["B"] = 0, ["C"] = 0, ["D"] = 0 };
    public Dictionary<LeadStage, int> Stages { get; set; } = [];
    public int PendingFollowUps { get; set; }
    public int ReadyDrafts { get; set; }
    public int FailedAnalyses { get; set; }
    public List<Lead> PriorityLeads { get; set; } = [];
    public string LastImportText { get; set; } = "暂无导入记录";
}

public sealed class AppSettings
{
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
}

public static class Labels
{
    public static string Stage(LeadStage value) => value switch
    {
        LeadStage.New => "新商机", LeadStage.Contacted => "已联系", LeadStage.Interested => "有兴趣",
        LeadStage.Negotiation => "谈判中", LeadStage.Waiting => "等待中", LeadStage.Customer => "已成交",
        LeadStage.Lost => "已流失", _ => value.ToString()
    };

    public static string Analysis(AnalysisStatus value) => value switch
    {
        AnalysisStatus.Running => "分析中", AnalysisStatus.Succeeded => "已完成",
        AnalysisStatus.RetryableFailed => "可重试", _ => "未分析"
    };
}
