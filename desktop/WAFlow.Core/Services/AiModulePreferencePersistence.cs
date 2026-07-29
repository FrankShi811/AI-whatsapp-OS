using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public sealed record AiModulePreferenceSelection(
    string ModuleKey,
    string ProviderId,
    string Model,
    string ReasoningEffort);

public static class AiModulePreferencePersistence
{
    public static Dictionary<string, AiModuleModelPreference> CreateSnapshot(
        IEnumerable<AiModulePreferenceSelection> selections)
    {
        var snapshot = selections.ToDictionary(
            selection => selection.ModuleKey,
            selection => new AiModuleModelPreference
            {
                ProviderId = selection.ProviderId.Trim(),
                Model = selection.Model.Trim(),
                ReasoningEffort = AiReasoningEfforts.Normalize(selection.ReasoningEffort)
            },
            StringComparer.OrdinalIgnoreCase);

        var missing = AiModuleKeys.Configurable
            .Where(key => !snapshot.ContainsKey(key))
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"缺少板块模型设置：{string.Join("、", missing)}。");

        return snapshot;
    }

    public static IReadOnlyList<string> FindMismatches(
        IReadOnlyDictionary<string, AiModuleModelPreference> expected,
        IReadOnlyDictionary<string, AiModuleModelPreference>? actual)
    {
        actual ??= new Dictionary<string, AiModuleModelPreference>(StringComparer.OrdinalIgnoreCase);
        return AiModuleKeys.Configurable
            .Where(key =>
            {
                if (!expected.TryGetValue(key, out var expectedPreference)
                    || !actual.TryGetValue(key, out var actualPreference))
                    return true;

                return !expectedPreference.ProviderId.Equals(
                        actualPreference.ProviderId,
                        StringComparison.OrdinalIgnoreCase)
                    || !expectedPreference.Model.Equals(
                        actualPreference.Model,
                        StringComparison.OrdinalIgnoreCase)
                    || !AiReasoningEfforts.Normalize(expectedPreference.ReasoningEffort).Equals(
                        AiReasoningEfforts.Normalize(actualPreference.ReasoningEffort),
                        StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }
}
