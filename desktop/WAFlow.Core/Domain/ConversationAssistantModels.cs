using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

public sealed class ConversationAssistantResult
{
    public string ReplyText { get; set; } = "";
    public string ReplyLanguage { get; set; } = "";
    public string NeedsSummary { get; set; } = "";
    public string CustomerIntent { get; set; } = "";
    public List<string> PurchaseSignals { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public string RecommendedNextAction { get; set; } = "";
    public double Confidence { get; set; }
    public List<ConversationFieldUpdate> FieldUpdates { get; set; } = [];

    [JsonIgnore] public string Model { get; set; } = "";
    [JsonIgnore] public string LatestIncomingMessage { get; set; } = "";
}

public sealed class ConversationFieldUpdate
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public string Reason { get; set; } = "";

    [JsonIgnore] public string FieldLabel { get; set; } = "";
    [JsonIgnore] public string CurrentValue { get; set; } = "";
}

public enum ConversationAssistantAction
{
    Cancel,
    FillComposer,
    SendAndSync
}
