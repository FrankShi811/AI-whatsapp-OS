using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static class LeadConnectionStatus
{
    public const string CanonicalHeader = "\u5efa\u8054\u60c5\u51b5";

    public static bool ApplyFromMessage(Lead lead, WhatsAppMessage message)
    {
        if (message.IsStatusUpdate) return false;
        if (lead.LastContactAt is not null && message.Timestamp < lead.LastContactAt) return false;
        var label = message.Direction == WhatsAppMessageDirection.Incoming
            ? "\u5ba2\u6237\u5df2\u56de\u590d"
            : message.Status switch
            {
                WhatsAppMessageStatus.Read => "\u5df2\u8bfb",
                WhatsAppMessageStatus.Delivered => "\u5df2\u9001\u8fbe",
                WhatsAppMessageStatus.Sent => "\u5df2\u53d1\u9001",
                WhatsAppMessageStatus.Failed => "\u53d1\u9001\u5931\u8d25",
                _ => "\u53d1\u9001\u4e2d"
            };
        lead.LastContactAt = message.Timestamp;
        if (message.Direction == WhatsAppMessageDirection.Incoming && !string.IsNullOrWhiteSpace(message.Body)) lead.LatestMessage = message.Body;
        Apply(lead, label, message.Timestamp);
        return true;
    }

    public static void Apply(Lead lead, string status, DateTimeOffset timestamp)
    {
        var keys = lead.CustomFields.Keys.Where(IsDimension).ToList();
        if (keys.Count == 0) keys.Add(CanonicalHeader);
        var value = $"{status} \u00b7 {timestamp.LocalDateTime:yyyy-MM-dd HH:mm}";
        foreach (var key in keys) lead.CustomFields[key] = value;
    }

    public static bool IsDimension(string header)
    {
        var normalized = new string(header.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains(CanonicalHeader, StringComparison.OrdinalIgnoreCase);
    }
}
