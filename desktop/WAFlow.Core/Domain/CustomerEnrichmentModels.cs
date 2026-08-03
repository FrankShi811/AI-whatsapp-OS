using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerEnrichmentJobStatus
{
    Queued,
    Running,
    NeedsReview,
    Succeeded,
    Failed,
    Cancelled,
    NoResults
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerEnrichmentVerificationStatus
{
    Verified,
    HumanConfirmed,
    LikelyMatch,
    PossibleMatch,
    Rejected,
    Outdated,
    Conflicting
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerEnrichmentReviewAction
{
    Confirm,
    Reject,
    EditAndConfirm,
    MarkOutdated
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerEnrichmentTriggerType
{
    Manual,
    NewCustomer,
    NewWhatsAppConversation,
    CustomerImport,
    PreConversationCheck,
    ScheduledRefresh,
    HighValueLead,
    Reactivation
}

public static class CustomerEnrichmentErrorCodes
{
    public const string SearchProviderUnavailable = "SEARCH_PROVIDER_UNAVAILABLE";
    public const string SearXngNotRunning = "SEARXNG_NOT_RUNNING";
    public const string ProviderQuotaExhausted = "PROVIDER_QUOTA_EXHAUSTED";
    public const string PaidRequestBlocked = "PAID_REQUEST_BLOCKED";
    public const string WebFetchTimeout = "WEB_FETCH_TIMEOUT";
    public const string WebFetchBlocked = "WEB_FETCH_BLOCKED";
    public const string InvalidModelResponse = "INVALID_MODEL_RESPONSE";
    public const string AnalysisProviderUnavailable = "ANALYSIS_PROVIDER_UNAVAILABLE";
    public const string EntityMatchInsufficient = "ENTITY_MATCH_INSUFFICIENT";
    public const string NoPublicResults = "NO_PUBLIC_RESULTS";
    public const string CustomerIdentityMissing = "CUSTOMER_IDENTITY_MISSING";
    public const string CustomerIdentityChanged = "CUSTOMER_IDENTITY_CHANGED";
    public const string JobCancelled = "JOB_CANCELLED";
    public const string RecoveryReviewRequired = "RECOVERY_REVIEW_REQUIRED";
    public const string AiAnalysisPaymentNotAuthorized = "AI_ANALYSIS_PAYMENT_NOT_AUTHORIZED";
}

public sealed class CustomerEnrichmentSettings
{
    public const decimal DefaultAiAnalysisReservationUsd = 0.05m;

    public List<string> ProviderOrder { get; set; } = ["tavily", "brave", "searxng"];
    public bool SearXngEnabled { get; set; }
    public string SearXngBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public decimal MonthlyBudgetUsd { get; set; }
    public bool AllowPaidRequests { get; set; }
    public bool AllowAiAnalysisRequests { get; set; }
    public decimal AiAnalysisReservationUsd { get; set; } = DefaultAiAnalysisReservationUsd;
    public int TavilyMonthlyFreeRequests { get; set; } = 1000;
    public int BraveMonthlyFreeRequests { get; set; } = 1000;
    public int MaxQueriesPerCustomer { get; set; } = 6;
    public int MaxResultsPerQuery { get; set; } = 8;
    public int MaxPagesPerCustomer { get; set; } = 12;
    public int CacheDays { get; set; } = 30;
    public int StandardRefreshDays { get; set; } = 90;
    public int HighValueRefreshDays { get; set; } = 30;
    public int MajorOpportunityRefreshDays { get; set; } = 7;
    public int DataRetentionDays { get; set; } = 730;
    public bool ManualEnrichmentEnabled { get; set; } = true;
    public List<string> AutoEnrichmentGrades { get; set; } = ["A", "B"];
    public int MaxAutomaticJobsPerStartup { get; set; } = 5;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerEnrichmentIdentity
{
    public string CustomerId { get; set; } = "";
    public string BuyerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Country { get; set; } = "";
    public string Language { get; set; } = "";
    public string RawEmail { get; set; } = "";
    public string Email { get; set; } = "";
    public string EmailUserName { get; set; } = "";
    public string EmailDomain { get; set; } = "";
    public bool IsBusinessEmail { get; set; }
    public string RawPhone { get; set; } = "";
    public string PhoneE164 { get; set; } = "";
    public string PhoneDigits { get; set; } = "";
    public string PhoneTail8 { get; set; } = "";
    public string IdentityHash { get; set; } = "";
}

public sealed class CustomerEnrichmentJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public CustomerEnrichmentTriggerType TriggerType { get; set; } = CustomerEnrichmentTriggerType.Manual;
    public CustomerEnrichmentJobStatus Status { get; set; } = CustomerEnrichmentJobStatus.Queued;
    public string Provider { get; set; } = "";
    public string IdentityHash { get; set; } = "";
    public string ConfigurationHash { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public int QueriesCount { get; set; }
    public int SourcesCount { get; set; }
    public int FactsCount { get; set; }
    public decimal CostUsd { get; set; }
    public bool ReusedCache { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerEnrichmentQuery
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string QueryText { get; set; } = "";
    public string QueryHash { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int ResultsCount { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? RetrievedAt { get; set; }
}

public sealed class CustomerEnrichmentSearchResult
{
    public string Provider { get; set; } = "";
    public string Query { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.Now;
    public int Rank { get; set; }
}

public sealed class CustomerEnrichmentSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public string QueryId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Url { get; set; } = "";
    public string CanonicalUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string ContentText { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.Now;
    public string Provider { get; set; } = "";
    public int Rank { get; set; }
    public int IdentityMatchScore { get; set; }
    public CustomerEnrichmentVerificationStatus IdentityMatchStatus { get; set; } = CustomerEnrichmentVerificationStatus.PossibleMatch;
    public List<string> IdentityMatchReasons { get; set; } = [];
    public List<string> IdentityConflicts { get; set; } = [];
    public string FetchStatus { get; set; } = "snippet_only";
    public string FetchErrorCode { get; set; } = "";
}

public sealed class CustomerEnrichmentFact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string FieldType { get; set; } = "";
    public string FieldValue { get; set; } = "";
    public string NormalizedValue { get; set; } = "";
    public string Category { get; set; } = "";
    public string FactType { get; set; } = "verified_fact";
    public int ConfidenceScore { get; set; }
    public CustomerEnrichmentVerificationStatus VerificationStatus { get; set; } = CustomerEnrichmentVerificationStatus.PossibleMatch;
    public List<string> SourceIds { get; set; } = [];
    public string EvidenceQuote { get; set; } = "";
    public string ReviewNote { get; set; } = "";
    public string HumanReviewId { get; set; } = "";
    public DateTimeOffset FirstDiscoveredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public int SourceCount => SourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed class CustomerEnrichmentReview
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string FactId { get; set; } = "";
    public string JobId { get; set; } = "";
    public CustomerEnrichmentReviewAction Action { get; set; }
    public string Actor { get; set; } = "user";
    public string PreviousValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerEnrichmentProviderUsage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string JobId { get; set; } = "";
    public int Requests { get; set; } = 1;
    public decimal EstimatedCostUsd { get; set; }
    public bool Succeeded { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string CircuitState { get; set; } = "closed";
    public string RequestState { get; set; } = "completed";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerEnrichmentUsageSummary
{
    public int TodayRequests { get; set; }
    public int MonthRequests { get; set; }
    public decimal MonthEstimatedCostUsd { get; set; }
    public string LastError { get; set; } = "";
    public Dictionary<string, int> ProviderRequests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ProviderFreeRemaining { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CustomerEnrichmentSnapshot
{
    public CustomerEnrichmentJob? LatestJob { get; set; }
    public List<CustomerEnrichmentJob> Jobs { get; set; } = [];
    public List<CustomerEnrichmentFact> Facts { get; set; } = [];
    public List<CustomerEnrichmentFact> ActiveFacts { get; set; } = [];
    public List<CustomerEnrichmentSource> Sources { get; set; } = [];
    public CustomerEnrichmentUsageSummary Usage { get; set; } = new();
}

public sealed class CustomerEnrichmentEntityMatch
{
    public int Score { get; set; }
    public string Status { get; set; } = "possible_match";
    public List<string> Reasons { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
}

public sealed class CustomerEnrichmentExtractedFact
{
    public string FieldType { get; set; } = "";
    public string Value { get; set; } = "";
    public string Category { get; set; } = "";
    public int Confidence { get; set; }
    public string FactType { get; set; } = "verified_fact";
    public List<string> SourceIds { get; set; } = [];
    public string EvidenceQuote { get; set; } = "";
}

public sealed class CustomerEnrichmentAnalysisResult
{
    public CustomerEnrichmentEntityMatch EntityMatch { get; set; } = new();
    public List<CustomerEnrichmentExtractedFact> Facts { get; set; } = [];
    public List<CustomerEnrichmentExtractedFact> PossibleContext { get; set; } = [];
    public List<CustomerEnrichmentExtractedFact> ConflictingInformation { get; set; } = [];
    public List<string> Unknowns { get; set; } = [];
}

public sealed record CustomerEnrichmentChangedEventArgs(
    string CustomerId,
    string JobId,
    CustomerEnrichmentJobStatus Status,
    string Message);

public sealed record CustomerSearchProviderHealth(
    string Provider,
    bool Available,
    string Message,
    DateTimeOffset CheckedAt);

public sealed class CustomerEnrichmentException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public CustomerEnrichmentException(string code, string message, bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }
}
