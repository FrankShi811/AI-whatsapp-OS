using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerReportStatus { Running, Succeeded, RetryableFailed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerReportStepStatus { Pending, Running, Succeeded, RetryableFailed }

public sealed class CustomerAnalysisReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string AiModel { get; set; } = "";
    public CustomerIntelligenceSourceSnapshot SourceSnapshot { get; set; } = new();
    public CustomerIntelligenceReportContent Report { get; set; } = new();
    public DateTimeOffset CreatedTime { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedTime { get; set; } = DateTimeOffset.Now;
    public int Version { get; set; } = 1;
    public List<CustomerReportExportRecord> ExportHistory { get; set; } = [];
    public CustomerReportStatus Status { get; set; } = CustomerReportStatus.Running;
    public string Error { get; set; } = "";

    [JsonIgnore] public string VersionLabel => $"V{Version} · {CreatedTime.LocalDateTime:yyyy-MM-dd HH:mm}";
    [JsonIgnore] public string StatusLabel => Status switch
    {
        CustomerReportStatus.Succeeded => "报告已完成",
        CustomerReportStatus.RetryableFailed => "生成失败，可重试",
        _ => "AI 正在生成"
    };
}

public sealed class CustomerAnalysisReportStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ReportId { get; set; } = "";
    public string StepKey { get; set; } = "";
    public int Sequence { get; set; }
    public CustomerReportStepStatus Status { get; set; } = CustomerReportStepStatus.Pending;
    public string ResultJson { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string StepLabel => StepKey switch
    {
        "data_assembly" => "客户数据整理",
        "fact_extraction" => "事实提取",
        "commercial_analysis" => "商业分析",
        "sales_strategy" => "销售策略",
        "report_generation" => "报告生成",
        "format_rendering" => "格式渲染",
        _ => StepKey
    };
}

public sealed class CustomerReportExportRecord
{
    public string Format { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public long FileSize { get; set; }
}

public sealed class CustomerIntelligenceSourceSnapshot
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public Lead Lead { get; set; } = new();
    public List<WhatsAppMessage> WhatsAppMessages { get; set; } = [];
    public List<EmailMessage> EmailMessages { get; set; } = [];
    public List<CustomerCampaignTouch> CampaignTouches { get; set; } = [];
    public List<CustomerHistoryEvent> Timeline { get; set; } = [];
    public List<LeadAnalysisRunSnapshot> LeadAnalysisHistory { get; set; } = [];
}

public sealed class CustomerCampaignTouch
{
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string Channel { get; set; } = "WhatsApp";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class CustomerHistoryEvent
{
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LeadAnalysisRunSnapshot
{
    public string Status { get; set; } = "";
    public string Model { get; set; } = "";
    public string Error { get; set; } = "";
    public LeadAnalysis? Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CustomerFactSet
{
    public List<ReportStatement> Facts { get; set; } = [];
    public List<CustomerQuote> Quotes { get; set; } = [];
    public List<string> InformationGaps { get; set; } = [];
}

public sealed class ReportStatement
{
    public string Nature { get; set; } = "事实";
    public string Topic { get; set; } = "";
    public string Statement { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Source { get; set; } = "";
    public double Confidence { get; set; } = 1;
}

public sealed class CustomerQuote
{
    public string Original { get; set; } = "";
    public string ChineseMeaning { get; set; } = "";
    public string AiAnalysis { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public string Source { get; set; } = "WhatsApp";
}

public sealed class CustomerIntelligenceReportContent
{
    public CustomerExecutiveSummary ExecutiveSummary { get; set; } = new();
    public CustomerBasicProfile BasicProfile { get; set; } = new();
    public CustomerBusinessBackground BusinessBackground { get; set; } = new();
    public CustomerPainAnalysis PainAnalysis { get; set; } = new();
    public CustomerPurchaseMotivation PurchaseMotivation { get; set; } = new();
    public CustomerWhatsAppAnalysis WhatsAppAnalysis { get; set; } = new();
    public CustomerOpportunityJudgment OpportunityJudgment { get; set; } = new();
    public CustomerProductFit ProductFit { get; set; } = new();
    public CustomerSalesStrategy SalesStrategy { get; set; } = new();
    public CustomerRiskAnalysis RiskAnalysis { get; set; } = new();
    public string ManagementSummary { get; set; } = "";
    public List<ReportStatement> EvidenceLedger { get; set; } = [];
}

public sealed class CustomerExecutiveSummary
{
    public string OneLinePositioning { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public string BusinessStage { get; set; } = "";
    public string OverallValueJudgment { get; set; } = "";
    public string CurrentSalesRecommendation { get; set; } = "";
}

public sealed class CustomerBasicProfile
{
    public string CustomerType { get; set; } = "";
    public List<string> BusinessModels { get; set; } = [];
    public string ProductDirection { get; set; } = "";
    public string OperatingScale { get; set; } = "";
    public string DevelopmentStage { get; set; } = "";
}

public sealed class CustomerBusinessBackground
{
    public string CurrentBusinessModel { get; set; } = "";
    public List<string> CoreAdvantages { get; set; } = [];
    public List<string> CurrentLimitations { get; set; } = [];
    public List<string> GrowthOpportunities { get; set; } = [];
}

public sealed class CustomerPainAnalysis
{
    public List<string> SurfacePains { get; set; } = [];
    public List<string> DeepBusinessProblems { get; set; } = [];
}

public sealed class CustomerPurchaseMotivation
{
    public List<string> InterestReasons { get; set; } = [];
    public List<string> TriggerEvents { get; set; } = [];
    public List<string> DecisionFactors { get; set; } = [];
}

public sealed class CustomerWhatsAppAnalysis
{
    public string EngagementLevel { get; set; } = "";
    public List<string> FocusTopics { get; set; } = [];
    public List<string> PurchaseSignals { get; set; } = [];
    public List<string> Concerns { get; set; } = [];
    public List<CustomerQuote> Quotes { get; set; } = [];
}

public sealed class CustomerOpportunityJudgment
{
    public string Grade { get; set; } = "D";
    public int AiScore { get; set; }
    public int DealProbability { get; set; }
    public List<string> PositiveFactors { get; set; } = [];
    public List<string> NegativeFactors { get; set; } = [];
    public List<LeadFactor> DimensionScores { get; set; } = [];
}

public sealed class CustomerProductFit
{
    public List<string> HighMatchPoints { get; set; } = [];
    public List<string> LowMatchPoints { get; set; } = [];
    public List<string> QuestionsToValidate { get; set; } = [];
}

public sealed class CustomerSalesStrategy
{
    public List<CustomerSalesAction> Actions { get; set; } = [];
    public string RecommendedTalkTrack { get; set; } = "";
    public List<string> PendingQuestions { get; set; } = [];
}

public sealed class CustomerSalesAction
{
    public string Timeframe { get; set; } = "";
    public string Action { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string SuccessCriterion { get; set; } = "";
}

public sealed class CustomerRiskAnalysis
{
    public List<string> DealRisks { get; set; } = [];
    public List<string> AdoptionRisks { get; set; } = [];
    public List<string> ChurnRisks { get; set; } = [];
}

public sealed class CustomerBusinessAnalysisResult
{
    public CustomerExecutiveSummary ExecutiveSummary { get; set; } = new();
    public CustomerBasicProfile BasicProfile { get; set; } = new();
    public CustomerBusinessBackground BusinessBackground { get; set; } = new();
    public CustomerPainAnalysis PainAnalysis { get; set; } = new();
    public CustomerPurchaseMotivation PurchaseMotivation { get; set; } = new();
    public CustomerWhatsAppAnalysis WhatsAppAnalysis { get; set; } = new();
    public CustomerOpportunityJudgment OpportunityJudgment { get; set; } = new();
    public CustomerProductFit ProductFit { get; set; } = new();
    public CustomerRiskAnalysis RiskAnalysis { get; set; } = new();
}

public sealed class CustomerReportSynthesisResult
{
    public string ManagementSummary { get; set; } = "";
    public string OverallValueJudgment { get; set; } = "";
    public string CurrentSalesRecommendation { get; set; } = "";
    public int DealProbability { get; set; }
}

public sealed record CustomerAnalysisProgress(string StepKey, int Sequence, int Total, string Message);
