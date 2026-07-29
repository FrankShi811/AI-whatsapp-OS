namespace WAFlow.Desktop;

internal static class UiScaleManager
{
    internal static readonly IReadOnlyList<int> SupportedPercentages = [80, 90, 100, 110, 125];

    internal static int Normalize(int percentage) =>
        SupportedPercentages
            .OrderBy(value => Math.Abs(value - percentage))
            .ThenBy(value => value)
            .First();

    internal static double ToScale(int percentage) => Normalize(percentage) / 100d;
}
