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
            "interested" or "qualified" or "有兴趣" or "已确认需求" => LeadStage.Interested,
            "negotiation" or "quote_evaluation" or "谈判中" or "报价评估" => LeadStage.Negotiation,
            "waiting" or "等待中" => LeadStage.Waiting,
            "customer" or "won" or "已成交" or "客户" => LeadStage.Customer,
            "lost" or "已流失" => LeadStage.Lost,
            _ => LeadStage.New
        };
    }
}
