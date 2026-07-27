using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public sealed class CompositeKnowledgeDocumentParser : DocumentParser
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".html", ".htm", ".docx", ".pptx", ".xlsx", ".pdf",
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff"
    };

    private readonly ImageTextExtractor _imageTextExtractor;

    public CompositeKnowledgeDocumentParser(ImageTextExtractor? imageTextExtractor = null)
    {
        _imageTextExtractor = imageTextExtractor ?? new UnavailableImageTextExtractor();
    }

    public bool CanParse(string extension) => SupportedExtensions.Contains(NormalizeExtension(extension));

    public async Task<KnowledgeParseResult> ParseAsync(
        KnowledgeParseRequest request,
        CancellationToken cancellationToken = default)
    {
        var extension = NormalizeExtension(Path.GetExtension(request.OriginalFileName));
        if (!CanParse(extension)) throw new NotSupportedException($"暂不支持 {extension} 文件。");
        cancellationToken.ThrowIfCancellationRequested();
        return extension switch
        {
            ".txt" or ".md" or ".markdown" => await ParsePlainTextAsync(request.FilePath, cancellationToken),
            ".csv" => await ParseCsvAsync(request.FilePath, cancellationToken),
            ".html" or ".htm" => await ParseHtmlAsync(request.FilePath, cancellationToken),
            ".docx" => ParseDocx(request.FilePath),
            ".pptx" => ParsePptx(request.FilePath),
            ".xlsx" => ParseXlsx(request.FilePath),
            ".pdf" => ParsePdf(request.FilePath),
            _ => await ParseImageAsync(request, cancellationToken)
        };
    }

    private static async Task<KnowledgeParseResult> ParsePlainTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var text = await ReadTextWithEncodingFallbackAsync(path, cancellationToken);
        var sections = SplitTextSections(text);
        return BuildResult("plain-text", sections);
    }

    private static async Task<KnowledgeParseResult> ParseCsvAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var text = await ReadTextWithEncodingFallbackAsync(path, cancellationToken);
        var rawLines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var separator = DetectSeparator(rawLines.FirstOrDefault() ?? "");
        var sections = new List<KnowledgeParsedSection>();
        var header = rawLines.Length == 0 ? [] : ParseDelimitedLine(rawLines[0], separator);
        for (var index = 1; index < rawLines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawLines[index])) continue;
            var values = ParseDelimitedLine(rawLines[index], separator);
            var cells = values.Select((value, cellIndex) =>
            {
                var name = cellIndex < header.Count && !string.IsNullOrWhiteSpace(header[cellIndex])
                    ? header[cellIndex].Trim()
                    : $"列 {cellIndex + 1}";
                return $"{name}: {value.Trim()}";
            }).Where(value => !value.EndsWith(": ", StringComparison.Ordinal)).ToList();
            sections.Add(new KnowledgeParsedSection
            {
                Heading = $"第 {index + 1} 行",
                Locator = $"行 {index + 1}",
                RowNumber = index + 1,
                Content = string.Join("；", cells)
            });
        }
        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(text))
            sections.Add(new KnowledgeParsedSection { Heading = "CSV 内容", Content = text.Trim(), Locator = "全文" });
        return BuildResult("csv", sections);
    }

    private static async Task<KnowledgeParseResult> ParseHtmlAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var html = await ReadTextWithEncodingFallbackAsync(path, cancellationToken);
        var withoutUnsafeBlocks = Regex.Replace(
            html,
            @"<(script|style|iframe|object|embed)\b[^>]*>.*?</\1>",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
        var withBreaks = Regex.Replace(
            withoutUnsafeBlocks,
            @"</?(p|div|section|article|h[1-6]|li|tr|br)\b[^>]*>",
            "\n",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));
        var text = WebUtility.HtmlDecode(Regex.Replace(
            withBreaks,
            @"<[^>]+>",
            " ",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(2)));
        return BuildResult("html", SplitTextSections(text));
    }

    private static KnowledgeParseResult ParseDocx(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var sections = new List<KnowledgeParsedSection>();
        if (document.MainDocumentPart?.Document?.Body is not { } body)
            throw new InvalidDataException("DOCX 不包含可读取正文。");
        var paragraphNumber = 0;
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            var text = paragraph.InnerText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            paragraphNumber++;
            var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
            var heading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                || style.StartsWith("标题", StringComparison.OrdinalIgnoreCase)
                ? text
                : "";
            sections.Add(new KnowledgeParsedSection
            {
                Heading = heading,
                Content = text,
                Locator = $"段落 {paragraphNumber}"
            });
        }
        var tableNumber = 0;
        foreach (var table in body.Descendants<Table>())
        {
            tableNumber++;
            var rowNumber = 0;
            foreach (var row in table.Elements<TableRow>())
            {
                rowNumber++;
                var cells = row.Elements<TableCell>()
                    .Select(cell => NormalizeWhitespace(cell.InnerText))
                    .Where(value => value.Length > 0)
                    .ToList();
                if (cells.Count == 0) continue;
                sections.Add(new KnowledgeParsedSection
                {
                    Heading = $"表格 {tableNumber}",
                    TableName = $"表格 {tableNumber}",
                    RowNumber = rowNumber,
                    Locator = $"表格 {tableNumber} · 行 {rowNumber}",
                    Content = string.Join(" | ", cells)
                });
            }
        }
        return BuildResult("openxml-docx", sections);
    }

    private static KnowledgeParseResult ParsePptx(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentation = document.PresentationPart
            ?? throw new InvalidDataException("PPTX 不包含演示文稿内容。");
        var slideParts = presentation.Presentation?.SlideIdList?.Elements<SlideId>()
            .Select(slideId => presentation.GetPartById(slideId.RelationshipId!) as SlidePart)
            .Where(part => part is not null)
            .Cast<SlidePart>()
            .ToList() ?? [];
        var sections = new List<KnowledgeParsedSection>();
        for (var index = 0; index < slideParts.Count; index++)
        {
            if (slideParts[index].Slide is not { } slide) continue;
            var texts = slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                .Select(item => item.Text?.Trim() ?? "")
                .Where(value => value.Length > 0)
                .ToList();
            if (texts.Count == 0) continue;
            sections.Add(new KnowledgeParsedSection
            {
                Heading = texts[0],
                Content = string.Join("\n", texts),
                Locator = $"幻灯片 {index + 1}",
                PageNumber = index + 1
            });
        }
        return BuildResult("openxml-pptx", sections);
    }

    private static KnowledgeParseResult ParseXlsx(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sections = new List<KnowledgeParsedSection>();
        foreach (var worksheet in workbook.Worksheets)
        {
            var usedRange = worksheet.RangeUsed();
            if (usedRange is null) continue;
            var rows = usedRange.RowsUsed().Take(100_000);
            foreach (var row in rows)
            {
                var cells = row.Cells(usedRange.RangeAddress.FirstAddress.ColumnNumber, usedRange.RangeAddress.LastAddress.ColumnNumber)
                    .Select(cell => cell.GetFormattedString().Trim())
                    .ToList();
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                sections.Add(new KnowledgeParsedSection
                {
                    Heading = worksheet.Name,
                    TableName = worksheet.Name,
                    RowNumber = row.RowNumber(),
                    Locator = $"{worksheet.Name} · 行 {row.RowNumber()}",
                    Content = string.Join(" | ", cells.Select((value, index) => $"列{index + 1}: {value}"))
                });
            }
        }
        return BuildResult("closedxml-xlsx", sections);
    }

    private static KnowledgeParseResult ParsePdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var sections = new List<KnowledgeParsedSection>();
        foreach (var page in document.GetPages())
        {
            var text = NormalizeWhitespace(ContentOrderTextExtractor.GetText(page));
            if (string.IsNullOrWhiteSpace(text)) continue;
            sections.Add(new KnowledgeParsedSection
            {
                Heading = $"第 {page.Number} 页",
                Content = text,
                Locator = $"页 {page.Number}",
                PageNumber = page.Number
            });
        }
        var result = BuildResult("pdfpig", sections);
        if (sections.Count == 0)
        {
            result.RequiresManualReview = true;
            result.Warnings.Add("PDF 未提取到文本，可能是扫描件；请使用图片 OCR 或提供可搜索 PDF。");
        }
        return result;
    }

    private async Task<KnowledgeParseResult> ParseImageAsync(
        KnowledgeParseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await _imageTextExtractor.ExtractImageTextAsync(
                request.FilePath,
                request.MimeType,
                cancellationToken);
            var result = BuildResult("image-ocr", SplitTextSections(text));
            if (string.IsNullOrWhiteSpace(text))
            {
                result.RequiresManualReview = true;
                result.Warnings.Add("图片 OCR 没有识别出可索引文本。");
            }
            return result;
        }
        catch (NotSupportedException error)
        {
            return new KnowledgeParseResult
            {
                ParserName = "image-ocr-unavailable",
                RequiresManualReview = true,
                Warnings = [error.Message]
            };
        }
    }

    private static KnowledgeParseResult BuildResult(
        string parser,
        IReadOnlyList<KnowledgeParsedSection> sections)
    {
        var clean = sections
            .Where(section => !string.IsNullOrWhiteSpace(section.Content))
            .Select(section =>
            {
                section.Content = NormalizeWhitespace(section.Content);
                return section;
            })
            .ToList();
        return new KnowledgeParseResult
        {
            ParserName = parser,
            Sections = clean,
            Text = string.Join("\n\n", clean.Select(section =>
                string.IsNullOrWhiteSpace(section.Heading) || section.Content.StartsWith(section.Heading, StringComparison.Ordinal)
                    ? section.Content
                    : $"{section.Heading}\n{section.Content}"))
        };
    }

    private static List<KnowledgeParsedSection> SplitTextSections(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = Regex.Split(normalized, @"\n\s*\n+")
            .Select(NormalizeWhitespace)
            .Where(value => value.Length > 0)
            .ToList();
        var sections = new List<KnowledgeParsedSection>();
        string currentHeading = "";
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var firstLine = block.Split('\n', 2)[0].Trim();
            if (LooksLikeHeading(firstLine))
            {
                currentHeading = firstLine.TrimStart('#', ' ', '\t');
                if (string.Equals(block, firstLine, StringComparison.Ordinal)) continue;
            }
            sections.Add(new KnowledgeParsedSection
            {
                Heading = currentHeading,
                Content = block,
                Locator = string.IsNullOrWhiteSpace(currentHeading) ? $"段落 {index + 1}" : currentHeading
            });
        }
        return sections;
    }

    private static bool LooksLikeHeading(string value) =>
        value.StartsWith('#') ||
        (value.Length is > 1 and <= 80 && !value.EndsWith('。') && !value.EndsWith('.') && !value.EndsWith('；'));

    private static async Task<string> ReadTextWithEncodingFallbackAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length == 0) return "";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    private static char DetectSeparator(string line)
    {
        var candidates = new[] { ',', '\t', ';', '|' };
        return candidates.OrderByDescending(candidate => line.Count(character => character == candidate)).First();
    }

    private static List<string> ParseDelimitedLine(string line, char separator)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == separator && !quoted)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else builder.Append(character);
        }
        values.Add(builder.ToString());
        return values;
    }

    private static string NormalizeExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.StartsWith('.') ? value.ToLowerInvariant() : "." + value.ToLowerInvariant();
    }

    internal static string NormalizeWhitespace(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(line => Regex.Replace(line.Trim(), @"[ \t\u00A0]+", " "))
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }
}

