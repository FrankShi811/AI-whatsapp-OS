using System.Security.Cryptography;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

/// <summary>
/// Materializes one evidence-aware customer view from the existing CRM, channel,
/// analysis and campaign stores. This service deliberately does not score a lead,
/// call an AI provider or overwrite authoritative CRM fields.
/// </summary>
public sealed class CustomerBrainService
{
    private readonly LocalRepository _repository;

    public CustomerBrainService(LocalRepository repository) => _repository = repository;

    public async Task<CustomerIntelligenceProfile> RefreshAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var lead = await _repository.GetLeadAsync(customerId, cancellationToken)
            ?? throw new InvalidOperationException("客户不存在或已经删除。");
        var whatsApp = (await _repository.GetWhatsAppMessagesForLeadAsync(lead, 5000, cancellationToken))
            .Where(message => !message.IsStatusUpdate)
            .OrderBy(message => message.Timestamp)
            .ToList();
        var emails = (await _repository.GetEmailMessagesForLeadAsync(lead.Id, 5000, cancellationToken))
            .OrderBy(message => message.Timestamp)
            .ToList();
        var reports = await _repository.GetCustomerAnalysisReportsAsync(lead.Id, cancellationToken);
        var latestReport = reports.FirstOrDefault(report => report.Status == CustomerReportStatus.Succeeded);
        var campaignTouches = await GetCampaignTouchesAsync(lead.Id, cancellationToken);

        await SynchronizeBehaviorTimelineAsync(lead, whatsApp, emails, campaignTouches, reports, cancellationToken);

        var coverage = new CustomerIntelligenceCoverage
        {
            HasCrmData = HasCrmData(lead),
            HasWhatsAppHistory = whatsApp.Count > 0,
            HasEmailHistory = emails.Count > 0,
            HasLeadAnalysis = lead.HasCurrentAiScore,
            HasCustomerReport = latestReport is not null,
            HasCampaignHistory = campaignTouches.Count > 0
        };
        var sourceHash = ComputeSourceHash(lead, whatsApp, emails, campaignTouches, latestReport);
        var current = await _repository.GetCustomerIntelligenceProfileAsync(lead.Id, cancellationToken);
        if (current is not null && string.Equals(current.SourceSnapshotHash, sourceHash, StringComparison.Ordinal))
            return current;

