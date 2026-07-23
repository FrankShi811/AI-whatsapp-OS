using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeadStage
{
    New,
    Contacted,
    Interested,
    RequirementConfirmed,
    Quotation,
    Negotiation,
    Waiting,
    Customer,
    RepeatPurchase,
    Lost
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisStatus { NotRun, Queued, Running, Succeeded, RetryableFailed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DraftStatus { Draft, Approved, Superseded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignStatus { Draft, Scheduled, Running, Paused, SafetyStopped, Completed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignRecipientStatus { Queued, Sending, Sent, Skipped, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignScheduleMode { Immediate, Scheduled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignIntervalUnit { Seconds, Minutes }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignChannel { WhatsApp, Email }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailProviderKind { Gmail, Microsoft365, Yahoo, ICloud, Custom }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailConnectionStatus { NotConfigured, Connected, Error }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailMessageDirection { Incoming, Outgoing }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailMessageStatus { Draft, Sending, Sent, Received, Failed }

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
    public bool EmailOptedOut { get; set; }
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
    public bool StageManuallyLocked { get; set; }
    public string StageSource { get; set; } = "system";
    public DateTimeOffset? StageManuallyUpdatedAt { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; } = "D";
    public int AnalysisContractVersion { get; set; }
    public int BaseProfileScore { get; set; }
    public int BehaviorSignalScore { get; set; }
    public Dictionary<string, int> ScoreBreakdown { get; set; } = [];
    public List<string> ScoreReasons { get; set; } = [];
    public List<LeadFactor> ScoreFactors { get; set; } = [];
    public List<LeadBehaviorSignal> BehaviorSignals { get; set; } = [];
    public string ProfileSummary { get; set; } = "等待 AI 分析";
    public string CustomerSegment { get; set; } = "未分析";
    public string NextAction { get; set; } = "补充客户信息后进行分析";
    public string RiskWarning { get; set; } = "";
    public List<string> Risks { get; set; } = [];
    public List<AnalysisEvidence> Evidence { get; set; } = [];
    public double AnalysisConfidence { get; set; }
    public int PurchaseProbability { get; set; }
    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.NotRun;
    public string AnalysisError { get; set; } = "";
    public bool AiScoreApplied { get; set; }
    public string AnalysisTrigger { get; set; } = "";
    public DateTimeOffset? AnalysisRequestedAt { get; set; }
    public DateTimeOffset? LastAnalyzedAt { get; set; }
    public List<string> LatestReplySignals { get; set; } = [];
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
    [JsonIgnore] public string AnalysisStateLabel => AnalysisStatus == AnalysisStatus.RetryableFailed
        ? AnalysisError switch
        {
            var value when value.Contains("invalid_structured_output", StringComparison.OrdinalIgnoreCase) => "AI 格式异常 · 可重试",
            var value when value.Contains("provider_rate_limited", StringComparison.OrdinalIgnoreCase) => "API 限流 · 可重试",
            var value when value.Contains("provider_timeout", StringComparison.OrdinalIgnoreCase) || value.Contains("provider_unavailable", StringComparison.OrdinalIgnoreCase) => "网络异常 · 可重试",
            var value when value.Contains("provider_unauthorized", StringComparison.OrdinalIgnoreCase) || value.Contains("model_not_selected", StringComparison.OrdinalIgnoreCase) || value.Contains("provider_not_configured", StringComparison.OrdinalIgnoreCase) => "AI 配置异常",
            var value when value.Contains("取消", StringComparison.OrdinalIgnoreCase) => "已停止 · 可重试",
            _ => "执行失败 · 可重试"
        }
        : Labels.Analysis(AnalysisStatus);
    [JsonIgnore] public bool HasCurrentAiScore => AiScoreApplied && AnalysisContractVersion == LeadIntelligenceContract.Version && AnalysisStatus == AnalysisStatus.Succeeded;
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
    public DateTimeOffset? LastReadAt { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string LastTimeLabel => LastMessageAt == default ? "" : LastMessageAt.LocalDateTime.ToString("MM-dd HH:mm");
}

public sealed class WhatsAppContact
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "primary";
    public string Jid { get; set; } = "";
    public string SourceJid { get; set; } = "";
    public string Phone { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SavedName { get; set; } = "";
    public string NotifyName { get; set; } = "";
    public string VerifiedName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Source { get; set; } = "live";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string SearchText => string.Join(' ', new[] { DisplayName, SavedName, NotifyName, VerifiedName, Username, Phone, Jid }.Where(value => !string.IsNullOrWhiteSpace(value)));
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
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string MediaPath { get; set; } = "";
    public string MediaDownloadError { get; set; } = "";
    public string PushName { get; set; } = "";
    public string QuotedMessageId { get; set; } = "";
    public string QuotedText { get; set; } = "";
    public bool QuotedFromMe { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public bool IsStatusUpdate { get; set; }
    public DateTimeOffset? StatusExpiresAt { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StatusUpdatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string FailureReason { get; set; } = "";
    public string Source { get; set; } = "notify";
}

public sealed class WhatsAppIpState
{
    public string AccountId { get; set; } = "primary";
    public string CurrentIp { get; set; } = "";
    public string PreviousIp { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Country { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public string Isp { get; set; } = "";
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? ChangedAt { get; set; }

    [JsonIgnore] public string LocationLabel => string.Join(" · ", new[] { Country, Region, City }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.CurrentCultureIgnoreCase));
}

public sealed class WhatsAppAccount
{
    public string Id { get; set; } = "primary";
    public string Name { get; set; } = "个人号 1";
    public string LinkedPhone { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(LinkedPhone) ? Name : $"{Name} · {LinkedPhone}";
}

public sealed class EmailAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public EmailProviderKind Provider { get; set; } = EmailProviderKind.Custom;
    public string UserName { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public bool SmtpUseSsl { get; set; } = true;
    public EmailConnectionStatus Status { get; set; } = EmailConnectionStatus.NotConfigured;
    public string LastError { get; set; } = "";
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : $"{DisplayName} · {EmailAddress}";
    [JsonIgnore] public string StatusLabel => Status switch
    {
        EmailConnectionStatus.Connected => "已连接",
        EmailConnectionStatus.Error => "连接异常",
        _ => "未配置"
    };
}

public sealed class EmailConversation
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string LeadId { get; set; } = "";
    public string PeerEmail { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string LastMessage { get; set; } = "";
    public DateTimeOffset LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(PeerName) ? PeerEmail : PeerName;
    [JsonIgnore] public string LastTimeLabel => LastMessageAt == default ? "" : LastMessageAt.LocalDateTime.ToString("MM-dd HH:mm");
}

public sealed class EmailMessage
{
    public string Id { get; set; } = "";
    public string ProviderMessageId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string LeadId { get; set; } = "";
    public EmailMessageDirection Direction { get; set; }
    public EmailMessageStatus Status { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public List<string> ToAddresses { get; set; } = [];
    public List<string> CcAddresses { get; set; } = [];
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string FailureReason { get; set; } = "";

    [JsonIgnore] public string TimeLabel => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string DirectionLabel => Direction == EmailMessageDirection.Outgoing ? "已发送" : "已接收";
}

public sealed class WhatsAppCampaign
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CampaignChannel Channel { get; set; } = CampaignChannel.WhatsApp;
    public string AccountId { get; set; } = "primary";
    public string Name { get; set; } = "";
    public string GradeFilter { get; set; } = "全部";
    public LeadStage? StageFilter { get; set; }
    public string TagFilter { get; set; } = "";
    public string OwnerFilter { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string MessageTemplate { get; set; } = "Hi {name}, I'd like to follow up about {product}.";
    public string EmailSubjectTemplate { get; set; } = "Follow-up for {name}";
    public List<string> SelectedLeadIds { get; set; } = [];
    public CampaignScheduleMode ScheduleMode { get; set; } = CampaignScheduleMode.Scheduled;
    public DateTimeOffset StartsAt { get; set; } = DateTimeOffset.Now.AddMinutes(5);
    public int IntervalValue { get; set; }
    public CampaignIntervalUnit IntervalUnit { get; set; } = CampaignIntervalUnit.Minutes;
    // Kept for existing 1.7.x campaign JSON. New campaigns use IntervalValue + IntervalUnit.
    public int IntervalMinutes { get; set; } = 5;
    public int DailyLimit { get; set; } = 50;
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public string PauseReason { get; set; } = "";
    public string BaselinePublicIp { get; set; } = "";
    public string SafetyStopFromIp { get; set; } = "";
    public string SafetyStopToIp { get; set; } = "";
    public string SafetyStopPosition { get; set; } = "";
    public DateTimeOffset? SafetyStoppedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string StatusLabel => Status switch
    {
        CampaignStatus.Scheduled => "已排期", CampaignStatus.Running => "发送中", CampaignStatus.Paused => "已暂停",
        CampaignStatus.SafetyStopped => "IP 安全停止",
        CampaignStatus.Completed => "已完成", CampaignStatus.Cancelled => "已取消", _ => "草稿"
    };
    [JsonIgnore] public int EffectiveIntervalValue => IntervalValue > 0 ? IntervalValue : Math.Max(1, IntervalMinutes);
    [JsonIgnore] public TimeSpan IntervalDelay => IntervalUnit == CampaignIntervalUnit.Seconds
        ? TimeSpan.FromSeconds(EffectiveIntervalValue)
        : TimeSpan.FromMinutes(EffectiveIntervalValue);
    [JsonIgnore] public string ScheduleLabel => ScheduleMode == CampaignScheduleMode.Immediate ? "立即发送" : StartsAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public bool IsEditable => Status == CampaignStatus.Draft;
    [JsonIgnore] public string ChannelLabel => Channel == CampaignChannel.Email ? "邮件" : "WhatsApp";
}

public sealed class CampaignMessageTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CampaignRecipient
{
    public string Id { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string LeadId { get; set; } = "";
    public string AccountId { get; set; } = "primary";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RenderedSubject { get; set; } = "";
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
    public List<string> Evidence { get; set; } = [];
}

public sealed class LeadBehaviorSignal
{
    public string Signal { get; set; } = "";
    public int Score { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class LeadAnalysis
{
    public int ContractVersion { get; set; } = LeadIntelligenceContract.Version;
    public int Score { get; set; }
    public int BaseProfileScore { get; set; }
    public int BehaviorSignalScore { get; set; }
    public string Grade { get; set; } = "D";
    public List<LeadFactor> Factors { get; set; } = [];
    public List<LeadBehaviorSignal> BehaviorSignals { get; set; } = [];
    public LeadStage Stage { get; set; }
    public double Confidence { get; set; }
    public int PurchaseProbability { get; set; }
    public List<AnalysisEvidence> Evidence { get; set; } = [];
    public string ProfileSummary { get; set; } = "";
    public string CustomerSegment { get; set; } = "";
    public string NextAction { get; set; } = "";
    public string RiskWarning { get; set; } = "";
    public List<string> Risks { get; set; } = [];
}

public static class LeadIntelligenceContract
{
    public const int Version = 2;
    public const int BehaviorSignalMinimum = -20;
    public const int BehaviorSignalMaximum = 20;
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
    public string Provider { get; set; } = "compatible";
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
    public int ActiveCampaigns { get; set; }
    public int FailedAnalyses { get; set; }
    public int AnalyzedLeads { get; set; }
    public int QueuedAnalyses { get; set; }
    public int CampaignSent { get; set; }
    public int CampaignFailed { get; set; }
    public int CampaignQueued { get; set; }
    public int SafetyStoppedCampaigns { get; set; }
    public List<Lead> PriorityLeads { get; set; } = [];
    public string LastImportText { get; set; } = "暂无导入记录";
}

public sealed class AppSettings
{
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string ActiveProviderId { get; set; } = "deepseek";
    public List<AiProviderProfile> ConfiguredAiProviders { get; set; } = [];
    public string ThemeMode { get; set; } = "System";
    public List<string> AvailableModels { get; set; } = [];
    public string ModelsBaseUrl { get; set; } = "";
    public DateTimeOffset? ModelsFetchedAt { get; set; }
}

public sealed class AiProviderProfile
{
    public string ProviderId { get; set; } = "deepseek";
    public string DisplayName { get; set; } = "DeepSeek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "";
    public List<string> AvailableModels { get; set; } = [];
    public DateTimeOffset? ModelsFetchedAt { get; set; }
    public bool IsConfigured { get; set; }
}

public sealed class LeadBulkAnalysisRunState
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public List<string> AllLeadIds { get; set; } = [];
    public List<string> PendingLeadIds { get; set; } = [];
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public bool IsComplete { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class OnboardingState
{
    public bool Completed { get; set; }
    public int GuideVersion { get; set; } = 1;
    public DateTimeOffset? CompletedAt { get; set; }
    public int ModuleGuideVersion { get; set; }
    public List<string> SeenModuleGuides { get; set; } = [];
    public Dictionary<string, int> SeenGuideVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class Labels
{
    public static string Stage(LeadStage value) => value switch
    {
        LeadStage.RequirementConfirmed => "\u9700\u6c42\u5df2\u786e\u8ba4",
        LeadStage.Quotation => "\u62a5\u4ef7\u4e2d",
        LeadStage.RepeatPurchase => "\u590d\u8d2d\u5ba2\u6237",
        LeadStage.New => "新商机", LeadStage.Contacted => "已联系", LeadStage.Interested => "有兴趣",
        LeadStage.Negotiation => "谈判中", LeadStage.Waiting => "等待中", LeadStage.Customer => "已成交",
        LeadStage.Lost => "已流失", _ => value.ToString()
    };

    public static string Analysis(AnalysisStatus value) => value switch
    {
        AnalysisStatus.Queued => "等待 AI", AnalysisStatus.Running => "分析中", AnalysisStatus.Succeeded => "已完成",
        AnalysisStatus.RetryableFailed => "可重试", _ => "未分析"
    };
}
