using System.Globalization;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public sealed class LeadScoringService
{
    public static readonly IReadOnlyDictionary<string, int> Weights = new Dictionary<string, int>
    {
        ["marketValue"] = 15, ["companyScale"] = 10, ["productFit"] = 20,
        ["purchasePower"] = 15, ["replyEngagement"] = 15, ["recency"] = 10,
        ["explicitDemand"] = 10, ["registeredOrConsulted"] = 5
    };

    public void Score(Lead lead)
    {
        var orderSignal = Math.Clamp((double)lead.EstimatedOrderValue / 25_000d, 0, 1);
        var productSignal = string.IsNullOrWhiteSpace(lead.ProductInterest) ? 0.25 : 0.8;
        var replySignal = string.IsNullOrWhiteSpace(lead.LatestMessage) ? 0 : 0.55;
        var recencySignal = lead.LastContactAt is null ? 0.2 : Math.Clamp(1 - (DateTimeOffset.Now - lead.LastContactAt.Value).TotalDays / 30d, 0.1, 1);
        var signals = new Dictionary<string, double>
        {
            ["marketValue"] = orderSignal,
            ["companyScale"] = Clamp01(lead.CompanyScale),
            ["productFit"] = productSignal,
            ["purchasePower"] = lead.PurchasePower > 0 ? Clamp01(lead.PurchasePower) : Math.Clamp(orderSignal * .8, 0.15, .8),
            ["replyEngagement"] = replySignal,
            ["recency"] = recencySignal,
            ["explicitDemand"] = lead.ExplicitDemand ? 1 : 0,
            ["registeredOrConsulted"] = lead.RegisteredOrConsulted ? 1 : 0
        };
        lead.ScoreBreakdown = signals.ToDictionary(x => x.Key, x => (int)Math.Round(x.Value * Weights[x.Key], MidpointRounding.AwayFromZero));
        lead.Score = lead.ScoreBreakdown.Values.Sum();
        lead.Grade = GradeFromScore(lead.Score);
        lead.ScoreReasons = [];
        if (lead.ScoreBreakdown["productFit"] >= 16) lead.ScoreReasons.Add("产品匹配度高");
        if (lead.ScoreBreakdown["marketValue"] >= 12) lead.ScoreReasons.Add("预计市场价值高");
        if (lead.ScoreBreakdown["purchasePower"] >= 12) lead.ScoreReasons.Add("采购能力强");
        if (lead.ScoreBreakdown["explicitDemand"] >= 8) lead.ScoreReasons.Add("需求明确");
        if (lead.ScoreBreakdown["recency"] >= 8) lead.ScoreReasons.Add("近期活跃");
        if (lead.ScoreReasons.Count == 0) lead.ScoreReasons.Add("需要补充更多采购信号");
        lead.UpdatedAt = DateTimeOffset.Now;
    }

    public static string GradeFromScore(int score) => score >= 80 ? "A" : score >= 60 ? "B" : score >= 40 ? "C" : "D";
    public static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public static double ParseSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var normalized = value.Trim().TrimEnd('%');
        if (!double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) &&
            !double.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out number)) return 0;
        if (value.Contains('%')) return Clamp01(number / 100d);
        if (number > 1) return Clamp01(number <= 10 ? number / 10d : number / 100d);
        return Clamp01(number);
    }
}
