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

public sealed record CampaignTemplateField(string Key, string Label, string Source)
{
    public string Token => $"{{{Key}}}";
    public string DisplayLabel => $"{Source} · {Label}  {Token}";
}

public sealed record CampaignExecutionSummary(
    WhatsAppCampaign Campaign,
    int Total,
    int Sent,
    int Failed,
    int Skipped,
    int Cancelled,
    int Queued,
    string NextPosition)
{
    public string Name => Campaign.Name;
    public string AccountId => Campaign.AccountId;
    public string Status => Campaign.StatusLabel;
    public string Trigger => Campaign.ScheduleLabel;
    public string Progress => $"{Sent + Failed + Skipped + Cancelled} / {Total}";
    public string SuccessRate
    {
        get
        {
            var attempted = Sent + Failed;
            return attempted == 0 ? "—" : $"{Sent * 100d / attempted:0.#}%";
        }
    }
    public string StopOrNext => !string.IsNullOrWhiteSpace(Campaign.SafetyStopPosition)
        ? Campaign.SafetyStopPosition
        : NextPosition;
    public string Updated => Campaign.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string Detail => string.IsNullOrWhiteSpace(Campaign.PauseReason) ? "—" : Campaign.PauseReason;
}

public sealed class CampaignSafetyStoppedEventArgs : EventArgs
{
    public required string PreviousIp { get; init; }
    public required string CurrentIp { get; init; }
    public required IReadOnlyList<CampaignExecutionSummary> Campaigns { get; init; }
}

