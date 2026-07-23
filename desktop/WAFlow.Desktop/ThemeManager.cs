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
            ["Ink"] = ("#08130F", "#F7FAF9"),
            ["InkSecondary"] = ("#4A5D56", "#C5D3CE"),
            ["Muted"] = ("#687B74", "#96A8A1"),
            ["MutedSubtle"] = ("#96A8A1", "#687B74"),
            ["Primary"] = ("#0C9A70", "#16B889"),
            ["PrimaryDark"] = ("#087A59", "#0C9A70"),
            ["PrimaryHover"] = ("#087A59", "#27C99A"),
            ["PrimarySoft"] = ("#D9F5EB", "#173B30"),
            ["PrimarySurface"] = ("#ECFAF5", "#102E26"),
            ["AiAccent"] = ("#7868FF", "#B9AEFF"),
            ["AiAccentDeep"] = ("#5040D8", "#7868FF"),
            ["AiProcessing"] = ("#31C8E5", "#62D9EF"),
            ["AiSoft"] = ("#E8E3FF", "#3A2E83"),
            ["AiSurface"] = ("#F4F1FF", "#292066"),
            ["Surface"] = ("#FFFFFF", "#10221C"),
            ["SurfaceElevated"] = ("#FFFFFF", "#1D3029"),
            ["SurfaceMuted"] = ("#F7FAF9", "#172A23"),
            ["SurfaceInput"] = ("#FFFFFF", "#10221C"),
            ["Canvas"] = ("#EFF4F2", "#08130F"),
            ["CanvasDeep"] = ("#E3EBE7", "#1D3029"),
            ["Line"] = ("#D5E0DC", "#32453E"),
            ["LineStrong"] = ("#B9C9C3", "#4A5D56"),
            ["Sidebar"] = ("#081B15", "#050D0A"),
            ["SidebarElevated"] = ("#102A22", "#10221C"),
            ["SidebarHover"] = ("#173B30", "#18382E"),
            ["SidebarActive"] = ("#1B4A3B", "#1B493A"),
            ["SidebarText"] = ("#D2E1DC", "#D2E1DC"),
            ["SidebarMuted"] = ("#82A095", "#82A095"),
            ["Success"] = ("#16B889", "#3CD0A2"),
            ["SuccessSoft"] = ("#E0F7EF", "#173B30"),
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
            ["ChatOutbound"] = ("#D1F5E8", "#153D31"),
            ["ChatInbound"] = ("#FFFFFF", "#1D3029"),
            ["Overlay"] = ("#B80A1813", "#D9060D0A")
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
