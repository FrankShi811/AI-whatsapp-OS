namespace WAFlow.Core.Services;

public sealed record WhatsAppGroupCreateRequest(string Subject, IReadOnlyList<string> ParticipantPhones)
{
    public static WhatsAppGroupCreateRequest CreateValidated(string? subject, IEnumerable<string>? participantPhones)
    {
        var cleanSubject = (subject ?? "").Trim();
        if (cleanSubject.Length is < 1 or > 100)
            throw new InvalidOperationException("群名称必须为 1–100 个字符。");

        var phones = (participantPhones ?? [])
            .Select(value => PhoneNormalizer.Normalize(value, null))
            .Where(value => value.Valid)
            .Select(value => value.E164)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (phones.Count == 0) throw new InvalidOperationException("至少选择 1 位具有有效国际号码的群成员。");
        if (phones.Count > 256) throw new InvalidOperationException("一次建群最多选择 256 位成员。");
        return new WhatsAppGroupCreateRequest(cleanSubject, phones);
    }
}

public sealed record WhatsAppGroupCreateResult(string GroupJid, string Subject, int ParticipantCount, IReadOnlyList<string> Participants);
