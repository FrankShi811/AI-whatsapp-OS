using System.Security.Cryptography;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class KnowledgeLearningService
{
    private readonly LocalRepository _repository;
    private readonly PersonalSalesLearningService _learning;

    public KnowledgeLearningService(
        LocalRepository repository,
        PersonalSalesLearningService learning)
    {
        _repository = repository;
        _learning = learning;
    }

    public async Task<List<KnowledgeCandidate>> RefreshCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var performances = await _learning.GetTopTalkTracksAsync(10, cancellationToken);
        var existing = (await _repository.GetKnowledgeCandidatesAsync(null, cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var performance in performances.Where(item => item.SentCount >= 3))
        {
            var id = StableId("talk-track", performance.Key);
            var validated = performance.SentCount >= 10 &&
                            performance.Replies >= 3 &&
                            (performance.StageProgressions >= 2 || performance.Deals >= 1);
            var candidate = existing.GetValueOrDefault(id) ?? new KnowledgeCandidate
            {
                Id = id,
                CreatedAt = DateTimeOffset.Now,
                Status = KnowledgeCandidateStatus.Proposed
            };
            candidate.Title = $"{performance.Channel} 真实互动话术 · {performance.SentCount} 次样本";
            candidate.Content = performance.TalkTrack;
            candidate.Category = KnowledgeCategory.SalesScript;
            candidate.SourceKind = validated
                ? KnowledgeSourceKind.OutcomeValidatedPractice
                : KnowledgeSourceKind.VerifiedInteractionMemory;
            candidate.EvidenceLevel = validated
                ? KnowledgeEvidenceLevel.OutcomeValidated
                : KnowledgeEvidenceLevel.PreliminaryObservation;
            candidate.Scope = new KnowledgeScope { Kind = KnowledgeScopeKind.Global };
            candidate.SampleSize = performance.SentCount;
            candidate.Replies = performance.Replies;
            candidate.StageProgressions = performance.StageProgressions;
            candidate.Conversions = performance.Deals;
            candidate.SourceIds = [performance.Key];
            candidate.ReviewNote = validated
                ? "达到结果验证门槛：至少 10 次真实发送、3 次回复，并出现至少 2 次阶段推进或 1 次成交。仍需人工审批。"
                : "样本量或业务结果尚不足，只能作为初步观察；不得描述为有效策略或成交因果。";
            await _repository.UpsertKnowledgeCandidateAsync(candidate, cancellationToken);
        }
        return await _repository.GetKnowledgeCandidatesAsync(null, cancellationToken);
    }

    public async Task<KnowledgeCandidate> ReviewAsync(
        string candidateId,
        bool approve,
        string actor = "user",
        string note = "",
        CancellationToken cancellationToken = default)
    {
        var candidate = (await _repository.GetKnowledgeCandidatesAsync(null, cancellationToken))
            .FirstOrDefault(item => item.Id == candidateId)
            ?? throw new InvalidOperationException("知识候选不存在。");
        if (candidate.Status == KnowledgeCandidateStatus.Published)
            throw new InvalidOperationException("已经发布的候选不能重新审批。");
        candidate.Status = approve ? KnowledgeCandidateStatus.Approved : KnowledgeCandidateStatus.Rejected;
        candidate.ReviewedBy = actor;
        candidate.ReviewedAt = DateTimeOffset.Now;
        if (!string.IsNullOrWhiteSpace(note)) candidate.ReviewNote = note.Trim();
        await _repository.UpsertKnowledgeCandidateAsync(candidate, cancellationToken);
        await _repository.LogEventAsync(
            approve ? "knowledge_candidate_approved" : "knowledge_candidate_rejected",
            null,
            null,
            Json.Serialize(new
            {
                candidate.Id,
                candidate.SampleSize,
                candidate.Replies,
                candidate.StageProgressions,
                candidate.Conversions,
                candidate.EvidenceLevel,
                actor
            }),
            cancellationToken);
        return candidate;
    }

    private static string StableId(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))))
            .ToLowerInvariant()[..32];
}
