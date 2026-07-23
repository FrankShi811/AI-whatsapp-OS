using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;
using Microsoft.Win32;

namespace WAFlow.Desktop;

internal static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Palette =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["Ink"] = ("#08130F", "#F4F7FB"),
            ["InkSecondary"] = ("#4A5D56", "#C4CDDD"),
            ["Muted"] = ("#687B74", "#98A5BA"),
            ["MutedSubtle"] = ("#96A8A1", "#6F7D94"),
            ["Primary"] = ("#0C9A70", "#38D1AE"),
            ["PrimaryDark"] = ("#087A59", "#20B894"),
            ["PrimaryHover"] = ("#087A59", "#53DEBF"),
            ["PrimarySoft"] = ("#D9F5EB", "#15352F"),
            ["PrimarySurface"] = ("#ECFAF5", "#102A27"),
            ["AiAccent"] = ("#7868FF", "#A99BFF"),
            ["AiAccentDeep"] = ("#5040D8", "#8172F5"),
            ["AiProcessing"] = ("#31C8E5", "#69D7EF"),
            ["AiSoft"] = ("#E8E3FF", "#332D66"),
            ["AiSurface"] = ("#F4F1FF", "#211D45"),
            ["Surface"] = ("#FFFFFF", "#11182A"),
            ["SurfaceElevated"] = ("#FFFFFF", "#172137"),
            ["SurfaceMuted"] = ("#F3F2EF", "#131C2F"),
            ["SurfaceInput"] = ("#FFFFFF", "#0F1728"),
            ["Canvas"] = ("#F7F7F5", "#0B1020"),
            ["CanvasDeep"] = ("#ECEDE9", "#0F1728"),
            ["Line"] = ("#E3E5E2", "#2A3650"),
            ["LineStrong"] = ("#B9C9C3", "#43516B"),
            ["Sidebar"] = ("#081B15", "#070B14"),
            ["SidebarElevated"] = ("#102A22", "#11192A"),
            ["SidebarHover"] = ("#173B30", "#17243A"),
            ["SidebarActive"] = ("#1B4A3B", "#1B493F"),
            ["SidebarText"] = ("#D2E1DC", "#D2E1DC"),
            ["SidebarMuted"] = ("#82A095", "#82A095"),
            ["Success"] = ("#16B889", "#43D6B2"),
            ["SuccessSoft"] = ("#E0F7EF", "#15352F"),
            ["Warning"] = ("#E0A12B", "#F0B94F"),
            ["WarningSoft"] = ("#FFF2D6", "#3D3018"),
            ["Danger"] = ("#E35D5D", "#F57D7D"),
            ["DangerSoft"] = ("#FDE7E7", "#402323"),
            ["Info"] = ("#4E8CF7", "#75A9FF"),
            ["InfoSoft"] = ("#E9F1FF", "#182D47"),
            ["GradeA"] = ("#16B889", "#3CD0A2"),
            ["GradeB"] = ("#4E8CF7", "#75A9FF"),
            ["GradeC"] = ("#E0A12B", "#F0B94F"),
            ["GradeD"] = ("#83958E", "#96A8A1"),
            ["ChatOutbound"] = ("#D1F5E8", "#163A36"),
            ["ChatInbound"] = ("#FFFFFF", "#1A2337"),
            ["Overlay"] = ("#B80A1813", "#E0060A12"),
            ["GlassSurface"] = ("#EFFFFFFF", "#E6172137"),
            ["GlassSurfaceStrong"] = ("#F8FFFFFF", "#F21A253B"),
            ["GlassLine"] = ("#90D9E0DD", "#8A35425D")
        };

    private static readonly IReadOnlyDictionary<string, (string[] Light, string[] Dark)> GradientPalette =
        new Dictionary<string, (string[], string[])>(StringComparer.Ordinal)
        {
            ["AuroraAmbient"] = (
                ["#F8FFFFFF", "#EFF2EEFF", "#E8EAF9FF", "#E7E6FAF3"],
                ["#F21A2137", "#F01B2440", "#ED1B3041", "#EA172E34"]),
            ["AuroraBorder"] = (
                ["#55FFFFFF", "#807868FF", "#5031C8E5"],
                ["#739B8CFF", "#6247D8B7", "#3F50617E"])
        };

    public static string CurrentMode { get; private set; } = "System";
    public static bool IsDark { get; private set; }

    public static void Apply(string? mode)
    {
        CurrentMode = Normalize(mode);
        IsDark = CurrentMode == "Dark" || CurrentMode == "System" && SystemUsesDarkTheme();
        foreach (var (key, value) in Palette)
        {
            if (Application.Current.Resources[key] is not SolidColorBrush brush) continue;
            var color = (Color)ColorConverter.ConvertFromString(IsDark ? value.Dark : value.Light);
            if (brush.IsFrozen) Application.Current.Resources[key] = new SolidColorBrush(color);
            else brush.Color = color;
        }
        foreach (var (key, value) in GradientPalette)
        {
            if (Application.Current.Resources[key] is not LinearGradientBrush brush) continue;
            var colors = IsDark ? value.Dark : value.Light;
            if (brush.IsFrozen)
            {
                var clone = brush.Clone();
                ApplyGradientColors(clone, colors);
                Application.Current.Resources[key] = clone;
            }
            else
            {
                ApplyGradientColors(brush, colors);
            }
        }
    }

    public static string Next(string? mode) => Normalize(mode) switch
    {
        "System" => "Light",
        "Light" => "Dark",
        _ => "System"
    };

    public static string Label(string? mode) => Normalize(mode) switch
    {
        "Light" => "浅色",
        "Dark" => "深色",
        _ => "跟随系统"
    };

    public static string Glyph(string? mode) => Normalize(mode) switch
    {
        "Light" => "☀",
        "Dark" => "☾",
        _ => "◐"
    };

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System"
    };

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return Convert.ToInt32(value) == 0;
        }
        catch { return false; }
    }

    private static void ApplyGradientColors(LinearGradientBrush brush, IReadOnlyList<string> colors)
    {
        for (var index = 0; index < Math.Min(brush.GradientStops.Count, colors.Count); index++)
            brush.GradientStops[index].Color = (Color)ColorConverter.ConvertFromString(colors[index]);
    }
}

public sealed class ComboSelectionTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is null || values[0] == DependencyProperty.UnsetValue) return "";
        var item = values[0];
        var path = values.Length > 1 ? values[1]?.ToString()?.Trim() : "";
        if (string.IsNullOrWhiteSpace(path)) return item.ToString() ?? "";
        object? current = item;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) return "";
            current = current.GetType().GetProperty(part)?.GetValue(current);
        }
        return current?.ToString() ?? "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
