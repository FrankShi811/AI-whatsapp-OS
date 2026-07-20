using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record CampaignAudienceItem(Lead Lead, bool Eligible, string Reason, string PreviewMessage)
{
    public string DisplayName => Lead.DisplayName;
    public string Phone => Lead.PhoneE164;
    public string Grade => Lead.Grade;
    public string Stage => Labels.Stage(Lead.Stage);
    public string EligibilityLabel => Eligible ? "可发送" : "已排除";
}

public sealed class CampaignAutomationService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly WhatsAppConnectionManager _bridge;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private readonly Dictionary<string, DateTimeOffset> _lastConnectAttempts = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? CampaignChanged;

    public CampaignAutomationService(LocalRepository repository, WhatsAppConnectionManager bridge)
    {
        _repository = repository;
        _bridge = bridge;
        _bridge.EventReceived += Bridge_EventReceived;
    }

    public async Task<List<CampaignAudienceItem>> PreviewAudienceAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        return leads.Where(x => MatchesFilter(campaign, x))
            .Select(x => CreateAudienceItem(campaign, x))
            .OrderByDescending(x => x.Eligible).ThenByDescending(x => x.Lead.Score).ThenBy(x => x.DisplayName)
            .ToList();
    }

    private static CampaignAudienceItem CreateAudienceItem(WhatsAppCampaign campaign, Lead lead)
    {
        var eligible = IsEligible(lead, out var reason);
        return new CampaignAudienceItem(lead, eligible, reason, RenderTemplate(campaign.MessageTemplate, lead));
    }

    public async Task SaveDraftAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        var existing = await _repository.GetCampaignAsync(campaign.Id, cancellationToken);
        if (existing is not null && existing.Status != CampaignStatus.Draft)
            throw new InvalidOperationException("已排期的 Campaign 不能直接修改；请暂停或取消后新建。");
        campaign.Status = CampaignStatus.Draft;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_draft_saved", null, null, $"campaign_id={campaign.Id};name={campaign.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> ApproveAndScheduleAsync(WhatsAppCampaign campaign, string actor = "当前用户", CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        var audience = await PreviewAudienceAsync(campaign, cancellationToken);
        var eligible = audience.Where(x => x.Eligible).ToList();
        if (eligible.Count == 0) throw new InvalidOperationException("当前筛选没有可发送客户。客户必须号码有效、已记录 WhatsApp 营销同意且未退订。");

        var firstSendAt = campaign.StartsAt > DateTimeOffset.Now ? campaign.StartsAt : DateTimeOffset.Now.AddSeconds(10);
        var recipients = eligible.Select((item, index) => new CampaignRecipient
        {
            Id = $"{campaign.Id}:{item.Lead.Id}", CampaignId = campaign.Id, LeadId = item.Lead.Id,
            AccountId = campaign.AccountId, Phone = item.Lead.PhoneE164, DisplayName = item.DisplayName,
            RenderedMessage = item.PreviewMessage, Status = CampaignRecipientStatus.Queued,
            ScheduledAt = firstSendAt.AddMinutes((long)index * campaign.IntervalMinutes),
            NextAttemptAt = firstSendAt.AddMinutes((long)index * campaign.IntervalMinutes)
        }).ToList();

        campaign.StartsAt = firstSendAt;
        campaign.Status = CampaignStatus.Scheduled;
        campaign.ApprovedAt = DateTimeOffset.Now;
        campaign.ApprovedBy = actor;
        campaign.PauseReason = "";
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.ReplaceCampaignRecipientsAsync(campaign.Id, recipients, cancellationToken);
        await _repository.LogEventAsync("campaign_approved", null, null, $"campaign_id={campaign.Id};recipients={recipients.Count};interval={campaign.IntervalMinutes};daily_limit={campaign.DailyLimit}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
        return recipients.Count;
    }

    public async Task PauseAsync(WhatsAppCampaign campaign, string reason = "用户手动暂停", CancellationToken cancellationToken = default)
    {
        if (campaign.Status is CampaignStatus.Completed or CampaignStatus.Cancelled) return;
        campaign.Status = CampaignStatus.Paused; campaign.PauseReason = reason;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_paused", null, null, $"campaign_id={campaign.Id};reason={reason}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResumeAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        if (campaign.Status != CampaignStatus.Paused) throw new InvalidOperationException("只有已暂停的 Campaign 可以继续。");
        campaign.Status = CampaignStatus.Scheduled; campaign.PauseReason = "";
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_resumed", null, null, $"campaign_id={campaign.Id}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CancelAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        if (campaign.Status == CampaignStatus.Completed) return;
        campaign.Status = CampaignStatus.Cancelled; campaign.PauseReason = "用户已取消";
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        foreach (var recipient in await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken))
        {
            if (recipient.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending)
            {
                recipient.Status = CampaignRecipientStatus.Cancelled;
                await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
            }
        }
        await _repository.LogEventAsync("campaign_cancelled", null, null, $"campaign_id={campaign.Id}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PauseAccountAsync(string accountId, string reason, CancellationToken cancellationToken = default)
    {
        await _repository.PauseActiveCampaignsAsync(accountId, reason, cancellationToken);
        await _repository.LogEventAsync("campaign_account_paused", null, null, $"account_id={accountId};reason={reason}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null) return;
        await _repository.RecoverInterruptedCampaignRecipientsAsync(cancellationToken);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await ProcessNextAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (!await _sendLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var recipient = await _repository.GetNextDueCampaignRecipientAsync(DateTimeOffset.Now, cancellationToken);
            if (recipient is null) return;
            var campaign = await _repository.GetCampaignAsync(recipient.CampaignId, cancellationToken);
            if (campaign is null || campaign.Status is not (CampaignStatus.Scheduled or CampaignStatus.Running)) return;

            var lead = await _repository.GetLeadAsync(recipient.LeadId, cancellationToken);
            var reason = "客户记录不存在";
            var eligible = lead is not null && IsEligible(lead, out reason);
            if (!eligible)
            {
                recipient.Status = CampaignRecipientStatus.Skipped; recipient.SkipReason = reason;
                await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
                await CompleteCampaignIfFinishedAsync(campaign, cancellationToken);
                CampaignChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            var eligibleLead = lead!;

            var sentToday = await _repository.CountCampaignMessagesSentAsync(campaign.AccountId, BeijingDayStart(DateTimeOffset.Now), cancellationToken);
            if (sentToday >= campaign.DailyLimit)
            {
                recipient.NextAttemptAt = NextBeijingDay(DateTimeOffset.Now, campaign.StartsAt);
                recipient.LastError = $"已达到当日上限 {campaign.DailyLimit}，自动顺延至次日。";
                await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
                CampaignChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!await EnsureConnectedAsync(campaign.AccountId, cancellationToken)) return;
            if (campaign.Status == CampaignStatus.Scheduled)
            {
                campaign.Status = CampaignStatus.Running; campaign.PauseReason = "";
                await _repository.SaveCampaignAsync(campaign, cancellationToken);
            }

            recipient.Status = CampaignRecipientStatus.Sending; recipient.AttemptCount++;
            await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
            try
            {
                var result = await _bridge.SendTextAsync(campaign.AccountId, recipient.Phone, recipient.RenderedMessage, cancellationToken);
                recipient.ProviderMessageId = result.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                recipient.Status = CampaignRecipientStatus.Sent; recipient.SentAt = DateTimeOffset.Now; recipient.LastError = "";
                eligibleLead.LastContactAt = recipient.SentAt; eligibleLead.LatestMessage = recipient.RenderedMessage;
                if (eligibleLead.Stage == LeadStage.New) eligibleLead.Stage = LeadStage.Contacted;
                await _repository.UpsertLeadAsync(eligibleLead, cancellationToken);
                await _repository.LogEventAsync("campaign_message_sent", eligibleLead.Id, null, $"campaign_id={campaign.Id};recipient_id={recipient.Id};message_id={recipient.ProviderMessageId}", cancellationToken);
            }
            catch (Exception error)
            {
                recipient.LastError = Safe(error.Message);
                if (!_bridge.IsConnectedFor(campaign.AccountId))
                {
                    recipient.Status = CampaignRecipientStatus.Queued;
                    recipient.NextAttemptAt = DateTimeOffset.Now.AddMinutes(5);
                    await PauseAsync(campaign, "WhatsApp 已断开；重新连接后请继续 Campaign。", cancellationToken);
                }
                else if (recipient.AttemptCount >= 3) recipient.Status = CampaignRecipientStatus.Failed;
                else
                {
                    recipient.Status = CampaignRecipientStatus.Queued;
                    recipient.NextAttemptAt = DateTimeOffset.Now.AddMinutes(recipient.AttemptCount == 1 ? 2 : 10);
                }
            }
            await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
            await CompleteCampaignIfFinishedAsync(campaign, cancellationToken);
            CampaignChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _sendLock.Release(); }
    }

    private async Task<bool> EnsureConnectedAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_bridge.IsConnectedFor(accountId)) return true;
        var lastAttempt = _lastConnectAttempts.GetValueOrDefault(accountId);
        if (DateTimeOffset.Now - lastAttempt < TimeSpan.FromSeconds(15)) return false;
        _lastConnectAttempts[accountId] = DateTimeOffset.Now;
        try
        {
            await _bridge.ConnectAsync(accountId, cancellationToken);
        }
        catch { return false; }
        return _bridge.IsConnectedFor(accountId);
    }

    private async Task CompleteCampaignIfFinishedAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken)
    {
        var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
        if (recipients.Count > 0 && recipients.All(x => x.Status is CampaignRecipientStatus.Sent or CampaignRecipientStatus.Skipped or CampaignRecipientStatus.Failed or CampaignRecipientStatus.Cancelled))
        {
            campaign.Status = CampaignStatus.Completed; campaign.PauseReason = "";
            await _repository.SaveCampaignAsync(campaign, cancellationToken);
            await _repository.LogEventAsync("campaign_completed", null, null, $"campaign_id={campaign.Id};sent={recipients.Count(x => x.Status == CampaignRecipientStatus.Sent)}", cancellationToken);
        }
    }

    private async void Bridge_EventReceived(object? sender, WhatsAppBridgeEvent e)
    {
        if (e.Name != "connection" || !e.Data.TryGetProperty("state", out var state) || state.GetString() != "logged_out") return;
        try
        {
            await _repository.PauseActiveCampaignsAsync(string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId, "WhatsApp 登录已失效，Campaign 已自动暂停。");
            CampaignChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    public static string RenderTemplate(string template, Lead lead)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = lead.Name, ["company"] = lead.Company, ["country"] = lead.Country,
            ["product"] = lead.ProductInterest, ["owner"] = lead.Owner, ["grade"] = lead.Grade,
            ["stage"] = Labels.Stage(lead.Stage), ["phone"] = lead.PhoneE164
        };
        foreach (var pair in lead.CustomFields) fields[pair.Key] = pair.Value;
        return Regex.Replace(template, "\\{([^{}]+)\\}", match => fields.TryGetValue(match.Groups[1].Value.Trim(), out var value) ? value : match.Value, RegexOptions.CultureInvariant);
    }

    private static bool MatchesFilter(WhatsAppCampaign campaign, Lead lead) =>
        (campaign.GradeFilter is "" or "全部" || lead.Grade.Equals(campaign.GradeFilter, StringComparison.OrdinalIgnoreCase)) &&
        (campaign.StageFilter is null || lead.Stage == campaign.StageFilter) &&
        (string.IsNullOrWhiteSpace(campaign.TagFilter) || lead.Tags.Any(x => x.Contains(campaign.TagFilter.Trim(), StringComparison.CurrentCultureIgnoreCase))) &&
        (string.IsNullOrWhiteSpace(campaign.OwnerFilter) || lead.Owner.Contains(campaign.OwnerFilter.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private static bool IsEligible(Lead lead, out string reason)
    {
        if (!lead.PhoneValid) { reason = "号码无效"; return false; }
        if (lead.OptedOut) { reason = "客户已退订"; return false; }
        if (!lead.WhatsAppOptIn) { reason = "未记录营销同意"; return false; }
        reason = "已同意且号码有效"; return true;
    }

    private static void ValidateDraft(WhatsAppCampaign campaign)
    {
        if (string.IsNullOrWhiteSpace(campaign.Name)) throw new InvalidOperationException("请填写 Campaign 名称。");
        if (string.IsNullOrWhiteSpace(campaign.MessageTemplate)) throw new InvalidOperationException("请填写发送话术。");
        if (campaign.MessageTemplate.Length > 4096) throw new InvalidOperationException("WhatsApp 话术不能超过 4096 字符。");
        if (campaign.IntervalMinutes is < 1 or > 1440) throw new InvalidOperationException("发送间隔必须在 1–1440 分钟之间。");
        if (campaign.DailyLimit is < 1 or > 1000) throw new InvalidOperationException("每日上限必须在 1–1000 之间。");
    }

    private static TimeZoneInfo BeijingTimeZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Local;
    }

    private static DateTimeOffset BeijingDayStart(DateTimeOffset now)
    {
        var zone = BeijingTimeZone(); var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(start, zone.GetUtcOffset(start));
    }

    private static DateTimeOffset NextBeijingDay(DateTimeOffset now, DateTimeOffset campaignStart)
    {
        var zone = BeijingTimeZone(); var localNow = TimeZoneInfo.ConvertTime(now, zone); var localStart = TimeZoneInfo.ConvertTime(campaignStart, zone);
        var next = DateTime.SpecifyKind(localNow.Date.AddDays(1).Add(localStart.TimeOfDay), DateTimeKind.Unspecified);
        return new DateTimeOffset(next, zone.GetUtcOffset(next));
    }

    private static string Safe(string value)
    {
        var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean[..Math.Min(clean.Length, 500)];
    }

    public async ValueTask DisposeAsync()
    {
        _bridge.EventReceived -= Bridge_EventReceived;
        _lifetime?.Cancel();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
        _lifetime?.Dispose(); _sendLock.Dispose();
    }
}
