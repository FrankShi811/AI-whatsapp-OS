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

    public async Task<Lead> AnalyzeLeadAsync(Lead lead, SalesProfile profile, CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        var runId = Guid.NewGuid().ToString("N");
        lead.AnalysisStatus = AnalysisStatus.Running; lead.AnalysisError = "";
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.SaveAnalysisRunAsync(runId, lead.Id, "running", settings.DeepSeekModel, null, null, cancellationToken);
        try
        {
            var payload = new
            {
                seller = profile,
                lead = new { lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.EstimatedOrderValue, lead.Currency, lead.CompanyScale, lead.PurchasePower, lead.ExplicitDemand, lead.RegisteredOrConsulted, lead.Source, stage = lead.Stage.ToString(), lead.LatestMessage, deterministicScore = lead.Score, deterministicGrade = lead.Grade },
                scoringWeights = LeadScoringService.Weights
            };
            var instructions = """
                You are AI Sales OS's auditable B2B lead analyst. Use only the input evidence and return one JSON object, without markdown.
                Required properties: score(integer 0..100), grade(A/B/C/D), factors(array of exactly 8 objects: key, score, maxScore, rationale),
                stage(one of new,contacted,interested,negotiation,waiting,customer,lost), confidence(0..1), evidence(array of field,value,interpretation),
                profileSummary, customerSegment, nextAction, risks(array of strings).
                Factor keys and maximums must exactly match scoringWeights. Score must equal the factor sum. Grade: A>=80, B=60..79, C=40..59, D<40.
                State uncertainty in risks; never invent facts. Answer profileSummary, nextAction and rationale in Simplified Chinese.
                """;
            var content = await CompleteJsonAsync(settings, instructions, Infrastructure.Json.Serialize(payload), cancellationToken);
            var analysis = ParseAnalysis(content);
            Validate(analysis);
            lead.Score = analysis.Score; lead.Grade = analysis.Grade; lead.ScoreBreakdown = analysis.Factors.ToDictionary(f => f.Key, f => f.Score);
            lead.ScoreReasons = analysis.Factors.OrderByDescending(f => f.Score / (double)Math.Max(1, f.MaxScore)).Take(3).Select(f => f.Rationale).ToList();
            lead.Stage = analysis.Stage; lead.AnalysisConfidence = analysis.Confidence; lead.Evidence = analysis.Evidence;
            lead.ProfileSummary = analysis.ProfileSummary; lead.CustomerSegment = analysis.CustomerSegment; lead.NextAction = analysis.NextAction; lead.Risks = analysis.Risks;
            lead.AnalysisStatus = AnalysisStatus.Succeeded; lead.AnalysisError = "";
            await _repository.UpsertLeadAsync(lead, cancellationToken);
            await _repository.SaveAnalysisRunAsync(runId, lead.Id, "succeeded", settings.DeepSeekModel, analysis, null, cancellationToken);
            await _repository.LogEventAsync("lead_analyzed", lead.Id, null, $"provider=deepseek; model={settings.DeepSeekModel}", cancellationToken);
            return lead;
        }
        catch (Exception error)
        {
            var safe = error is DeepSeekException dse ? $"{dse.Code}: {dse.Message}" : "DeepSeek 返回内容无法验证，请重试。";
            lead.AnalysisStatus = AnalysisStatus.RetryableFailed; lead.AnalysisError = safe;
            await _repository.UpsertLeadAsync(lead, cancellationToken);
            await _repository.SaveAnalysisRunAsync(runId, lead.Id, "retryable_failed", settings.DeepSeekModel, null, safe, cancellationToken);
            throw error is DeepSeekException ? error : new DeepSeekException("invalid_structured_output", safe, true, error);
        }
    }

    public async Task<OutreachDraft> GenerateDraftAsync(Lead lead, SalesProfile profile, string purpose, string language, string extraInstructions, CancellationToken cancellationToken = default)
    {
        if (lead.OptedOut) throw new InvalidOperationException("客户已退订，禁止生成触达话术。");
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        var payload = new { seller=profile, lead=new { lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.EstimatedOrderValue, lead.Currency, lead.ProfileSummary, lead.NextAction, lead.Risks, lead.LatestMessage }, purpose, language, extraInstructions };
        var instructions = """
            You are AI Sales OS's B2B WhatsApp copywriter. Return one JSON object only, without markdown.
            Required properties: purpose, language, body, rationale(array of strings), assumptions(array of strings), risks(array of strings).
            Write a concise professional message for human approval. Do not invent discounts, certifications, dates, inventory, pricing or delivery promises.
            The body must be in the requested language. Keep rationale, assumptions and risks in Simplified Chinese.
            """;
        var content = await CompleteJsonAsync(settings, instructions, Infrastructure.Json.Serialize(payload), cancellationToken);
        GeneratedDraft? generated;
        try { generated = Infrastructure.Json.Deserialize<GeneratedDraft>(ExtractJson(content)); }
        catch (Exception error) { throw new DeepSeekException("invalid_structured_output", "DeepSeek 话术 JSON 解析失败。", true, error); }
        if (generated is null || string.IsNullOrWhiteSpace(generated.Body) || generated.Body.Length > 4096) throw new DeepSeekException("invalid_structured_output", "DeepSeek 话术缺少正文或正文过长。", true);
        var draft = new OutreachDraft
        {
            LeadId=lead.Id, LeadName=lead.DisplayName, Purpose=purpose, Language=language, Body=generated.Body.Trim(),
            Rationale=generated.Rationale ?? [], Assumptions=generated.Assumptions ?? [], Risks=generated.Risks ?? [],
            Provider="deepseek", Model=settings.DeepSeekModel
        };
        await _repository.SaveDraftAsync(draft, "generated", cancellationToken: cancellationToken);
        await _repository.LogEventAsync("draft_generated", lead.Id, draft.Id, $"purpose={purpose}; language={language}", cancellationToken);
        return draft;
    }

    private async Task<string> CompleteJsonAsync(AppSettings settings, string instructions, string payload, CancellationToken cancellationToken)
    {
        var key = _secrets.Read();
        if (string.IsNullOrWhiteSpace(key)) throw new DeepSeekException("provider_not_configured", "请先在企业设置中填写 DeepSeek API Key。", false);
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
        catch (TaskCanceledException error) { throw new DeepSeekException("provider_timeout", "DeepSeek 请求超时，请稍后重试。", true, error); }
        catch (HttpRequestException error) { throw new DeepSeekException("provider_unavailable", "无法连接 DeepSeek，请检查网络和 Base URL。", true, error); }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode == HttpStatusCode.TooManyRequests ? "provider_rate_limited" : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "provider_unauthorized" : "provider_request_failed";
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                throw new DeepSeekException(code, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "DeepSeek API Key 无效或无权限。" : $"DeepSeek 请求失败（HTTP {(int)response.StatusCode}）。", retryable);
            }
            try
            {
                using var document = JsonDocument.Parse(body);
                var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) throw new JsonException("Empty content");
                return content;
            }
            catch (Exception error) { throw new DeepSeekException("invalid_provider_response", "DeepSeek 响应缺少有效内容。", true, error); }
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
        catch (Exception error) { throw new DeepSeekException("invalid_structured_output", "DeepSeek 分析 JSON 解析失败。", true, error); }
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
        if (start < 0 || end <= start) throw new DeepSeekException("invalid_structured_output", "DeepSeek 未返回 JSON 对象。", true);
        return trimmed[start..(end + 1)];
    }

    private sealed class GeneratedDraft
    {
        public string Purpose { get; set; } = ""; public string Language { get; set; } = ""; public string Body { get; set; } = "";
        public List<string>? Rationale { get; set; } public List<string>? Assumptions { get; set; } public List<string>? Risks { get; set; }
    }
}
