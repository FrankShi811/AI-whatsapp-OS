using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static class BuyerIdentity
{
    private static readonly HashSet<string> FieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "buyerid",
        "buyeridentifier",
        "buyeraccountid",
        "dhgatebuyerid",
        "customerid",
        "customeridentifier",
        "买家id",
        "客户id",
        "采购商id",
        "买家编号",
        "客户编号",
        "采购商编号"
    };

    public static string Canonicalize(string? value) => (value ?? "").Trim();

    public static string Normalize(string? value) => Canonicalize(value).ToUpperInvariant();

    public static bool IsField(string? header)
    {
        var normalized = NormalizeHeader(header);
        return normalized.Length > 0 && FieldNames.Contains(normalized);
    }

    public static string FromFields(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null) return "";
        var values = fields
            .Where(pair => IsField(pair.Key))
            .Select(pair => Canonicalize(pair.Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return values.Count == 1 ? values[0] : "";
    }

    public static string Resolve(Lead lead)
    {
        var explicitValue = Canonicalize(lead.BuyerId);
        return explicitValue.Length > 0 ? explicitValue : FromFields(lead.CustomFields);
    }

    public static void Synchronize(Lead lead)
    {
        lead.BuyerId = Resolve(lead);
        foreach (var key in lead.CustomFields.Keys.Where(IsField).ToList())
            lead.CustomFields[key] = lead.BuyerId;
    }

    public static string CanonicalKey(Lead lead)
    {
        var buyerId = Normalize(Resolve(lead));
        if (buyerId.Length > 0) return $"buyer:{buyerId}";
        var phone = PhoneIdentity.Digits(lead.PhoneE164);
        return phone.Length > 0 ? $"phone:{phone}" : $"customer:{lead.Id}";
    }

    private static string NormalizeHeader(string? value) =>
        new((value ?? "")
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
}
