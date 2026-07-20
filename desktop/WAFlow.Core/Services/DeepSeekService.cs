using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class DeepSeekException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public DeepSeekException(string code, string message, bool retryable, Exception? inner = null) : base(message, inner) { Code = code; Retryable = retryable; }
}

public sealed record AiModelCatalog(IReadOnlyList<string> Models, DateTimeOffset FetchedAt);

public sealed class DeepSeekService
{
    private readonly LocalRepository _repository;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;

    public DeepSeekService(LocalRepository repository, ISecretStore secrets, HttpClient? httpClient = null)
    {
        _repository = repository; _secrets = secrets;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public bool HasApiKey()
    {
        try { return !string.IsNullOrWhiteSpace(_secrets.Read()); }
        catch { return false; }
    }

    public async Task<AiModelCatalog> DiscoverModelsAsync(string baseUrl, string? apiKeyOverride = null, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(apiKeyOverride) ? _secrets.Read() : apiKeyOverride.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw new DeepSeekException("provider_not_configured", "请先填写 API Key，再自动拉取模型。", false);
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new DeepSeekException("invalid_base_url", "AI Base URL 必须是有效的 HTTPS 地址。", false);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri.ToString().TrimEnd('/') + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (TaskCanceledException error) { throw new DeepSeekException("model_discovery_timeout", "拉取模型列表超时，请检查网络后重试。", true, error); }
        catch (HttpRequestException error) { throw new DeepSeekException("model_discovery_unavailable", "无法连接 AI 模型列表接口，请检查网络和 Base URL。", true, error); }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var unauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                throw new DeepSeekException(
                    unauthorized ? "provider_unauthorized" : "model_discovery_failed",
                    unauthorized ? "API Key 无效或没有读取模型列表的权限。" : $"拉取模型列表失败（HTTP {(int)response.StatusCode}）。",
                    response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var array = root.ValueKind == JsonValueKind.Array
                    ? root
                    : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                        ? data
                        : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                            ? models
                            : throw new JsonException("Missing model array");
                var ids = array.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
                            ? id.GetString()
                            : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name)
                                ? name.GetString()
                                : null)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .Take(500)
                    .ToList();
                if (ids.Count == 0) throw new JsonException("Empty model array");
                return new AiModelCatalog(ids, DateTimeOffset.Now);
            }
            catch (Exception error) when (error is not DeepSeekException)
            {
                throw new DeepSeekException("invalid_model_catalog", "模型列表接口未返回可选择的模型名称。", true, error);
            }
        }
    }

    public async Task<Lead> AnalyzeLeadAsync(Lead lead, CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.DeepSeekModel)) throw new DeepSeekException("model_not_selected", "请先从自动拉取的模型列表中选择一个模型。", false);
        var runId = Guid.NewGuid().ToString("N");
        var requestedAt = lead.AnalysisRequestedAt;
        lead.AnalysisStatus = AnalysisStatus.Running; lead.AnalysisError = "";
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.SaveAnalysisRunAsync(runId, lead.Id, "running", settings.DeepSeekModel, null, null, cancellationToken);
        try
        {
            var recentMessages = await _repository.GetWhatsAppMessagesForLeadAsync(lead, 40, cancellationToken);
            var replySignals = WhatsAppReplySignalExtractor.Extract(recentMessages);
            var payload = new
            {
                lead = new { lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.EstimatedOrderValue, lead.Currency, lead.CompanyScale, lead.PurchasePower, lead.ExplicitDemand, lead.RegisteredOrConsulted, lead.Source, lead.Tags, lead.Owner, lead.CustomFields, stage = lead.Stage.ToString(), lead.LatestMessage },
                whatsapp = new
                {
                    replySignals,
                    recentMessages = recentMessages.Select(message => new
                    {
                        direction = message.Direction == WhatsAppMessageDirection.Incoming ? "customer" : "seller",
                        timestamp = message.Timestamp,
                        message.Kind,
                        message.Body
                    })
                },
                scoringWeights = LeadScoringService.Weights
            };
            var instructions = """
                You are AI Sales OS's auditable B2B lead analyst. Use only the input evidence and return one JSON object, without markdown.
                Required properties: score(integer 0..100), grade(A/B/C/D), factors(array of exactly 8 objects: key, score, maxScore, rationale),
                stage(one of new,contacted,interested,negotiation,waiting,customer,lost), confidence(0..1), evidence(array of field,value,interpretation),
                profileSummary, customerSegment, nextAction, risks(array of strings).
                Factor keys and maximums must exactly match scoringWeights. Score must equal the factor sum. Grade: A>=80, B=60..79, C=40..59, D<40.
                WhatsApp customer replies are the primary evidence for replyEngagement, explicitDemand, recency and stage. Keyword signals are retrieval hints only:
                verify every signal against the exact message text and context, and do not treat a keyword alone as intent. CRM/imported fields are supporting context.
                State uncertainty in risks; never invent facts. Answer profileSummary, nextAction and rationale in Simplified Chinese.
                """;
            var content = await CompleteJsonAsync(settings, instructions, Infrastructure.Json.Serialize(payload), cancellationToken);
            var analysis = ParseAnalysis(content);
            Validate(analysis);
            var target = await _repository.GetLeadAsync(lead.Id, cancellationToken) ?? lead;
            target.Score = analysis.Score; target.Grade = analysis.Grade; target.ScoreBreakdown = analysis.Factors.ToDictionary(f => f.Key, f => f.Score);
            target.ScoreReasons = analysis.Factors.OrderByDescending(f => f.Score / (double)Math.Max(1, f.MaxScore)).Take(3).Select(f => f.Rationale).ToList();
            target.Stage = analysis.Stage; target.AnalysisConfidence = analysis.Confidence; target.Evidence = analysis.Evidence;
            target.ProfileSummary = analysis.ProfileSummary; target.CustomerSegment = analysis.CustomerSegment; target.NextAction = analysis.NextAction; target.Risks = analysis.Risks;
            target.LatestReplySignals = replySignals;
            target.AnalysisStatus = AnalysisStatus.Succeeded; target.AnalysisError = ""; target.AiScoreApplied = true; target.LastAnalyzedAt = DateTimeOffset.Now;
            await _repository.UpsertLeadAsync(target, cancellationToken);
            await _repository.SaveAnalysisRunAsync(runId, lead.Id, "succeeded", settings.DeepSeekModel, analysis, null, cancellationToken);
            await _repository.LogEventAsync("lead_analyzed", lead.Id, null, $"provider=compatible; model={settings.DeepSeekModel}; trigger={target.AnalysisTrigger}", cancellationToken);
            return target;
        }
        catch (Exception error)
        {
            var safe = error is DeepSeekException dse ? $"{dse.Code}: {dse.Message}" : "AI 返回内容无法验证，请重试。";
            var target = await _repository.GetLeadAsync(lead.Id, cancellationToken) ?? lead;
            var hasNewerRequest = target.AnalysisRequestedAt is not null && (requestedAt is null || target.AnalysisRequestedAt > requestedAt);
            target.AnalysisStatus = hasNewerRequest ? AnalysisStatus.Queued : AnalysisStatus.RetryableFailed;
            target.AnalysisError = hasNewerRequest ? $"{safe} 新回复已重新排队。" : safe;
            await _repository.UpsertLeadAsync(target, cancellationToken);
            await _repository.SaveAnalysisRunAsync(runId, lead.Id, "retryable_failed", settings.DeepSeekModel, null, safe, cancellationToken);
            throw error is DeepSeekException ? error : new DeepSeekException("invalid_structured_output", safe, true, error);
        }
    }

    public async Task<OutreachDraft> GenerateDraftAsync(Lead lead, string purpose, string language, string extraInstructions, CancellationToken cancellationToken = default)
    {
        if (lead.OptedOut) throw new InvalidOperationException("客户已退订，禁止生成触达话术。");
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        var payload = new { lead=new { lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.EstimatedOrderValue, lead.Currency, lead.ProfileSummary, lead.NextAction, lead.Risks, lead.LatestMessage, lead.CustomFields }, purpose, language, extraInstructions };
        var instructions = """
            You are AI Sales OS's B2B WhatsApp copywriter. Return one JSON object only, without markdown.
            Required properties: purpose, language, body, rationale(array of strings), assumptions(array of strings), risks(array of strings).
            Write a concise professional message for human approval. Do not invent discounts, certifications, dates, inventory, pricing or delivery promises.
            The body must be in the requested language. Keep rationale, assumptions and risks in Simplified Chinese.
            """;
        var content = await CompleteJsonAsync(settings, instructions, Infrastructure.Json.Serialize(payload), cancellationToken);
        GeneratedDraft? generated;
        try { generated = Infrastructure.Json.Deserialize<GeneratedDraft>(ExtractJson(content)); }
        catch (Exception error) { throw new DeepSeekException("invalid_structured_output", "AI 话术 JSON 解析失败。", true, error); }
        if (generated is null || string.IsNullOrWhiteSpace(generated.Body) || generated.Body.Length > 4096) throw new DeepSeekException("invalid_structured_output", "AI 话术缺少正文或正文过长。", true);
        var draft = new OutreachDraft
        {
            LeadId=lead.Id, LeadName=lead.DisplayName, Purpose=purpose, Language=language, Body=generated.Body.Trim(),
            Rationale=generated.Rationale ?? [], Assumptions=generated.Assumptions ?? [], Risks=generated.Risks ?? [],
            Provider="compatible", Model=settings.DeepSeekModel
        };
        await _repository.SaveDraftAsync(draft, "generated", cancellationToken: cancellationToken);
        await _repository.LogEventAsync("draft_generated", lead.Id, draft.Id, $"purpose={purpose}; language={language}", cancellationToken);
        return draft;
    }

    private async Task<string> CompleteJsonAsync(AppSettings settings, string instructions, string payload, CancellationToken cancellationToken)
    {
        var key = _secrets.Read();
        if (string.IsNullOrWhiteSpace(key)) throw new DeepSeekException("provider_not_configured", "请先在 AI 设置中填写 API Key。", false);
        var endpoint = settings.DeepSeekBaseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(Infrastructure.Json.Serialize(new
        {
            model = settings.DeepSeekModel,
            messages = new[] { new { role="system", content=instructions }, new { role="user", content="Input JSON: " + payload } },
            response_format = new { type="json_object" }, temperature = 0.2, stream = false
        }), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (TaskCanceledException error) { throw new DeepSeekException("provider_timeout", "AI 请求超时，请稍后重试。", true, error); }
        catch (HttpRequestException error) { throw new DeepSeekException("provider_unavailable", "无法连接 AI Provider，请检查网络和 Base URL。", true, error); }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode == HttpStatusCode.TooManyRequests ? "provider_rate_limited" : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "provider_unauthorized" : "provider_request_failed";
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                throw new DeepSeekException(code, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "AI API Key 无效或无权限。" : $"AI 请求失败（HTTP {(int)response.StatusCode}）。", retryable);
            }
            try
            {
                using var document = JsonDocument.Parse(body);
                var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) throw new JsonException("Empty content");
                return content;
            }
            catch (Exception error) { throw new DeepSeekException("invalid_provider_response", "AI Provider 响应缺少有效内容。", true, error); }
        }
    }

    private static LeadAnalysis ParseAnalysis(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(content));
            var root = document.RootElement;
            var factors = root.GetProperty("factors").EnumerateArray().Select(x => new LeadFactor
            {
                Key=x.GetProperty("key").GetString() ?? "", Score=x.GetProperty("score").GetInt32(), MaxScore=x.GetProperty("maxScore").GetInt32(), Rationale=x.GetProperty("rationale").GetString() ?? ""
            }).ToList();
            var evidence = root.TryGetProperty("evidence", out var ev) ? ev.EnumerateArray().Select(x => new AnalysisEvidence { Field=x.GetProperty("field").GetString() ?? "", Value=x.GetProperty("value").ToString(), Interpretation=x.GetProperty("interpretation").GetString() ?? "" }).ToList() : [];
            var stageText = root.GetProperty("stage").GetString();
            return new LeadAnalysis
            {
                Score=root.GetProperty("score").GetInt32(), Grade=root.GetProperty("grade").GetString() ?? "D", Factors=factors, Stage=StageParser.Parse(stageText),
                Confidence=root.GetProperty("confidence").GetDouble(), Evidence=evidence, ProfileSummary=root.GetProperty("profileSummary").GetString() ?? "",
                CustomerSegment=root.GetProperty("customerSegment").GetString() ?? "", NextAction=root.GetProperty("nextAction").GetString() ?? "",
                Risks=root.TryGetProperty("risks", out var risks) ? risks.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : []
            };
        }
        catch (Exception error) { throw new DeepSeekException("invalid_structured_output", "AI 分析 JSON 解析失败。", true, error); }
    }

    private static void Validate(LeadAnalysis analysis)
    {
        if (analysis.Factors.Count != 8 || analysis.Factors.Select(x => x.Key).Distinct().Count() != 8) throw new DeepSeekException("invalid_structured_output", "分析必须包含 8 个唯一评分因素。", true);
        foreach (var factor in analysis.Factors)
            if (!LeadScoringService.Weights.TryGetValue(factor.Key, out var max) || factor.MaxScore != max || factor.Score < 0 || factor.Score > max) throw new DeepSeekException("invalid_structured_output", $"评分因素 {factor.Key} 超出规则。", true);
        if (analysis.Factors.Sum(x => x.Score) != analysis.Score || LeadScoringService.GradeFromScore(analysis.Score) != analysis.Grade) throw new DeepSeekException("invalid_structured_output", "总分、等级与因素分数不一致。", true);
        if (analysis.Confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(analysis.ProfileSummary) || string.IsNullOrWhiteSpace(analysis.NextAction)) throw new DeepSeekException("invalid_structured_output", "分析缺少必需字段。", true);
    }

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstLine = trimmed.IndexOf('\n'); var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
        }
        var start = trimmed.IndexOf('{'); var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) throw new DeepSeekException("invalid_structured_output", "AI Provider 未返回 JSON 对象。", true);
        return trimmed[start..(end + 1)];
    }

    private sealed class GeneratedDraft
    {
        public string Purpose { get; set; } = ""; public string Language { get; set; } = ""; public string Body { get; set; } = "";
        public List<string>? Rationale { get; set; } public List<string>? Assumptions { get; set; } public List<string>? Risks { get; set; }
    }
}
