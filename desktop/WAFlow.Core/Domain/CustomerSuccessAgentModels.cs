using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerIdentityMatchResult
{
    ExactMatch,
    ConfirmedAliasMatch,
    UniqueInferredMatch,
    AmbiguousMatch,
    NoMatch,
    Conflict
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerIdentityMatchMethod
{
    ManualBinding,
    ExactJid,
    ConfirmedE164,
    ConfirmedAlias,
    CountryAssistedUnique,
    UniqueDigitBody,
    CandidateOnly
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationAgentMode
{
    AutoOff,
    SuggestOnly,
    CopilotActive,
    AutoActive,
    IdentityResolutionRequired,
    HumanRequired,
    HumanActive,
    ResumeReview
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentQuestionSafety
{
    SafeToAnswer,
    DeferredHuman,
    ImmediateHuman
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourcingRequestStatus
{
    Draft,
    Collecting,
    FieldConflict,
    Complete,
    HumanReview,
    Acknowledged,
    Submitted,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourcingFieldKey
{
    ProductImage,
    Quantity,
    TargetPrice,
    Destination,
    ShippingPreference
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HandoffStatus
{
    Open,
    TakenOver,
    Resolved,
    Resumed
}

public sealed class GlobalCustomerIdentity
{
    public string CustomerId { get; set; } = "";
    public string CanonicalName { get; set; } = "";
    public List<string> ConfirmedAliases { get; set; } = [];
    public List<string> LinkedAccountIds { get; set; } = [];
    public string PrimaryAccountId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerPhoneIdentity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RawValue { get; set; } = "";
    public string Digits { get; set; } = "";
    public string CountryHint { get; set; } = "";
    public string E164 { get; set; } = "";
    public string Jid { get; set; } = "";
    public string Lid { get; set; } = "";
    public string SourceAccountId { get; set; } = "";
    public string SourceConversationId { get; set; } = "";
    public bool ManuallyConfirmed { get; set; }
    public double Confidence { get; set; }
    public CustomerIdentityMatchMethod Method { get; set; } = CustomerIdentityMatchMethod.CandidateOnly;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class WhatsAppIdentityLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string ContactJid { get; set; } = "";
    public string ContactLid { get; set; } = "";
    public string PhoneIdentityId { get; set; } = "";
    public CustomerIdentityMatchResult MatchResult { get; set; } = CustomerIdentityMatchResult.NoMatch;
    public CustomerIdentityMatchMethod MatchMethod { get; set; } = CustomerIdentityMatchMethod.CandidateOnly;
    public double Confidence { get; set; }
    public bool ManuallyConfirmed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AccountPersona
{
    public string AccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RoleName { get; set; } = "DHgate Customer Success";
    public string Introduction { get; set; } =
        "I’m the intelligent assistant for DHgate’s customer success team. I can help collect your sourcing needs and coordinate the next steps. A human colleague will follow up on matters that need judgment.";
    public string DefaultLanguage { get; set; } = "en";
    public string Tone { get; set; } = "warm, professional, patient, natural and credible";
    public List<string> AllowedClaims { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AccountRelationshipMemory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string RelationshipStage { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Commitments { get; set; } = [];
    public List<string> KnownPreferences { get; set; } = [];
    public DateTimeOffset? LastInteractionAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerIdentityMatchLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string RawIdentity { get; set; } = "";
    public CustomerIdentityMatchResult Result { get; set; }
    public CustomerIdentityMatchMethod Method { get; set; }
    public List<string> CandidateCustomerIds { get; set; } = [];
    public string Reason { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GlobalCustomerAgentLock
{
    public string CustomerId { get; set; } = "";
    public string ActiveAccountId { get; set; } = "";
    public string ActiveConversationId { get; set; } = "";
    public string AcquiredBy { get; set; } = "";
    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ConversationAgentState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public ConversationAgentMode Mode { get; set; } = ConversationAgentMode.SuggestOnly;
    public string StateReason { get; set; } = "";
    public int PausedMessageCount { get; set; }
    public string LastProcessedMessageId { get; set; } = "";
    public string LastHoldingReplyMessageId { get; set; } = "";
    public bool ExplicitResumeRequired { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class RelationshipMemory
{
    public string CustomerId { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Facts { get; set; } = [];
    public List<string> Preferences { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> Promises { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SourcingFieldValue
{
    public SourcingFieldKey Field { get; set; }
    public string Value { get; set; } = "";
    public string NormalizedValue { get; set; } = "";
    public bool IsStructurallyValid { get; set; }
    public bool HumanConfirmed { get; set; }
    public string SourceAccountId { get; set; } = "";
    public string SourceConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SourcingFieldConflict
{
    public SourcingFieldKey Field { get; set; }
    public List<SourcingFieldValue> Values { get; set; } = [];
    public string Resolution { get; set; } = "";
    public bool IsResolved { get; set; }
}

public sealed class SourcingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public int Version { get; set; } = 1;
    public SourcingRequestStatus Status { get; set; } = SourcingRequestStatus.Draft;
    public Dictionary<SourcingFieldKey, SourcingFieldValue> Fields { get; set; } = [];
    public List<SourcingFieldConflict> Conflicts { get; set; } = [];
    public string Summary { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public int Completeness => Fields.Values.Count(value => value.IsStructurallyValid) * 20;

    [JsonIgnore]
    public IReadOnlyList<SourcingFieldKey> MissingFields =>
        Enum.GetValues<SourcingFieldKey>().Where(field => !Fields.TryGetValue(field, out var value) || !value.IsStructurallyValid).ToList();
}

public sealed class HumanHandoffEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string OriginalMessage { get; set; } = "";
    public string Language { get; set; } = "";
    public string ChineseAssistTranslation { get; set; } = "";
    public string HoldingReply { get; set; } = "";
    public string Reason { get; set; } = "";
    public AgentQuestionSafety Safety { get; set; } = AgentQuestionSafety.ImmediateHuman;
    public HandoffStatus Status { get; set; } = HandoffStatus.Open;
    public List<string> RelatedAccountIds { get; set; } = [];
    public int PausedMessageCount { get; set; }
    public string TakenOverBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class PendingQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Question { get; set; } = "";
    public AgentQuestionSafety Safety { get; set; }
    public string ClassificationReason { get; set; } = "";
    public bool IsResolved { get; set; }
    public string Resolution { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerMergeAudit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceCustomerId { get; set; } = "";
    public string TargetCustomerId { get; set; } = "";
    public string IdentityLinkId { get; set; } = "";
    public string Action { get; set; } = "merge";
    public string Reason { get; set; } = "";
    public string Actor { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AgentTurnLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string StateBefore { get; set; } = "";
    public string StateAfter { get; set; } = "";
    public CustomerIdentityMatchResult IdentityResult { get; set; } = CustomerIdentityMatchResult.NoMatch;
    public AgentQuestionSafety Safety { get; set; } = AgentQuestionSafety.SafeToAnswer;
    public string ContextHash { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string Decision { get; set; } = "";
    public string OutputText { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerIdentityResolution
{
    public CustomerIdentityMatchResult Result { get; set; }
    public CustomerIdentityMatchMethod Method { get; set; }
    public string CustomerId { get; set; } = "";
    public List<string> CandidateCustomerIds { get; set; } = [];
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public bool AllowsAutomation =>
        Result is CustomerIdentityMatchResult.ExactMatch
            or CustomerIdentityMatchResult.ConfirmedAliasMatch
            or CustomerIdentityMatchResult.UniqueInferredMatch;
}

public sealed class CustomerSuccessContext
{
    public string CustomerId { get; set; } = "";
    public Lead? Customer { get; set; }
    public GlobalCustomerIdentity? Identity { get; set; }
    public AccountPersona? Persona { get; set; }
    public AccountRelationshipMemory? AccountRelationship { get; set; }
    public RelationshipMemory? GlobalRelationship { get; set; }
    public CustomerIntelligenceProfile? Brain { get; set; }
    public SourcingRequest? SourcingRequest { get; set; }
    public ConversationAgentState? AgentState { get; set; }
    public GlobalCustomerAgentLock? AgentLock { get; set; }
    public HumanHandoffEvent? OpenHandoff { get; set; }
    public List<WhatsAppIdentityLink> IdentityLinks { get; set; } = [];
    public List<WhatsAppMessage> Messages { get; set; } = [];
    public List<PendingQuestion> PendingQuestions { get; set; } = [];
}

public sealed class CustomerSuccessFieldProposal
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class CustomerSuccessSourcingProposal
{
    public SourcingFieldKey Field { get; set; }
    public string Value { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public bool HumanConfirmed { get; set; }
}

public sealed class CustomerSuccessAgentDecision
{
    public string ReplyText { get; set; } = "";
    public string ReplyLanguage { get; set; } = "";
    public AgentQuestionSafety Safety { get; set; } = AgentQuestionSafety.SafeToAnswer;
    public string SafetyReason { get; set; } = "";
    public string ChineseSummary { get; set; } = "";
    public string CustomerIntent { get; set; } = "";
    public List<string> Signals { get; set; } = [];
    public List<CustomerSuccessSourcingProposal> SourcingFields { get; set; } = [];
    public string PendingQuestion { get; set; } = "";
    public string RecommendedNextAction { get; set; } = "";
    public List<CustomerSuccessFieldProposal> CrmProposals { get; set; } = [];
    public double Confidence { get; set; }
    public string Model { get; set; } = "";
    public string LatestIncomingMessageId { get; set; } = "";
    public bool RequiresHuman => Safety == AgentQuestionSafety.ImmediateHuman;
}

public sealed class CustomerSuccessAgentRunResult
{
    public CustomerIdentityResolution Identity { get; set; } = new();
    public CustomerSuccessContext? Context { get; set; }
    public CustomerSuccessAgentDecision? Decision { get; set; }
    public SourcingRequest? SourcingRequest { get; set; }
    public HumanHandoffEvent? Handoff { get; set; }
    public ConversationAgentState? AgentState { get; set; }
    public bool AutoReplyAllowed { get; set; }
    public string BlockReason { get; set; } = "";
}

public static class CustomerSuccessAgentLabels
{
    public static string Mode(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "自动关闭",
        ConversationAgentMode.SuggestOnly => "仅建议",
        ConversationAgentMode.CopilotActive => "协作模式",
        ConversationAgentMode.AutoActive => "自动回复",
        ConversationAgentMode.IdentityResolutionRequired => "待确认客户身份",
        ConversationAgentMode.HumanRequired => "需要人工处理",
        ConversationAgentMode.HumanActive => "人工接管中",
        ConversationAgentMode.ResumeReview => "恢复前复核",
        _ => value.ToString()
    };

    public static string Match(CustomerIdentityMatchResult value) => value switch
    {
        CustomerIdentityMatchResult.ExactMatch => "精确匹配",
        CustomerIdentityMatchResult.ConfirmedAliasMatch => "确认别名匹配",
        CustomerIdentityMatchResult.UniqueInferredMatch => "唯一推断匹配",
        CustomerIdentityMatchResult.AmbiguousMatch => "匹配有歧义",
        CustomerIdentityMatchResult.NoMatch => "未匹配",
        CustomerIdentityMatchResult.Conflict => "身份冲突",
        _ => value.ToString()
    };
}
