using System.Globalization;
using System.Text.RegularExpressions;

namespace WAFlow.Core.Services;

public sealed record NormalizedPhone(string Input, string E164, bool Valid, bool CountryInferred, string? Reason);

public static partial class PhoneNormalizer
{
    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigit();

    public static NormalizedPhone Normalize(string? input, string? country)
    {
        var raw = (input ?? "").Trim().TrimStart('\'');
        if (raw.Length == 0) return new(raw, "", false, false, "missing_phone");
        var canonical = raw;
        if ((raw.Contains('e') || raw.Contains('E'))
            && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var scientific))
        {
            canonical = decimal.Round(scientific, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }
        var digits = NonDigit().Replace(canonical, "");
        // The spreadsheet is the source of truth. Country is deliberately ignored:
        // importing must never guess or prepend a dialing code that was not in the cell.
        var valid = digits.Length is >= 8 and <= 15 && digits[0] != '0';
        return new(raw, digits.Length > 0 ? "+" + digits : "", valid, false, valid ? null : "invalid_phone");
    }

    public static string BuildWaMeUrl(string phoneE164, string body)
    {
        var digits = NonDigit().Replace(phoneE164, "");
        if (digits.Length is < 8 or > 15) throw new InvalidOperationException("号码无效，不能打开 WhatsApp。");
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(body)}";
    }
}
