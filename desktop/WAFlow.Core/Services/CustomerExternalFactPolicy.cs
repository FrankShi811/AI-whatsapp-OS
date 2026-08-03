using System.Security.Cryptography;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

/// <summary>
/// Defines the one authoritative gate for public-web facts consumed by AI.
/// Facts remain in the audit/history tables, but only facts from the customer's
/// current identity revision can be materialized into a current decision.
/// </summary>
public static class CustomerExternalFactPolicy
{
    private static readonly HashSet<string> SingleValueFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "job_title", "public_role", "role", "title",
        "company_size", "employee_count", "employees",
        "company_name", "legal_name", "official_name",
        "website", "official_website", "company_website", "company_domain", "domain",
        "email", "business_email", "phone", "business_phone",
        "country", "location", "headquarters", "headquarter",
        "annual_revenue", "revenue", "founded_year", "founded",
        "linkedin_profile", "ownership"
    };

    public static async Task<List<CustomerEnrichmentFact>> GetCurrentFactsAsync(
        LocalRepository repository,
        string customerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var lead = await repository.GetLeadAsync(customerId, cancellationToken);
        var jobs = await repository.GetCustomerEnrichmentJobsAsync(customerId, cancellationToken);
        var facts = await repository.GetCustomerEnrichmentFactsAsync(
            customerId,
            latestPerValue: false,
            cancellationToken);
        return BuildDependency(lead, jobs, facts, now).ActiveFacts;
    }

    public static async Task<List<CustomerEnrichmentFact>> GetFactsForCurrentIdentityAsync(
        LocalRepository repository,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var identityHash = await GetCurrentIdentityHashAsync(repository, customerId, cancellationToken);
        if (identityHash.Length == 0) return [];
        var currentJobIds = (await repository.GetCustomerEnrichmentJobsAsync(customerId, cancellationToken))
            .Where(job => job.IdentityHash.Equals(identityHash, StringComparison.Ordinal))
            .Select(job => job.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.Now;
        return (await repository.GetCustomerEnrichmentFactsAsync(
                customerId,
                latestPerValue: false,
                cancellationToken))
            .Where(fact => currentJobIds.Contains(fact.JobId))
            .GroupBy(fact => $"{fact.FieldType}|{fact.NormalizedValue}", StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveDisplayLifecycle(group, now))
            .OrderByDescending(fact => fact.UpdatedAt)
            .ToList();
    }

    public static async Task<string> GetCurrentIdentityHashAsync(
        LocalRepository repository,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var lead = await repository.GetLeadAsync(customerId, cancellationToken);
        return lead is null ? "" : CustomerEnrichmentIdentityService.Build(lead).IdentityHash;
    }

    /// <summary>
    /// Captures the exact identity revision and active public-fact set consumed by
    /// an AI run. The identity is always part of the hash, including when the
    /// active fact set is empty.
    /// </summary>
    public static async Task<CustomerExternalFactDependencySnapshot> CaptureDependencyAsync(
        LocalRepository repository,
        string customerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var lead = await repository.GetLeadAsync(customerId, cancellationToken);
        var jobs = await repository.GetCustomerEnrichmentJobsAsync(customerId, cancellationToken);
        var allFacts = await repository.GetCustomerEnrichmentFactsAsync(
            customerId,
            latestPerValue: false,
            cancellationToken);
        return BuildDependency(lead, jobs, allFacts, now);
    }

    internal static CustomerExternalFactDependencySnapshot BuildDependency(
        Lead? lead,
        IReadOnlyCollection<CustomerEnrichmentJob> jobs,
        IReadOnlyCollection<CustomerEnrichmentFact> allFacts,
        DateTimeOffset now)
    {
        var identityHash = lead is null
            ? ""
            : CustomerEnrichmentIdentityService.Build(lead).IdentityHash;
        var currentJobIds = identityHash.Length == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : jobs
                .Where(job => job.IdentityHash.Equals(identityHash, StringComparison.Ordinal))
                .Select(job => job.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligible = allFacts
            .Where(fact => currentJobIds.Contains(fact.JobId))
            .GroupBy(fact => $"{fact.FieldType}|{fact.NormalizedValue}", StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveActiveLifecycle(group, now))
            .Where(fact => fact is not null)
            .Cast<CustomerEnrichmentFact>()
            .ToList();
        var facts = new List<CustomerEnrichmentFact>();
        foreach (var field in eligible.GroupBy(fact => fact.FieldType, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSingleValueField(field.Key))
            {
                facts.AddRange(field);
                continue;
            }

            var humanConfirmed = field
                .Where(fact => fact.VerificationStatus == CustomerEnrichmentVerificationStatus.HumanConfirmed)
                .OrderByDescending(fact => fact.UpdatedAt)
                .ThenByDescending(fact => fact.ConfidenceScore)
                .FirstOrDefault();
            if (humanConfirmed is not null)
            {
                facts.Add(humanConfirmed);
                continue;
            }

            if (field.Select(fact => fact.NormalizedValue)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 1)
                facts.Add(SelectPreferred(field));
        }
        facts = facts.OrderByDescending(fact => fact.UpdatedAt).ToList();
        var canonical = Json.Serialize(new
        {
            identityHash,
            facts = facts
                .OrderBy(fact => fact.Id, StringComparer.OrdinalIgnoreCase)
                .Select(fact => new
                {
                    fact.Id,
                    fact.FieldType,
                    fact.FieldValue,
                    fact.NormalizedValue,
                    fact.Category,
                    fact.FactType,
                    fact.ConfidenceScore,
                    fact.EvidenceQuote,
                    fact.ReviewNote,
                    fact.HumanReviewId,
                    sourceIds = fact.SourceIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                })
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new CustomerExternalFactDependencySnapshot(identityHash, facts, hash);
    }

    public static bool IsActive(CustomerEnrichmentFact fact, DateTimeOffset now) =>
        (fact.VerificationStatus is CustomerEnrichmentVerificationStatus.Verified
            or CustomerEnrichmentVerificationStatus.HumanConfirmed)
        && (fact.ExpiresAt is null || fact.ExpiresAt > now);

    public static bool HasSameMaterial(CustomerEnrichmentFact left, CustomerEnrichmentFact right) =>
        string.Equals(left.FieldType, right.FieldType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.FieldValue, right.FieldValue, StringComparison.Ordinal)
        && string.Equals(left.NormalizedValue, right.NormalizedValue, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.FactType, right.FactType, StringComparison.OrdinalIgnoreCase)
        && left.ConfidenceScore == right.ConfidenceScore
        && string.Equals(left.EvidenceQuote, right.EvidenceQuote, StringComparison.Ordinal)
        && string.Equals(left.ReviewNote, right.ReviewNote, StringComparison.Ordinal)
        && string.Equals(left.HumanReviewId, right.HumanReviewId, StringComparison.Ordinal)
        && left.SourceIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.SourceIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    public static bool HasSameFactSet(
        IReadOnlyCollection<CustomerEnrichmentFact> captured,
        IReadOnlyCollection<CustomerEnrichmentFact> active) =>
        captured.Count == active.Count
        && captured.All(oldFact => active.Any(current =>
            current.Id.Equals(oldFact.Id, StringComparison.OrdinalIgnoreCase)
            && HasSameMaterial(oldFact, current)));

    private static CustomerEnrichmentFact SelectPreferred(IEnumerable<CustomerEnrichmentFact> facts) => facts
        .OrderByDescending(fact => fact.VerificationStatus == CustomerEnrichmentVerificationStatus.HumanConfirmed)
        .ThenByDescending(fact => fact.UpdatedAt)
        .ThenByDescending(fact => fact.ConfidenceScore)
        .First();

    private static CustomerEnrichmentFact? ResolveActiveLifecycle(
        IEnumerable<CustomerEnrichmentFact> facts,
        DateTimeOffset now)
    {
        var values = facts.ToList();
        var active = values.Where(fact => IsActive(fact, now)).ToList();
        if (active.Count == 0) return null;
        var newestActive = active.Max(fact => fact.UpdatedAt);
        var newestExplicitRetirement = values
            .Where(fact => fact.VerificationStatus is CustomerEnrichmentVerificationStatus.Rejected
                or CustomerEnrichmentVerificationStatus.Outdated)
            .Select(fact => (DateTimeOffset?)fact.UpdatedAt)
            .Max();
        if (newestExplicitRetirement is not null && newestExplicitRetirement >= newestActive)
            return null;
        return SelectPreferred(active);
    }

    private static CustomerEnrichmentFact ResolveDisplayLifecycle(
        IEnumerable<CustomerEnrichmentFact> facts,
        DateTimeOffset now)
    {
        var values = facts.ToList();
        var preferred = values
            .OrderByDescending(fact => GetDisplaySelectionRank(fact, now))
            .ThenByDescending(fact => fact.UpdatedAt)
            .First();
        var newestActive = values
            .Where(fact => IsActive(fact, now))
            .Select(fact => (DateTimeOffset?)fact.UpdatedAt)
            .Max();
        var newestRetired = values
            .Where(fact => fact.VerificationStatus is CustomerEnrichmentVerificationStatus.Rejected
                or CustomerEnrichmentVerificationStatus.Outdated)
            .OrderByDescending(fact => fact.UpdatedAt)
            .FirstOrDefault();
        return newestRetired is not null
               && (newestActive is null || newestRetired.UpdatedAt >= newestActive)
            ? newestRetired
            : preferred;
    }

    private static int GetDisplaySelectionRank(CustomerEnrichmentFact fact, DateTimeOffset now)
    {
        var current = fact.ExpiresAt is null || fact.ExpiresAt > now;
        return fact.VerificationStatus switch
        {
            CustomerEnrichmentVerificationStatus.HumanConfirmed when current => 600,
            CustomerEnrichmentVerificationStatus.Verified when current => 500,
            CustomerEnrichmentVerificationStatus.LikelyMatch => 400,
            CustomerEnrichmentVerificationStatus.PossibleMatch => 300,
            CustomerEnrichmentVerificationStatus.Conflicting => 200,
            CustomerEnrichmentVerificationStatus.HumanConfirmed => 120,
            CustomerEnrichmentVerificationStatus.Verified => 100,
            CustomerEnrichmentVerificationStatus.Outdated => 50,
            _ => 0
        };
    }

    private static bool IsSingleValueField(string fieldType)
    {
        var normalized = fieldType.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        return SingleValueFields.Contains(normalized)
               || normalized.EndsWith("_count", StringComparison.Ordinal)
               || normalized.EndsWith("_size", StringComparison.Ordinal)
               || normalized.EndsWith("_revenue", StringComparison.Ordinal)
               || normalized.EndsWith("_website", StringComparison.Ordinal)
               || normalized.EndsWith("_domain", StringComparison.Ordinal);
    }
}

public sealed record CustomerExternalFactDependencySnapshot(
    string IdentityHash,
    List<CustomerEnrichmentFact> ActiveFacts,
    string Hash);

/// <summary>
/// Reconciles a materialized Lead Intelligence score with the identity and
/// external-fact dependency captured by the run that produced it.
/// </summary>
public static class LeadIntelligenceFreshness
{
    public const string StaleReason = "客户身份或外部调查事实已变化，旧 Lead Intelligence 结果已保留在历史中，请重新分析。";

    public static async Task<Lead> EnsureCurrentAsync(
        LocalRepository repository,
        Lead lead,
        CancellationToken cancellationToken = default)
    {
        if (!lead.HasCurrentAiScore) return lead;

        var dependency = await CustomerExternalFactPolicy.CaptureDependencyAsync(
            repository,
            lead.Id,
            DateTimeOffset.Now,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(lead.AnalysisDependencyHash))
        {
            // Scores created before dependency tracking are bound once to the
            // current revision so upgrades preserve validated V2 results while
            // all later identity/fact changes become detectable.
            lead.AnalysisDependencyHash = dependency.Hash;
            await repository.UpsertLeadAsync(lead, cancellationToken);
            return lead;
        }
        if (lead.AnalysisDependencyHash.Equals(dependency.Hash, StringComparison.Ordinal))
            return lead;

        LeadScoringService.ResetToAiBaseline(
            lead,
            "客户资料已变化，等待重新运行 Lead Intelligence",
            "核对当前客户身份与外部调查事实后重新分析。");
        lead.AnalysisStatus = AnalysisStatus.RetryableFailed;
        lead.AnalysisError = StaleReason;
        lead.LastAnalyzedAt = null;
        await repository.UpsertLeadAsync(lead, cancellationToken);
        return lead;
    }
}

public static class CustomerAnalysisFreshness
{
    public const string StaleReason = "客户资料或外部调查事实已变化、冲突或过期；此版本保留为历史快照，请重新生成当前报告。";

    public static async Task<List<CustomerAnalysisReport>> SynchronizeAsync(
        LocalRepository repository,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var reports = await repository.GetCustomerAnalysisReportsAsync(customerId, cancellationToken);
        var activeFacts = await CustomerExternalFactPolicy.GetCurrentFactsAsync(
            repository,
            customerId,
            DateTimeOffset.Now,
            cancellationToken);
        var currentIdentityHash = await CustomerExternalFactPolicy.GetCurrentIdentityHashAsync(
            repository,
            customerId,
            cancellationToken);
        foreach (var report in reports.Where(report => report.Status == CustomerReportStatus.Succeeded))
        {
            var captured = report.SourceSnapshot?.VerifiedExternalFacts ?? [];
            var capturedIdentityHash = report.SourceSnapshot?.Lead is null
                ? ""
                : CustomerEnrichmentIdentityService.Build(report.SourceSnapshot.Lead).IdentityHash;
            if (currentIdentityHash.Length > 0
                && capturedIdentityHash.Equals(currentIdentityHash, StringComparison.Ordinal)
                && CustomerExternalFactPolicy.HasSameFactSet(captured, activeFacts)) continue;
            report.Status = CustomerReportStatus.Stale;
            report.Error = StaleReason;
            await repository.SaveCustomerAnalysisReportAsync(report, cancellationToken);
        }
        return reports;
    }

    public static async Task<CustomerAnalysisReport?> GetCurrentAsync(
        LocalRepository repository,
        string reportId,
        CancellationToken cancellationToken = default)
    {
        var report = await repository.GetCustomerAnalysisReportAsync(reportId, cancellationToken);
        if (report is null) return null;
        var reports = await SynchronizeAsync(repository, report.CustomerId, cancellationToken);
        return reports.FirstOrDefault(item => item.Id.Equals(reportId, StringComparison.OrdinalIgnoreCase));
    }
}
