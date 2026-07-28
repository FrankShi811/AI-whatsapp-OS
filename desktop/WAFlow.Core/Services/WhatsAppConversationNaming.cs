using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static class WhatsAppConversationNaming
{
    public static string Resolve(Lead? lead, string? phone, params string?[] whatsappNames)
    {
        var crmName = WhatsAppTextEncodingRepair.Repair(lead?.DisplayName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(crmName)) return crmName;

        var digits = PhoneIdentity.Digits(phone);
        foreach (var rawName in whatsappNames)
        {
            var candidate = WhatsAppTextEncodingRepair.Repair(rawName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(candidate) || IsPhoneFallback(candidate, digits)) continue;
            return candidate;
        }

        return digits.Length > 0 ? $"+{digits}" : "WhatsApp 联系人";
    }

    private static bool IsPhoneFallback(string value, string digits) =>
        digits.Length > 0 &&
        PhoneIdentity.Digits(value).Equals(digits, StringComparison.Ordinal) &&
        value.All(character => char.IsDigit(character) || character is '+' or '-' or ' ' or '(' or ')');
}