public sealed class UnavailableImageTextExtractor : ImageTextExtractor
{
    public Task<string> ExtractImageTextAsync(
        string filePath,
        string mimeType,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 AI 模型未提供可验证的图片 OCR；文件已保留，等待人工补充文本或更换支持视觉的模型。");
}

public sealed class AiProviderImageTextExtractor : ImageTextExtractor
{
    private readonly IStructuredAiProvider _provider;

    public AiProviderImageTextExtractor(IStructuredAiProvider provider)
    {
        _provider = provider;
    }

    public async Task<string> ExtractImageTextAsync(
        string filePath,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _provider.ExtractImageTextAsync(filePath, mimeType, cancellationToken);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new NotSupportedException($"图片 OCR 暂不可用：{error.Message}", error);
        }
    }
}

public sealed class RuleBasedDocumentClassifier : DocumentClassifier
{
    private static readonly string[] InjectionTerms =
    [
        "ignore previous", "ignore all instructions", "system prompt", "developer message", "api key",
        "reveal prompt", "execute command", "忽略之前", "忽略所有指令", "系统提示词", "开发者消息",
        "输出密钥", "泄露密钥", "执行命令", "改变角色"
    ];

    private static readonly string[] HighRiskTerms =
    [
        "guaranteed delivery", "guarantee delivery", "guaranteed stock", "final price approved",
        "退款保证", "赔偿保证", "交期保证", "清关保证", "库存保证", "最终价格已批准"
    ];

