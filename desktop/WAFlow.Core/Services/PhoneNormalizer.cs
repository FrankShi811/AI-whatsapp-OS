using System.Text.RegularExpressions;

namespace WAFlow.Core.Services;

public sealed record NormalizedPhone(string Input, string E164, bool Valid, bool CountryInferred, string? Reason);

public static partial class PhoneNormalizer
{
    private static readonly Dictionary<string, string> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["china"]="86", ["中国"]="86", ["cn"]="86", ["italy"]="39", ["意大利"]="39", ["it"]="39",
        ["egypt"]="20", ["埃及"]="20", ["eg"]="20", ["mexico"]="52", ["墨西哥"]="52", ["mx"]="52",
        ["united kingdom"]="44", ["uk"]="44", ["gb"]="44", ["英国"]="44", ["sweden"]="46", ["瑞典"]="46", ["se"]="46",
        ["germany"]="49", ["德国"]="49", ["de"]="49", ["france"]="33", ["法国"]="33", ["fr"]="33",
        ["spain"]="34", ["西班牙"]="34", ["es"]="34", ["united states"]="1", ["usa"]="1", ["us"]="1", ["美国"]="1",
        ["canada"]="1", ["加拿大"]="1", ["ca"]="1", ["australia"]="61", ["澳大利亚"]="61", ["au"]="61",
        ["india"]="91", ["印度"]="91", ["in"]="91", ["brazil"]="55", ["巴西"]="55", ["br"]="55",
        ["saudi arabia"]="966", ["沙特阿拉伯"]="966", ["sa"]="966", ["united arab emirates"]="971", ["uae"]="971", ["ae"]="971", ["阿联酋"]="971"
    };

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigit();

    public static NormalizedPhone Normalize(string? input, string? country)
    {
        var raw = (input ?? "").Trim().TrimStart('\'');
        if (raw.Length == 0) return new(raw, "", false, false, "missing_phone");
        var hasPlus = raw.StartsWith('+');
        var digits = NonDigit().Replace(raw, "");
        var inferred = false;
        if (!hasPlus)
        {
            var key = (country ?? "").Trim();
            if (!CountryCodes.TryGetValue(key, out var prefix)) return new(raw, digits, false, false, "country_code_required");
            digits = digits.TrimStart('0');
            digits = prefix + digits;
            inferred = true;
        }
        var valid = digits.Length is >= 8 and <= 15 && digits[0] != '0';
        return new(raw, digits.Length > 0 ? "+" + digits : "", valid, inferred, valid ? null : "invalid_phone");
    }

    public static string BuildWaMeUrl(string phoneE164, string body)
    {
        var digits = NonDigit().Replace(phoneE164, "");
        if (digits.Length is < 8 or > 15) throw new InvalidOperationException("号码无效，不能打开 WhatsApp。");
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(body)}";
    }
}
