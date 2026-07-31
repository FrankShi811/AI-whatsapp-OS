using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpportunityEventKind
{
    PaymentSucceeded,
    PaymentFailed,
    AwaitingPayment,
    Dispute
}

public sealed class OpportunityTransactionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EventKey { get; set; } = "";
    public OpportunityEventKind Kind { get; set; }
    public string BuyerId { get; set; } = "";
    public string LeadId { get; set; } = "";
    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset? DataDate { get; set; }
    public string OrderId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string BuyerLevel { get; set; } = "";
    public string Country { get; set; } = "";
    public string PrimaryChannel { get; set; } = "";
    public string SecondaryChannel { get; set; } = "";
    public string PaymentChannel { get; set; } = "";
    public bool? Is3DSecure { get; set; }
    public string FailureReason { get; set; } = "";
    public string PrimaryCategory { get; set; } = "";
    public string SecondaryCategory { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SellerId { get; set; } = "";
    public string DisputePrimaryReason { get; set; } = "";
    public string DisputeSecondaryReason { get; set; } = "";
    public string DisputeSubtype { get; set; } = "";
    public bool IsChargeback { get; set; }
    public string SourceFileHash { get; set; } = "";
    public string SourceSheet { get; set; } = "";
    public int SourceRow { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class OpportunitySnapshot
{
    public string LeadId { get; set; } = "";
    public string BuyerId { get; set; } = "";
    public int SuccessfulPaymentCount { get; set; }
    public decimal SuccessfulPaymentTotal { get; set; }
    public decimal AverageOrderValue { get; set; }
    public DateTimeOffset? LatestSuccessfulPaymentAt { get; set; }
    public decimal PaidAmount30Days { get; set; }
    public decimal PaidAmount90Days { get; set; }
    public decimal PaidAmount365Days { get; set; }
    public int PaidCount30Days { get; set; }
    public int PaidCount90Days { get; set; }
    public int PaidCount365Days { get; set; }
    public int AwaitingPaymentCount { get; set; }
    public decimal AwaitingPaymentTotal { get; set; }
    public DateTimeOffset? LatestAwaitingPaymentAt { get; set; }
    public int FailedPaymentCount { get; set; }
    public decimal FailedPaymentTotal { get; set; }
    public DateTimeOffset? LatestFailedPaymentAt { get; set; }
    public string LatestFailureReason { get; set; } = "";
    public string LatestPaymentChannel { get; set; } = "";
    public int DisputeCount { get; set; }
    public decimal DisputeTotal { get; set; }
    public DateTimeOffset? LatestDisputeAt { get; set; }
    public decimal DisputeRate { get; set; }
    public bool HasChargeback { get; set; }
    public string PrimaryDisputeReason { get; set; } = "";
    public string PrimaryCategory { get; set; } = "";
    public string SecondaryCategory { get; set; } = "";
    public string FrequentProduct { get; set; } = "";
    public string LatestProduct { get; set; } = "";
    public string MainCountry { get; set; } = "";
    public string MainChannel { get; set; } = "";
    public string MainSeller { get; set; } = "";
    public string LatestBuyerLevel { get; set; } = "";
    public DateTimeOffset? LatestActivityAt { get; set; }
    public string DataFingerprint { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public bool HasPurchaseIntent => AwaitingPaymentCount > 0 || FailedPaymentCount > 0;
    [JsonIgnore] public bool HasRisk => DisputeCount > 0 || HasChargeback;
    [JsonIgnore] public string ValueSummary => SuccessfulPaymentCount == 0
        ? "尚无支付成功记录"
        : $"累计支付 {SuccessfulPaymentTotal:N2} · {SuccessfulPaymentCount:N0} 笔 · 客单价 {AverageOrderValue:N2}";
    [JsonIgnore] public string IntentSummary => AwaitingPaymentCount == 0 && FailedPaymentCount == 0
        ? "当前无未付款或支付失败信号"
        : $"未付款 {AwaitingPaymentCount:N0} 笔 / {AwaitingPaymentTotal:N2} · 支付失败 {FailedPaymentCount:N0} 次 / {FailedPaymentTotal:N2}";
    [JsonIgnore] public string RiskSummary => DisputeCount == 0
        ? "当前无纠纷记录"
        : $"纠纷 {DisputeCount:N0} 笔 / {DisputeTotal:N2} · 纠纷率 {DisputeRate:P1}";
}

public sealed class OpportunityImportPreview
{
    public string SourceFilePath { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string SourceFileHash { get; set; } = "";
    public DateTimeOffset SourceModifiedAt { get; set; }
    public int TotalRows { get; set; }
    public int MatchedCustomers { get; set; }
    public int MatchedEvents { get; set; }
    public int UnmatchedRows { get; set; }
    public int InvalidBuyerIdRows { get; set; }
    public int DuplicateEvents { get; set; }
    public int ChangedCustomers { get; set; }
    public int UnchangedCustomers { get; set; }
    public int BuyerIdConflicts { get; set; }
    public int ReanalysisCount { get; set; }
    public bool IsPreviouslyImportedFile { get; set; }
    public List<string> UnmatchedBuyerIds { get; set; } = [];
    public List<string> ConflictBuyerIds { get; set; } = [];
    public List<OpportunityTransactionEvent> NewEvents { get; set; } = [];
    public List<OpportunitySnapshot> ChangedSnapshots { get; set; } = [];
    public List<string> UnchangedLeadIds { get; set; } = [];
}

public sealed class OpportunityImportResult
{
    public int InsertedEvents { get; set; }
    public int ChangedCustomers { get; set; }
    public int QueuedForAnalysis { get; set; }
    public List<string> ChangedLeadIds { get; set; } = [];
}
