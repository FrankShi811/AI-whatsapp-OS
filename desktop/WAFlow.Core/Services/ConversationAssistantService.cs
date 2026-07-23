using System.Globalization;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class ConversationAssistantService
{
    private static readonly IReadOnlyDictionary<string, string> CoreFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["company"] = "公司",
        ["country"] = "国家 / 地区",
        ["product_interest"] = "关注产品",
        ["estimated_order_value"] = "预计订单额",
        ["currency"] = "币种",
        ["preferred_language"] = "首选沟通语言",
        ["stage"] = "销售阶段",
        ["tags"] = "标签"
    };

    private static readonly string[] EnrichmentFields =
    [
        "采购数量", "采购周期", "采购预算", "目标价格", "关注产品", "价格反馈", "主要顾虑",
        "决策因素", "期望交期", "客户业务模式", "销售渠道", "合作意向", "需求优先级"
    ];

    private const string Instructions = """
        你是 AI Sales OS 的 WhatsApp 销售助理。你必须依据输入中的客户原话和 CRM 事实工作，不得臆测。

        目标：
        1. 根据最近一条客户来信和上下文，生成一条可直接发送的简洁、自然、专业回复。回复语言跟随客户最近使用的语言；不要虚构价格、库存、交期、承诺或政策。
        2. 用中文总结客户当前需求、意向、采购信号、风险和下一步动作。
        3. 只在客户原话明确支持时，提出 CRM 字段更新。field 必须逐字来自 allowedFields 的 key；evidenceQuote 必须逐字摘录客户发送的 incoming 消息。无法确认就不要提出更新。
        4. 不得根据销售人员自己发送的 outgoing 消息反推客户需求。不得改写姓名、电话、WhatsApp 号码、负责人、退订状态或 AI 分数。
        5. stage 仅允许 new、contacted、interested、negotiation、waiting、customer、lost；没有明确阶段证据时不要返回 stage 更新。

        只返回一个严格 JSON 对象，字段固定为：
        {
          "replyText":"string",
          "replyLanguage":"string",
          "needsSummary":"中文 string",
          "customerIntent":"中文 string",
          "purchaseSignals":["中文 string"],
          "risks":["中文 string"],
          "recommendedNextAction":"中文 string",
          "confidence":0.0,
          "fieldUpdates":[{"field":"allowed key","value":"string","evidenceQuote":"客户原话","reason":"中文 string"}]
        }
        """;

    private readonly LocalRepository _repository;
    private readonly IStructuredAiProvider _provider;

    public ConversationAssistantService(LocalRepository repository, IStructuredAiProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public async Task<ConversationAssistantResult> AnalyzeAsync(
        string conversationId,
        Lead? lead,
        CancellationToken cancellationToken = default)
    {
        if (!_provider.HasApiKey())
            throw new DeepSeekException("provider_not_configured", "请先完成 AI API 对接并选择模型。", false);
        var messages = (await _repository.GetWhatsAppMessagesAsync(conversationId, 160, cancellationToken))
            .Where(message => !message.IsStatusUpdate && !message.IsRevoked && !string.IsNullOrWhiteSpace(message.Body))
            .OrderBy(message => message.Timestamp)
            .TakeLast(100)
            .ToList();
        var incoming = messages.Where(message => message.Direction == WhatsAppMessageDirection.Incoming).ToList();
        if (incoming.Count == 0)
            throw new InvalidOperationException("当前会话还没有可分析的客户来信（白色气泡）。请先同步历史消息或等待客户回复。");

        var allowedFields = BuildAllowedFields(lead);
        var incomingEvidence = incoming.Select(message => message.Body).ToList();
        var payload = new
        {
            crm = lead is null ? null : new
            {
                lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.Stage, lead.Tags,
                lead.PreferredLanguage, lead.EstimatedOrderValue, lead.Currency, lead.CustomFields
            },
            allowedFields = allowedFields.Select(field => new { key = field.Key, label = field.Value, currentValue = GetCurrentValue(lead, field.Key) }),
            conversation = messages.Select(message => new
            {
                direction = message.Direction == WhatsAppMessageDirection.Incoming ? "incoming" : "outgoing",
                timestamp = message.Timestamp,
                text = message.Body
            }),
            latestIncomingMessage = incoming[^1].Body
        };

        var result = await _provider.CompleteStructuredAsync<ConversationAssistantResult>(
            Instructions,
            payload,
            candidate => Validate(candidate, allowedFields.Keys, incomingEvidence),
            cancellationToken);
        result.Model = await _provider.GetSelectedModelAsync(cancellationToken);
        result.LatestIncomingMessage = incoming[^1].Body;
        result.PurchaseSignals = CleanList(result.PurchaseSignals);
        result.Risks = CleanList(result.Risks);
        result.FieldUpdates = result.FieldUpdates
            .Where(update => allowedFields.ContainsKey(update.Field))
            .GroupBy(update => update.Field, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .ToList();
        foreach (var update in result.FieldUpdates)
        {
            update.FieldLabel = allowedFields[update.Field];
            update.CurrentValue = GetCurrentValue(lead, update.Field);
        }
        await _repository.LogEventAsync(
            "whatsapp_ai_assistant_generated",
            lead?.Id,
            null,
            Infrastructure.Json.Serialize(new
            {
                model = result.Model,
                confidence = result.Confidence,
                result.NeedsSummary,
                result.CustomerIntent,
                result.PurchaseSignals,
                result.Risks,
                result.RecommendedNextAction,
                proposals = result.FieldUpdates.Select(update => new { update.Field, update.Value, update.EvidenceQuote })
            }),
            cancellationToken);
        return result;
    }

    public async Task<Lead> ApplyAsync(
        Lead? lead,
        string phone,
        string displayName,
        ConversationAssistantResult result,
        IReadOnlyCollection<ConversationFieldUpdate> selectedUpdates,
        CancellationToken cancellationToken = default)
    {
        var isNew = lead is null;
        if (lead is null)
        {
            var normalized = PhoneNormalizer.Normalize(phone, null);
            lead = new Lead
            {
                Name = string.IsNullOrWhiteSpace(displayName) ? normalized.E164 : displayName.Trim(),
                PhoneE164 = normalized.E164,
                PhoneValid = normalized.Valid,
                Source = "WhatsApp Inbox · AI 助理",
                Stage = LeadStage.New,
                Score = 0,
                Grade = "D"
            };
        }
        lead.CustomFields = new Dictionary<string, string>(lead.CustomFields, StringComparer.OrdinalIgnoreCase);
        foreach (var update in selectedUpdates)
            ApplyField(lead, update.Field, update.Value);

        var now = DateTimeOffset.Now;
        lead.CustomFields["AI需求摘要"] = result.NeedsSummary.Trim();
        lead.CustomFields["AI意向判断"] = result.CustomerIntent.Trim();
        lead.CustomFields["AI采购信号"] = string.Join("；", CleanList(result.PurchaseSignals));
        lead.CustomFields["AI风险提醒"] = string.Join("；", CleanList(result.Risks));
        lead.CustomFields["AI建议动作"] = result.RecommendedNextAction.Trim();
        lead.CustomFields["AI最近分析模型"] = result.Model;
        lead.CustomFields["AI最近分析时间"] = now.ToString("yyyy-MM-dd HH:mm:ss");
        lead.CustomFields["AI对话证据"] = string.Join(" | ", selectedUpdates.Select(update => update.EvidenceQuote.Trim()).Where(value => value.Length > 0).Distinct().Take(8));
        lead.UpdatedAt = now;
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.LogEventAsync(
            "whatsapp_ai_assistant_crm_synced",
            lead.Id,
            null,
            Infrastructure.Json.Serialize(new
            {
                createdCustomer = isNew,
                model = result.Model,
                confidence = result.Confidence,
                needsSummary = result.NeedsSummary,
                customerIntent = result.CustomerIntent,
                purchaseSignals = result.PurchaseSignals,
                risks = result.Risks,
                recommendedNextAction = result.RecommendedNextAction,
                appliedFields = selectedUpdates.Select(update => new { update.Field, update.Value, update.EvidenceQuote, update.Reason })
            }),
            cancellationToken);
        return lead;
    }

    public static string? Validate(
        ConversationAssistantResult result,
        IEnumerable<string> allowedFieldKeys,
        IReadOnlyCollection<string> incomingMessages)
    {
        result.PurchaseSignals ??= [];
        result.Risks ??= [];
        result.FieldUpdates ??= [];
        if (string.IsNullOrWhiteSpace(result.ReplyText) || result.ReplyText.Trim().Length > 4096)
            return "replyText 必须是 1–4096 个字符的可发送回复。";
        if (string.IsNullOrWhiteSpace(result.NeedsSummary) || string.IsNullOrWhiteSpace(result.CustomerIntent) ||
            string.IsNullOrWhiteSpace(result.RecommendedNextAction))
            return "必须提供中文需求总结、客户意向和下一步动作。";
        if (result.Confidence is < 0 or > 1) return "confidence 必须在 0 到 1 之间。";
        var allowed = allowedFieldKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (result.FieldUpdates.Count > 12) return "fieldUpdates 不得超过 12 项。";
        foreach (var update in result.FieldUpdates)
        {
            if (string.IsNullOrWhiteSpace(update.Field) || !allowed.Contains(update.Field))
                return $"字段 {update.Field} 不在允许写入的 CRM 维度中。";
            if (string.IsNullOrWhiteSpace(update.Value) || string.IsNullOrWhiteSpace(update.EvidenceQuote) || string.IsNullOrWhiteSpace(update.Reason))
                return $"字段 {update.Field} 缺少值、客户原话证据或原因。";
            if (!incomingMessages.Any(message => ContainsEvidence(message, update.EvidenceQuote)))
                return $"字段 {update.Field} 的证据不是客户 incoming 原话。";
            if (update.Field.Equals("stage", StringComparison.OrdinalIgnoreCase) &&
                !new[] { "new", "contacted", "interested", "negotiation", "waiting", "customer", "lost" }.Contains(update.Value.Trim(), StringComparer.OrdinalIgnoreCase))
                return "stage 必须是约定的销售阶段枚举。";
        }
        return null;
    }

    public static string GetCurrentValue(Lead? lead, string field)
    {
        if (lead is null) return "";
        return field.ToLowerInvariant() switch
        {
            "company" => lead.Company,
            "country" => lead.Country,
            "product_interest" => lead.ProductInterest,
            "estimated_order_value" => lead.EstimatedOrderValue <= 0 ? "" : lead.EstimatedOrderValue.ToString(CultureInfo.InvariantCulture),
            "currency" => lead.Currency,
            "preferred_language" => lead.PreferredLanguage,
            "stage" => lead.Stage.ToString().ToLowerInvariant(),
            "tags" => string.Join("，", lead.Tags),
            _ => lead.CustomFields.GetValueOrDefault(field) ?? ""
        };
    }

    private static IReadOnlyDictionary<string, string> BuildAllowedFields(Lead? lead)
    {
        var fields = new Dictionary<string, string>(CoreFields, StringComparer.OrdinalIgnoreCase);
        foreach (var field in EnrichmentFields) fields.TryAdd(field, field);
        if (lead is not null)
            foreach (var key in lead.CustomFields.Keys.Where(key => !string.IsNullOrWhiteSpace(key)))
                fields.TryAdd(key.Trim(), key.Trim());
        return fields;
    }

    private static void ApplyField(Lead lead, string field, string rawValue)
    {
        var value = rawValue.Trim();
        switch (field.ToLowerInvariant())
        {
            case "company": lead.Company = value; break;
            case "country": lead.Country = value; break;
            case "product_interest": lead.ProductInterest = value; break;
            case "estimated_order_value":
                if (TryParseAmount(value, out var amount)) lead.EstimatedOrderValue = amount;
                break;
            case "currency": lead.Currency = value.ToUpperInvariant(); break;
            case "preferred_language": lead.PreferredLanguage = value; break;
            case "stage":
                if (!lead.StageManuallyLocked)
                {
                    lead.Stage = StageParser.Parse(value);
                    lead.StageSource = "ai";
                }
                break;
            case "tags":
                lead.Tags = lead.Tags.Concat(value.Split([',', '，', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                break;
            default: lead.CustomFields[field] = value; break;
        }
    }

    private static bool TryParseAmount(string value, out decimal amount)
    {
        var normalized = new string(value.Where(character => char.IsDigit(character) || character is '.' or '-').ToArray());
        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }

    private static bool ContainsEvidence(string message, string quote)
    {
        var normalizedMessage = NormalizeEvidence(message);
        var normalizedQuote = NormalizeEvidence(quote).Trim('"', '\'', '“', '”', '‘', '’');
        return normalizedQuote.Length >= 2 && normalizedMessage.Contains(normalizedQuote, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEvidence(string value)
    {
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKC).Trim())
        {
            if (char.IsWhiteSpace(character)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static List<string> CleanList(IEnumerable<string>? values) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Take(12)
        .ToList();
}