        var profile = BuildProfile(lead, whatsApp, emails, campaignTouches, latestReport, coverage, sourceHash, current);
        await _repository.SaveCustomerIntelligenceProfileAsync(profile, cancellationToken);
        await SynchronizeRecommendationAsync(profile, cancellationToken);
        await _repository.LogEventAsync(
            "customer_brain_materialized",
            lead.Id,
            null,
            $"profile_id={profile.Id};version={profile.Version};coverage={profile.Coverage.Percentage};source_hash={profile.SourceSnapshotHash}",
            cancellationToken);
        return profile;
    }

    public Task<CustomerIntelligenceProfile?> GetAsync(string customerId, CancellationToken cancellationToken = default) =>
        _repository.GetCustomerIntelligenceProfileAsync(customerId, cancellationToken);

    private async Task<List<CustomerCampaignTouch>> GetCampaignTouchesAsync(string customerId, CancellationToken cancellationToken)
    {
        var touches = new List<CustomerCampaignTouch>();
        foreach (var campaign in await _repository.GetCampaignsAsync(null, cancellationToken))
        {
            foreach (var recipient in (await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken))
                         .Where(item => item.LeadId == customerId))
            {
                touches.Add(new CustomerCampaignTouch
                {
                    CampaignId = campaign.Id,
                    CampaignName = campaign.Name,
                    Channel = campaign.ChannelLabel,
                    Message = recipient.RenderedMessage,
                    Status = recipient.StatusLabel,
                    ScheduledAt = recipient.ScheduledAt,
                    SentAt = recipient.SentAt,
                    LastError = recipient.LastError
                });
            }
        }
        return touches.OrderBy(item => item.ScheduledAt).ToList();
    }

    private async Task SynchronizeBehaviorTimelineAsync(
        Lead lead,
        IReadOnlyList<WhatsAppMessage> whatsApp,
        IReadOnlyList<EmailMessage> emails,
        IReadOnlyList<CustomerCampaignTouch> campaignTouches,
        IReadOnlyList<CustomerAnalysisReport> reports,
        CancellationToken cancellationToken)
    {
        foreach (var message in whatsApp)
        {
            await _repository.UpsertCustomerBehaviorEventAsync(new CustomerBehaviorEvent
            {
                Id = StableId("wa", lead.Id, message.Id),
                CustomerId = lead.Id,
                Channel = "WhatsApp",
                EventType = "message",
                Direction = message.Direction == WhatsAppMessageDirection.Incoming ? "incoming" : "outgoing",
                Summary = Summarize(message.IsRevoked ? "[消息已撤回]" : string.IsNullOrWhiteSpace(message.Body) ? $"[{message.Kind}]" : message.Body),
                SourceId = message.Id,
                SourceType = "whatsapp_message",
                OccurredAt = message.Timestamp
            }, cancellationToken);
        }

        foreach (var message in emails)
        {
            await _repository.UpsertCustomerBehaviorEventAsync(new CustomerBehaviorEvent
            {
                Id = StableId("email", lead.Id, message.Id),
                CustomerId = lead.Id,
                Channel = "Email",
                EventType = "message",
                Direction = message.Direction == EmailMessageDirection.Incoming ? "incoming" : "outgoing",
                Summary = Summarize($"{message.Subject} {message.TextBody}".Trim()),
                SourceId = message.Id,
                SourceType = "email_message",
                OccurredAt = message.Timestamp
            }, cancellationToken);
        }

        foreach (var touch in campaignTouches)
        {
            var sourceId = $"{touch.CampaignId}:{touch.ScheduledAt:O}";
            await _repository.UpsertCustomerBehaviorEventAsync(new CustomerBehaviorEvent
            {
                Id = StableId("campaign", lead.Id, sourceId),
                CustomerId = lead.Id,
                Channel = touch.Channel,
                EventType = "campaign_touch",
                Direction = "outgoing",
                Summary = Summarize($"{touch.CampaignName} · {touch.Status} · {touch.Message}"),
                SourceId = sourceId,
                SourceType = "campaign_recipient",
                OccurredAt = touch.SentAt ?? touch.ScheduledAt
            }, cancellationToken);
        }

        foreach (var report in reports)
        {
            await _repository.UpsertCustomerBehaviorEventAsync(new CustomerBehaviorEvent
            {
                Id = StableId("report", lead.Id, report.Id),
                CustomerId = lead.Id,
                Channel = "AI",
                EventType = "customer_report",
                Direction = "system",
                Summary = $"客户情报报告 V{report.Version} · {report.StatusLabel}",
                SourceId = report.Id,
                SourceType = "customer_analysis_report",
                OccurredAt = report.CreatedTime
            }, cancellationToken);
        }
    }

    private static CustomerIntelligenceProfile BuildProfile(
        Lead lead,
        IReadOnlyList<WhatsAppMessage> whatsApp,
        IReadOnlyList<EmailMessage> emails,
        IReadOnlyList<CustomerCampaignTouch> campaignTouches,
        CustomerAnalysisReport? latestReport,
        CustomerIntelligenceCoverage coverage,
        string sourceHash,
        CustomerIntelligenceProfile? current)
    {
        var report = latestReport?.Report;
        var profile = new CustomerIntelligenceProfile
        {
            Id = current?.Id ?? Guid.NewGuid().ToString("N"),
            CustomerId = lead.Id,
            Version = (current?.Version ?? 0) + 1,
            CustomerName = lead.DisplayName,
            Summary = FirstUseful(
                report?.ExecutiveSummary.OneLinePositioning,
                lead.HasCurrentAiScore ? lead.ProfileSummary : null,
                $"{lead.DisplayName} 已进入客户工作区；当前商业背景和采购条件仍需通过沟通核实。"),
            CustomerType = FirstUseful(report?.BasicProfile.CustomerType, lead.CustomerSegment, "客户类型待核实"),
            BusinessModels = Clean(report?.BasicProfile.BusinessModels),
            PurchaseMotivations = Clean(
                report?.PurchaseMotivation.InterestReasons,
                report?.PurchaseMotivation.TriggerEvents),
            PainPoints = Clean(report?.PainAnalysis.SurfacePains, report?.PainAnalysis.DeepBusinessProblems),
            OpportunitySignals = Clean(
                report?.OpportunityJudgment.PositiveFactors,
                report?.WhatsAppAnalysis.PurchaseSignals,
                lead.BehaviorSignals.Select(signal => signal.Signal)),
            Risks = Clean(
                report?.RiskAnalysis.DealRisks,
                report?.RiskAnalysis.AdoptionRisks,
                report?.RiskAnalysis.ChurnRisks,
                lead.Risks,
                string.IsNullOrWhiteSpace(lead.RiskWarning) ? [] : [lead.RiskWarning]),
            NextBestAction = FirstUseful(
                report?.ExecutiveSummary.CurrentSalesRecommendation,
                report?.SalesStrategy.Actions.FirstOrDefault()?.Action,
                lead.HasCurrentAiScore ? lead.NextAction : null,
                "补齐客户业务模式、需求、预算、数量与决策时间后重新分析。"),
            Confidence = lead.HasCurrentAiScore
                ? Math.Clamp(lead.AnalysisConfidence, 0, 1)
                : latestReport is null ? 0 : Math.Min(.75, Math.Max(.35, coverage.Percentage / 100d)),
            AiModel = latestReport?.AiModel ?? "",
            Coverage = coverage,
            SourceSnapshotHash = sourceHash,
            SourceCapturedAt = DateTimeOffset.Now,
            CreatedAt = current?.CreatedAt ?? DateTimeOffset.Now
        };

        AddCrmFacts(profile.Statements, lead);
        if (latestReport is not null || lead.HasCurrentAiScore)
        {
            profile.Statements.Add(new CustomerIntelligenceStatement
            {
                Nature = IntelligenceStatementNature.Inference,
                Topic = "客户理解",
                Text = profile.Summary,
                Evidence = string.Join("；", profile.OpportunitySignals.Take(4)),
                Source = latestReport is null ? "Lead Intelligence" : $"客户情报报告 V{latestReport.Version}",
                Confidence = profile.Confidence,
                ObservedAt = latestReport?.CreatedTime ?? lead.LastAnalyzedAt ?? lead.UpdatedAt
            });
        }
        if (report is not null)
        {
            foreach (var evidence in report.EvidenceLedger)
            {
                profile.Statements.Add(new CustomerIntelligenceStatement
                {
                    Nature = string.Equals(evidence.Nature, "事实", StringComparison.OrdinalIgnoreCase)
                        ? IntelligenceStatementNature.Fact
                        : IntelligenceStatementNature.Inference,
                    Topic = evidence.Topic,
                    Text = evidence.Statement,
                    Evidence = evidence.Evidence,
                    Source = evidence.Source,
                    Confidence = Math.Clamp(evidence.Confidence, 0, 1),
                    ObservedAt = latestReport!.CreatedTime
                });
            }
        }
        foreach (var evidence in lead.Evidence)
        {
            profile.Statements.Add(new CustomerIntelligenceStatement
            {
                Nature = IntelligenceStatementNature.Inference,
                Topic = evidence.Field,
                Text = evidence.Interpretation,
                Evidence = evidence.Value,
                Source = "Lead Intelligence",
                Confidence = profile.Confidence,
                ObservedAt = lead.LastAnalyzedAt ?? lead.UpdatedAt
            });
        }
        profile.Statements.Add(new CustomerIntelligenceStatement
        {
            Nature = IntelligenceStatementNature.Recommendation,
            Topic = "下一步动作",
            Text = profile.NextBestAction,
            Evidence = string.Join("；", profile.OpportunitySignals.Take(4)),
            Source = latestReport is null ? "Lead Intelligence / Customer Brain" : $"客户情报报告 V{latestReport.Version}",
            Confidence = profile.Confidence,
            ObservedAt = DateTimeOffset.Now
        });
        AddCoverageGaps(profile.Statements, coverage);
        profile.Statements = profile.Statements
            .Where(statement => !string.IsNullOrWhiteSpace(statement.Text))
            .GroupBy(statement => $"{statement.Nature}|{statement.Topic}|{statement.Text}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (profile.BusinessModels.Count == 0) profile.BusinessModels.Add("主要经营渠道待核实");
        if (profile.PurchaseMotivations.Count == 0) profile.PurchaseMotivations.Add("尚无足够证据确认购买动机");
        if (profile.PainPoints.Count == 0) profile.PainPoints.Add("尚无足够客户原话确认核心痛点");
        if (profile.OpportunitySignals.Count == 0) profile.OpportunitySignals.Add("尚无 AI 验证的明确购买信号");
        if (profile.Risks.Count == 0) profile.Risks.Add("当前资料有限，销售结论需要人工复核");
        return profile;
    }

    private async Task SynchronizeRecommendationAsync(CustomerIntelligenceProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.NextBestAction)) return;
        var history = await _repository.GetAiRecommendationHistoryAsync(profile.CustomerId, cancellationToken);
        var active = history.FirstOrDefault(item => item.Status is AiRecommendationStatus.Proposed
            or AiRecommendationStatus.Accepted
            or AiRecommendationStatus.InProgress);
        if (active is not null && string.Equals(active.Action.Trim(), profile.NextBestAction.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        if (active is not null)
        {
            active.Status = AiRecommendationStatus.Superseded;
            await _repository.SaveAiRecommendationAsync(active, cancellationToken);
        }
        await _repository.SaveAiRecommendationAsync(new AiRecommendationRecord
        {
            CustomerId = profile.CustomerId,
            Title = "Customer Brain 下一步建议",
            Action = profile.NextBestAction,
            Rationale = profile.Summary,
            Evidence = profile.Statements
                .Where(statement => statement.Nature is IntelligenceStatementNature.Fact or IntelligenceStatementNature.Inference)
                .Select(statement => statement.Evidence)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            Confidence = profile.Confidence,
            SourceProfileId = profile.Id,
            SourceProfileVersion = profile.Version
        }, cancellationToken);
    }

    private static void AddCrmFacts(ICollection<CustomerIntelligenceStatement> statements, Lead lead)
    {
        void Add(string topic, string text, string evidence)
        {
            if (string.IsNullOrWhiteSpace(evidence)) return;
            statements.Add(new CustomerIntelligenceStatement
            {
                Nature = IntelligenceStatementNature.Fact,
                Topic = topic,
                Text = text,
                Evidence = evidence,
                Source = "CRM",
                Confidence = 1,
                ObservedAt = lead.UpdatedAt
            });
        }
        Add("客户身份", $"客户姓名或账号为 {lead.DisplayName}。", lead.DisplayName);
        Add("市场", $"客户国家或地区为 {lead.Country}。", lead.Country);
        Add("联系方式", $"客户 WhatsApp 号码为 {lead.PhoneE164}。", lead.PhoneE164);
        Add("邮箱", $"客户邮箱为 {lead.Email}。", lead.Email);
        Add("产品方向", $"客户产品方向为 {lead.ProductInterest}。", lead.ProductInterest);
        foreach (var field in lead.CustomFields.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
            Add(field.Key, $"{field.Key}：{field.Value}", field.Value);
    }

    private static void AddCoverageGaps(ICollection<CustomerIntelligenceStatement> statements, CustomerIntelligenceCoverage coverage)
    {
        void Gap(bool available, string text)
        {
            if (available) return;
            statements.Add(new CustomerIntelligenceStatement
            {
                Nature = IntelligenceStatementNature.InformationGap,
                Topic = "数据缺口",
                Text = text,
                Source = "Customer Brain coverage",
                Confidence = 1
            });
        }
        Gap(coverage.HasWhatsAppHistory, "暂无该客户的正常 WhatsApp 历史消息。");
        Gap(coverage.HasEmailHistory, "暂无该客户的邮件历史。");
        Gap(coverage.HasLeadAnalysis, "尚未完成有效的 Lead Intelligence 分析。");
        Gap(coverage.HasCustomerReport, "尚未生成成功的客户情报报告。");
        Gap(coverage.HasCampaignHistory, "暂无该客户的自动化触达历史。");
    }

    private static bool HasCrmData(Lead lead) =>
        !string.IsNullOrWhiteSpace(lead.DisplayName)
        || !string.IsNullOrWhiteSpace(lead.PhoneE164)
        || !string.IsNullOrWhiteSpace(lead.Email)
        || lead.CustomFields.Any(item => !string.IsNullOrWhiteSpace(item.Value));

    private static string ComputeSourceHash(
        Lead lead,
        IReadOnlyList<WhatsAppMessage> whatsApp,
        IReadOnlyList<EmailMessage> emails,
        IReadOnlyList<CustomerCampaignTouch> campaignTouches,
        CustomerAnalysisReport? latestReport)
    {
        var payload = new
        {
            lead = new
            {
                lead.Name, lead.Company, lead.Country, lead.PhoneE164, lead.Email, lead.ProductInterest, lead.Tags, lead.CustomFields,
                lead.Stage, lead.Score, lead.Grade, lead.AnalysisContractVersion, lead.AiScoreApplied, lead.AnalysisStatus,
                lead.ProfileSummary, lead.CustomerSegment, lead.NextAction, lead.RiskWarning, lead.Risks, lead.ScoreFactors,
                lead.BehaviorSignals, lead.Evidence, lead.AnalysisConfidence, lead.LastAnalyzedAt
            },
            whatsApp = whatsApp.Select(message => new
            {
                message.Id, message.Direction, message.Status, message.Kind, message.Body, message.FileName,
                message.IsRevoked, message.Timestamp, message.DeliveredAt, message.ReadAt
            }),
            emails = emails.Select(message => new
            {
                message.Id, message.Direction, message.Status, message.Subject, message.TextBody, message.Timestamp
            }),
            campaigns = campaignTouches.Select(touch => new
            {
                touch.CampaignId, touch.Status, touch.ScheduledAt, touch.SentAt, touch.LastError, touch.Message
            }),
            report = latestReport is null ? null : new { latestReport.Id, latestReport.Version, latestReport.Status, latestReport.UpdatedTime }
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(payload)))).ToLowerInvariant();
    }

    private static string StableId(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values)))).ToLowerInvariant()[..32];

    private static string Summarize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : $"{normalized[..237]}...";
    }

    private static string FirstUseful(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static List<string> Clean(params IEnumerable<string>?[] sources) =>
        sources.Where(source => source is not null)
            .SelectMany(source => source!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
