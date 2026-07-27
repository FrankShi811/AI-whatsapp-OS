using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class KnowledgeUploadOptions
{
    public string ExistingDocumentId { get; set; } = "";
    public string Title { get; set; } = "";
    public KnowledgeCategory? Category { get; set; }
    public KnowledgeSourceKind SourceKind { get; set; } = KnowledgeSourceKind.ApprovedDocument;
    public KnowledgeUsageMode UsageMode { get; set; } = KnowledgeUsageMode.StyleReference;
    public KnowledgeScope Scope { get; set; } = new();
    public bool ExactTemplate { get; set; }
    public DateTimeOffset? EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveUntil { get; set; }
}

public sealed class KnowledgeBaseService
{
    public const long MaximumFileSize = 50L * 1024 * 1024;
    private const long MaximumExpandedOfficeSize = 250L * 1024 * 1024;
    private const int MaximumOfficeEntries = 20_000;

    private static readonly IReadOnlyDictionary<string, string> MimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain",
            [".md"] = "text/markdown",
            [".markdown"] = "text/markdown",
            [".csv"] = "text/csv",
            [".html"] = "text/html",
            [".htm"] = "text/html",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff"
        };

    private readonly LocalRepository _repository;
    private readonly DocumentParser _parser;
    private readonly DocumentClassifier _classifier;
    private readonly KnowledgeChunker _chunker;
    private readonly EmbeddingProvider _embedding;
    private readonly SemaphoreSlim _processingGate = new(1, 1);

    public KnowledgeBaseService(
        LocalRepository repository,
        DocumentParser? parser = null,
        DocumentClassifier? classifier = null,
        KnowledgeChunker? chunker = null,
        EmbeddingProvider? embedding = null)
    {
        _repository = repository;
        _parser = parser ?? new CompositeKnowledgeDocumentParser();
        _classifier = classifier ?? new RuleBasedDocumentClassifier();
        _chunker = chunker ?? new StructuredKnowledgeChunker();
        _embedding = embedding ?? new LocalSemanticEmbeddingProvider();
    }

    public string StorageRoot => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(_repository.DatabasePath))!,
        "knowledge");

    public Task<List<KnowledgeDocument>> GetDocumentsAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default) =>
        _repository.GetKnowledgeDocumentsAsync(includeDeleted, cancellationToken);

    public Task<KnowledgeDocument?> GetDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        _repository.GetKnowledgeDocumentAsync(documentId, cancellationToken);

    public Task<List<KnowledgeDocumentVersion>> GetVersionsAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        _repository.GetKnowledgeDocumentVersionsAsync(documentId, cancellationToken);

    public Task<List<KnowledgeChunk>> GetChunksAsync(
        string documentId,
        string? versionId = null,
        CancellationToken cancellationToken = default) =>
        _repository.GetKnowledgeChunksAsync(documentId, versionId, cancellationToken);

    public Task<List<KnowledgeConflict>> GetConflictsAsync(
        string? documentId = null,
        CancellationToken cancellationToken = default) =>
        _repository.GetKnowledgeConflictsAsync(documentId, cancellationToken);

    public async Task<KnowledgeDocument> UploadAsync(
        string sourcePath,
        KnowledgeUploadOptions options,
        CancellationToken cancellationToken = default)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("选择的知识文件不存在。", fullSourcePath);
        var fileInfo = new FileInfo(fullSourcePath);
        if (fileInfo.Length <= 0) throw new InvalidDataException("知识文件为空。");
        if (fileInfo.Length > MaximumFileSize)
            throw new InvalidDataException($"知识文件超过 {MaximumFileSize / 1024 / 1024} MB 上限。");
        var extension = Path.GetExtension(fileInfo.Name).ToLowerInvariant();
        if (!MimeTypes.TryGetValue(extension, out var mimeType) || !_parser.CanParse(extension))
            throw new NotSupportedException($"不支持 {extension} 文件；请使用 PDF、DOCX、TXT、Markdown、XLSX、CSV、PPTX、HTML 或常见图片。");
        ValidateSignature(fullSourcePath, extension);
        if (extension is ".docx" or ".pptx" or ".xlsx")
            ValidateOfficeArchive(fullSourcePath);
        ValidateScope(options.Scope);

        if (options.SourceKind == KnowledgeSourceKind.AiDraft)
            throw new InvalidOperationException("AI 未发送草稿不能直接进入知识库；请先转为知识候选并由用户审批。");

        await _processingGate.WaitAsync(cancellationToken);
        try
        {
            var existing = string.IsNullOrWhiteSpace(options.ExistingDocumentId)
                ? null
                : await _repository.GetKnowledgeDocumentAsync(options.ExistingDocumentId, cancellationToken)
                  ?? throw new InvalidOperationException("要更新版本的知识文档不存在。");
            var document = existing ?? new KnowledgeDocument
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.Now
            };
            document.Title = string.IsNullOrWhiteSpace(options.Title)
                ? (string.IsNullOrWhiteSpace(document.Title) ? Path.GetFileNameWithoutExtension(fileInfo.Name) : document.Title)
                : options.Title.Trim();
            document.OriginalFileName = fileInfo.Name;
            document.Extension = extension;
            document.MimeType = mimeType;
            document.Status = KnowledgeDocumentStatus.Uploading;
            document.SourceKind = options.SourceKind;
            document.UsageMode = options.ExactTemplate ? KnowledgeUsageMode.ExactTemplate : options.UsageMode;
            document.EvidenceLevel = options.SourceKind switch
            {
                KnowledgeSourceKind.OutcomeValidatedPractice => KnowledgeEvidenceLevel.OutcomeValidated,
                KnowledgeSourceKind.VerifiedInteractionMemory => KnowledgeEvidenceLevel.VerifiedInteraction,
                _ => KnowledgeEvidenceLevel.ApprovedStatic
            };
            document.Scope = options.Scope;
            document.IsExactTemplate = options.ExactTemplate;
            document.EffectiveFrom = options.EffectiveFrom;
            document.EffectiveUntil = options.EffectiveUntil;
            document.UserApproved = false;
            document.ProcessingError = "";
            await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);

            var nextVersion = await _repository.GetNextKnowledgeDocumentVersionAsync(document.Id, cancellationToken);
            var versionId = Guid.NewGuid().ToString("N");
            var versionDirectory = ResolveVersionDirectory(document.Id, nextVersion);
            Directory.CreateDirectory(versionDirectory);
            var storedPath = Path.Combine(versionDirectory, "original" + extension);
            if (File.Exists(storedPath))
                throw new IOException("知识版本目标已存在，系统拒绝覆盖原件。");
            File.Copy(fullSourcePath, storedPath, false);
            var hash = await ComputeFileHashAsync(storedPath, cancellationToken);
            var version = new KnowledgeDocumentVersion
            {
                Id = versionId,
                DocumentId = document.Id,
                Version = nextVersion,
                OriginalFileName = fileInfo.Name,
                StoredFilePath = storedPath,
                Sha256 = hash,
                FileSize = fileInfo.Length,
                Status = KnowledgeDocumentStatus.Processing
            };
            document.Status = KnowledgeDocumentStatus.Processing;
            document.CurrentVersion = nextVersion;
            document.CurrentVersionId = version.Id;
            await _repository.SaveKnowledgeDocumentVersionAsync(version, cancellationToken);
            await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);

            try
            {
                var parsed = await _parser.ParseAsync(new KnowledgeParseRequest
                {
                    FilePath = storedPath,
                    OriginalFileName = fileInfo.Name,
                    MimeType = mimeType
                }, cancellationToken);
                var classification = _classifier.Classify(fileInfo.Name, parsed.Text);
                document.Category = options.Category ?? classification.SuggestedCategory;
                document.Summary = classification.Summary;
                document.DetectedLanguage = classification.Language;
                document.Tags = classification.Tags;
                document.RiskFlags = classification.RiskFlags.Concat(parsed.Warnings).Distinct().ToList();
                document.RiskLevel = classification.RiskLevel;
                document.Status = KnowledgeDocumentStatus.ReadyForReview;
                document.ProcessingError = parsed.RequiresManualReview && string.IsNullOrWhiteSpace(parsed.Text)
                    ? string.Join("；", parsed.Warnings)
                    : "";
                if (document.EffectiveUntil is { } until && until < DateTimeOffset.Now)
                    document.Status = KnowledgeDocumentStatus.Outdated;

                version.ParserName = parsed.ParserName;
                version.ExtractedText = parsed.Text;
                version.ExtractionSummary = classification.Summary;
                version.ChapterTitles = parsed.Sections.Select(section => section.Heading)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Take(200)
                    .ToList();
                version.Warnings = parsed.Warnings;
                var chunks = _chunker.Chunk(document, version, parsed).ToList();
                foreach (var chunk in chunks)
                {
                    chunk.Embedding = _embedding.Embed($"{chunk.Heading}\n{chunk.Content}").ToList();
                    chunk.EmbeddingProvider = _embedding.Name;
                    chunk.EmbeddingVersion = _embedding.Version;
                    chunk.IsActive = false;
                }
                version.ChunkCount = chunks.Count;
                version.Status = document.Status;
                document.ChunkCount = chunks.Count;
                await _repository.SaveKnowledgeDocumentVersionAsync(version, cancellationToken);
                await _repository.SaveKnowledgeChunksAsync(chunks, cancellationToken);
                await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
                await _repository.LogEventAsync(
                    "knowledge_document_processed",
                    document.Scope.CustomerId,
                    null,
                    Json.Serialize(new
                    {
                        document.Id,
                        version = version.Version,
                        version.Sha256,
                        document.Category,
                        scope = document.Scope.Kind,
                        chunks = chunks.Count,
                        document.RiskLevel,
                        warnings = document.RiskFlags
                    }),
                    cancellationToken);
                return document;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                document.Status = KnowledgeDocumentStatus.Failed;
                document.ProcessingError = SafeError(error);
                version.Status = KnowledgeDocumentStatus.Failed;
                version.Warnings = [document.ProcessingError];
                await _repository.SaveKnowledgeDocumentVersionAsync(version, CancellationToken.None);
                await _repository.UpsertKnowledgeDocumentAsync(document, CancellationToken.None);
                await _repository.LogEventAsync(
                    "knowledge_document_processing_failed",
                    document.Scope.CustomerId,
                    null,
                    Json.Serialize(new { document.Id, version = version.Version, error = document.ProcessingError }),
                    CancellationToken.None);
                return document;
            }
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public async Task<KnowledgeDocument> ActivateAsync(
        string documentId,
        string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        if (!document.CanActivate)
            throw new InvalidOperationException(document.RiskLevel is KnowledgeRiskLevel.High or KnowledgeRiskLevel.Blocked
                ? "该知识包含高风险承诺或提示注入，不能启用。"
                : "该知识尚未形成可用知识块，或当前状态不允许启用。");
        var unresolved = (await _repository.GetKnowledgeConflictsAsync(document.Id, cancellationToken))
            .Where(item => item.Status == KnowledgeConflictStatus.Open)
            .ToList();
        if (unresolved.Count > 0)
        {
            document.Status = KnowledgeDocumentStatus.Conflicted;
            document.ProcessingError = $"仍有 {unresolved.Count} 项知识冲突未处理，不能启用。";
            await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
            throw new InvalidOperationException(document.ProcessingError);
        }
        var conflicts = await DetectConflictsAsync(document, cancellationToken);
        if (conflicts.Count > 0)
        {
            document.Status = KnowledgeDocumentStatus.Conflicted;
            document.ProcessingError = $"检测到 {conflicts.Count} 项可能冲突，需人工处理后再启用。";
            await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
            throw new InvalidOperationException(document.ProcessingError);
        }
        document.UserApproved = true;
        document.Status = document.EffectiveUntil is { } until && until < DateTimeOffset.Now
            ? KnowledgeDocumentStatus.Outdated
            : KnowledgeDocumentStatus.Active;
        if (document.Status != KnowledgeDocumentStatus.Active)
            throw new InvalidOperationException("知识已过有效期，不能启用。");
        document.ActivatedAt = DateTimeOffset.Now;
        document.ProcessingError = "";
        await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
        await _repository.LogEventAsync(
            "knowledge_document_activated",
            document.Scope.CustomerId,
            null,
            Json.Serialize(new { document.Id, document.CurrentVersion, actor, document.Scope.Kind }),
            cancellationToken);
        return document;
    }

    public async Task<KnowledgeDocument> DisableAsync(
        string documentId,
        string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        if (document.Status == KnowledgeDocumentStatus.Deleted)
            throw new InvalidOperationException("已删除知识不能停用。");
        document.Status = KnowledgeDocumentStatus.Disabled;
        await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
        await _repository.LogEventAsync(
            "knowledge_document_disabled",
            document.Scope.CustomerId,
            null,
            Json.Serialize(new { document.Id, actor }),
            cancellationToken);
        return document;
    }

    public async Task<KnowledgeDocument> DeleteAsync(
        string documentId,
        string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        document.Status = KnowledgeDocumentStatus.Deleted;
        document.DeletedAt = DateTimeOffset.Now;
        await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
        await _repository.LogEventAsync(
            "knowledge_document_deleted",
            document.Scope.CustomerId,
            null,
            Json.Serialize(new
            {
                document.Id,
                document.CurrentVersion,
                actor,
                retainedOriginals = true,
                retainedAudit = true
            }),
            cancellationToken);
        return document;
    }

    public async Task<KnowledgeDocument> UpdateReviewMetadataAsync(
        string documentId,
        string title,
        KnowledgeCategory category,
        KnowledgeUsageMode usageMode,
        IReadOnlyCollection<string> tags,
        DateTimeOffset? effectiveFrom,
        DateTimeOffset? effectiveUntil,
        CancellationToken cancellationToken = default)
    {
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        if (document.Status == KnowledgeDocumentStatus.Deleted)
            throw new InvalidOperationException("已删除知识不能编辑。");
        document.Title = string.IsNullOrWhiteSpace(title) ? document.Title : title.Trim();
        document.Category = category;
        document.UsageMode = usageMode;
        document.IsExactTemplate = usageMode == KnowledgeUsageMode.ExactTemplate;
        document.Tags = tags.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(50).ToList();
        document.EffectiveFrom = effectiveFrom;
        document.EffectiveUntil = effectiveUntil;
        if (effectiveUntil is { } until && until < DateTimeOffset.Now)
            document.Status = KnowledgeDocumentStatus.Outdated;
        var chunks = await _repository.GetKnowledgeChunksAsync(document.Id, document.CurrentVersionId, cancellationToken);
        foreach (var chunk in chunks)
        {
            chunk.Category = document.Category;
            chunk.UsageMode = document.UsageMode;
            chunk.EffectiveFrom = effectiveFrom;
            chunk.EffectiveUntil = effectiveUntil;
        }
        await _repository.SaveKnowledgeChunksAsync(chunks, cancellationToken);
        await _repository.UpsertKnowledgeDocumentAsync(document, cancellationToken);
        return document;
    }

    public async Task ResolveConflictAsync(
        string conflictId,
        string preferredDocumentId,
        string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var conflict = (await _repository.GetKnowledgeConflictsAsync(null, cancellationToken))
            .FirstOrDefault(item => item.Id == conflictId)
            ?? throw new InvalidOperationException("知识冲突不存在。");
        if (conflict.Status != KnowledgeConflictStatus.Open)
            throw new InvalidOperationException("该知识冲突已经处理。");
        if (preferredDocumentId != conflict.DocumentId && preferredDocumentId != conflict.ConflictingDocumentId)
            throw new InvalidOperationException("保留文档不属于当前冲突。");
        var otherDocumentId = preferredDocumentId == conflict.DocumentId
            ? conflict.ConflictingDocumentId
            : conflict.DocumentId;
        conflict.Status = KnowledgeConflictStatus.Resolved;
        conflict.Resolution = $"人工选择保留文档 {preferredDocumentId}；另一文档保持停用。";
        conflict.ResolvedBy = actor;
        await _repository.SaveKnowledgeConflictAsync(conflict, cancellationToken);
        var preferred = await RequireDocumentAsync(preferredDocumentId, cancellationToken);
        preferred.Status = KnowledgeDocumentStatus.ReadyForReview;
        preferred.ProcessingError = "冲突已由人工处理；请再次审核后启用。";
        await _repository.UpsertKnowledgeDocumentAsync(preferred, cancellationToken);
        var other = await RequireDocumentAsync(otherDocumentId, cancellationToken);
        other.Status = KnowledgeDocumentStatus.Disabled;
        other.ProcessingError = $"冲突处理中选择保留另一文档 {preferredDocumentId}。";
        await _repository.UpsertKnowledgeDocumentAsync(other, cancellationToken);
        await _repository.LogEventAsync(
            "knowledge_conflict_resolved",
            preferred.Scope.CustomerId,
            null,
            Json.Serialize(new { conflict.Id, preferredDocumentId, otherDocumentId, actor }),
            cancellationToken);
    }

    public async Task<string> GetOriginalPathAsync(
        string documentId,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        var version = string.IsNullOrWhiteSpace(versionId)
            ? (await _repository.GetKnowledgeDocumentVersionsAsync(document.Id, cancellationToken)).FirstOrDefault()
            : await _repository.GetKnowledgeDocumentVersionAsync(versionId, cancellationToken);
        if (version is null || !File.Exists(version.StoredFilePath))
            throw new FileNotFoundException("知识原件不存在或已被外部移动。");
        return version.StoredFilePath;
    }

    public async Task<KnowledgeDocument> PublishCandidateAsync(
        string candidateId,
        KnowledgeScope? approvedScope = null,
        string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var candidate = (await _repository.GetKnowledgeCandidatesAsync(null, cancellationToken))
            .FirstOrDefault(item => item.Id == candidateId)
            ?? throw new InvalidOperationException("知识候选不存在。");
        if (candidate.Status != KnowledgeCandidateStatus.Approved)
            throw new InvalidOperationException("只有已经人工批准的候选知识可以发布。");
        Directory.CreateDirectory(StorageRoot);
        var temporarySource = Path.Combine(StorageRoot, $".candidate-{candidate.Id}.md");
        if (File.Exists(temporarySource)) File.Delete(temporarySource);
        try
        {
            await File.WriteAllTextAsync(
                temporarySource,
                $"# {candidate.Title}\n\n{candidate.Content}",
                new UTF8Encoding(false),
                cancellationToken);
            var document = await UploadAsync(temporarySource, new KnowledgeUploadOptions
            {
                Title = candidate.Title,
                Category = candidate.Category,
                SourceKind = candidate.SourceKind,
                Scope = approvedScope ?? candidate.Scope,
                UsageMode = KnowledgeUsageMode.StyleReference
            }, cancellationToken);
            candidate.Status = KnowledgeCandidateStatus.Published;
            candidate.ReviewedBy = actor;
            candidate.ReviewedAt ??= DateTimeOffset.Now;
            await _repository.UpsertKnowledgeCandidateAsync(candidate, cancellationToken);
            return document;
        }
        finally
        {
            try { if (File.Exists(temporarySource)) File.Delete(temporarySource); }
            catch { }
        }
    }

    private async Task<List<KnowledgeConflict>> DetectConflictsAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken)
    {
        var chunks = await _repository.GetKnowledgeChunksAsync(document.Id, document.CurrentVersionId, cancellationToken);
        var request = new KnowledgeRetrievalRequest
        {
            Query = string.Join(' ', chunks.SelectMany(chunk => chunk.Keywords).Distinct().Take(80)),
            AccountId = document.Scope.AccountId,
            CustomerId = document.Scope.CustomerId,
            ConversationId = document.Scope.ConversationId,
            TemporaryTaskId = document.Scope.TemporaryTaskId,
            ExcludedDocumentIds = [document.Id],
            Limit = 100
        };
        var existing = await _repository.GetEligibleKnowledgeChunksAsync(request, cancellationToken);
        var documents = (await _repository.GetKnowledgeDocumentsAsync(false, cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<KnowledgeConflict>();
        foreach (var chunk in chunks)
        {
            foreach (var other in existing.Where(item => item.Category == chunk.Category))
            {
                if (!KnowledgeConflictDetector.IsPotentialConflict(chunk, other, out var reason)) continue;
                var conflict = new KnowledgeConflict
                {
                    Id = StableId("conflict", chunk.Id, other.Id),
                    DocumentId = document.Id,
                    VersionId = document.CurrentVersionId,
                    ChunkId = chunk.Id,
                    ConflictingDocumentId = other.DocumentId,
                    ConflictingVersionId = other.VersionId,
                    ConflictingChunkId = other.Id,
                    Topic = chunk.Heading,
                    Detail = $"{reason}；对照资料：{documents.GetValueOrDefault(other.DocumentId)?.Title ?? other.DocumentId}",
                    Status = KnowledgeConflictStatus.Open
                };
                await _repository.SaveKnowledgeConflictAsync(conflict, cancellationToken);
                if (documents.GetValueOrDefault(other.DocumentId) is { } otherDocument)
                {
                    otherDocument.Status = KnowledgeDocumentStatus.Conflicted;
                    otherDocument.ProcessingError = "与新启用知识存在待人工处理的冲突。";
                    await _repository.UpsertKnowledgeDocumentAsync(otherDocument, cancellationToken);
                }
                conflicts.Add(conflict);
                if (conflicts.Count >= 20) return conflicts;
            }
        }
        return conflicts;
    }

    private async Task<KnowledgeDocument> RequireDocumentAsync(
        string documentId,
        CancellationToken cancellationToken) =>
        await _repository.GetKnowledgeDocumentAsync(documentId, cancellationToken)
        ?? throw new InvalidOperationException("知识文档不存在。");

    private string ResolveVersionDirectory(string documentId, int version)
    {
        var root = Path.GetFullPath(StorageRoot);
        var target = Path.GetFullPath(Path.Combine(root, documentId, $"v{version}"));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("知识存储路径越界。");
        return target;
    }

    private static void ValidateScope(KnowledgeScope scope)
    {
        var valid = scope.Kind switch
        {
            KnowledgeScopeKind.Global => true,
            KnowledgeScopeKind.Account => !string.IsNullOrWhiteSpace(scope.AccountId),
            KnowledgeScopeKind.Customer => !string.IsNullOrWhiteSpace(scope.CustomerId),
            KnowledgeScopeKind.Conversation => !string.IsNullOrWhiteSpace(scope.AccountId)
                                                    && !string.IsNullOrWhiteSpace(scope.ConversationId),
            KnowledgeScopeKind.Temporary => !string.IsNullOrWhiteSpace(scope.TemporaryTaskId),
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"知识作用域 {scope.Kind} 缺少必要绑定信息。");
        if (scope.Kind == KnowledgeScopeKind.Global &&
            (!string.IsNullOrWhiteSpace(scope.AccountId) || !string.IsNullOrWhiteSpace(scope.CustomerId)
             || !string.IsNullOrWhiteSpace(scope.ConversationId) || !string.IsNullOrWhiteSpace(scope.TemporaryTaskId)))
            throw new InvalidOperationException("全局知识不能混入账号、客户、会话或临时任务标识。");
    }

    private static void ValidateSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        var value = header[..read];
        var valid = extension switch
        {
            ".pdf" => value.StartsWith("%PDF"u8),
            ".png" => value.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            ".jpg" or ".jpeg" => value.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".webp" => value.Length >= 12 && value[..4].SequenceEqual("RIFF"u8) && value[8..12].SequenceEqual("WEBP"u8),
            ".bmp" => value.StartsWith("BM"u8),
            ".tif" or ".tiff" => value.StartsWith("II*"u8) || value.StartsWith("MM"u8),
            ".docx" or ".pptx" or ".xlsx" => value.StartsWith("PK"u8),
            _ => true
        };
        if (!valid) throw new InvalidDataException("文件内容与扩展名不一致，已阻止导入。");
    }

    private static void ValidateOfficeArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaximumOfficeEntries)
            throw new InvalidDataException("Office 文件包含过多条目，已阻止处理。");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded += entry.Length;
            if (expanded > MaximumExpandedOfficeSize)
                throw new InvalidDataException("Office 文件解压后体积过大，已阻止处理。");
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("Office 文件包含越界路径，已阻止处理。");
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 500)
                throw new InvalidDataException("Office 文件压缩比异常，已阻止处理。");
        }
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SafeError(Exception error)
    {
        var message = error.Message.Replace(Environment.NewLine, " ").Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    private static string StableId(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))))
            .ToLowerInvariant()[..32];
}