public sealed class CampaignAutomationService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly WhatsAppConnectionManager _bridge;
    private readonly PublicIpMonitor _publicIp;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private Task? _safetyWorker;
    private readonly Dictionary<string, DateTimeOffset> _lastConnectAttempts = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? CampaignChanged;
    public event EventHandler<CampaignSafetyStoppedEventArgs>? SafetyStopped;

    public CampaignAutomationService(LocalRepository repository, WhatsAppConnectionManager bridge, PublicIpMonitor publicIp)
    {
        _repository = repository;
        _bridge = bridge;
        _publicIp = publicIp;
        _bridge.EventReceived += Bridge_EventReceived;
    }

    public async Task<List<CampaignAudienceItem>> ListAudienceAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        return leads.Where(x => MatchesFilter(campaign, x))
            .Select(x => CreateAudienceItem(campaign, x))
            .OrderByDescending(x => x.Eligible).ThenByDescending(x => x.Lead.Score).ThenBy(x => x.DisplayName)
            .ToList();
    }

    public async Task<List<CampaignAudienceItem>> PreviewAudienceAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        var selected = campaign.SelectedLeadIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return [];
        return (await ListAudienceAsync(campaign, cancellationToken)).Where(item => selected.Contains(item.Lead.Id)).ToList();
    }

    public async Task<List<CampaignTemplateField>> GetTemplateFieldsAsync(CancellationToken cancellationToken = default)
    {
        var fields = CoreTemplateFields().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
                     .SelectMany(lead => lead.CustomFields.Keys)
                     .Where(key => !string.IsNullOrWhiteSpace(key)))
            fields.TryAdd(key.Trim(), new CampaignTemplateField(key.Trim(), key.Trim(), "导入表格 / 客户列表"));
        return fields.Values.OrderBy(field => field.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<List<CampaignExecutionSummary>> GetExecutionHistoryAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CampaignExecutionSummary>();
        foreach (var campaign in (await _repository.GetCampaignsAsync(null, cancellationToken)).Where(item => item.ApprovedAt is not null || item.Status != CampaignStatus.Draft))
            result.Add(await BuildExecutionSummaryAsync(campaign, cancellationToken));
        return result.OrderByDescending(item => item.Campaign.UpdatedAt).ToList();
    }

    public async Task<CampaignMessageTemplate> SaveMessageTemplateAsync(CampaignMessageTemplate template, CancellationToken cancellationToken = default)
    {
        template.Name = template.Name.Trim(); template.Body = template.Body.Trim();
        if (string.IsNullOrWhiteSpace(template.Name)) throw new InvalidOperationException("请填写话术模板名称。");
        if (string.IsNullOrWhiteSpace(template.Body)) throw new InvalidOperationException("请填写话术模板内容。");
        if (template.Body.Length > 4096) throw new InvalidOperationException("WhatsApp 话术不能超过 4096 字符。");
        await ValidateTemplateFieldsAsync(template.Body, cancellationToken);
        await _repository.SaveCampaignMessageTemplateAsync(template, cancellationToken);
        await _repository.LogEventAsync("campaign_template_saved", null, null, $"template_id={template.Id};name={template.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
        return template;
    }

    public async Task DeleteMessageTemplateAsync(CampaignMessageTemplate template, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteCampaignMessageTemplateAsync(template.Id, cancellationToken);
        await _repository.LogEventAsync("campaign_template_deleted", null, null, $"template_id={template.Id};name={template.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CampaignAudienceItem CreateAudienceItem(WhatsAppCampaign campaign, Lead lead)
    {
        var eligible = IsEligible(lead, out var reason);
        return new CampaignAudienceItem(lead, eligible, reason, RenderTemplate(campaign.MessageTemplate, lead));
    }

    public async Task SaveDraftAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        await ValidateTemplateFieldsAsync(campaign.MessageTemplate, cancellationToken);
        var existing = await _repository.GetCampaignAsync(campaign.Id, cancellationToken);
        if (existing is not null && existing.Status != CampaignStatus.Draft)
            throw new InvalidOperationException("已排期的 Campaign 不能直接修改；请暂停或取消后新建。");
        campaign.SelectedLeadIds = campaign.SelectedLeadIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        campaign.Status = CampaignStatus.Draft;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_draft_saved", null, null, $"campaign_id={campaign.Id};name={campaign.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> ApproveAndScheduleAsync(WhatsAppCampaign campaign, string actor = "当前用户", CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        await ValidateTemplateFieldsAsync(campaign.MessageTemplate, cancellationToken);
        if (campaign.SelectedLeadIds.Count == 0) throw new InvalidOperationException("请至少勾选 1 位客户后再建立发送任务。");
        var audience = await PreviewAudienceAsync(campaign, cancellationToken);
        var eligible = audience.Where(x => x.Eligible).ToList();
        if (eligible.Count == 0) throw new InvalidOperationException("当前筛选没有可发送客户。客户必须号码有效、已记录 WhatsApp 营销同意且未退订。");

        var ip = await _publicIp.CheckAsync(campaign.AccountId, true, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ip.Error) || string.IsNullOrWhiteSpace(ip.State.CurrentIp))
            throw new InvalidOperationException("无法取得当前公网 IP，安全阀门未能建立基线，因此没有创建发送任务。请检查网络后重试。");

        var now = DateTimeOffset.Now;
        var firstSendAt = campaign.ScheduleMode == CampaignScheduleMode.Immediate
            ? now.AddSeconds(2)
            : campaign.StartsAt > now ? campaign.StartsAt : now.AddSeconds(2);
        var interval = campaign.IntervalDelay;
        var recipients = eligible.Select((item, index) => new CampaignRecipient
        {
            Id = $"{campaign.Id}:{item.Lead.Id}", CampaignId = campaign.Id, LeadId = item.Lead.Id,
            AccountId = campaign.AccountId, Phone = item.Lead.PhoneE164, DisplayName = item.DisplayName,
            RenderedMessage = item.PreviewMessage, Status = CampaignRecipientStatus.Queued,
            ScheduledAt = firstSendAt.AddTicks(interval.Ticks * index),
            NextAttemptAt = firstSendAt.AddTicks(interval.Ticks * index)
        }).ToList();

        campaign.StartsAt = firstSendAt;
        campaign.Status = CampaignStatus.Scheduled;
        campaign.ApprovedAt = DateTimeOffset.Now;
        campaign.ApprovedBy = actor;
        campaign.PauseReason = "";
        campaign.BaselinePublicIp = ip.State.CurrentIp;
        campaign.SafetyStopFromIp = "";
        campaign.SafetyStopToIp = "";
        campaign.SafetyStopPosition = "";
        campaign.SafetyStoppedAt = null;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.ReplaceCampaignRecipientsAsync(campaign.Id, recipients, cancellationToken);
        await _repository.LogEventAsync("campaign_approved", null, null, $"campaign_id={campaign.Id};mode={campaign.ScheduleMode};recipients={recipients.Count};interval={campaign.EffectiveIntervalValue};unit={campaign.IntervalUnit};daily_limit={campaign.DailyLimit}", cancellationToken);
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
        if (campaign.Status is not (CampaignStatus.Paused or CampaignStatus.SafetyStopped)) throw new InvalidOperationException("只有已暂停或被安全阀门停止的 Campaign 可以继续。");
        var ip = await _publicIp.CheckAsync(campaign.AccountId, true, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ip.Error) || string.IsNullOrWhiteSpace(ip.State.CurrentIp))
            throw new InvalidOperationException("无法验证当前公网 IP，任务仍保持停止。请检查网络后重试。");
        campaign.BaselinePublicIp = ip.State.CurrentIp;
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
        _safetyWorker = Task.Run(() => RunSafetyMonitorAsync(_lifetime.Token), CancellationToken.None);
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

    private async Task RunSafetyMonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await CheckSafetyValveAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> CheckSafetyValveAsync(CancellationToken cancellationToken = default)
    {
        var active = await _repository.GetActiveCampaignsAsync(cancellationToken);
        foreach (var accountGroup in active.GroupBy(item => item.AccountId, StringComparer.OrdinalIgnoreCase))
        {
            var result = await _publicIp.CheckAsync(accountGroup.Key, true, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Error) || string.IsNullOrWhiteSpace(result.State.CurrentIp)) continue;
            foreach (var campaign in accountGroup)
            {
                if (string.IsNullOrWhiteSpace(campaign.BaselinePublicIp))
                {
                    campaign.BaselinePublicIp = result.State.CurrentIp;
                    await _repository.SaveCampaignAsync(campaign, cancellationToken);
                    continue;
                }
                if (!campaign.BaselinePublicIp.Equals(result.State.CurrentIp, StringComparison.OrdinalIgnoreCase))
                {
                    await StopAllForIpChangeAsync(campaign.BaselinePublicIp, result.State.CurrentIp, false, cancellationToken);
                    return false;
                }
            }
        }
        return true;
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

            if (!await EnsureCampaignIpSafeAsync(campaign, cancellationToken)) return;

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

    private async Task<bool> EnsureCampaignIpSafeAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken)
    {
        var result = await _publicIp.CheckAsync(campaign.AccountId, true, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error) || string.IsNullOrWhiteSpace(result.State.CurrentIp)) return true;
        if (string.IsNullOrWhiteSpace(campaign.BaselinePublicIp))
        {
            campaign.BaselinePublicIp = result.State.CurrentIp;
            await _repository.SaveCampaignAsync(campaign, cancellationToken);
            return true;
        }
        if (campaign.BaselinePublicIp.Equals(result.State.CurrentIp, StringComparison.OrdinalIgnoreCase)) return true;
        await StopAllForIpChangeAsync(campaign.BaselinePublicIp, result.State.CurrentIp, true, cancellationToken);
        return false;
    }

    private async Task StopAllForIpChangeAsync(string previousIp, string currentIp, bool sendLockAlreadyHeld, CancellationToken cancellationToken)
    {
        if (!sendLockAlreadyHeld) await _sendLock.WaitAsync(cancellationToken);
        List<CampaignExecutionSummary> summaries;
        try
        {
            var active = await _repository.GetActiveCampaignsAsync(cancellationToken);
            if (active.Count == 0) return;
            foreach (var campaign in active)
            {
                var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
                var terminal = recipients.Count(item => IsTerminal(item.Status));
                var next = recipients.FirstOrDefault(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending);
                campaign.Status = CampaignStatus.SafetyStopped;
                campaign.PauseReason = $"公网 IP 从 {previousIp} 变化为 {currentIp}，安全阀门已停止所有自动触达。";
                campaign.SafetyStopFromIp = previousIp;
                campaign.SafetyStopToIp = currentIp;
                campaign.SafetyStoppedAt = DateTimeOffset.Now;
                campaign.SafetyStopPosition = next is null
                    ? $"已处理 {terminal}/{recipients.Count}"
                    : $"第 {terminal + 1}/{recipients.Count} 位前停止 · {next.DisplayName}";
                await _repository.SaveCampaignAsync(campaign, cancellationToken);
            }
            summaries = [];
            foreach (var campaign in active) summaries.Add(await BuildExecutionSummaryAsync(campaign, cancellationToken));
            await _repository.LogEventAsync("campaign_ip_safety_stop", null, null, $"from={previousIp};to={currentIp};campaigns={summaries.Count};sent={summaries.Sum(item => item.Sent)};failed={summaries.Sum(item => item.Failed)}", cancellationToken);
        }
        finally { if (!sendLockAlreadyHeld) _sendLock.Release(); }

        CampaignChanged?.Invoke(this, EventArgs.Empty);
        SafetyStopped?.Invoke(this, new CampaignSafetyStoppedEventArgs { PreviousIp = previousIp, CurrentIp = currentIp, Campaigns = summaries });
    }

    private async Task<CampaignExecutionSummary> BuildExecutionSummaryAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken)
    {
        var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
        var next = recipients.FirstOrDefault(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending);
        var terminal = recipients.Count(item => IsTerminal(item.Status));
        var nextPosition = next is null ? "—" : $"第 {terminal + 1}/{recipients.Count} 位 · {next.DisplayName}";
        return new CampaignExecutionSummary(
            campaign,
            recipients.Count,
            recipients.Count(item => item.Status == CampaignRecipientStatus.Sent),
            recipients.Count(item => item.Status == CampaignRecipientStatus.Failed),
            recipients.Count(item => item.Status == CampaignRecipientStatus.Skipped),
            recipients.Count(item => item.Status == CampaignRecipientStatus.Cancelled),
            recipients.Count(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending),
            nextPosition);
    }

    private static bool IsTerminal(CampaignRecipientStatus status) => status is CampaignRecipientStatus.Sent or CampaignRecipientStatus.Failed or CampaignRecipientStatus.Skipped or CampaignRecipientStatus.Cancelled;

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
            ["stage"] = Labels.Stage(lead.Stage), ["phone"] = lead.PhoneE164, ["email"] = lead.Email,
            ["language"] = lead.PreferredLanguage, ["order_value"] = lead.EstimatedOrderValue.ToString("0.##"),
            ["currency"] = lead.Currency, ["score"] = lead.Score.ToString(), ["tags"] = string.Join(", ", lead.Tags),
            ["profile_summary"] = lead.ProfileSummary, ["customer_segment"] = lead.CustomerSegment,
            ["next_action"] = lead.NextAction, ["latest_message"] = lead.LatestMessage, ["source"] = lead.Source
        };
        foreach (var pair in lead.CustomFields) fields.TryAdd(pair.Key, pair.Value);
        return Regex.Replace(template, "\\{([^{}]+)\\}", match => fields.TryGetValue(match.Groups[1].Value.Trim(), out var value) ? value : "", RegexOptions.CultureInvariant);
    }

    public static IReadOnlyList<CampaignTemplateField> CoreTemplateFields() =>
    [
        new("name", "姓名", "客户列表"), new("company", "公司", "客户列表"), new("country", "国家 / 地区", "客户列表"), new("phone", "WhatsApp 号码", "客户列表"),
        new("email", "邮箱", "客户列表"), new("language", "客户语言", "客户列表"), new("product", "产品兴趣", "客户列表"), new("order_value", "预计订单金额", "客户列表"),
        new("currency", "币种", "客户列表"), new("owner", "负责人", "客户列表"), new("tags", "标签", "客户列表"), new("source", "客户来源", "客户列表"),
        new("grade", "商机等级", "商机智能"), new("stage", "跟进阶段", "商机智能"), new("score", "商机评分", "商机智能"), new("profile_summary", "客户画像", "商机智能"),
        new("customer_segment", "客户分组", "商机智能"), new("next_action", "下一步建议", "商机智能"), new("latest_message", "最近消息", "商机智能")
    ];

    private async Task ValidateTemplateFieldsAsync(string template, CancellationToken cancellationToken)
    {
        var available = (await GetTemplateFieldsAsync(cancellationToken)).Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = Regex.Matches(template, "\\{([^{}]+)\\}")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(key => !available.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"话术包含不存在的客户字段：{string.Join("、", unknown)}。请从“插入客户字段”列表选择。");
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
        var interval = campaign.EffectiveIntervalValue;
        if (campaign.IntervalUnit == CampaignIntervalUnit.Seconds && interval is < 10 or > 3600)
            throw new InvalidOperationException("按秒发送时，间隔必须在 10–3600 秒之间。过密发送不会让个人账号更安全。");
        if (campaign.IntervalUnit == CampaignIntervalUnit.Minutes && interval is < 1 or > 1440)
            throw new InvalidOperationException("按分钟发送时，间隔必须在 1–1440 分钟之间。");
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
        if (_safetyWorker is not null) try { await _safetyWorker; } catch (OperationCanceledException) { }
        _lifetime?.Dispose(); _sendLock.Dispose();
    }
}
