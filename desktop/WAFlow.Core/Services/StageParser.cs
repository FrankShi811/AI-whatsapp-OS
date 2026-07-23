using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public static class StageParser
{
    public static LeadStage Parse(string? value)
    {
        var key = (value ?? "").Trim().ToLowerInvariant().Replace(" ", "_");
        return key switch
        {
            "contacted" or "已联系" => LeadStage.Contacted,
            "interested" or "qualified" or "有兴趣" => LeadStage.Interested,
            "requirement_confirmed" or "需求确认" or "已确认需求" => LeadStage.RequirementConfirmed,
            "quotation" or "quote_evaluation" or "报价" or "报价评估" => LeadStage.Quotation,
            "negotiation" or "谈判中" => LeadStage.Negotiation,
            "waiting" or "等待中" => LeadStage.Waiting,
            "customer" or "won" or "已成交" or "客户" => LeadStage.Customer,
            "repeat_purchase" or "repurchase" or "复购" or "已复购" => LeadStage.RepeatPurchase,
            "lost" or "已流失" => LeadStage.Lost,
            _ => LeadStage.New
        };
    }
}