    public KnowledgeClassificationResult Classify(string fileName, string text)
    {
        var sample = text.Length <= 100_000 ? text : text[..100_000];
        var combined = $"{fileName}\n{sample}";
        var injection = InjectionTerms.Where(term => combined.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var highRisk = HighRiskTerms.Where(term => combined.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var category = DetectCategory(combined);
        var keywords = KnowledgeTextAnalysis.ExtractTerms(combined, 12);
        var summarySource = CompositeKnowledgeDocumentParser.NormalizeWhitespace(sample);
        var summary = summarySource.Length <= 320 ? summarySource : summarySource[..320] + "…";
        var risks = new List<string>();
        if (injection.Count > 0) risks.Add($"疑似提示词注入：{string.Join("、", injection.Take(3))}");
        if (highRisk.Count > 0) risks.Add($"包含高风险承诺：{string.Join("、", highRisk.Take(3))}");
        return new KnowledgeClassificationResult
        {
            Summary = string.IsNullOrWhiteSpace(summary) ? "尚未提取到可摘要文本。" : summary,
            Language = DetectLanguage(sample),
            SuggestedCategory = category,
            Tags = keywords,
            RiskFlags = risks,
            ContainsPromptInjection = injection.Count > 0,
            RiskLevel = injection.Count > 0
                ? KnowledgeRiskLevel.Blocked
                : highRisk.Count > 0 ? KnowledgeRiskLevel.High : KnowledgeRiskLevel.None
        };
    }

    private static KnowledgeCategory DetectCategory(string value)
    {
        if (ContainsAny(value, "dhgate policy", "平台政策", "平台规则", "处罚规则")) return KnowledgeCategory.DhgatePolicy;
        if (ContainsAny(value, "customer success", "客户成功", "sop", "标准作业")) return KnowledgeCategory.CustomerSuccessSop;
        if (ContainsAny(value, "搜品", "采购五要素", "sourcing requirement", "target price", "运输偏好")) return KnowledgeCategory.SourcingRequirement;
        if (ContainsAny(value, "物流", "shipping", "清关", "freight")) return KnowledgeCategory.ShippingKnowledge;
        if (ContainsAny(value, "异议", "objection", "拒绝", "顾虑")) return KnowledgeCategory.ObjectionHandling;
        if (ContainsAny(value, "话术", "script", "talk track", "回复模板")) return KnowledgeCategory.SalesScript;
        if (ContainsAny(value, "faq", "常见问题", "问答")) return KnowledgeCategory.Faq;
        if (ContainsAny(value, "客户案例", "case study", "成功案例")) return KnowledgeCategory.CustomerCase;
        if (ContainsAny(value, "分析模板", "analysis template")) return KnowledgeCategory.AnalysisTemplate;
        if (ContainsAny(value, "报告模板", "report template")) return KnowledgeCategory.ReportTemplate;
        if (ContainsAny(value, "培训", "training")) return KnowledgeCategory.TrainingMaterial;
        if (ContainsAny(value, "产品", "product", "型号", "sku")) return KnowledgeCategory.ProductKnowledge;
        return KnowledgeCategory.Other;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string DetectLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var chinese = value.Count(character => character is >= '\u4e00' and <= '\u9fff');
        var latin = value.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (chinese > latin / 3) return latin > chinese ? "zh-en" : "zh";
        return latin > 0 ? "en" : "unknown";
    }
}

public sealed class StructuredKnowledgeChunker : KnowledgeChunker
{
    private const int TargetLength = 1200;
    private const int Overlap = 160;

    public IReadOnlyList<KnowledgeChunk> Chunk(
        KnowledgeDocument document,
        KnowledgeDocumentVersion version,
        KnowledgeParseResult parsed)
    {
        var result = new List<KnowledgeChunk>();
        var ordinal = 0;
        foreach (var section in parsed.Sections)
        {
            foreach (var part in Split(section.Content))
            {
                var normalized = KnowledgeTextAnalysis.Normalize(part);
                if (normalized.Length < 2) continue;
                result.Add(new KnowledgeChunk
                {
                    Id = StableId(document.Id, version.Id, ordinal.ToString()),
                    DocumentId = document.Id,
                    VersionId = version.Id,
                    DocumentVersion = version.Version,
                    Ordinal = ordinal++,
                    Content = part.Trim(),
                    NormalizedText = normalized,
                    Heading = section.Heading,
                    Locator = section.Locator,
                    PageNumber = section.PageNumber,
                    TableName = section.TableName,
                    RowNumber = section.RowNumber,
                    Keywords = KnowledgeTextAnalysis.ExtractTerms(part, 24),
                    ContentHash = Sha256(normalized),
                    Language = document.DetectedLanguage,
                    Category = document.Category,
                    SourceKind = document.SourceKind,
                    UsageMode = document.UsageMode,
                    EvidenceLevel = document.EvidenceLevel,
                    Scope = document.Scope,
                    RiskLevel = document.RiskLevel,
                    EffectiveFrom = document.EffectiveFrom,
                    EffectiveUntil = document.EffectiveUntil
                });
            }
        }
        return result;
    }

    private static IEnumerable<string> Split(string value)
    {
        var text = value.Trim();
        if (text.Length <= TargetLength)
        {
            if (text.Length > 0) yield return text;
            yield break;
        }
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            var length = Math.Min(TargetLength, remaining);
            if (remaining > TargetLength)
            {
                var searchStart = start + Math.Max(400, TargetLength - 260);
                var searchEnd = Math.Min(text.Length - 1, start + TargetLength);
                var boundary = text.LastIndexOfAny(['。', '.', '！', '!', '？', '?', '；', ';', '\n'], searchEnd, searchEnd - searchStart + 1);
                if (boundary > start + 400) length = boundary - start + 1;
            }
            var part = text.Substring(start, length).Trim();
            if (part.Length > 0) yield return part;
            if (start + length >= text.Length) break;
            start = Math.Max(start + 1, start + length - Overlap);
        }
    }

    private static string StableId(params string[] values)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class LocalSemanticEmbeddingProvider : EmbeddingProvider
{
    private const int Dimensions = 384;
    public string Name => "local-token-ngram";
    public string Version => "1";

    public IReadOnlyList<double> Embed(string text)
    {
        var normalized = KnowledgeTextAnalysis.Normalize(text);
        var vector = new double[Dimensions];
        foreach (var token in KnowledgeTextAnalysis.Tokenize(normalized))
        {
            Add(vector, token, 1.0);
            if (token.Length > 2)
                for (var index = 0; index <= token.Length - 3; index++)
                    Add(vector, token.Substring(index, 3), 0.35);
        }
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
            for (var index = 0; index < vector.Length; index++) vector[index] /= norm;
        return vector;
    }

    private static void Add(double[] vector, string token, double weight)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var index = BitConverter.ToUInt32(bytes, 0) % (uint)vector.Length;
        var sign = (bytes[4] & 1) == 0 ? 1d : -1d;
        vector[index] += sign * weight;
    }
}

public sealed class CosineVectorSearchProvider : VectorSearchProvider
{
    public double Score(IReadOnlyList<double> query, IReadOnlyList<double> candidate)
    {
        if (query.Count == 0 || candidate.Count == 0 || query.Count != candidate.Count) return 0;
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < query.Count; index++)
        {
            dot += query[index] * candidate[index];
            leftNorm += query[index] * query[index];
            rightNorm += candidate[index] * candidate[index];
        }
        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : Math.Clamp((dot / Math.Sqrt(leftNorm * rightNorm) + 1) / 2, 0, 1);
    }
}

