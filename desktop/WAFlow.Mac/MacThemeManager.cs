using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace WAFlow.Mac;

internal static class MacThemeManager
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Palette =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["Ink"] = ("#15251E", "#F4F8F6"),
            ["InkSecondary"] = ("#43564D", "#C4D1CB"),
            ["Muted"] = ("#586B64", "#98AAA2"),
            ["MutedSubtle"] = ("#96A8A1", "#6F7D94"),
            ["Primary"] = ("#087A59", "#19BD8C"),
            ["PrimaryDark"] = ("#066A4D", "#19BD8C"),
            ["PrimaryHover"] = ("#066A4D", "#38D5A3"),
            ["OnPrimary"] = ("#FFFFFF", "#07100D"),
            ["PrimarySoft"] = ("#D9F5EB", "#15352F"),
            ["PrimarySurface"] = ("#ECFAF5", "#102A27"),
            ["AiAccent"] = ("#6659B8", "#B9AEFF"),
            ["AiAccentDeep"] = ("#51459F", "#A79BFA"),
            ["OnAi"] = ("#FFFFFF", "#17112D"),
            ["AiProcessing"] = ("#31C8E5", "#69D7EF"),
            ["AiSoft"] = ("#E8E3FF", "#332D66"),
            ["AiSurface"] = ("#F4F1FF", "#211D45"),
            ["Surface"] = ("#FFFFFF", "#0E1714"),
            ["SurfaceElevated"] = ("#FFFFFF", "#121D19"),
            ["SurfaceMuted"] = ("#F4F7F5", "#111B17"),
            ["SurfaceInput"] = ("#FFFFFF", "#0A1410"),
            ["Canvas"] = ("#F4F7F5", "#07100D"),
            ["CanvasDeep"] = ("#E8EEEB", "#0B1612"),
            ["Line"] = ("#DDE5E1", "#26362F"),
            ["LineStrong"] = ("#B9C9C3", "#40544B"),
            ["Sidebar"] = ("#FFFFFF", "#07100D"),
            ["SidebarElevated"] = ("#F1F6F3", "#0E1915"),
            ["SidebarHover"] = ("#EAF4EF", "#10231C"),
            ["SidebarActive"] = ("#E1F1EA", "#073C2D"),
            ["SidebarText"] = ("#24362F", "#DCE8E3"),
            ["SidebarMuted"] = ("#586B64", "#91A39B"),
            ["LogoSurface"] = ("#EEF2F0", "#3A4440"),
            ["LogoBorder"] = ("#D6DEDA", "#59645F"),
            ["UnreadBadgeBackground"] = ("#C43131", "#C43131"),
            ["UnreadBadgeText"] = ("#FFFFFF", "#FFFFFF"),
            ["Success"] = ("#16B889", "#43D6B2"),
            ["SuccessSoft"] = ("#E0F7EF", "#15352F"),
            ["Warning"] = ("#8A5A00", "#F0B94F"),
            ["WarningSoft"] = ("#FFF2D6", "#3D3018"),
            ["Danger"] = ("#A52D2D", "#F57D7D"),
            ["DangerSoft"] = ("#FDE7E7", "#402323"),
            ["OnDanger"] = ("#FFFFFF", "#2B0B0B"),
            ["Info"] = ("#4E8CF7", "#75A9FF"),
            ["InfoSoft"] = ("#E9F1FF", "#182D47"),
            ["GradeA"] = ("#16B889", "#3CD0A2"),
            ["GradeB"] = ("#4E8CF7", "#75A9FF"),
            ["GradeC"] = ("#E0A12B", "#F0B94F"),
            ["GradeD"] = ("#83958E", "#96A8A1"),
            ["ChatOutbound"] = ("#D1F5E8", "#0D3025"),
            ["ChatInbound"] = ("#FFFFFF", "#13211B"),
            ["Overlay"] = ("#B80A1813", "#E0030906"),
            ["GlassSurface"] = ("#EFFFFFFF", "#E6121D19"),
            ["GlassSurfaceStrong"] = ("#F8FFFFFF", "#F216241E"),
            ["GlassLine"] = ("#90D9E0DD", "#8A33483F")
        };

    public static string CurrentMode { get; private set; } = "System";
    public static bool IsDark { get; private set; }

    public static void Apply(string? mode)
    {
        if (Application.Current is null) return;
        CurrentMode = Normalize(mode);
        Application.Current.RequestedThemeVariant = CurrentMode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        IsDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
        foreach (var (key, value) in Palette)
            Application.Current.Resources[key] =
                new SolidColorBrush(Color.Parse(IsDark ? value.Dark : value.Light));
    }

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System"
    };
}
