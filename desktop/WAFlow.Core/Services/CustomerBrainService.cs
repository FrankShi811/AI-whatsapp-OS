using System.Security.Cryptography;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

/// <summary>
/// Materializes one evidence-aware customer view from the existing CRM, channel,
/// analysis and campaign stores. AI decisions are generated through the configured
/// structured provider, while authoritative CRM fields remain user-controlled.
/// </summary>
public sealed class CustomerBrainService
{
    private readonly LocalRepository _repository;
    private readonly IStructuredAiProvider? _provider;

    public CustomerBrainService(LocalRepository repository, IStructuredAiProvider? provider = null)
    {
        _repository = repository;
        _provider = provider;
    }

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

    public async Task<CustomerIntelligenceProfile> AnalyzeAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var profile = await RefreshAsync(customerId, cancellationToken);
        var lead = await _repository.GetLeadAsync(customerId, cancellationToken)
            ?? throw new InvalidOperationException("\u5ba2\u6237\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664\u3002");
        if (_provider is null || !_provider.HasApiKey())
            throw new InvalidOperationException("\u8bf7\u5148\u5728 API \u5bf9\u63a5\u4e2d\u914d\u7f6e\u53ef\u7528\u7684 AI Provider \u548c\u6a21\u578b\u3002");

        var timeline = await _repository.GetCustomerBehaviorTimelineAsync(customerId, cancellationToken);
        var reports = await _repository.GetCustomerAnalysisReportsAsync(customerId, cancellationToken);
        var recommendations = await _repository.GetAiRecommendationHistoryAsync(customerId, cancellationToken);
        var actions = await _repository.GetSalesActionsAsync(customerId, cancellationToken);
        var feedback = await _repository.GetAiLearningFeedbackAsync(customerId, cancellationToken);
        var sourceSnapshot = new
        {
            customer = new
            {
                lead.Id, lead.Name, lead.Company, lead.Country, lead.PhoneE164, lead.Email,
                lead.ProductInterest, lead.EstimatedOrderValue, lead.Currency, lead.Tags, lead.CustomFields,
                stage = lead.Stage.ToString(), lead.Score, lead.Grade, lead.PurchaseProbability,
                lead.ProfileSummary, lead.CustomerSegment, lead.NextAction, lead.Risks, lead.Evidence
            },
            coverage = profile.Coverage,
            verifiedStatements = profile.Statements.Where(statement => statement.Nature == IntelligenceStatementNature.Fact).Take(200),
            behaviorTimeline = timeline.Take(500),
            latestReport = reports.FirstOrDefault(report => report.Status == CustomerReportStatus.Succeeded)?.Report,
            recommendationHistory = recommendations.Take(20),
            salesActions = actions.Take(30),
            learningFeedback = feedback.Take(30)
        };
        var run = new CustomerBrainRun
        {
            CustomerId = customerId,
            Status = CustomerBrainRunStatus.Collecting,
            AiModel = await _provider.GetSelectedModelAsync(cancellationToken),
            SourceSnapshotHash = profile.SourceSnapshotHash,
            SourceSnapshotJson = Json.Serialize(sourceSnapshot)
        };
        await _repository.SaveCustomerBrainRunAsync(run, cancellationToken);

