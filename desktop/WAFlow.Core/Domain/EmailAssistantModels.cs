using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

public sealed class EmailAssistantResult
{
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Language { get; set; } = "";
    public string ContextSummary { get; set; } = "";
    public string CustomerIntent { get; set; } = "";
    public List<string> Risks { get; set; } = [];
    public string RecommendedNextAction { get; set; } = "";
    public double Confidence { get; set; }
    public List<string> KnowledgeChunkIds { get; set; } = [];

    [JsonIgnore] public string Model { get; set; } = "";
    [JsonIgnore] public string KnowledgeRetrievalId { get; set; } = "";
    [JsonIgnore] public List<KnowledgeRetrievalHit> KnowledgeCitations { get; set; } = [];
}
