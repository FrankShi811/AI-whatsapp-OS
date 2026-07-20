using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static class WhatsAppReplySignalExtractor
{
    private static readonly (string Label, string[] Keywords)[] Groups =
    [
        ("价格 / 报价", ["price", "pricing", "quote", "quotation", "cost", "discount", "fob", "cif", "报价", "价格", "折扣", "多少钱"]),
        ("采购数量", ["qty", "quantity", "units", "pieces", "pcs", "moq", "order", "数量", "采购", "下单", "起订量"]),
        ("交期 / 物流", ["lead time", "delivery", "shipping", "ship", "freight", "arrival", "交期", "发货", "物流", "运费", "到货"]),
        ("样品 / 目录", ["sample", "catalog", "catalogue", "brochure", "样品", "目录", "选品"]),
        ("明确需求", ["need", "require", "looking for", "interested", "want", "需求", "需要", "感兴趣", "想要"]),
        ("积极回复", ["yes", "sure", "great", "good", "okay", "ok", "thanks", "thank you", "可以", "好的", "谢谢", "没问题"]),
        ("等待 / 延后", ["later", "wait", "next week", "next month", "not now", "稍后", "等等", "下周", "下个月", "暂时"]),
        ("异议 / 拒绝", ["no", "not interested", "too expensive", "stop", "don't", "do not", "不需要", "没兴趣", "太贵", "停止", "不要联系"])
    ];

    public static List<string> Extract(string? reply) => ExtractReplies(string.IsNullOrWhiteSpace(reply) ? [] : [reply]);

    public static List<string> Extract(IEnumerable<WhatsAppMessage> messages) =>
        ExtractReplies(messages
            .Where(message => message.Direction == WhatsAppMessageDirection.Incoming && !string.IsNullOrWhiteSpace(message.Body))
            .Select(message => message.Body));

    private static List<string> ExtractReplies(IEnumerable<string> replies)
    {
        var text = string.Join('\n', replies).ToLowerInvariant();
        if (text.Length == 0) return [];
        return Groups
            .Where(group => group.Keywords.Any(keyword => ContainsKeyword(text, keyword)))
            .Select(group => group.Label)
            .ToList();
    }

    private static bool ContainsKeyword(string text, string keyword)
    {
        if (keyword.Any(character => character > 127) || keyword.Contains(' '))
            return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        var start = 0;
        while ((start = text.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var end = start + keyword.Length;
            var after = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
            start = end;
        }
        return false;
    }
}
