using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PhoneNumbers;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static partial class CustomerEnrichmentIdentityService
{
    private static readonly HashSet<string> PublicEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com",
        "icloud.com", "me.com", "qq.com", "163.com", "126.com", "sina.com", "proton.me", "protonmail.com"
    };

    private static readonly IReadOnlyDictionary<string, string> Regions = BuildRegions();
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    public static CustomerEnrichmentIdentity Build(Lead lead)
    {
        var email = NormalizeEmail(lead.Email);
        var at = email.LastIndexOf('@');
        var emailUser = at > 0 ? email[..at] : "";
        var emailDomain = at > 0 ? email[(at + 1)..] : "";
        var phone = NormalizePhone(lead.PhoneE164, lead.Country);
        var digits = PhoneIdentity.Digits(phone);
        var identity = new CustomerEnrichmentIdentity
        {
            CustomerId = lead.Id,
            Name = lead.Name.Trim(),
            Company = lead.Company.Trim(),
            Country = lead.Country.Trim(),
            Language = lead.PreferredLanguage.Trim(),
            RawEmail = lead.Email.Trim(),
            Email = email,
            EmailUserName = emailUser,
            EmailDomain = emailDomain,
            IsBusinessEmail = emailDomain.Length > 0 && !PublicEmailDomains.Contains(emailDomain),
            RawPhone = lead.PhoneE164.Trim(),
            PhoneE164 = phone,
            PhoneDigits = digits,
            PhoneTail8 = digits.Length >= 8 ? digits[^8..] : ""
        };
        identity.IdentityHash = Hash(string.Join('|',
            identity.Email,
            identity.PhoneE164,
            identity.Name.ToLowerInvariant(),
            identity.Company.ToLowerInvariant(),
            identity.Country.ToLowerInvariant()));
        return identity;
    }

    public static string NormalizeEmail(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return MailAddress.TryCreate(normalized, out var parsed)
            ? parsed.Address.Trim().ToLowerInvariant()
            : "";
    }

    public static string NormalizePhone(string? value, string? country)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        try
        {
            var region = raw.StartsWith('+') ? null : ResolveRegion(country);
            if (!raw.StartsWith('+') && string.IsNullOrWhiteSpace(region)) return "";
            var parsed = PhoneUtil.Parse(raw, region);
            return PhoneUtil.IsValidNumber(parsed)
                ? PhoneUtil.Format(parsed, PhoneNumberFormat.E164)
                : "";
        }
        catch (NumberParseException)
        {
            return "";
        }
    }

    private static string? ResolveRegion(string? country)
    {
        var value = (country ?? "").Trim();
        if (value.Length == 2 && value.All(char.IsLetter)) return value.ToUpperInvariant();
        return Regions.GetValueOrDefault(value);
    }

    private static IReadOnlyDictionary<string, string> BuildRegions()
    {
        var result = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                result.TryAdd(region.TwoLetterISORegionName, region.TwoLetterISORegionName);
                result.TryAdd(region.EnglishName, region.TwoLetterISORegionName);
                result.TryAdd(region.NativeName, region.TwoLetterISORegionName);
                result.TryAdd(region.DisplayName, region.TwoLetterISORegionName);
            }
            catch (ArgumentException) { }
        }
        return result;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class CustomerEnrichmentQueryGenerator
{
    private static readonly string[] ForbiddenTerms =
    [
        "ssn", "social security", "bank account", "credit score", "password", "credential",
        "leaked database", "data breach dump", "home address", "family member", "medical record",
        "religion", "political affiliation", "sexual orientation", "家庭地址", "家庭成员", "银行账户",
        "信用信息", "密码", "凭据", "泄露数据库", "健康信息", "宗教", "政治倾向", "性取向"
    ];

    public static IReadOnlyList<string> Generate(CustomerEnrichmentIdentity identity, int maximum = 6)
    {
        var queries = new List<string>();
        void Add(string value)
        {
            var clean = value.Trim();
            if (clean.Length == 0 || IsForbidden(clean) || queries.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;
            queries.Add(clean);
        }

        if (identity.Email.Length > 0) Add(Quote(identity.Email));
        if (identity.PhoneE164.Length > 0)
        {
            Add(Quote(identity.PhoneE164));
            if (identity.PhoneDigits.Length > 0) Add(Quote(identity.PhoneDigits));
        }
        if (identity.Name.Length > 0 && identity.Company.Length > 0)
            Add($"{Quote(identity.Name)} {Quote(identity.Company)}");
        if (identity.IsBusinessEmail && identity.EmailDomain.Length > 0)
        {
            if (identity.Name.Length > 0) Add($"site:{identity.EmailDomain} {Quote(identity.Name)}");
            else Add($"site:{identity.EmailDomain} {Quote(identity.Company)}");
        }
        if (identity.Company.Length > 0)
        {
            Add($"{Quote(identity.Company)} importer distributor wholesale");
            Add($"{Quote(identity.Company)} trade show news hiring");
        }
        return queries.Take(Math.Clamp(maximum, 1, 12)).ToList();
    }

    public static bool IsForbidden(string query) => ForbiddenTerms.Any(term =>
        query.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static string HashQuery(string query) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(query.Trim().ToLowerInvariant())));

    private static string Quote(string value) => $"\"{value.Replace("\"", "", StringComparison.Ordinal).Trim()}\"";
}

