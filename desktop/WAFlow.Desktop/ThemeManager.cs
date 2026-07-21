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
            ["Ink"] = ("#10211A", "#EAF5F0"),
            ["InkSecondary"] = ("#31453C", "#B9CAC2"),
            ["Muted"] = ("#6C7C74", "#8FA39A"),
            ["MutedSubtle"] = ("#94A29B", "#667B72"),
            ["Primary"] = ("#0C9A70", "#24C792"),
            ["PrimaryDark"] = ("#087657", "#17A87A"),
            ["PrimaryHover"] = ("#0A8763", "#2BD49D"),
            ["PrimarySoft"] = ("#E4F7F0", "#123D31"),
            ["PrimarySurface"] = ("#F0FAF6", "#102E26"),
            ["AiAccent"] = ("#6C63FF", "#9B94FF"),
            ["AiAccentDeep"] = ("#5147DB", "#7A72F2"),
            ["AiSoft"] = ("#EFEDFF", "#29264A"),
            ["AiSurface"] = ("#F8F7FF", "#1D1B31"),
            ["Surface"] = ("#FFFFFF", "#14221D"),
            ["SurfaceElevated"] = ("#FFFFFF", "#192A23"),
            ["SurfaceMuted"] = ("#F7FAF8", "#182720"),
            ["SurfaceInput"] = ("#FFFFFF", "#102019"),
            ["Canvas"] = ("#F1F5F3", "#0C1511"),
            ["CanvasDeep"] = ("#E9EFEC", "#1A2A23"),
            ["Line"] = ("#DBE5E0", "#283C33"),
            ["LineStrong"] = ("#C6D4CD", "#385047"),
            ["Sidebar"] = ("#091D17", "#07110E"),
            ["SidebarElevated"] = ("#102C23", "#0F221B"),
            ["SidebarHover"] = ("#173B30", "#18382E"),
            ["SidebarActive"] = ("#1C4A3B", "#1B493A"),
            ["SidebarText"] = ("#C8D9D2", "#CDDED7"),
            ["SidebarMuted"] = ("#78998C", "#739185"),
            ["Success"] = ("#0C9067", "#38C997"),
            ["SuccessSoft"] = ("#E3F6EE", "#173B30"),
            ["Warning"] = ("#B77608", "#E2A73B"),
            ["WarningSoft"] = ("#FFF1D4", "#3D3018"),
            ["Danger"] = ("#C34747", "#F07474"),
            ["DangerSoft"] = ("#FCE7E7", "#402323"),
            ["Info"] = ("#2676D9", "#62A5F2"),
            ["InfoSoft"] = ("#E8F2FF", "#182D47"),
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
