using System.Text;

namespace WAFlow.Core.Services;

public static class WhatsAppTextEncodingRepair
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding StrictGbk;
    private static readonly string[] KnownMarkers = ["鈥", "檒", "锟", "銆", "锛", "鈻", "鍒", "瑙"];

    static WhatsAppTextEncodingRepair()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        StrictGbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string Repair(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Any(IsCjk)) return value;
        try
        {
            var repaired = StrictUtf8.GetString(StrictGbk.GetBytes(value));
            if (repaired == value || repaired.Contains('\uFFFD')) return value;

            var originalCjk = value.Count(IsCjk);
            var repairedCjk = repaired.Count(IsCjk);
            var hasKnownMarker = KnownMarkers.Any(value.Contains);
            var mixedLatinCorruption = value.Count(IsAsciiLetter) >= 2 && originalCjk <= 8 && repairedCjk < originalCjk;
            return hasKnownMarker || mixedLatinCorruption ? repaired : value;
        }
        catch (EncoderFallbackException) { return value; }
        catch (DecoderFallbackException) { return value; }
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u9FFF';
}
