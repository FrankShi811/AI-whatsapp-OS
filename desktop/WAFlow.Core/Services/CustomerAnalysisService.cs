using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class CustomerAnalysisService
{
    private const int TotalSteps = 5;
    private readonly LocalRepository _repository;
    private readonly IStructuredAiProvider _provider;

    public CustomerAnalysisService(LocalRepository repository, IStructuredAiProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public async Task<CustomerAnalysisReport> GenerateAsync(
        string customerId,
        IProgress<CustomerAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_provider.HasApiKey()) throw new DeepSeekException("provider_not_configured", "请先完成 AI API 对接并选择模型。", false);
        var lead = await _repository.GetLeadAsync(customerId, cancellationToken) ?? throw new InvalidOperationException("客户不存在或已经删除。");
        var model = await _provider.GetSelectedModelAsync(cancellationToken);
        var report = new CustomerAnalysisReport
        {
            CustomerId = lead.Id,
            CustomerName = lead.DisplayName,
            AiModel = model,
            Version = await _repository.GetNextCustomerReportVersionAsync(lead.Id, cancellationToken),
            Status = CustomerReportStatus.Running
        };
        await _repository.SaveCustomerAnalysisReportAsync(report, cancellationToken);

        try
        {
            var snapshot = await RunStepAsync(report, "data_assembly", 1, progress, "正在整合 CRM、WhatsApp、商机评分、群发与历史轨迹",
                () => BuildSnapshotAsync(lead, cancellationToken), cancellationToken);
            report.SourceSnapshot = snapshot;
            await _repository.SaveCustomerAnalysisReportAsync(report, cancellationToken);

            var facts = await RunStepAsync(report, "fact_extraction", 2, progress, "正在逐段读取全部聊天并提取可核验事实",
                () => ExtractFactsAsync(snapshot, cancellationToken), cancellationToken);
            var business = await RunStepAsync(report, "commercial_analysis", 3, progress, "正在区分事实、商业判断与风险",
                () => AnalyzeBusinessAsync(snapshot, facts, cancellationToken), cancellationToken);
            var strategy = await RunStepAsync(report, "sales_strategy", 4, progress, "正在生成 24 小时、7 天与 30 天推进策略",
                () => BuildSalesStrategyAsync(snapshot, facts, business, cancellationToken), cancellationToken);
            var synthesis = await RunStepAsync(report, "report_generation", 5, progress, "正在生成管理层摘要与最终报告",
                () => SynthesizeReportAsync(snapshot, facts, business, strategy, cancellationToken), cancellationToken);

            business.ExecutiveSummary.OverallValueJudgment = synthesis.OverallValueJudgment;
            business.ExecutiveSummary.CurrentSalesRecommendation = synthesis.CurrentSalesRecommendation;
            business.OpportunityJudgment.DealProbability = synthesis.DealProbability;
            report.Report = new CustomerIntelligenceReportContent
            {
                ExecutiveSummary = business.ExecutiveSummary,
                BasicProfile = business.BasicProfile,
                BusinessBackground = business.BusinessBackground,
                PainAnalysis = business.PainAnalysis,
                PurchaseMotivation = business.PurchaseMotivation,
                WhatsAppAnalysis = business.WhatsAppAnalysis,
                OpportunityJudgment = business.OpportunityJudgment,
                ProductFit = business.ProductFit,
                SalesStrategy = strategy,
                RiskAnalysis = business.RiskAnalysis,
                ManagementSummary = synthesis.ManagementSummary,
                EvidenceLedger = facts.Facts
            };
            report.Status = CustomerReportStatus.Succeeded;
            report.Error = "";
            await _repository.SaveCustomerAnalysisReportAsync(report, cancellationToken);
            await _repository.LogEventAsync("customer_intelligence_report_generated", lead.Id, null, $"report_id={report.Id};version={report.Version};model={model};whatsapp_messages={snapshot.WhatsAppMessages.Count};email_messages={snapshot.EmailMessages.Count}", cancellationToken);
            return report;
        }
        catch (OperationCanceledException)
        {
            report.Status = CustomerReportStatus.RetryableFailed;
            report.Error = "用户取消了报告生成，可重新分析。";
            await _repository.SaveCustomerAnalysisReportAsync(report, CancellationToken.None);
            throw;
        }
        catch (Exception error)
        {
            report.Status = CustomerReportStatus.RetryableFailed;
            report.Error = error is DeepSeekException dse ? $"{dse.Code}: {dse.Message}" : error.Message;
            await _repository.SaveCustomerAnalysisReportAsync(report, cancellationToken);
            await _repository.LogEventAsync("customer_intelligence_report_failed", lead.Id, null, $"report_id={report.Id};version={report.Version};error={report.Error}", cancellationToken);
            throw;
        }
    }

    public Task<List<CustomerAnalysisReport>> GetHistoryAsync(string customerId, CancellationToken cancellationToken = default) =>
        _repository.GetCustomerAnalysisReportsAsync(customerId, cancellationToken);

    private async Task<T> RunStepAsync<T>(
        CustomerAnalysisReport report,
        string stepKey,
        int sequence,
        IProgress<CustomerAnalysisProgress>? progress,
        string message,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(stepKey, sequence, TotalSteps, message));
        var step = new CustomerAnalysisReportStep
        {
            ReportId = report.Id, StepKey = stepKey, Sequence = sequence,
            Status = CustomerReportStepStatus.Running
        };
        await _repository.SaveCustomerAnalysisStepAsync(step, cancellationToken);
        try
        {
            var result = await action();
            step.Status = CustomerReportStepStatus.Succeeded;
            step.ResultJson = Json.Serialize(result);
            step.Error = "";
            await _repository.SaveCustomerAnalysisStepAsync(step, cancellationToken);
            return result;
        }
        catch (Exception error)
        {
            step.Status = CustomerReportStepStatus.RetryableFailed;
            step.Error = error is DeepSeekException dse ? $"{dse.Code}: {dse.Message}" : error.Message;
            await _repository.SaveCustomerAnalysisStepAsync(step, cancellationToken);
            throw;
        }
    }

    private async Task<CustomerIntelligenceSourceSnapshot> BuildSnapshotAsync(Lead lead, CancellationToken cancellationToken)
    {
        var messages = await _repository.GetWhatsAppMessagesForLeadAsync(lead, 5000, cancellationToken);
        var emailMessages = await _repository.GetEmailMessagesForLeadAsync(lead.Id, 5000, cancellationToken);
        var campaigns = await _repository.GetCampaignsAsync(null, cancellationToken);
        var touches = new List<CustomerCampaignTouch>();
        foreach (var campaign in campaigns)
        {
            foreach (var recipient in (await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken)).Where(item => item.LeadId == lead.Id))
                touches.Add(new CustomerCampaignTouch
                {
                    CampaignId = campaign.Id, CampaignName = campaign.Name, Channel = campaign.ChannelLabel, Message = recipient.RenderedMessage,
                    Status = recipient.StatusLabel, ScheduledAt = recipient.ScheduledAt, SentAt = recipient.SentAt, LastError = recipient.LastError
                });
        }
        var timeline = await _repository.GetCustomerHistoryAsync(lead.Id, cancellationToken);
        timeline.Insert(0, new CustomerHistoryEvent { Type = "customer_created", Detail = "客户资料进入 AI Sales OS", CreatedAt = lead.CreatedAt });
        timeline.Add(new CustomerHistoryEvent { Type = "customer_snapshot", Detail = $"当前阶段：{lead.StageLabel}；当前等级：{(lead.HasCurrentAiScore ? lead.Grade : "D")}", CreatedAt = lead.UpdatedAt });
        return new CustomerIntelligenceSourceSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Lead = lead,
            WhatsAppMessages = messages,
            EmailMessages = emailMessages,
            CampaignTouches = touches.OrderBy(item => item.ScheduledAt).ToList(),
            Timeline = timeline.OrderBy(item => item.CreatedAt).ToList(),
            LeadAnalysisHistory = await _repository.GetLeadAnalysisHistoryAsync(lead.Id, cancellationToken)
        };
    }

    private async Task<CustomerFactSet> ExtractFactsAsync(CustomerIntelligenceSourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var result = BuildDeterministicFacts(snapshot);
        var batches = BuildMessageBatches(snapshot.WhatsAppMessages);
        if (batches.Count == 0)
        {
            result.InformationGaps.Add("系统中暂无该客户的 WhatsApp 历史消息，沟通判断可信度受限。");
            return result;
        }

        foreach (var batch in batches)
        {
            var payload = new
            {
                customer = new { snapshot.Lead.Name, snapshot.Lead.Country, snapshot.Lead.ProductInterest, snapshot.Lead.CustomFields },
                messages = batch.Select(message => new
                {
                    message.Id,
                    direction = message.Direction == WhatsAppMessageDirection.Incoming ? "客户" : "销售",
                    timestamp = message.Timestamp,
                    message.Kind,
                    message.Body,
                    message.FileName,
                    message.IsRevoked
                })
            };
            var extracted = await _provider.CompleteStructuredAsync<CustomerFactSet>(FactExtractionPrompt, payload, ValidateFactSet, cancellationToken);
            result.Facts.AddRange(extracted.Facts);
            result.Quotes.AddRange(extracted.Quotes);
            result.InformationGaps.AddRange(extracted.InformationGaps);
        }
        result.Facts = result.Facts
            .Where(item => !string.IsNullOrWhiteSpace(item.Statement))
            .GroupBy(item => $"{item.Topic}|{item.Statement}", StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        result.Quotes = result.Quotes
            .Where(item => !string.IsNullOrWhiteSpace(item.Original))
            .GroupBy(item => item.Original.Trim(), StringComparer.Ordinal).Select(group => group.First()).Take(20).ToList();
        result.InformationGaps = result.InformationGaps.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
        return result;
    }

    private static CustomerFactSet BuildDeterministicFacts(CustomerIntelligenceSourceSnapshot snapshot)
    {
        var lead = snapshot.Lead;
        var facts = new CustomerFactSet();
        void Add(string topic, string statement, string evidence, string source = "CRM") => facts.Facts.Add(new ReportStatement
        {
            Nature = "事实", Topic = topic, Statement = statement, Evidence = evidence, Source = source, Confidence = 1
        });
        if (!string.IsNullOrWhiteSpace(lead.Name)) Add("身份", $"客户姓名或账号为 {lead.Name}。", lead.Name);
        if (!string.IsNullOrWhiteSpace(lead.Company)) Add("公司", $"客户资料记录的公司为 {lead.Company}。", lead.Company);
        if (!string.IsNullOrWhiteSpace(lead.Country)) Add("市场", $"客户所在国家或地区为 {lead.Country}。", lead.Country);
        if (!string.IsNullOrWhiteSpace(lead.PhoneE164)) Add("联系方式", $"客户 WhatsApp 号码为 {lead.PhoneE164}。", lead.PhoneE164);
        if (!string.IsNullOrWhiteSpace(lead.ProductInterest)) Add("产品方向", $"客户产品兴趣为 {lead.ProductInterest}。", lead.ProductInterest);
        Add("销售状态", $"CRM 当前阶段为 {lead.StageLabel}，AI 等级为 {(lead.HasCurrentAiScore ? lead.Grade : "D")}，评分为 {(lead.HasCurrentAiScore ? lead.Score : 0)}。", $"stage={lead.Stage}; score={lead.Score}; grade={lead.Grade}", "Lead Intelligence");
        foreach (var field in lead.CustomFields.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
            Add("导入字段", $"{field.Key}：{field.Value}", field.Value, "Excel/CRM 自定义字段");
        foreach (var touch in snapshot.CampaignTouches)
            Add("自动化触达", $"群发任务“{touch.CampaignName}”状态为 {touch.Status}。", touch.Message, "WhatsApp Automation");
        return facts;
    }

    private async Task<CustomerBusinessAnalysisResult> AnalyzeBusinessAsync(CustomerIntelligenceSourceSnapshot snapshot, CustomerFactSet facts, CancellationToken cancellationToken)
    {
        var lead = snapshot.Lead;
        var payload = new
        {
            crm = new { lead.Name, lead.Company, lead.Country, lead.ProductInterest, lead.Stage, lead.Owner, lead.Tags, lead.CustomFields },
            leadIntelligence = new
            {
                score = lead.HasCurrentAiScore ? lead.Score : 0,
                grade = lead.HasCurrentAiScore ? lead.Grade : "D",
                lead.ScoreFactors, lead.BehaviorSignals, lead.ProfileSummary, lead.NextAction, lead.RiskWarning, lead.AnalysisConfidence
            },
            facts,
            emailMessages = snapshot.EmailMessages.Select(message => new
            {
                message.Id,
                direction = message.Direction,
                message.Timestamp,
                message.Subject,
                message.TextBody,
                message.FromAddress,
                message.ToAddresses
            }),
            campaignTouches = snapshot.CampaignTouches,
            timeline = snapshot.Timeline
        };
        var analysis = await _provider.CompleteStructuredAsync<CustomerBusinessAnalysisResult>(BusinessAnalysisPrompt, payload,
            value => ValidateBusinessAnalysis(value, lead), cancellationToken);
        analysis.OpportunityJudgment.AiScore = lead.HasCurrentAiScore ? lead.Score : 0;
        analysis.OpportunityJudgment.Grade = lead.HasCurrentAiScore ? lead.Grade : "D";
        analysis.OpportunityJudgment.DimensionScores = lead.HasCurrentAiScore ? lead.ScoreFactors : [];
        analysis.WhatsAppAnalysis.Quotes = facts.Quotes.Take(8).ToList();
        return analysis;
    }

    private Task<CustomerSalesStrategy> BuildSalesStrategyAsync(CustomerIntelligenceSourceSnapshot snapshot, CustomerFactSet facts, CustomerBusinessAnalysisResult business, CancellationToken cancellationToken)
    {
        var payload = new
        {
            customer = new { snapshot.Lead.Name, snapshot.Lead.Country, snapshot.Lead.Stage, snapshot.Lead.ProductInterest },
            facts,
            business,
            constraint = "只能基于证据提出销售建议，不得承诺价格、库存、认证、交付时间或折扣。"
        };
        return _provider.CompleteStructuredAsync<CustomerSalesStrategy>(SalesStrategyPrompt, payload, ValidateSalesStrategy, cancellationToken);
    }

    private Task<CustomerReportSynthesisResult> SynthesizeReportAsync(CustomerIntelligenceSourceSnapshot snapshot, CustomerFactSet facts, CustomerBusinessAnalysisResult business, CustomerSalesStrategy strategy, CancellationToken cancellationToken)
    {
        var payload = new { customer = snapshot.Lead.DisplayName, facts, business, strategy };
        return _provider.CompleteStructuredAsync<CustomerReportSynthesisResult>(ReportSynthesisPrompt, payload, ValidateSynthesis, cancellationToken);
    }

    private static List<List<WhatsAppMessage>> BuildMessageBatches(IReadOnlyList<WhatsAppMessage> messages)
    {
        var result = new List<List<WhatsAppMessage>>();
        var current = new List<WhatsAppMessage>();
        var characters = 0;
        foreach (var message in messages)
        {
            var size = (message.Body?.Length ?? 0) + (message.FileName?.Length ?? 0) + 120;
            if (current.Count > 0 && (current.Count >= 80 || characters + size > 24000))
            {
                result.Add(current); current = []; characters = 0;
            }
            current.Add(message); characters += size;
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    private static string? ValidateFactSet(CustomerFactSet value)
    {
        if (value.Facts.Any(item => item.Nature != "事实" || string.IsNullOrWhiteSpace(item.Topic) || string.IsNullOrWhiteSpace(item.Statement) || string.IsNullOrWhiteSpace(item.Evidence) || string.IsNullOrWhiteSpace(item.Source)))
            return "事实提取结果缺少事实类型、证据或来源。";
        if (value.Quotes.Any(item => string.IsNullOrWhiteSpace(item.Original) || string.IsNullOrWhiteSpace(item.ChineseMeaning) || string.IsNullOrWhiteSpace(item.AiAnalysis)))
            return "WhatsApp 引用必须包含原文、中文含义和 AI 分析。";
        return null;
    }

    private static string? ValidateBusinessAnalysis(CustomerBusinessAnalysisResult value, Lead lead)
    {
        if (string.IsNullOrWhiteSpace(value.ExecutiveSummary.OneLinePositioning) || string.IsNullOrWhiteSpace(value.ExecutiveSummary.CustomerType) ||
            string.IsNullOrWhiteSpace(value.BasicProfile.CustomerType) || string.IsNullOrWhiteSpace(value.BusinessBackground.CurrentBusinessModel) ||
            string.IsNullOrWhiteSpace(value.WhatsAppAnalysis.EngagementLevel)) return "商业分析缺少客户定位、画像或沟通积极度。";
        if (!ContainsChinese(value.ExecutiveSummary.OneLinePositioning) || !ContainsChinese(value.BusinessBackground.CurrentBusinessModel))
            return "报告分析必须以简体中文输出。";
        if (value.OpportunityJudgment.DealProbability is < 0 or > 100) return "成交概率必须在 0 到 100 之间。";
        return null;
    }

    private static string? ValidateSalesStrategy(CustomerSalesStrategy value)
    {
        var windows = value.Actions.Select(item => item.Timeframe).ToList();
        if (value.Actions.Count < 3 || !windows.Any(item => item.Contains("24")) || !windows.Any(item => item.Contains("7")) || !windows.Any(item => item.Contains("30")))
            return "销售策略必须包含 24 小时、7 天和 30 天行动。";
        if (value.Actions.Any(item => string.IsNullOrWhiteSpace(item.Action) || string.IsNullOrWhiteSpace(item.Rationale) || string.IsNullOrWhiteSpace(item.SuccessCriterion)) || string.IsNullOrWhiteSpace(value.RecommendedTalkTrack))
            return "销售策略缺少行动、依据、成功标准或推荐话术。";
        return null;
    }

    private static string? ValidateSynthesis(CustomerReportSynthesisResult value)
    {
        if (value.ManagementSummary.Length is < 300 or > 500) return "管理层摘要必须控制在 300 到 500 字。";
        if (!ContainsChinese(value.ManagementSummary) || string.IsNullOrWhiteSpace(value.OverallValueJudgment) || string.IsNullOrWhiteSpace(value.CurrentSalesRecommendation))
            return "最终报告必须包含中文管理层摘要、价值判断和当前销售建议。";
        if (value.DealProbability is < 0 or > 100) return "成交概率必须在 0 到 100 之间。";
        return null;
    }

    private static bool ContainsChinese(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');

    private const string FactExtractionPrompt = """
        你是 AI Sales OS 的事实核验员。只读取输入中的 WhatsApp 消息，不做商业推断，不补充外部事实。
        返回一个 JSON 对象，属性名必须使用 camelCase：
        {"facts":[{"nature":"事实","topic":"","statement":"中文事实陈述","evidence":"客户原话或消息证据","source":"WhatsApp 消息ID","confidence":0.0}],"quotes":[{"original":"客户原文","chineseMeaning":"中文含义","aiAnalysis":"这句原话说明了什么，不得夸大","timestamp":"ISO-8601时间","source":"WhatsApp"}],"informationGaps":["缺失信息"]}
        事实陈述、中文含义、分析和缺口必须使用简体中文；客户原话保持原始语言。销售方发出的内容不能作为客户事实。撤回消息不得作为有效证据。
        没有可提取内容时返回空数组。不得输出 Markdown。
        """;

    private const string BusinessAnalysisPrompt = """
        你是专业 B2B 销售情报分析师。仅使用输入中的 CRM、Lead Intelligence、事实清单、WhatsApp 引用、群发触达和历史轨迹。
        输出必须以简体中文为主，平台名与客户原话可保留英文。不得把推断写成事实，不得发明公司、收入、团队、预算、采购量或渠道。
        返回严格 camelCase JSON，完整匹配以下结构：
        {
          "executiveSummary":{"oneLinePositioning":"","customerType":"","businessStage":"","overallValueJudgment":"待最终综合","currentSalesRecommendation":"待最终综合"},
          "basicProfile":{"customerType":"","businessModels":[],"productDirection":"","operatingScale":"","developmentStage":""},
          "businessBackground":{"currentBusinessModel":"","coreAdvantages":[],"currentLimitations":[],"growthOpportunities":[]},
          "painAnalysis":{"surfacePains":[],"deepBusinessProblems":[]},
          "purchaseMotivation":{"interestReasons":[],"triggerEvents":[],"decisionFactors":[]},
          "whatsAppAnalysis":{"engagementLevel":"","focusTopics":[],"purchaseSignals":[],"concerns":[],"quotes":[]},
          "opportunityJudgment":{"grade":"D","aiScore":0,"dealProbability":0,"positiveFactors":[],"negativeFactors":[],"dimensionScores":[]},
          "productFit":{"highMatchPoints":[],"lowMatchPoints":[],"questionsToValidate":[]},
          "riskAnalysis":{"dealRisks":[],"adoptionRisks":[],"churnRisks":[]}
        }
        如果缺少卖方产品资料，产品匹配必须明确写“尚无足够产品资料”，并把需要核实的问题放入 questionsToValidate。不得输出 Markdown。
        """;

    private const string SalesStrategyPrompt = """
        你是资深销售策略顾问。基于已核验事实和商业分析制定可执行计划，不得创造新事实或商业承诺。
        返回严格 camelCase JSON：
        {"actions":[{"timeframe":"24小时","action":"","rationale":"","successCriterion":""},{"timeframe":"7天","action":"","rationale":"","successCriterion":""},{"timeframe":"30天","action":"","rationale":"","successCriterion":""}],"recommendedTalkTrack":"中文推荐话术；必要的平台名可保留英文","pendingQuestions":[""]}
        所有分析与建议使用简体中文。不得输出 Markdown。
        """;

    private const string ReportSynthesisPrompt = """
        你是 AI Sales OS 的报告主编。根据事实、商业分析和销售策略生成管理层可直接复制到周报或月报的摘要。
        返回严格 camelCase JSON：{"managementSummary":"300至500字中文摘要","overallValueJudgment":"综合价值判断","currentSalesRecommendation":"当前最优先销售建议","dealProbability":0}
        摘要必须明确区分已知事实、AI判断和销售建议，不得加入输入中没有的事实，不得输出 Markdown或大段英文。
        """;
}
