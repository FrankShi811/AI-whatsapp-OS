using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Imports;

public sealed record CustomerDimension(
    string Key,
    string Label,
    IReadOnlyList<string> SourceKeys,
    string ToolTip)
{
    public string SortKey => "custom:" + Key;
}

public static partial class CustomerDimensionCatalog
{
    public static IReadOnlyList<CustomerDimension> Build(IEnumerable<Lead> leads)
    {
        var ordered = new List<DimensionBuilder>();
        var byKey = new Dictionary<string, DimensionBuilder>(StringComparer.OrdinalIgnoreCase);
        var unnamedOrdinal = 0;

        foreach (var lead in leads)
        {
            foreach (var rawKey in lead.CustomFields.Keys)
            {
                var sourceKey = rawKey ?? "";
                if (ImportService.IsCoreDimension(sourceKey)) continue;

                var visibleLabel = DisplayLabel(sourceKey);
                var normalizedLabel = RemoveDuplicateSuffix(visibleLabel);
                var semanticKey = NormalizeSemanticKey(normalizedLabel);
                if (semanticKey.Length == 0)
                {
                    semanticKey = "unnamed:" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)));
                    if (!byKey.TryGetValue(semanticKey, out var unnamed))
                    {
                        unnamedOrdinal++;
                        unnamed = new DimensionBuilder(semanticKey, $"未命名维度 {unnamedOrdinal}");
                        byKey[semanticKey] = unnamed;
                        ordered.Add(unnamed);
                    }
                    unnamed.Add(sourceKey);
                    continue;
                }

                var key = "named:" + semanticKey;
                if (!byKey.TryGetValue(key, out var dimension))
                {
                    dimension = new DimensionBuilder(key, normalizedLabel);
                    byKey[key] = dimension;
                    ordered.Add(dimension);
                }
                dimension.Add(sourceKey);
            }
        }

        return ordered.Select(builder => builder.Build()).ToList();
    }

    public static string ResolveValue(
        IReadOnlyDictionary<string, string> fields,
        CustomerDimension dimension)
    {
        string? firstValue = null;
        foreach (var sourceKey in dimension.SourceKeys)
        {
            var match = fields.FirstOrDefault(pair =>
                pair.Key.Equals(sourceKey, StringComparison.CurrentCultureIgnoreCase));
            if (match.Key is null) continue;
            firstValue ??= match.Value ?? "";
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value.Trim();
        }
        return firstValue?.Trim() ?? "";
    }

    public static string NormalizeForStorage(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format) continue;
            if (category == UnicodeCategory.Control)
            {
                if (rune.Value is '\r' or '\n') builder.Append('\n');
                else if (rune.Value == '\t') builder.Append(' ');
                continue;
            }
            builder.Append(Rune.IsWhiteSpace(rune) ? ' ' : rune.ToString());
        }

        var lines = builder.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CollapseSpaces)
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }

    public static string DisplayLabel(string? value)
    {
        var normalized = NormalizeForStorage(value);
        return normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
    }

    public static string NormalizeSemanticKey(string? value)
    {
        var normalized = NormalizeForStorage(value).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.OtherPunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation)
                continue;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(rune.ToString());
            pendingSpace = false;
        }
        return builder.ToString().Trim();
    }

    private static string RemoveDuplicateSuffix(string value) =>
        DuplicateSuffixRegex().Replace(value, "").Trim();

    [GeneratedRegex(@"\s*[\(（]\d+[\)）]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateSuffixRegex();

    private sealed class DimensionBuilder(string key, string label)
    {
        private readonly List<string> _sourceKeys = [];
        private readonly HashSet<string> _seen = new(StringComparer.CurrentCultureIgnoreCase);

        public void Add(string sourceKey)
        {
            if (_seen.Add(sourceKey)) _sourceKeys.Add(sourceKey);
        }

        public CustomerDimension Build()
        {
            var tooltipHeaders = _sourceKeys
                .Select(NormalizeForStorage)
                .Where(header => header.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var tooltip = tooltipHeaders.Count == 0
                ? "原表表头为空或仅包含不可见字符；数据已保留。"
                : string.Join("\n", tooltipHeaders);
            return new CustomerDimension(key, label, _sourceKeys.ToList(), tooltip);
        }
    }
}
