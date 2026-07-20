using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static class PhoneIdentity
{
    public static string Digits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());

    public static bool IsMatch(string? first, string? second)
    {
        var left = Digits(first);
        var right = Digits(second);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left.Equals(right, StringComparison.Ordinal)) return true;

        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length > right.Length ? left : right;
        return shorter.Length >= 8
            && longer.Length - shorter.Length <= 4
            && longer.EndsWith(shorter, StringComparison.Ordinal);
    }

    public static Lead? FindUniqueLead(IEnumerable<Lead> leads, string? phone)
    {
        var target = Digits(phone);
        if (target.Length == 0) return null;

        var candidates = leads
            .SelectMany(lead => LeadPhoneCandidates(lead).Select(candidate => new { Lead = lead, Phone = candidate }))
            .Where(item => IsMatch(item.Phone, target))
            .Select(item => new { item.Lead, Difference = Math.Abs(item.Phone.Length - target.Length) })
            .OrderBy(item => item.Difference)
            .ToList();
        if (candidates.Count == 0) return null;

        var bestDifference = candidates[0].Difference;
        var best = candidates.Where(item => item.Difference == bestDifference).Select(item => item.Lead).DistinctBy(lead => lead.Id).ToList();
        return best.Count == 1 ? best[0] : null;
    }

    public static IEnumerable<string> LeadPhoneCandidates(Lead lead)
    {
        var values = new List<string> { lead.PhoneE164 };
        values.AddRange(lead.CustomFields
            .Where(field => IsPhoneField(field.Key))
            .Select(field => field.Value));
        return values.Select(Digits).Where(value => value.Length >= 8).Distinct(StringComparer.Ordinal);
    }

    private static bool IsPhoneField(string name)
    {
        var normalized = name.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("whatsapp", StringComparison.Ordinal)
            || normalized.Contains("电话", StringComparison.Ordinal)
            || normalized.Contains("手机号", StringComparison.Ordinal)
            || normalized.Contains("手机号码", StringComparison.Ordinal)
            || normalized.Contains("联系电话", StringComparison.Ordinal);
    }
}