public sealed class ExactAwareKeywordSearchProvider : KeywordSearchProvider
{
    public KeywordSearchScore Score(string query, KnowledgeChunk candidate)
    {
        var terms = KnowledgeTextAnalysis.Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (terms.Count == 0) return new KeywordSearchScore(0, []);
        var normalized = candidate.NormalizedText;
        var matched = terms.Where(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var exactWeight = matched.Sum(term => KnowledgeTextAnalysis.IsExactBusinessTerm(term) ? 1.8 : 1.0);
        var denominator = terms.Sum(term => KnowledgeTextAnalysis.IsExactBusinessTerm(term) ? 1.8 : 1.0);
        var phraseBonus = normalized.Contains(KnowledgeTextAnalysis.Normalize(query), StringComparison.OrdinalIgnoreCase) ? 0.25 : 0;
        return new KeywordSearchScore(Math.Clamp(exactWeight / Math.Max(1, denominator) + phraseBonus, 0, 1), matched);
    }
}

public sealed class ScopeFreshnessKnowledgeRanker : KnowledgeRanker
{
    public double Rank(
        KnowledgeRetrievalRequest request,
        KnowledgeChunk chunk,
        double keywordScore,
        double vectorScore,
        out double scopeScore,
        out double freshnessScore)
    {
        scopeScore = chunk.Scope.Kind switch
        {
            KnowledgeScopeKind.Conversation => 1,
            KnowledgeScopeKind.Customer => 0.95,
            KnowledgeScopeKind.Account => 0.86,
            KnowledgeScopeKind.Temporary => 0.9,
            _ => 0.76
        };
        var now = DateTimeOffset.Now;
        freshnessScore = chunk.EffectiveUntil is { } until && until < now
            ? 0
            : chunk.UpdatedAt < now.AddYears(-2) ? 0.45
            : chunk.UpdatedAt < now.AddYears(-1) ? 0.7
            : 1;
        var authority = chunk.Category == KnowledgeCategory.DhgatePolicy ? 1.08
            : chunk.EvidenceLevel == KnowledgeEvidenceLevel.OutcomeValidated ? 1.06
            : chunk.EvidenceLevel == KnowledgeEvidenceLevel.PreliminaryObservation ? 0.86
            : 1;
        var risk = chunk.RiskLevel switch
        {
            KnowledgeRiskLevel.Blocked => 0,
            KnowledgeRiskLevel.High => 0.2,
            KnowledgeRiskLevel.Medium => 0.72,
            _ => 1
        };
        if (chunk.HasOpenConflict) risk *= 0.18;
        return Math.Clamp(
            (keywordScore * 0.48 + vectorScore * 0.32 + scopeScore * 0.12 + freshnessScore * 0.08)
            * authority * risk,
            0,
            1);
    }
}

internal static class KnowledgeTextAnalysis
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "this", "that", "from", "your", "are", "was", "will",
        "一个", "我们", "你们", "以及", "可以", "进行", "需要", "客户", "知识", "内容", "相关"
    };

    public static string Normalize(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), @"\s+", " ");

    public static IEnumerable<string> Tokenize(string value)
    {
        var normalized = Normalize(value);
        foreach (Match match in Regex.Matches(normalized, @"[\p{L}\p{N}][\p{L}\p{N}_./+\-]{1,63}"))
        {
            var token = match.Value.Trim('.', '/', '-', '_');
            if (token.Length >= 2 && !StopWords.Contains(token)) yield return token;
        }
        var chinese = new string(normalized.Where(character => character is >= '\u4e00' and <= '\u9fff').ToArray());
        for (var index = 0; index + 1 < chinese.Length; index++)
            yield return chinese.Substring(index, Math.Min(3, chinese.Length - index));
    }

    public static List<string> ExtractTerms(string value, int limit)
    {
        return Tokenize(value)
            .GroupBy(term => term, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count() * (IsExactBusinessTerm(group.Key) ? 2 : 1))
            .ThenByDescending(group => group.Key.Length)
            .Select(group => group.Key)
            .Take(limit)
            .ToList();
    }

    public static bool IsExactBusinessTerm(string term) =>
        term.Any(char.IsDigit) || term.Contains('-') || term.Contains('/') ||
        Regex.IsMatch(term, @"^[a-z]{1,6}\d+[a-z0-9-]*$", RegexOptions.IgnoreCase);
}
