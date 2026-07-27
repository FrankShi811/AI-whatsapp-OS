using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public sealed class KnowledgeParseRequest
{
    public string FilePath { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string MimeType { get; set; } = "";
}

public sealed class KnowledgeParsedSection
{
    public string Heading { get; set; } = "";
    public string Content { get; set; } = "";
    public string Locator { get; set; } = "";
    public int? PageNumber { get; set; }
    public string TableName { get; set; } = "";
    public int? RowNumber { get; set; }
}

public sealed class KnowledgeParseResult
{
    public string ParserName { get; set; } = "";
    public string Text { get; set; } = "";
    public List<KnowledgeParsedSection> Sections { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool RequiresManualReview { get; set; }
}

public sealed class KnowledgeClassificationResult
{
    public string Summary { get; set; } = "";
    public string Language { get; set; } = "";
    public KnowledgeCategory SuggestedCategory { get; set; } = KnowledgeCategory.Other;
    public List<string> Tags { get; set; } = [];
    public List<string> RiskFlags { get; set; } = [];
    public KnowledgeRiskLevel RiskLevel { get; set; }
    public bool ContainsPromptInjection { get; set; }
}

public interface DocumentParser
{
    bool CanParse(string extension);
    Task<KnowledgeParseResult> ParseAsync(KnowledgeParseRequest request, CancellationToken cancellationToken = default);
}

public interface TextExtractor
{
    Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface TableExtractor
{
    Task<IReadOnlyList<KnowledgeParsedSection>> ExtractTablesAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface ImageTextExtractor
{
    Task<string> ExtractImageTextAsync(string filePath, string mimeType, CancellationToken cancellationToken = default);
}

public interface DocumentClassifier
{
    KnowledgeClassificationResult Classify(string fileName, string text);
}

public interface KnowledgeChunker
{
    IReadOnlyList<KnowledgeChunk> Chunk(
        KnowledgeDocument document,
        KnowledgeDocumentVersion version,
        KnowledgeParseResult parsed);
}

public interface EmbeddingProvider
{
    string Name { get; }
    string Version { get; }
    IReadOnlyList<double> Embed(string text);
}

public interface VectorSearchProvider
{
    double Score(IReadOnlyList<double> query, IReadOnlyList<double> candidate);
}

public interface KeywordSearchProvider
{
    KeywordSearchScore Score(string query, KnowledgeChunk candidate);
}

public sealed record KeywordSearchScore(double Score, IReadOnlyList<string> MatchedTerms);

public interface HybridRetriever
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalRequest request,
        CancellationToken cancellationToken = default);
}

public interface KnowledgeRanker
{
    double Rank(
        KnowledgeRetrievalRequest request,
        KnowledgeChunk chunk,
        double keywordScore,
        double vectorScore,
        out double scopeScore,
        out double freshnessScore);
}