        try
        {
            run.Status = CustomerBrainRunStatus.Understanding;
            await _repository.SaveCustomerBrainRunAsync(run, cancellationToken);
            var understanding = await _provider.CompleteStructuredAsync<CustomerUnderstandingResult>(
                """
                You are the Customer Understanding stage of AI Sales OS, a personal AI sales employee.
                Use only the supplied customer snapshot. Return one camelCase JSON object without markdown.
                Required shape:
                {
                  "customerDna":"",
                  "profileSummary":"",
                  "customerType":"",
                  "businessModels":[""],
                  "painPoints":[""],
                  "purchaseMotivations":[""],
                  "informationGaps":[""],
                  "statements":[{"nature":"inference","topic":"","text":"","evidence":"","source":"","sourceId":"","confidence":0.0,"observedAt":"2026-01-01T00:00:00Z"}]
                }
                Write analysis in Simplified Chinese; preserve customer quotes in their original language.
                Never invent company, budget, quantity, channel, intent or decision timing.
                AI statements must be inference or informationGap. Facts remain authoritative only in the supplied verifiedStatements.
                Every inference needs non-empty evidence and source. Unknown information belongs in informationGaps.
                """,
                sourceSnapshot,
                ValidateUnderstanding,
                cancellationToken);
            run.UnderstandingJson = Json.Serialize(understanding);

            run.Status = CustomerBrainRunStatus.EvaluatingOpportunity;
            await _repository.SaveCustomerBrainRunAsync(run, cancellationToken);
            var opportunity = await _provider.CompleteStructuredAsync<CustomerOpportunityEvaluation>(
                """
                You are the Opportunity Evaluation stage of AI Sales OS.
                Return one camelCase JSON object without markdown:
                {
                  "purchaseProbability":0,
                  "confidence":0.0,
                  "suggestedStage":"new",
                  "positiveSignals":[""],
                  "riskSignals":[""],
                  "evidence":[""],
                  "rationale":""
                }
                purchaseProbability is 0..100 and is not the Lead Intelligence score.
                suggestedStage must be one of new, contacted, interested, requirementConfirmed, quotation, negotiation, waiting, customer, repeatPurchase, lost.
                Evaluate from explicit demand, quantity, budget, timing, objections, engagement and verified customer context.
                When evidence is insufficient, use a low probability/confidence and explain the information gap. Do not invent evidence.
                Write rationale and signals in Simplified Chinese; preserve quoted evidence in its original language.
                """,
                new { sourceSnapshot, understanding },
                ValidateOpportunity,
                cancellationToken);
            run.OpportunityJson = Json.Serialize(opportunity);

            run.Status = CustomerBrainRunStatus.Recommending;
            await _repository.SaveCustomerBrainRunAsync(run, cancellationToken);
            var recommendation = await _provider.CompleteStructuredAsync<CustomerSalesRecommendation>(
                """
                You are the Sales Recommendation stage of AI Sales OS, serving one salesperson.
                Return one camelCase JSON object without markdown:
                {
                  "nextBestAction":"",
                  "rationale":"",
                  "suggestedTalkTrack":"",
                  "questionsToVerify":[""],
                  "evidence":[""],
                  "dueInHours":24,
                  "priority":"normal"
                }
                priority must be low, normal, high or urgent. dueInHours must be 1..720.
                Give one concrete, human-controlled next action. Do not send messages, change CRM fields or promise price, stock or delivery.
                Base the recommendation only on supplied evidence and make missing validation questions explicit.
                Write in Simplified Chinese except for any suggested customer-facing talk track requested by context.
                """,
                new { sourceSnapshot, understanding, opportunity },
                ValidateRecommendation,
                cancellationToken);
            run.RecommendationJson = Json.Serialize(recommendation);

            ApplyDecision(profile, understanding, opportunity, recommendation, run);
            await _repository.SaveCustomerIntelligenceProfileAsync(profile, cancellationToken);
            var recommendationRecord = await SynchronizeRecommendationAsync(profile, cancellationToken);
            await SynchronizeFollowUpTaskAsync(profile, recommendation, recommendationRecord, run, cancellationToken);

            run.Status = CustomerBrainRunStatus.Succeeded;
            run.CompletedAt = DateTimeOffset.Now;
            await _repository.SaveCustomerBrainRunAsync(run, cancellationToken);
            await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
            {
                Id = StableId("event", customerId, "customer_brain_run", run.Id),
                CustomerId = customerId,
                EventType = "customer_brain_analyzed",
                Title = "Customer Brain \u5206\u6790\u5b8c\u6210",
                Detail = $"\u91c7\u8d2d\u6982\u7387 {profile.PurchaseProbability}%\uff0c\u7f6e\u4fe1\u5ea6 {profile.Confidence:P0}\uff0c\u5efa\u8bae\u9636\u6bb5 {Labels.Stage(profile.SuggestedStage)}\u3002",
                SourceType = "customer_brain_run",
                SourceId = run.Id,
                OccurredAt = run.CompletedAt.Value
            }, cancellationToken);
            await _repository.LogEventAsync(
                "customer_brain_analyzed",
                customerId,
                null,
                $"run_id={run.Id};model={run.AiModel};purchase_probability={profile.PurchaseProbability};confidence={profile.Confidence:F2}",
                cancellationToken);
            return profile;
        }
        catch (Exception error)
        {
            run.Status = CustomerBrainRunStatus.RetryableFailed;
            run.Error = error.Message;
            run.CompletedAt = DateTimeOffset.Now;
            await _repository.SaveCustomerBrainRunAsync(run, CancellationToken.None);
            if (!profile.HasCurrentDecision)
            {
                profile.DecisionStatus = CustomerBrainDecisionStatus.RetryableFailed;
                profile.LastBrainRunId = run.Id;
                await _repository.SaveCustomerIntelligenceProfileAsync(profile, CancellationToken.None);
            }
            await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
            {
                Id = StableId("event", customerId, "customer_brain_failed", run.Id),
                CustomerId = customerId,
                EventType = "customer_brain_failed",
                Title = "Customer Brain \u5206\u6790\u5931\u8d25\uff0c\u53ef\u91cd\u8bd5",
                Detail = error.Message,
                SourceType = "customer_brain_run",
                SourceId = run.Id,
                OccurredAt = run.CompletedAt.Value
            }, CancellationToken.None);
            throw;
        }
    }

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
                current?.Summary,
                report?.ExecutiveSummary.OneLinePositioning,
                lead.HasCurrentAiScore ? lead.ProfileSummary : null,
                $"{lead.DisplayName} 已进入客户工作区；当前商业背景和采购条件仍需通过沟通核实。"),
            CustomerType = FirstUseful(current?.CustomerType, report?.BasicProfile.CustomerType, lead.CustomerSegment, "客户类型待核实"),
            BusinessModels = Clean(current?.BusinessModels, report?.BasicProfile.BusinessModels),
            PurchaseMotivations = Clean(
                current?.PurchaseMotivations,
                report?.PurchaseMotivation.InterestReasons,
                report?.PurchaseMotivation.TriggerEvents),
            PainPoints = Clean(current?.PainPoints, report?.PainAnalysis.SurfacePains, report?.PainAnalysis.DeepBusinessProblems),
            OpportunitySignals = Clean(
                current?.OpportunitySignals,
                report?.OpportunityJudgment.PositiveFactors,
                report?.WhatsAppAnalysis.PurchaseSignals,
                lead.BehaviorSignals.Select(signal => signal.Signal)),
            Risks = Clean(
                current?.Risks,
                report?.RiskAnalysis.DealRisks,
                report?.RiskAnalysis.AdoptionRisks,
                report?.RiskAnalysis.ChurnRisks,
                lead.Risks,
                string.IsNullOrWhiteSpace(lead.RiskWarning) ? [] : [lead.RiskWarning]),
            NextBestAction = FirstUseful(
                current?.NextBestAction,
                report?.ExecutiveSummary.CurrentSalesRecommendation,
                report?.SalesStrategy.Actions.FirstOrDefault()?.Action,
                lead.HasCurrentAiScore ? lead.NextAction : null,
                "补齐客户业务模式、需求、预算、数量与决策时间后重新分析。"),
            Confidence = current?.Confidence ?? (lead.HasCurrentAiScore
                ? Math.Clamp(lead.AnalysisConfidence, 0, 1)
                : latestReport is null ? 0 : Math.Min(.75, Math.Max(.35, coverage.Percentage / 100d))),
            PurchaseProbability = current?.PurchaseProbability ?? lead.PurchaseProbability,
            SuggestedStage = current?.SuggestedStage ?? lead.Stage,
            DecisionStatus = ResolveDecisionStatus(current),
            DecisionSourceSnapshotHash = current?.DecisionSourceSnapshotHash ?? "",
            LastBrainRunId = current?.LastBrainRunId ?? "",
            LastBrainAnalyzedAt = current?.LastBrainAnalyzedAt,
            AiModel = FirstUseful(current?.AiModel, latestReport?.AiModel),
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

    private async Task<AiRecommendationRecord?> SynchronizeRecommendationAsync(CustomerIntelligenceProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.NextBestAction)) return null;
        var history = await _repository.GetAiRecommendationHistoryAsync(profile.CustomerId, cancellationToken);
        var active = history.FirstOrDefault(item => item.Status is AiRecommendationStatus.Proposed
            or AiRecommendationStatus.Accepted
            or AiRecommendationStatus.InProgress);
        if (active is not null && string.Equals(active.Action.Trim(), profile.NextBestAction.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(active.SuggestedTalkTrack) && !string.IsNullOrWhiteSpace(profile.SuggestedTalkTrack))
            {
                active.SuggestedTalkTrack = profile.SuggestedTalkTrack;
                await _repository.SaveAiRecommendationAsync(active, cancellationToken);
            }
            return active;
        }

        if (active is not null)
        {
            active.Status = AiRecommendationStatus.Superseded;
            await _repository.SaveAiRecommendationAsync(active, cancellationToken);
        }
        var created = new AiRecommendationRecord
        {
            CustomerId = profile.CustomerId,
            Title = "Customer Brain 下一步建议",
            Action = profile.NextBestAction,
            Rationale = profile.Summary,
            SuggestedTalkTrack = profile.SuggestedTalkTrack,
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
        };
        await _repository.SaveAiRecommendationAsync(created, cancellationToken);
        return created;
    }

    private static CustomerBrainDecisionStatus ResolveDecisionStatus(CustomerIntelligenceProfile? current)
    {
        if (current is null) return CustomerBrainDecisionStatus.NotAnalyzed;
        if (!string.IsNullOrWhiteSpace(current.DecisionSourceSnapshotHash))
            return CustomerBrainDecisionStatus.Stale;
        return current.DecisionStatus == CustomerBrainDecisionStatus.RetryableFailed
            ? CustomerBrainDecisionStatus.RetryableFailed
            : CustomerBrainDecisionStatus.NotAnalyzed;
    }

    private static void ApplyDecision(
        CustomerIntelligenceProfile profile,
        CustomerUnderstandingResult understanding,
        CustomerOpportunityEvaluation opportunity,
        CustomerSalesRecommendation recommendation,
        CustomerBrainRun run)
    {
        profile.Version++;
        profile.Summary = understanding.ProfileSummary.Trim();
        profile.CustomerType = understanding.CustomerType.Trim();
        profile.BusinessModels = Clean(understanding.BusinessModels);
        profile.PurchaseMotivations = Clean(understanding.PurchaseMotivations);
        profile.PainPoints = Clean(understanding.PainPoints);
        profile.OpportunitySignals = Clean(opportunity.PositiveSignals);
        profile.Risks = Clean(opportunity.RiskSignals);
        profile.NextBestAction = recommendation.NextBestAction.Trim();
        profile.SuggestedTalkTrack = recommendation.SuggestedTalkTrack.Trim();
        profile.Confidence = Math.Clamp(opportunity.Confidence, 0, 1);
        profile.PurchaseProbability = Math.Clamp(opportunity.PurchaseProbability, 0, 100);
        profile.SuggestedStage = opportunity.SuggestedStage;
        profile.DecisionStatus = CustomerBrainDecisionStatus.Current;
        profile.DecisionSourceSnapshotHash = profile.SourceSnapshotHash;
        profile.LastBrainRunId = run.Id;
        profile.LastBrainAnalyzedAt = DateTimeOffset.Now;
        profile.AiModel = run.AiModel;

        var facts = profile.Statements
            .Where(statement => statement.Nature == IntelligenceStatementNature.Fact)
            .ToList();
        var statements = new List<CustomerIntelligenceStatement>(facts);
        foreach (var statement in understanding.Statements)
        {
            statement.Nature = statement.Nature == IntelligenceStatementNature.InformationGap
                ? IntelligenceStatementNature.InformationGap
                : IntelligenceStatementNature.Inference;
            statement.Confidence = Math.Clamp(statement.Confidence, 0, 1);
            statement.ObservedAt = statement.ObservedAt == default ? DateTimeOffset.Now : statement.ObservedAt;
            statements.Add(statement);
        }
        foreach (var gap in understanding.InformationGaps.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            statements.Add(new CustomerIntelligenceStatement
            {
                Nature = IntelligenceStatementNature.InformationGap,
                Topic = "待核实问题",
                Text = gap.Trim(),
                Source = $"Customer Brain · {run.AiModel}",
                SourceId = run.Id,
                Confidence = 1,
                ObservedAt = DateTimeOffset.Now
            });
        }
        statements.Add(new CustomerIntelligenceStatement
        {
            Nature = IntelligenceStatementNature.Inference,
            Topic = "商机机会判断",
            Text = opportunity.Rationale.Trim(),
            Evidence = string.Join("；", opportunity.Evidence),
            Source = $"Customer Brain · {run.AiModel}",
            SourceId = run.Id,
            Confidence = profile.Confidence,
            ObservedAt = DateTimeOffset.Now
        });
        statements.Add(new CustomerIntelligenceStatement
        {
            Nature = IntelligenceStatementNature.Recommendation,
            Topic = "下一步动作",
            Text = profile.NextBestAction,
            Evidence = string.Join("；", recommendation.Evidence),
            Source = $"Customer Brain · {run.AiModel}",
            SourceId = run.Id,
            Confidence = profile.Confidence,
            ObservedAt = DateTimeOffset.Now
        });
        profile.Statements = statements
            .Where(statement => !string.IsNullOrWhiteSpace(statement.Text))
            .GroupBy(statement => $"{statement.Nature}|{statement.Topic}|{statement.Text}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (profile.BusinessModels.Count == 0) profile.BusinessModels.Add("主要经营渠道待核实");
        if (profile.PurchaseMotivations.Count == 0) profile.PurchaseMotivations.Add("尚无足够证据确认购买动机");
        if (profile.PainPoints.Count == 0) profile.PainPoints.Add("尚无足够客户原话确认核心痛点");
        if (profile.OpportunitySignals.Count == 0) profile.OpportunitySignals.Add("尚无 AI 验证的明确购买信号");
        if (profile.Risks.Count == 0) profile.Risks.Add("当前资料有限，销售结论需要人工复核");
    }

    private async Task SynchronizeFollowUpTaskAsync(
        CustomerIntelligenceProfile profile,
        CustomerSalesRecommendation recommendation,
        AiRecommendationRecord? recommendationRecord,
        CustomerBrainRun run,
        CancellationToken cancellationToken)
    {
        var sourceId = recommendationRecord?.Id ?? run.Id;
        var task = new FollowUpTask
        {
            Id = StableId("follow_up", profile.CustomerId, "customer_brain", sourceId),
            CustomerId = profile.CustomerId,
            RecommendationId = recommendationRecord?.Id ?? "",
            Title = recommendation.NextBestAction.Trim(),
            Reason = recommendation.Rationale.Trim(),
            Priority = recommendation.Priority,
            Status = FollowUpTaskStatus.Proposed,
            DueAt = DateTimeOffset.Now.AddHours(recommendation.DueInHours),
            SourceType = "customer_brain",
            SourceId = sourceId
        };
        await _repository.UpsertFollowUpTaskAsync(task, cancellationToken);
        await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
        {
            Id = StableId("event", profile.CustomerId, "follow_up_proposed", sourceId),
            CustomerId = profile.CustomerId,
            EventType = "follow_up_proposed",
            Title = "AI 提出新的跟进任务",
            Detail = $"{task.Title}；建议在 {task.DueAt:yyyy-MM-dd HH:mm} 前处理。",
            SourceType = "follow_up_task",
            SourceId = sourceId,
            OccurredAt = DateTimeOffset.Now
        }, cancellationToken);
    }

    private static string? ValidateUnderstanding(CustomerUnderstandingResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CustomerDna)) return "customerDna 不能为空。";
        if (string.IsNullOrWhiteSpace(result.ProfileSummary)) return "profileSummary 不能为空。";
        if (string.IsNullOrWhiteSpace(result.CustomerType)) return "customerType 不能为空。";
        foreach (var statement in result.Statements)
        {
            if (statement.Nature is not IntelligenceStatementNature.Inference and not IntelligenceStatementNature.InformationGap)
                return "Customer Understanding 只能返回 inference 或 informationGap，不能把 AI 判断写成事实。";
            if (string.IsNullOrWhiteSpace(statement.Text)) return "statements.text 不能为空。";
            if (statement.Nature == IntelligenceStatementNature.Inference
                && (string.IsNullOrWhiteSpace(statement.Evidence) || string.IsNullOrWhiteSpace(statement.Source)))
                return "每条 inference 都必须提供 evidence 和 source。";
            if (statement.Confidence is < 0 or > 1) return "statements.confidence 必须在 0 到 1 之间。";
        }
        return null;
    }

    private static string? ValidateOpportunity(CustomerOpportunityEvaluation result)
    {
        if (result.PurchaseProbability is < 0 or > 100) return "purchaseProbability 必须在 0 到 100 之间。";
        if (result.Confidence is < 0 or > 1) return "confidence 必须在 0 到 1 之间。";
        if (string.IsNullOrWhiteSpace(result.Rationale)) return "rationale 不能为空。";
        if (result.Evidence.Count == 0 || result.Evidence.All(string.IsNullOrWhiteSpace))
            return "机会判断必须包含至少一条 evidence；资料不足也要明确写出缺口证据。";
        return null;
    }

    private static string? ValidateRecommendation(CustomerSalesRecommendation result)
    {
        if (string.IsNullOrWhiteSpace(result.NextBestAction)) return "nextBestAction 不能为空。";
        if (string.IsNullOrWhiteSpace(result.Rationale)) return "rationale 不能为空。";
        if (result.DueInHours is < 1 or > 720) return "dueInHours 必须在 1 到 720 之间。";
        if (result.Evidence.Count == 0 || result.Evidence.All(string.IsNullOrWhiteSpace))
            return "销售建议必须包含至少一条 evidence。";
        return null;
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
                lead.BehaviorSignals, lead.Evidence, lead.AnalysisConfidence, lead.PurchaseProbability, lead.LastAnalyzedAt
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