public sealed class KnowledgeRetrievalService : HybridRetriever
{
    private readonly LocalRepository _repository;
    private readonly EmbeddingProvider _embedding;
    private readonly VectorSearchProvider _vector;
    private readonly KeywordSearchProvider _keyword;
    private readonly KnowledgeRanker _ranker;

    public KnowledgeRetrievalService(
        LocalRepository repository,
        EmbeddingProvider? embedding = null,
        VectorSearchProvider? vector = null,
        KeywordSearchProvider? keyword = null,
        KnowledgeRanker? ranker = null)
    {
        _repository = repository;
        _embedding = embedding ?? new LocalSemanticEmbeddingProvider();
        _vector = vector ?? new CosineVectorSearchProvider();
        _keyword = keyword ?? new ExactAwareKeywordSearchProvider();
        _ranker = ranker ?? new ScopeFreshnessKnowledgeRanker();
    }

    public async Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Query = request.Query?.Trim() ?? "";
        request.Limit = Math.Clamp(request.Limit, 1, 30);
        request.MinimumScore = Math.Clamp(request.MinimumScore, 0, 1);
        var result = new KnowledgeRetrievalResult { Request = request };
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            result.InsufficiencyReason = "检索问题为空。";
            await SaveLogAsync(result, cancellationToken);
            return result;
        }

        var enrichedQuery = string.Join('\n',
            new[]
            {
                request.Query,
                request.CustomerIntent,
                request.CustomerStage,
                request.SourcingMissingFields,
                request.Language
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(request.CustomerId) && !string.IsNullOrWhiteSpace(request.ConversationId))
        {
            var exclusions = (await _repository.GetKnowledgeFeedbackAsync(request.CustomerId, cancellationToken))
                .Where(item => item.ExcludedForCurrentConversation &&
                               string.Equals(item.AccountId, request.AccountId, StringComparison.Ordinal) &&
                               string.Equals(item.ConversationId, request.ConversationId, StringComparison.Ordinal))
                .ToList();
            request.ExcludedChunkIds = request.ExcludedChunkIds
                .Concat(exclusions.Select(item => item.ChunkId))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        var queryVector = _embedding.Embed(enrichedQuery);
        var chunks = await _repository.GetEligibleKnowledgeChunksAsync(request, cancellationToken);
        var documents = (await _repository.GetKnowledgeDocumentsAsync(false, cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            if (!ScopeAllows(request, chunk.Scope)) continue;
            var keyword = _keyword.Score(enrichedQuery, chunk);
            var vector = _vector.Score(queryVector, chunk.Embedding);
            var score = _ranker.Rank(request, chunk, keyword.Score, vector, out var scope, out var freshness);
            if (score < request.MinimumScore) continue;
            var outdated = chunk.EffectiveUntil is { } until && until < DateTimeOffset.Now;
            result.Hits.Add(new KnowledgeRetrievalHit
            {
                ChunkId = chunk.Id,
                DocumentId = chunk.DocumentId,
                VersionId = chunk.VersionId,
                DocumentVersion = chunk.DocumentVersion,
                DocumentTitle = documents.GetValueOrDefault(chunk.DocumentId)?.Title ?? chunk.DocumentId,
                Content = chunk.Content,
                Heading = chunk.Heading,
                Locator = chunk.Locator,
                Category = chunk.Category,
                SourceKind = chunk.SourceKind,
                UsageMode = chunk.UsageMode,
                EvidenceLevel = chunk.EvidenceLevel,
                Scope = chunk.Scope,
                KeywordScore = keyword.Score,
                VectorScore = vector,
                ScopeScore = scope,
                FreshnessScore = freshness,
                RelevanceScore = score,
                HasOpenConflict = chunk.HasOpenConflict,
                IsOutdated = outdated,
                MatchedTerms = keyword.MatchedTerms.ToList()
            });
        }
        result.Hits = result.Hits
            .OrderByDescending(hit => hit.RelevanceScore)
            .ThenByDescending(hit => hit.DocumentVersion)
            .Take(request.Limit)
            .ToList();
        foreach (var hit in result.Hits.Where(hit => hit.HasOpenConflict))
            result.ConflictWarnings.Add($"{hit.CitationLabel} 存在未解决冲突。");
        foreach (var hit in result.Hits.Where(hit => hit.IsOutdated))
            result.RiskWarnings.Add($"{hit.CitationLabel} 已过有效期。");
        result.SufficientToAnswer = result.Hits.Any(hit =>
            !hit.HasOpenConflict && !hit.IsOutdated && hit.RelevanceScore >= Math.Max(request.MinimumScore, 0.28));
        if (!result.SufficientToAnswer)
            result.InsufficiencyReason = chunks.Count == 0
                ? "当前账号/客户/会话作用域内没有已启用知识。"
                : "没有达到可信阈值且无冲突、未过期的知识；不得据此猜测回答。";
        await SaveLogAsync(result, cancellationToken);
        return result;
    }

    private async Task SaveLogAsync(
        KnowledgeRetrievalResult result,
        CancellationToken cancellationToken)
    {
        await _repository.SaveKnowledgeRetrievalLogAsync(new KnowledgeRetrievalLog
        {
            Id = result.Id,
            Query = result.Request.Query,
            CustomerId = result.Request.CustomerId,
            AccountId = result.Request.AccountId,
            ConversationId = result.Request.ConversationId,
            UsageContext = result.Request.UsageContext,
            SufficientToAnswer = result.SufficientToAnswer,
            RetrievedChunkIds = result.Hits.Select(hit => hit.ChunkId).ToList(),
            ConflictWarnings = result.ConflictWarnings,
            ResultJson = Json.Serialize(result)
        }, cancellationToken);
    }

    private static bool ScopeAllows(KnowledgeRetrievalRequest request, KnowledgeScope scope) => scope.Kind switch
    {
        KnowledgeScopeKind.Global =>
            string.IsNullOrWhiteSpace(scope.AccountId) &&
            string.IsNullOrWhiteSpace(scope.CustomerId) &&
            string.IsNullOrWhiteSpace(scope.ConversationId) &&
            string.IsNullOrWhiteSpace(scope.TemporaryTaskId),
        KnowledgeScopeKind.Account =>
            !string.IsNullOrWhiteSpace(request.AccountId) &&
            string.Equals(scope.AccountId, request.AccountId, StringComparison.Ordinal),
        KnowledgeScopeKind.Customer =>
            !string.IsNullOrWhiteSpace(request.CustomerId) &&
            string.Equals(scope.CustomerId, request.CustomerId, StringComparison.Ordinal),
        KnowledgeScopeKind.Conversation =>
            !string.IsNullOrWhiteSpace(request.AccountId) &&
            !string.IsNullOrWhiteSpace(request.ConversationId) &&
            string.Equals(scope.AccountId, request.AccountId, StringComparison.Ordinal) &&
            string.Equals(scope.ConversationId, request.ConversationId, StringComparison.Ordinal),
        KnowledgeScopeKind.Temporary =>
            !string.IsNullOrWhiteSpace(request.TemporaryTaskId) &&
            string.Equals(scope.TemporaryTaskId, request.TemporaryTaskId, StringComparison.Ordinal),
        _ => false
    };
}

internal static class KnowledgeConflictDetector
{
    private static readonly Regex NumberPattern = new(
        @"(?<!\p{L})\d+(?:\.\d+)?\s*(?:%|usd|rmb|cny|kg|g|pcs?|days?|hours?|天|小时|件|个)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPotentialConflict(
        KnowledgeChunk left,
        KnowledgeChunk right,
        out string reason)
    {
        reason = "";
        var sharedTerms = left.Keywords.Intersect(right.Keywords, StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        if (sharedTerms.Count < 3) return false;
        var leftNumbers = NumberPattern.Matches(left.Content).Select(match => match.Value.ToLowerInvariant()).Distinct().ToList();
        var rightNumbers = NumberPattern.Matches(right.Content).Select(match => match.Value.ToLowerInvariant()).Distinct().ToList();
        if (leftNumbers.Count > 0 && rightNumbers.Count > 0 &&
            !leftNumbers.SequenceEqual(rightNumbers, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"同一主题包含不同数字/期限（{string.Join(",", leftNumbers.Take(4))} vs {string.Join(",", rightNumbers.Take(4))}）";
            return true;
        }
        var leftNegative = ContainsAny(left.Content, "不得", "禁止", "不允许", "must not", "prohibited", "cannot");
        var rightNegative = ContainsAny(right.Content, "不得", "禁止", "不允许", "must not", "prohibited", "cannot");
        var leftPositive = ContainsAny(left.Content, "可以", "允许", "必须", "may", "allowed", "must");
        var rightPositive = ContainsAny(right.Content, "可以", "允许", "必须", "may", "allowed", "must");
        if ((leftNegative && rightPositive) || (rightNegative && leftPositive))
        {
            reason = $"同一主题的允许/禁止规则可能相反（共同关键词：{string.Join("、", sharedTerms)}）";
            return true;
        }
        return false;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
