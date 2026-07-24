using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class CustomerIdentityService
{
    private readonly LocalRepository _repository;

    public CustomerIdentityService(LocalRepository repository) => _repository = repository;

    public async Task<CustomerIdentityResolution> ResolveAsync(
        string accountId,
        string conversationId,
        string rawPhone,
        string jid = "",
        string lid = "",
        string displayName = "",
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetWhatsAppIdentityLinkAsync(accountId, conversationId, cancellationToken);
        if (existing is { IsActive: true } &&
            existing.MatchResult is CustomerIdentityMatchResult.ExactMatch or
                CustomerIdentityMatchResult.ConfirmedAliasMatch or CustomerIdentityMatchResult.UniqueInferredMatch)
        {
            return await LogAndReturnAsync(accountId, conversationId, rawPhone, new CustomerIdentityResolution
            {
                Result = existing.MatchResult,
                Method = existing.MatchMethod,
                CustomerId = existing.CustomerId,
                CandidateCustomerIds = [existing.CustomerId],
                Confidence = existing.Confidence,
                Reason = existing.ManuallyConfirmed ? "已由用户确认绑定。" : "已有有效的跨账号身份绑定。"
            }, cancellationToken);
        }

        var identities = await _repository.GetCustomerPhoneIdentitiesAsync(cancellationToken: cancellationToken);
        var normalizedJid = NormalizeProviderId(jid);
        var normalizedLid = NormalizeProviderId(lid);
        var digits = PhoneIdentity.Digits(rawPhone);
        var normalized = PhoneNormalizer.Normalize(rawPhone, null);

        var result = MatchUnique(identities,
            item => normalizedJid.Length > 0 && NormalizeProviderId(item.Jid) == normalizedJid,
            CustomerIdentityMatchMethod.ExactJid, 1.0, "WhatsApp JID 精确匹配。");
        if (result is null && normalizedLid.Length > 0)
            result = MatchUnique(identities,
                item => NormalizeProviderId(item.Lid) == normalizedLid,
                CustomerIdentityMatchMethod.ExactJid, 1.0, "WhatsApp LID 精确匹配。");
        if (result is null && normalized.Valid)
            result = MatchUnique(identities,
                item => item.ManuallyConfirmed && string.Equals(item.E164, normalized.E164, StringComparison.OrdinalIgnoreCase),
                CustomerIdentityMatchMethod.ConfirmedE164, .98, "已确认 E.164 号码精确匹配。");
        if (result is null && digits.Length > 0)
            result = MatchUnique(identities,
                item => string.Equals(item.Digits, digits, StringComparison.Ordinal),
                CustomerIdentityMatchMethod.UniqueDigitBody, .82, "完整号码数字体唯一匹配；未推断国家区号。");

        if (result is null)
        {
            var nameCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                nameCandidates = (await _repository.GetLeadsAsync(search: displayName.Trim(), cancellationToken: cancellationToken))
                    .Where(lead => lead.Name.Equals(displayName.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
                                   lead.Company.Equals(displayName.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    .Select(lead => lead.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            result = new CustomerIdentityResolution
            {
                Result = CustomerIdentityMatchResult.NoMatch,
                Method = CustomerIdentityMatchMethod.CandidateOnly,
                CandidateCustomerIds = nameCandidates,
                Confidence = 0,
                Reason = nameCandidates.Count > 0
                    ? "名称或公司只能作为人工候选，禁止自动绑定。"
                    : "没有可验证的 JID、LID、已确认 E.164 或唯一完整号码匹配。"
            };
        }

        if (result.Result is CustomerIdentityMatchResult.AmbiguousMatch or CustomerIdentityMatchResult.NoMatch or CustomerIdentityMatchResult.Conflict)
        {
            await _repository.UpsertConversationAgentStateAsync(new ConversationAgentState
            {
                CustomerId = result.CustomerId,
                AccountId = accountId,
                ConversationId = conversationId,
                Mode = ConversationAgentMode.IdentityResolutionRequired,
                StateReason = result.Reason,
                ExplicitResumeRequired = true
            }, cancellationToken);
        }
        return await LogAndReturnAsync(accountId, conversationId, rawPhone, result, cancellationToken);
    }

    public async Task<WhatsAppIdentityLink> ConfirmBindingAsync(
        string customerId,
        string accountId,
        string conversationId,
        string rawPhone,
        string jid = "",
        string lid = "",
        string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var lead = await _repository.GetLeadAsync(customerId, cancellationToken)
                   ?? throw new InvalidOperationException("待绑定客户不存在。");
        var digits = PhoneIdentity.Digits(rawPhone);
        var normalized = PhoneNormalizer.Normalize(rawPhone, null);
        var phoneIdentity = new CustomerPhoneIdentity
        {
            CustomerId = customerId,
            RawValue = rawPhone.Trim(),
            Digits = digits,
            E164 = normalized.E164,
            Jid = jid.Trim(),
            Lid = lid.Trim(),
            SourceAccountId = accountId,
            SourceConversationId = conversationId,
            ManuallyConfirmed = true,
            Confidence = 1,
            Method = CustomerIdentityMatchMethod.ManualBinding
        };
        await _repository.UpsertCustomerPhoneIdentityAsync(phoneIdentity, cancellationToken);
        var existing = await _repository.GetWhatsAppIdentityLinkAsync(accountId, conversationId, cancellationToken);
        var previousCustomerId = existing?.CustomerId ?? "";
        var link = existing ?? new WhatsAppIdentityLink
        {
            AccountId = accountId,
            ConversationId = conversationId,
            CreatedAt = DateTimeOffset.Now
        };
        var before = existing is null ? "" : Json.Serialize(existing);
        link.CustomerId = customerId;
        link.PhoneIdentityId = phoneIdentity.Id;
        link.ContactJid = jid.Trim();
        link.ContactLid = lid.Trim();
        link.MatchResult = CustomerIdentityMatchResult.ExactMatch;
        link.MatchMethod = CustomerIdentityMatchMethod.ManualBinding;
        link.Confidence = 1;
        link.ManuallyConfirmed = true;
        link.IsActive = true;
        await _repository.UpsertWhatsAppIdentityLinkAsync(link, cancellationToken);

        var global = await _repository.GetGlobalCustomerIdentityAsync(customerId, cancellationToken) ?? new GlobalCustomerIdentity
        {
            CustomerId = customerId,
            CanonicalName = lead.Name
        };
        if (!global.LinkedAccountIds.Contains(accountId, StringComparer.OrdinalIgnoreCase))
            global.LinkedAccountIds.Add(accountId);
        if (string.IsNullOrWhiteSpace(global.PrimaryAccountId)) global.PrimaryAccountId = accountId;
        await _repository.UpsertGlobalCustomerIdentityAsync(global, cancellationToken);
        if (!string.IsNullOrWhiteSpace(previousCustomerId) &&
            !string.Equals(previousCustomerId, customerId, StringComparison.OrdinalIgnoreCase))
        {
            var previousGlobal = await _repository.GetGlobalCustomerIdentityAsync(previousCustomerId, cancellationToken);
            if (previousGlobal is not null)
            {
                var activeLinks = await _repository.GetWhatsAppIdentityLinksAsync(previousCustomerId, cancellationToken);
                previousGlobal.LinkedAccountIds = activeLinks
                    .Select(item => item.AccountId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!previousGlobal.LinkedAccountIds.Contains(previousGlobal.PrimaryAccountId, StringComparer.OrdinalIgnoreCase))
                    previousGlobal.PrimaryAccountId = previousGlobal.LinkedAccountIds.FirstOrDefault() ?? "";
                await _repository.UpsertGlobalCustomerIdentityAsync(previousGlobal, cancellationToken);
            }
        }
        await _repository.UpsertConversationAgentStateAsync(new ConversationAgentState
        {
            CustomerId = customerId,
            AccountId = accountId,
            ConversationId = conversationId,
            Mode = ConversationAgentMode.SuggestOnly,
            StateReason = "用户已确认客户身份。",
            ExplicitResumeRequired = false
        }, cancellationToken);
        await _repository.SaveCustomerMergeAuditAsync(new CustomerMergeAudit
        {
            SourceCustomerId = existing?.CustomerId ?? "",
            TargetCustomerId = customerId,
            IdentityLinkId = link.Id,
            Action = "confirm_binding",
            Actor = actor,
            Reason = "用户确认跨 WhatsApp 账号客户身份。",
            BeforeJson = before,
            AfterJson = Json.Serialize(link)
        }, cancellationToken);
        return link;
    }

    public async Task DetachAsync(string accountId, string conversationId, string actor = "user", CancellationToken cancellationToken = default)
    {
        var link = await _repository.GetWhatsAppIdentityLinkAsync(accountId, conversationId, cancellationToken);
        if (link is null) return;
        var before = Json.Serialize(link);
        link.IsActive = false;
        await _repository.UpsertWhatsAppIdentityLinkAsync(link, cancellationToken);
        await _repository.SaveCustomerMergeAuditAsync(new CustomerMergeAudit
        {
            SourceCustomerId = link.CustomerId,
            IdentityLinkId = link.Id,
            Action = "detach",
            Actor = actor,
            Reason = "用户解除错误身份绑定。",
            BeforeJson = before,
            AfterJson = Json.Serialize(link)
        }, cancellationToken);
    }

    private static CustomerIdentityResolution? MatchUnique(
        IEnumerable<CustomerPhoneIdentity> identities,
        Func<CustomerPhoneIdentity, bool> predicate,
        CustomerIdentityMatchMethod method,
        double confidence,
        string reason)
    {
        var candidates = identities.Where(predicate).Select(item => item.CustomerId)
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return candidates.Count switch
        {
            0 => null,
            1 => new CustomerIdentityResolution
            {
                Result = method == CustomerIdentityMatchMethod.UniqueDigitBody
                    ? CustomerIdentityMatchResult.UniqueInferredMatch : CustomerIdentityMatchResult.ExactMatch,
                Method = method,
                CustomerId = candidates[0],
                CandidateCustomerIds = candidates,
                Confidence = confidence,
                Reason = reason
            },
            _ => new CustomerIdentityResolution
            {
                Result = CustomerIdentityMatchResult.AmbiguousMatch,
                Method = method,
                CandidateCustomerIds = candidates,
                Confidence = 0,
                Reason = $"{reason} 但命中 {candidates.Count} 位客户，必须人工确认。"
            }
        };
    }

    private async Task<CustomerIdentityResolution> LogAndReturnAsync(
        string accountId, string conversationId, string rawIdentity,
        CustomerIdentityResolution resolution, CancellationToken cancellationToken)
    {
        await _repository.SaveIdentityMatchLogAsync(new CustomerIdentityMatchLog
        {
            CustomerId = resolution.CustomerId,
            AccountId = accountId,
            ConversationId = conversationId,
            RawIdentity = rawIdentity,
            Result = resolution.Result,
            Method = resolution.Method,
            CandidateCustomerIds = resolution.CandidateCustomerIds,
            Reason = resolution.Reason,
            Confidence = resolution.Confidence
        }, cancellationToken);
        return resolution;
    }

    private static string NormalizeProviderId(string value) =>
        value.Trim().ToLowerInvariant().Replace("@c.us", "").Replace("@s.whatsapp.net", "").Replace("@lid", "");
}
