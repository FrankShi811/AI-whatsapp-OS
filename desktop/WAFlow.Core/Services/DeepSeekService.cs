using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

public sealed class DeepSeekService : IStructuredAiProvider
{
    private static readonly JsonSerializerOptions CompatibleJsonOptions = new(Infrastructure.Json.Options)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly LocalRepository _repository;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);

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

    public async Task<string> GetSelectedModelAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.DeepSeekModel))
            throw new DeepSeekException("model_not_selected", "请先从自动拉取的模型列表中选择一个模型。", false);
        return settings.DeepSeekModel;
    }

    public async Task<T> CompleteStructuredAsync<T>(
        string instructions,
        object payload,
        Func<T, string?> validate,
        CancellationToken cancellationToken = default) where T : class
    {
        await _analysisGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await _repository.GetAppSettingsAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.DeepSeekModel))
                throw new DeepSeekException("model_not_selected", "请先从自动拉取的模型列表中选择一个模型。", false);
            DeepSeekException? lastError = null;
            var serializedPayload = Infrastructure.Json.Serialize(payload);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var attemptInstructions = attempt == 0
                    ? instructions
                    : $"{instructions}\n\n上一轮返回未通过结构校验。请修正后只返回一个严格 JSON 对象；不得输出 Markdown、解释或思考过程。校验提示：{lastError?.Message}";
                var content = await CompleteJsonAsync(settings, attemptInstructions, serializedPayload, cancellationToken);
                try
                {
                    var result = DeserializeCompatibleJson<T>(content);
                    if (result is null) throw new DeepSeekException("invalid_structured_output", "AI 未返回结构化分析结果。", true);
                    var validationError = validate(result);
                    if (!string.IsNullOrWhiteSpace(validationError))
                        throw new DeepSeekException("invalid_structured_output", validationError, true);
                    return result;
                }
                catch (DeepSeekException error) when (error.Code == "invalid_structured_output")
                {
                    lastError = error;
                }
                catch (Exception error)
                {
                    lastError = new DeepSeekException("invalid_structured_output", "AI 返回的结构化 JSON 无法解析。", true, error);
                }
            }
            throw lastError ?? new DeepSeekException("invalid_structured_output", "AI 返回的结构化 JSON 无法解析。", true);
        }
        finally { _analysisGate.Release(); }
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
        await _analysisGate.WaitAsync(cancellationToken);
        try { return await AnalyzeLeadCoreAsync(lead, cancellationToken); }
        finally { _analysisGate.Release(); }
    }

    private async Task<Lead> AnalyzeLeadCoreAsync(Lead lead, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.DeepSeekModel)) throw new DeepSeekException("model_not_selected", "请先从自动拉取的模型列表中选择一个模型。", false);
        var runId = Guid.NewGuid().ToString("N");
        var requestedAt = lead.AnalysisRequestedAt;
        LeadScoringService.ResetToAiBaseline(lead, "AI 正在分析客户资料与 WhatsApp 行为", "等待本次 AI 分析完成。");
        lead.AnalysisStatus = AnalysisStatus.Running; lead.AnalysisError = "";
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.SaveAnalysisRunAsync(runId, lead.Id, "running", settings.DeepSeekModel, null, null, cancellationToken);
        try
        {
            var recentMessages = (await _repository.GetWhatsAppMessagesForLeadAsync(lead, 80, cancellationToken))
                .Where(message => !message.IsStatusUpdate)
                .ToList();
            var payload = new
            {
                lead = new
                {
                    lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.EstimatedOrderValue, lead.Currency,
                    lead.CompanyScale, lead.PurchasePower, lead.ExplicitDemand, lead.RegisteredOrConsulted,
                    lead.Source, lead.Tags, lead.Owner, lead.CustomFields, stage = lead.Stage.ToString(), lead.LatestMessage
                },
                whatsapp = new
                {
                    recentMessages = recentMessages.Select(message => new
                    {
                        direction = message.Direction == WhatsAppMessageDirection.Incoming ? "customer" : "seller",
                        timestamp = message.Timestamp,
                        message.Kind,
                        message.Body
                    })
                },
                scoring_contract = new
                {
                    version = LeadIntelligenceContract.Version,
                    dimension_weights = LeadScoringService.Weights,
                    behavior_signal_range = new[] { LeadIntelligenceContract.BehaviorSignalMinimum, LeadIntelligenceContract.BehaviorSignalMaximum },
                    grade_rules = new { A = ">=80", B = "60-79", C = "40-59", D = "<40" },
                    final_score_formula = "clamp(base_profile_score + behavior_signal_score, 0, 100)"
                }
            };
            var instructions = """
                You are AI Sales OS's auditable B2B Lead Intelligence V2 analyst. Use only the supplied CRM/import data and WhatsApp message history.
                Return exactly one JSON object without markdown. Never use keyword matching as a scoring rule and never invent missing evidence.

                Required JSON shape (all property names are exact):
                {
                  "contract_version": 2,
                  "lead_score": 0,
                  "base_profile_score": 0,
                  "behavior_signal_score": 0,
                  "grade": "D",
                  "dimension_scores": {
                    "paid_marketing_willingness": 0,
                    "supply_stability": 0,
                    "ecommerce_foundation": 0,
                    "private_traffic": 0,
                    "existing_sales": 0,
                    "materials_readiness": 0
                  },
                  "dimension_evidence": {
                    "paid_marketing_willingness": { "reason": "", "evidence": [""] },
                    "supply_stability": { "reason": "", "evidence": [""] },
                    "ecommerce_foundation": { "reason": "", "evidence": [""] },
                    "private_traffic": { "reason": "", "evidence": [""] },
                    "existing_sales": { "reason": "", "evidence": [""] },
                    "materials_readiness": { "reason": "", "evidence": [""] }
                  },
                  "behavior_signals": ["requested quotation"],
                  "behavior_signal_details": [{ "signal": "requested quotation", "score": 10, "evidence": "exact message excerpt" }],
                  "customer_profile": "",
                  "customer_segment": "",
                  "stage": "new",
                  "confidence": 0.0,
                  "purchase_probability": 0,
                  "next_action": "",
                  "risk_warning": ""
                }

                Dimension maxima are exactly those in scoring_contract.dimension_weights. base_profile_score must equal the six dimension scores.
                behavior_signal_score must be an integer from -20 to +20 and equal the sum of behavior_signal_details[].score.
                behavior_signals must list the same signal names as behavior_signal_details. Use both arrays empty when the behavior score is zero.
                Positive WhatsApp evidence may include asking price or MOQ (+5), providing purchase quantity or requesting a quotation/cooperation (+10).
                Negative evidence may include prolonged non-response (-5), price-only inquiry without intent (-5), or explicit rejection (-15); interpret full context, not words alone.
                lead_score must equal clamp(base_profile_score + behavior_signal_score, 0, 100). Grade: A>=80, B=60..79, C=40..59, D<40.
                Every dimension must contain a non-empty Chinese reason and at least one evidence string. For a zero score, explicitly state that the supplied input contains no evidence.
                stage must be one of new, contacted, interested, requirement_confirmed, quotation, negotiation, waiting, customer, repeat_purchase, lost. Do not change stage without evidence.
                purchase_probability must be an integer from 0 to 100. It is a forward-looking opportunity estimate, not the same as lead_score.
                Use supplied purchase intent, decision timing, quantity, budget, objections and engagement evidence. When evidence is insufficient, return 0 and explain the gap.
                Answer customer_profile, customer_segment, reasons, next_action and risk_warning in Simplified Chinese. Keep message excerpts in their original language.
                """;
            LeadAnalysis? analysis = null;
            var analysisAccepted = false;
            DeepSeekException? lastContractError = null;
            var serializedPayload = Infrastructure.Json.Serialize(payload);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var attemptInstructions = attempt == 0
                    ? instructions
                    : $"{instructions}\n\n上一轮输出未通过 Lead Intelligence V2 校验。请严格补齐六个维度、证据、画像、风险和下一步；未知信息应给 0 分并明确写无可验证证据。校验提示：{lastContractError?.Message}";
                var content = await CompleteJsonAsync(settings, attemptInstructions, serializedPayload, cancellationToken);
                try
                {
                    analysis = ParseAnalysis(content, lead);
                    Validate(analysis);
                    analysisAccepted = true;
                    break;
                }
                catch (DeepSeekException error) when (error.Code == "invalid_structured_output")
                {
                    lastContractError = error;
                }
            }
            if (!analysisAccepted || analysis is null)
                throw lastContractError ?? new DeepSeekException("invalid_structured_output", "AI 未返回 Lead Intelligence V2 结果。", true);
            var target = await _repository.GetLeadAsync(lead.Id, cancellationToken) ?? lead;
            target.Score = analysis.Score; target.Grade = analysis.Grade; target.AnalysisContractVersion = analysis.ContractVersion;
            target.BaseProfileScore = analysis.BaseProfileScore; target.BehaviorSignalScore = analysis.BehaviorSignalScore;
            target.ScoreBreakdown = analysis.Factors.ToDictionary(f => f.Key, f => f.Score);
            target.ScoreReasons = analysis.Factors.Select(f => f.Rationale).ToList();
            target.ScoreFactors = analysis.Factors; target.BehaviorSignals = analysis.BehaviorSignals;
            target.Stage = analysis.Stage; target.AnalysisConfidence = analysis.Confidence; target.Evidence = analysis.Evidence;
            target.PurchaseProbability = analysis.PurchaseProbability;
            target.ProfileSummary = analysis.ProfileSummary; target.CustomerSegment = analysis.CustomerSegment; target.NextAction = analysis.NextAction;
            target.RiskWarning = analysis.RiskWarning; target.Risks = analysis.Risks;
            target.LatestReplySignals = analysis.BehaviorSignals.Select(signal => $"{signal.Signal} ({signal.Score:+#;-#;0})").ToList();
            target.AnalysisStatus = AnalysisStatus.Succeeded; target.AnalysisError = ""; target.AiScoreApplied = true; target.LastAnalyzedAt = DateTimeOffset.Now;
            await _repository.UpsertLeadAsync(target, cancellationToken);
            await _repository.SaveAnalysisRunAsync(runId, lead.Id, "succeeded", settings.DeepSeekModel, analysis, null, cancellationToken);
            await _repository.LogEventAsync("lead_analyzed", lead.Id, null, $"provider=compatible; model={settings.DeepSeekModel}; trigger={target.AnalysisTrigger}", cancellationToken);
            return target;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var target = await _repository.GetLeadAsync(lead.Id, CancellationToken.None) ?? lead;
            LeadScoringService.ResetToAiBaseline(target, "AI 批量分析已由用户取消", "可再次运行批量分析或重试。");
            target.AnalysisStatus = AnalysisStatus.RetryableFailed;
            target.AnalysisError = "用户取消了本次 AI 分析，可重试。";
            target.LastAnalyzedAt = null;
            await _repository.UpsertLeadAsync(target, CancellationToken.None);
            await _repository.SaveAnalysisRunAsync(runId, lead.Id, "cancelled", settings.DeepSeekModel, null, target.AnalysisError, CancellationToken.None);
            throw;
        }
        catch (Exception error)
        {
            var safe = error is DeepSeekException dse ? $"{dse.Code}: {dse.Message}" : "AI 返回内容无法验证，请重试。";
            var target = await _repository.GetLeadAsync(lead.Id, cancellationToken) ?? lead;
            var hasNewerRequest = target.AnalysisRequestedAt is not null && (requestedAt is null || target.AnalysisRequestedAt > requestedAt);
            LeadScoringService.ResetToAiBaseline(target, "本次 AI 分析失败，客户资料已保留", "检查 AI 配置后重试分析。");
            target.AnalysisStatus = hasNewerRequest ? AnalysisStatus.Queued : AnalysisStatus.RetryableFailed;
            target.AnalysisError = hasNewerRequest ? $"{safe} 新回复已重新排队。" : safe;
            target.LastAnalyzedAt = null;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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

    private static LeadAnalysis ParseAnalysis(string content, Lead lead)
    {
        try
        {
            var output = DeserializeCompatibleJson<LeadAnalysisOutput>(content)
                ?? throw new JsonException("Empty analysis output");
            if ((output.DimensionScores?.Count ?? 0) == 0 && (output.DimensionEvidence?.Count ?? 0) == 0)
                throw new JsonException("Missing Lead Intelligence dimensions");

            var factors = LeadScoringService.Weights.Select(weight =>
            {
                var requestedScore = output.DimensionScores?.GetValueOrDefault(weight.Key) ?? 0;
                var detail = output.DimensionEvidence?.GetValueOrDefault(weight.Key);
                var evidence = detail?.Evidence?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct().ToList() ?? [];
                var rationale = detail?.Reason?.Trim() ?? "";
                var score = Math.Clamp(requestedScore, 0, weight.Value);
                if (score > 0 && (string.IsNullOrWhiteSpace(rationale) || evidence.Count == 0))
                {
                    score = 0;
                    rationale = "AI 未提供可核验证据，本维度不计分。";
                    evidence = ["当前输入未提供可验证证据"];
                }
                else if (score == 0)
                {
                    if (string.IsNullOrWhiteSpace(rationale)) rationale = "当前输入未提供该维度的可验证证据。";
                    if (evidence.Count == 0) evidence.Add("当前输入未提供可验证证据");
                }
                return new LeadFactor
                {
                    Key = weight.Key,
                    Score = score,
                    MaxScore = weight.Value,
                    Rationale = rationale,
                    Evidence = evidence
                };
            }).ToList();

            var behaviorSignals = new List<LeadBehaviorSignal>();
            var behaviorTotal = 0;
            foreach (var item in output.BehaviorSignalDetails ?? [])
            {
                var signal = item.Signal?.Trim() ?? "";
                var evidenceText = item.Evidence?.Trim() ?? "";
                var score = Math.Clamp(item.Score, LeadIntelligenceContract.BehaviorSignalMinimum, LeadIntelligenceContract.BehaviorSignalMaximum);
                if (score == 0 || string.IsNullOrWhiteSpace(signal) || string.IsNullOrWhiteSpace(evidenceText)) continue;
                if (behaviorTotal + score is < LeadIntelligenceContract.BehaviorSignalMinimum or > LeadIntelligenceContract.BehaviorSignalMaximum) continue;
                behaviorSignals.Add(new LeadBehaviorSignal { Signal = signal, Score = score, Evidence = evidenceText });
                behaviorTotal += score;
            }
            var evidence = factors.SelectMany(factor => factor.Evidence.Select(value => new AnalysisEvidence
                { Field=factor.Key, Value=value, Interpretation=factor.Rationale }))
                .Concat(behaviorSignals.Select(signal => new AnalysisEvidence
                    { Field="whatsapp_behavior", Value=signal.Evidence, Interpretation=$"{signal.Signal} ({signal.Score:+#;-#;0})" }))
                .ToList();
            var stageText = output.Stage?.Trim();
            var validStages = new[] { "new", "contacted", "interested", "requirement_confirmed", "quotation", "negotiation", "waiting", "customer", "repeat_purchase", "lost" };
            var stage = stageText is not null && validStages.Contains(stageText, StringComparer.OrdinalIgnoreCase)
                ? StageParser.Parse(stageText)
                : lead.Stage;
            var baseScore = factors.Sum(factor => factor.Score);
            var finalScore = Math.Clamp(baseScore + behaviorTotal, 0, 100);
            var profile = string.IsNullOrWhiteSpace(output.CustomerProfile)
                ? $"{lead.DisplayName}{(string.IsNullOrWhiteSpace(lead.Country) ? "" : $"，来自{lead.Country}")}；当前可验证经营与采购信息有限。"
                : output.CustomerProfile.Trim();
            var segment = string.IsNullOrWhiteSpace(output.CustomerSegment) ? "待补充信息客户" : output.CustomerSegment.Trim();
            var nextAction = string.IsNullOrWhiteSpace(output.NextAction)
                ? "优先补充经营模式、采购需求、预算与时间计划，并核对 WhatsApp 原始回复。"
                : output.NextAction.Trim();
            var riskWarning = string.IsNullOrWhiteSpace(output.RiskWarning)
                ? "当前可验证信息有限，结论置信度较低，需人工核验。"
                : output.RiskWarning.Trim();
            return new LeadAnalysis
            {
                ContractVersion=LeadIntelligenceContract.Version,
                Score=finalScore,
                BaseProfileScore=baseScore,
                BehaviorSignalScore=behaviorTotal,
                Grade=LeadScoringService.GradeFromScore(finalScore), Factors=factors, BehaviorSignals=behaviorSignals, Stage=stage,
                Confidence=Math.Clamp(output.Confidence, 0, 1), PurchaseProbability=Math.Clamp(output.PurchaseProbability, 0, 100),
                Evidence=evidence, ProfileSummary=profile,
                CustomerSegment=segment, NextAction=nextAction,
                RiskWarning=riskWarning, Risks=string.IsNullOrWhiteSpace(riskWarning) ? [] : [riskWarning]
            };
        }
        catch (Exception error) { throw new DeepSeekException("invalid_structured_output", "AI 分析 JSON 解析失败。", true, error); }
    }

    private static void Validate(LeadAnalysis analysis)
    {
        if (analysis.ContractVersion != LeadIntelligenceContract.Version)
            throw new DeepSeekException("invalid_structured_output", "AI 未返回 Lead Intelligence V2 契约。", true);
        if (analysis.Factors.Count != LeadScoringService.Weights.Count || analysis.Factors.Select(x => x.Key).Distinct().Count() != LeadScoringService.Weights.Count)
            throw new DeepSeekException("invalid_structured_output", "分析必须包含 6 个唯一 V2 评分维度。", true);
        foreach (var factor in analysis.Factors)
            if (!LeadScoringService.Weights.TryGetValue(factor.Key, out var max) || factor.MaxScore != max || factor.Score < 0 || factor.Score > max ||
                string.IsNullOrWhiteSpace(factor.Rationale) || factor.Evidence.Count == 0 || factor.Evidence.Any(string.IsNullOrWhiteSpace))
                throw new DeepSeekException("invalid_structured_output", $"评分维度 {factor.Key} 的分数、原因或证据无效。", true);
        if (analysis.Factors.Sum(x => x.Score) != analysis.BaseProfileScore || analysis.BaseProfileScore is < 0 or > 100)
            throw new DeepSeekException("invalid_structured_output", "基础画像分与六维分数不一致。", true);
        if (analysis.BehaviorSignalScore is < LeadIntelligenceContract.BehaviorSignalMinimum or > LeadIntelligenceContract.BehaviorSignalMaximum ||
            analysis.BehaviorSignals.Sum(signal => signal.Score) != analysis.BehaviorSignalScore ||
            analysis.BehaviorSignals.Any(signal => signal.Score == 0 || signal.Score is < LeadIntelligenceContract.BehaviorSignalMinimum or > LeadIntelligenceContract.BehaviorSignalMaximum || string.IsNullOrWhiteSpace(signal.Signal) || string.IsNullOrWhiteSpace(signal.Evidence)))
            throw new DeepSeekException("invalid_structured_output", "WhatsApp 行为修正分与行为证据不一致。", true);
        var expectedScore = Math.Clamp(analysis.BaseProfileScore + analysis.BehaviorSignalScore, 0, 100);
        if (analysis.Score != expectedScore || LeadScoringService.GradeFromScore(analysis.Score) != analysis.Grade)
            throw new DeepSeekException("invalid_structured_output", "最终分、行为修正分与等级不一致。", true);
        if (analysis.PurchaseProbability is < 0 or > 100)
            throw new DeepSeekException("invalid_structured_output", "AI \u91c7\u8d2d\u6982\u7387\u5fc5\u987b\u4ecb\u4e8e 0 \u81f3 100\u3002", true);
        if (analysis.Confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(analysis.ProfileSummary) || string.IsNullOrWhiteSpace(analysis.CustomerSegment) ||
            string.IsNullOrWhiteSpace(analysis.NextAction) || string.IsNullOrWhiteSpace(analysis.RiskWarning))
            throw new DeepSeekException("invalid_structured_output", "分析缺少画像、分组、风险或下一步动作。", true);
    }

    private static T? DeserializeCompatibleJson<T>(string content) where T : class
    {
        try
        {
            var normalized = NormalizeJsonKeys(ExtractJson(content));
            return JsonSerializer.Deserialize<T>(normalized, CompatibleJsonOptions);
        }
        catch (DeepSeekException) { throw; }
        catch (Exception error) { throw new DeepSeekException("invalid_structured_output", "AI 返回的结构化 JSON 无法解析。", true, error); }
    }

    private static string NormalizeJsonKeys(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return NormalizeNode(node)?.ToJsonString(Infrastructure.Json.Options) ?? "null";
    }

    private static JsonNode? NormalizeNode(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.Select(item => KeyValuePair.Create(ShouldPreserveJsonKey(item.Key) ? item.Key : ToCamelCase(item.Key), NormalizeNode(item.Value)))),
        JsonArray value => new JsonArray(value.Select(NormalizeNode).ToArray()),
        null => null,
        _ => node.DeepClone()
    };

    private static string ToCamelCase(string key)
    {
        if (!key.Contains('_')) return key;
        var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return key;
        return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static bool ShouldPreserveJsonKey(string key) => LeadScoringService.Weights.ContainsKey(key);

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstLine = trimmed.IndexOf('\n'); var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
        }
        var start = trimmed.IndexOf('{');
        if (start < 0) throw new DeepSeekException("invalid_structured_output", "AI Provider 未返回 JSON 对象。", true);
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') { inString = true; continue; }
            if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return trimmed[start..(index + 1)];
        }
        throw new DeepSeekException("invalid_structured_output", "AI Provider 返回的 JSON 对象不完整。", true);
    }

    private sealed class LeadAnalysisOutput
    {
        public int ContractVersion { get; set; }
        public int LeadScore { get; set; }
        public int BaseProfileScore { get; set; }
        public int BehaviorSignalScore { get; set; }
        public string Grade { get; set; } = "D";
        public Dictionary<string, int>? DimensionScores { get; set; }
        public Dictionary<string, LeadDimensionEvidenceOutput>? DimensionEvidence { get; set; }
        public List<string>? BehaviorSignals { get; set; }
        public List<LeadBehaviorOutput>? BehaviorSignalDetails { get; set; }
        public string CustomerProfile { get; set; } = "";
        public string CustomerSegment { get; set; } = "";
        public string Stage { get; set; } = "";
        public double Confidence { get; set; }
        public int PurchaseProbability { get; set; }
        public string NextAction { get; set; } = "";
        public string RiskWarning { get; set; } = "";
    }

    private sealed class LeadDimensionEvidenceOutput
    {
        public string Reason { get; set; } = "";
        public List<string>? Evidence { get; set; }
    }

    private sealed class LeadBehaviorOutput
    {
        public string Signal { get; set; } = "";
        public int Score { get; set; }
        public string Evidence { get; set; } = "";
    }

    private sealed class GeneratedDraft
    {
        public string Purpose { get; set; } = ""; public string Language { get; set; } = ""; public string Body { get; set; } = "";
        public List<string>? Rationale { get; set; } public List<string>? Assumptions { get; set; } public List<string>? Risks { get; set; }
    }
}
