using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class PersonalSalesLearningService
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromDays(30);
    private readonly LocalRepository _repository;

    public PersonalSalesLearningService(LocalRepository repository)
    {
        _repository = repository;
    }

    public async Task<PersonalLearningSummary> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        await ObserveSalesActionsAsync(context, cancellationToken);
        await ObserveCampaignTouchesAsync(context, cancellationToken);
        return await BuildSummaryAsync(null, context.Actions, cancellationToken);
    }

    public async Task<PersonalLearningSummary> GetCustomerSummaryAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        await ObserveSalesActionsAsync(context, cancellationToken, customerId);
        await ObserveCampaignTouchesAsync(context, cancellationToken, customerId);
        return await BuildSummaryAsync(customerId, context.Actions, cancellationToken);
    }

    public async Task<List<TalkTrackPerformance>> GetTopTalkTracksAsync(
        int limit = 3,
        CancellationToken cancellationToken = default)
    {
        var actions = await _repository.GetAllSalesActionsAsync(cancellationToken);
        var summary = await BuildSummaryAsync(null, actions, cancellationToken);
        return summary.TopTalkTracks.Take(Math.Clamp(limit, 1, 10)).ToList();
    }

    private async Task<LearningContext> LoadContextAsync(CancellationToken cancellationToken)
    {
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var actions = await _repository.GetAllSalesActionsAsync(cancellationToken);
        var campaigns = await _repository.GetCampaignsAsync(null, cancellationToken);
        return new LearningContext(
            leads.ToDictionary(item => item.Id, StringComparer.Ordinal),
            actions,
            campaigns);
    }

    private async Task ObserveSalesActionsAsync(
        LearningContext context,
        CancellationToken cancellationToken,
        string? onlyCustomerId = null)
    {
        var executed = context.Actions
            .Where(item => item.ExecutedAt is not null)
            .Where(item => onlyCustomerId is null || string.Equals(item.CustomerId, onlyCustomerId, StringComparison.Ordinal))
            .OrderBy(item => item.ExecutedAt)
            .ToList();
        foreach (var action in executed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Leads.TryGetValue(action.CustomerId, out var lead)) continue;
            var actionAt = action.ExecutedAt!.Value;
            var nextActionAt = executed
                .Where(item => string.Equals(item.CustomerId, action.CustomerId, StringComparison.Ordinal)
                    && SameChannel(item.ExecutionChannel, action.ExecutionChannel)
                    && item.ExecutedAt!.Value > actionAt)
                .Select(item => item.ExecutedAt)
                .FirstOrDefault();
            var windowEndsAt = Min(actionAt.Add(ObservationWindow), nextActionAt);
            var reply = await FindFirstReplyAsync(lead, action.ExecutionChannel, actionAt, windowEndsAt, cancellationToken);
            int? baselineRank = action.BaselineStage is null ? null : StageRank(action.BaselineStage.Value);
            var observedRank = StageRank(lead.Stage);
            var canAttributeStage = action.BaselineStage is not null
                && (DateTimeOffset.Now <= windowEndsAt || lead.UpdatedAt <= windowEndsAt);
            var stageDelta = canAttributeStage && baselineRank is not null
                ? Math.Max(0, observedRank - baselineRank.Value)
                : 0;
            var progressed = stageDelta > 0;
            var converted = canAttributeStage
                && action.BaselineStage is not (LeadStage.Customer or LeadStage.RepeatPurchase)
                && lead.Stage is LeadStage.Customer or LeadStage.RepeatPurchase;
            var repeatPurchase = canAttributeStage && lead.Stage == LeadStage.RepeatPurchase;
            var observed = reply is not null || progressed || converted || repeatPurchase;
            var feedback = new AiLearningFeedback
            {
                Id = $"system-observed-action-{action.Id}",
                CustomerId = action.CustomerId,
                RecommendationId = action.RecommendationId,
                ActionId = action.Id,
                Outcome = DescribeOutcome(reply, lead.Stage, progressed, converted, repeatPurchase),
                Helpful = observed,
                FeedbackSource = "system_observed",
                Note = "依据本地保存的实际收件消息和 CRM 阶段变化自动归因。",
                ObservationStatus = observed
                    ? LearningObservationStatus.Observed
                    : DateTimeOffset.Now > windowEndsAt
                        ? LearningObservationStatus.Expired
                        : LearningObservationStatus.Pending,
                Channel = NormalizeChannel(action.ExecutionChannel),
                ActionAt = actionAt,
                BaselineStage = action.BaselineStage,
                ObservedStage = lead.Stage,
                Replied = reply is not null,
                FirstReplyAt = reply?.Timestamp,
                ReplyLatencyMinutes = reply is null ? null : Math.Round((reply.Timestamp - actionAt).TotalMinutes, 1),
                StageProgressed = progressed,
                StageDelta = stageDelta,
                Converted = converted,
                RepeatPurchase = repeatPurchase,
                TalkTrack = CleanTalkTrack(action.ExecutedContent),
                SourceMessageId = reply?.SourceId ?? "",
                ObservationWindowEndsAt = windowEndsAt,
                ObservedAt = DateTimeOffset.Now,
                CreatedAt = actionAt
            };
            await _repository.SaveAiLearningFeedbackAsync(feedback, cancellationToken);
        }
    }

    private async Task ObserveCampaignTouchesAsync(
        LearningContext context,
        CancellationToken cancellationToken,
        string? onlyCustomerId = null)
    {
        var touches = new List<CampaignTouch>();
        foreach (var campaign in context.Campaigns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
            touches.AddRange(recipients
                .Where(item => item.Status == CampaignRecipientStatus.Sent && item.SentAt is not null)
                .Where(item => onlyCustomerId is null || string.Equals(item.LeadId, onlyCustomerId, StringComparison.Ordinal))
                .Select(item => new CampaignTouch(campaign, item)));
        }

        foreach (var touch in touches.OrderBy(item => item.Recipient.SentAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Leads.TryGetValue(touch.Recipient.LeadId, out var lead)) continue;
            var sentAt = touch.Recipient.SentAt!.Value;
            var channel = touch.Campaign.Channel == CampaignChannel.Email ? "email" : "whatsapp";
            var nextTouchAt = touches
                .Where(item => string.Equals(item.Recipient.LeadId, touch.Recipient.LeadId, StringComparison.Ordinal)
                    && item.Campaign.Channel == touch.Campaign.Channel
                    && item.Recipient.SentAt!.Value > sentAt)
                .Select(item => item.Recipient.SentAt)
                .FirstOrDefault();
            var windowEndsAt = Min(sentAt.Add(ObservationWindow), nextTouchAt);
            var reply = await FindFirstReplyAsync(lead, channel, sentAt, windowEndsAt, cancellationToken);
            var feedback = new AiLearningFeedback
            {
                Id = $"system-observed-campaign-{touch.Recipient.Id}",
                CustomerId = touch.Recipient.LeadId,
                Outcome = reply is null ? "尚未观测到客户回复。" : "客户在自动化触达后产生了真实回复。",
                Helpful = reply is not null,
                FeedbackSource = "system_observed",
                Note = $"自动化任务：{touch.Campaign.Name}",
                ObservationStatus = reply is not null
                    ? LearningObservationStatus.Observed
                    : DateTimeOffset.Now > windowEndsAt
                        ? LearningObservationStatus.Expired
                        : LearningObservationStatus.Pending,
                Channel = channel,
                ActionAt = sentAt,
                ObservedStage = lead.Stage,
                Replied = reply is not null,
                FirstReplyAt = reply?.Timestamp,
                ReplyLatencyMinutes = reply is null ? null : Math.Round((reply.Timestamp - sentAt).TotalMinutes, 1),
                TalkTrack = CleanTalkTrack(touch.Recipient.RenderedMessage),
                SourceMessageId = reply?.SourceId ?? "",
                ObservationWindowEndsAt = windowEndsAt,
                ObservedAt = DateTimeOffset.Now,
                CreatedAt = sentAt
            };
            await _repository.SaveAiLearningFeedbackAsync(feedback, cancellationToken);
        }
    }

    private async Task<PersonalLearningSummary> BuildSummaryAsync(
        string? customerId,
        IReadOnlyCollection<SalesActionRecord> allActions,
        CancellationToken cancellationToken)
    {
        var actions = allActions
            .Where(item => customerId is null || string.Equals(item.CustomerId, customerId, StringComparison.Ordinal))
            .ToList();
        var allFeedback = customerId is null
            ? await _repository.GetAllAiLearningFeedbackAsync(cancellationToken)
            : await _repository.GetAiLearningFeedbackAsync(customerId, cancellationToken);
        var humanFeedback = allFeedback.Where(item => item.FeedbackSource == "human").ToList();
        var observations = allFeedback.Where(item => item.FeedbackSource == "system_observed").ToList();
        var talkTracks = observations
            .Where(item => !string.IsNullOrWhiteSpace(item.TalkTrack))
            .GroupBy(item => $"{NormalizeChannel(item.Channel)}|{NormalizeTalkTrack(item.TalkTrack)}", StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var latencies = group.Where(item => item.ReplyLatencyMinutes is not null)
                    .Select(item => item.ReplyLatencyMinutes!.Value)
                    .ToList();
                return new TalkTrackPerformance
                {
                    Key = group.Key,
                    Channel = NormalizeChannel(first.Channel),
                    TalkTrack = first.TalkTrack,
                    SentCount = group.Count(),
                    Replies = group.Count(item => item.Replied),
                    StageProgressions = group.Count(item => item.StageProgressed),
                    Deals = group.Count(item => item.Converted),
                    AverageReplyMinutes = latencies.Count == 0 ? null : Math.Round(latencies.Average(), 1)
                };
            })
            .OrderByDescending(item => item.HasReliableSample)
            .ThenByDescending(item => item.Replies)
            .ThenByDescending(item => item.ResponseRate)
            .ThenByDescending(item => item.SentCount)
            .Take(5)
            .ToList();
        var replyLatencies = observations
            .Where(item => item.ReplyLatencyMinutes is not null)
            .Select(item => item.ReplyLatencyMinutes!.Value)
            .ToList();
        var summary = new PersonalLearningSummary
        {
            Proposed = actions.Count(item => item.Status == SalesActionStatus.Planned),
            Accepted = actions.Count(item => item.Status is SalesActionStatus.Approved
                or SalesActionStatus.InProgress
                or SalesActionStatus.Completed
                or SalesActionStatus.Failed),
            Completed = actions.Count(item => item.Status == SalesActionStatus.Completed),
            Failed = actions.Count(item => item.Status == SalesActionStatus.Failed),
            Dismissed = actions.Count(item => item.Status == SalesActionStatus.Cancelled),
            FeedbackCount = humanFeedback.Count,
            HelpfulFeedback = humanFeedback.Count(item => item.Helpful),
            Executed = observations.Count,
            AwaitingOutcome = observations.Count(item => item.ObservationStatus == LearningObservationStatus.Pending),
            ObservedActions = observations.Count(item => item.ObservationStatus == LearningObservationStatus.Observed),
            Replies = observations.Count(item => item.Replied),
            StageProgressions = observations.Count(item => item.StageProgressed),
            Deals = observations.Count(item => item.Converted),
            RepeatPurchases = observations.Count(item => item.RepeatPurchase),
            AverageReplyMinutes = replyLatencies.Count == 0 ? null : Math.Round(replyLatencies.Average(), 1),
            TopTalkTracks = talkTracks
        };
        summary.StrategyReview = BuildStrategyReview(summary);
        return summary;
    }

    private async Task<ReplyObservation?> FindFirstReplyAsync(
        Lead lead,
        string channel,
        DateTimeOffset after,
        DateTimeOffset before,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeChannel(channel);
        if (normalized is "whatsapp" or "manual_action")
        {
            var message = (await _repository.GetWhatsAppMessagesForLeadAsync(lead, 5_000, cancellationToken))
                .Where(item => item.Direction == WhatsAppMessageDirection.Incoming
                    && !item.IsStatusUpdate
                    && !item.IsRevoked
                    && item.Timestamp > after
                    && item.Timestamp <= before)
                .OrderBy(item => item.Timestamp)
                .FirstOrDefault();
            if (message is not null)
                return new ReplyObservation(message.Timestamp, string.IsNullOrWhiteSpace(message.ProviderMessageId) ? message.Id : message.ProviderMessageId);
        }
        if (normalized is "email" or "manual_action")
        {
            var message = (await _repository.GetEmailMessagesForLeadAsync(lead.Id, 1_000, cancellationToken))
                .Where(item => item.Direction == EmailMessageDirection.Incoming
                    && item.Timestamp > after
                    && item.Timestamp <= before)
                .OrderBy(item => item.Timestamp)
                .FirstOrDefault();
            if (message is not null)
                return new ReplyObservation(message.Timestamp, string.IsNullOrWhiteSpace(message.ProviderMessageId) ? message.Id : message.ProviderMessageId);
        }
        return null;
    }

    private static DateTimeOffset Min(DateTimeOffset fallback, DateTimeOffset? candidate) =>
        candidate is not null && candidate.Value < fallback ? candidate.Value : fallback;

    private static bool SameChannel(string left, string right) =>
        string.Equals(NormalizeChannel(left), NormalizeChannel(right), StringComparison.Ordinal);

    private static string NormalizeChannel(string channel)
    {
        if (channel.Contains("email", StringComparison.OrdinalIgnoreCase) || channel.Contains("邮件", StringComparison.OrdinalIgnoreCase))
            return "email";
        if (channel.Contains("whatsapp", StringComparison.OrdinalIgnoreCase))
            return "whatsapp";
        return "manual_action";
    }

    private static int StageRank(LeadStage stage) => stage switch
    {
        LeadStage.Lost => -1,
        LeadStage.New => 0,
        LeadStage.Contacted => 1,
        LeadStage.Interested => 2,
        LeadStage.RequirementConfirmed => 3,
        LeadStage.Quotation => 4,
        LeadStage.Negotiation => 5,
        LeadStage.Waiting => 5,
        LeadStage.Customer => 6,
        LeadStage.RepeatPurchase => 7,
        _ => 0
    };

    private static string DescribeOutcome(
        ReplyObservation? reply,
        LeadStage stage,
        bool progressed,
        bool converted,
        bool repeatPurchase)
    {
        var outcomes = new List<string>();
        if (reply is not null) outcomes.Add($"客户已回复，首响时间 {reply.Timestamp:yyyy-MM-dd HH:mm}。");
        if (progressed) outcomes.Add($"CRM 阶段已推进至 {Labels.Stage(stage)}。");
        if (converted) outcomes.Add("已观测到成交阶段。");
        if (repeatPurchase) outcomes.Add("已观测到复购阶段。");
        return outcomes.Count == 0 ? "观察窗口内尚未发现可归因结果。" : string.Join(' ', outcomes);
    }

    private static string CleanTalkTrack(string value)
    {
        var clean = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= 800 ? clean : clean[..800];
    }

    private static string NormalizeTalkTrack(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string BuildStrategyReview(PersonalLearningSummary summary)
    {
        if (summary.Executed == 0)
            return "尚无可核验的已执行触达；完成一次建议或发送 AI 助理/自动化消息后，系统会依据真实回复与 CRM 阶段变化复盘。";
        var parts = new List<string>
        {
            $"已核验 {summary.Executed} 次触达，真实回复率 {summary.ResponseRate:0.#}%，阶段推进率 {summary.ProgressionRate:0.#}%，成交归因率 {summary.DealRate:0.#}%。"
        };
        if (summary.AwaitingOutcome > 0) parts.Add($"{summary.AwaitingOutcome} 次触达仍在 30 天观察窗口内。");
        if (summary.AverageReplyMinutes is not null) parts.Add($"已回复客户平均首响 {FormatDuration(summary.AverageReplyMinutes.Value)}。");
        var reliable = summary.TopTalkTracks.FirstOrDefault(item => item.HasReliableSample);
        if (reliable is not null)
            parts.Add($"当前样本较稳定的话术来自 {reliable.Channel}，{reliable.SentCount} 次发送获得 {reliable.Replies} 次回复。");
        else if (summary.TopTalkTracks.Count > 0)
            parts.Add("现有话术样本仍少，系统只展示事实结果，不提前下结论。");
        return string.Join(' ', parts);
    }

    private static string FormatDuration(double minutes)
    {
        if (minutes < 60) return $"{Math.Max(1, Math.Round(minutes)):0} 分钟";
        if (minutes < 1_440) return $"{minutes / 60:0.#} 小时";
        return $"{minutes / 1_440:0.#} 天";
    }

    private sealed record LearningContext(
        IReadOnlyDictionary<string, Lead> Leads,
        List<SalesActionRecord> Actions,
        List<WhatsAppCampaign> Campaigns);

    private sealed record CampaignTouch(WhatsAppCampaign Campaign, CampaignRecipient Recipient);

    private sealed record ReplyObservation(DateTimeOffset Timestamp, string SourceId);
}
