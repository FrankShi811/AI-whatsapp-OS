using System.Globalization;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public sealed class LeadScoringService
{
    public static readonly IReadOnlyDictionary<string, int> Weights = new Dictionary<string, int>
    {
        ["paid_marketing_willingness"] = 25,
        ["supply_stability"] = 20,
        ["ecommerce_foundation"] = 15,
        ["private_traffic"] = 15,
        ["existing_sales"] = 15,
        ["materials_readiness"] = 10
    };

    public static void ResetToAiBaseline(Lead lead, string profileSummary = "等待 AI 分析", string nextAction = "等待客户回复或手动运行 AI 分析")
    {
        lead.Score = 0;
        lead.Grade = "D";
        lead.AnalysisContractVersion = 0;
        lead.BaseProfileScore = 0;
        lead.BehaviorSignalScore = 0;
        lead.ScoreBreakdown = [];
        lead.ScoreReasons = [];
        lead.ScoreFactors = [];
        lead.BehaviorSignals = [];
        lead.LatestReplySignals = [];
        lead.AnalysisConfidence = 0;
        lead.PurchaseProbability = 0;
        lead.Evidence = [];
        lead.Risks = [];
        lead.RiskWarning = "";
        lead.ProfileSummary = profileSummary;
        lead.CustomerSegment = "未分析";
        lead.NextAction = nextAction;
        lead.AiScoreApplied = false;
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