public static partial class CustomerEnrichmentEntityMatcher
{
    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])", RegexOptions.IgnoreCase)]
    private static partial Regex EmailLike();

    [GeneratedRegex(@"[+()\d][+()\d\s.\-/]{6,24}[\d]")]
    private static partial Regex PhoneLike();

    public static (int Score, CustomerEnrichmentVerificationStatus Status, List<string> Reasons, List<string> Conflicts) Score(
        CustomerEnrichmentIdentity identity,
        CustomerEnrichmentSource source)
    {
        var text = string.Join(' ', source.Title, source.Snippet, source.ContentText);
        var score = 0;
        var reasons = new List<string>();
        var conflicts = new List<string>();
        var nameMatch = ContainsTerm(text, identity.Name);
        var companyMatch = ContainsTerm(text, identity.Company);
        var phones = PhoneLike().Matches(text)
            .Select(match => PhoneIdentity.Digits(match.Value))
            .Where(value => value.Length >= 7)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (identity.Email.Length > 0 && text.Contains(identity.Email, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
            reasons.Add("完整邮箱一致");
        }
        if (identity.PhoneDigits.Length >= 8 && ContainsExactPhone(text, identity.PhoneDigits))
        {
            score += 50;
            reasons.Add("完整电话号码一致");
        }
        else if (identity.PhoneTail8.Length == 8 && phones.Any(phone => phone.EndsWith(identity.PhoneTail8, StringComparison.Ordinal)))
        {
            score += 10;
            reasons.Add("电话号码末 8 位一致，仅作为候选");
        }
        if (identity.IsBusinessEmail && identity.EmailDomain.Length > 0 &&
            (source.Domain.Equals(identity.EmailDomain, StringComparison.OrdinalIgnoreCase)
             || source.Domain.EndsWith('.' + identity.EmailDomain, StringComparison.OrdinalIgnoreCase)))
        {
            score += 25;
            reasons.Add("企业邮箱域名与来源官网一致");
        }
        if (nameMatch && companyMatch)
        {
            score += 25;
            reasons.Add("姓名和公司同时一致");
        }
        else if (nameMatch)
        {
            score += 5;
            reasons.Add("只有姓名一致，仅作为候选");
        }
        if (ContainsTerm(text, identity.Country))
        {
            score += 10;
            reasons.Add("国家或地区一致");
        }
        if (companyMatch && ContainsBusinessContext(text))
        {
            score += 10;
            reasons.Add("公司与公开商业场景一致");
        }

        if (nameMatch && companyMatch && identity.Email.Length > 0)
        {
            var emails = EmailLike().Matches(text)
                .Select(match => CustomerEnrichmentIdentityService.NormalizeEmail(match.Value))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (emails.Count > 0 && !emails.Contains(identity.Email, StringComparer.OrdinalIgnoreCase))
                conflicts.Add("同名同公司页面出现不同邮箱，不能自动确认主体");
        }
        if (nameMatch && companyMatch && identity.PhoneDigits.Length >= 8
            && phones.Count > 0
            && !phones.Any(phone => phone.Equals(identity.PhoneDigits, StringComparison.Ordinal))
            && !phones.Any(phone => identity.PhoneTail8.Length == 8 && phone.EndsWith(identity.PhoneTail8, StringComparison.Ordinal)))
            conflicts.Add("同名同公司页面出现不同电话号码，不能自动确认主体");

        score = Math.Clamp(score, 0, 100);
        var status = conflicts.Count > 0
            ? CustomerEnrichmentVerificationStatus.Conflicting
            : score switch
        {
            >= 90 => CustomerEnrichmentVerificationStatus.Verified,
            >= 70 => CustomerEnrichmentVerificationStatus.LikelyMatch,
            >= 40 => CustomerEnrichmentVerificationStatus.PossibleMatch,
            _ => CustomerEnrichmentVerificationStatus.Rejected
        };
        if (conflicts.Count == 0 && reasons.Count == 1 && reasons[0].StartsWith("只有姓名", StringComparison.Ordinal))
            status = CustomerEnrichmentVerificationStatus.PossibleMatch;
        return (score, status, reasons, conflicts);
    }

    public static CustomerEnrichmentVerificationStatus FromScore(int score) => score switch
    {
        >= 90 => CustomerEnrichmentVerificationStatus.Verified,
        >= 70 => CustomerEnrichmentVerificationStatus.LikelyMatch,
        >= 40 => CustomerEnrichmentVerificationStatus.PossibleMatch,
        _ => CustomerEnrichmentVerificationStatus.Rejected
    };

    private static bool ContainsExactPhone(string text, string digits) => PhoneLike().Matches(text)
        .Select(match => PhoneIdentity.Digits(match.Value))
        .Any(candidate => candidate.Equals(digits, StringComparison.Ordinal));

    private static bool ContainsTerm(string text, string term) =>
        !string.IsNullOrWhiteSpace(term) && text.Contains(term.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static bool ContainsBusinessContext(string text) => new[]
    {
        "import", "distributor", "wholesale", "procurement", "purchasing", "trade show", "company",
        "business", "supplier", "采购", "进口", "经销", "批发", "展会", "招聘", "公司"
    }.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}
