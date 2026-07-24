using System.Diagnostics;
using Velopack.Locators;
using Velopack.Windows;

namespace WAFlow.Desktop;

/// <summary>
/// Velopack normally creates shortcuts during setup. This startup guard also
/// repairs the desktop and Start menu shortcuts after every installed update,
/// including machines where Windows removed or lost the previous shortcut.
/// Portable builds are intentionally left untouched.
/// </summary>
internal static class DesktopShortcutService
{
    internal static void EnsureForInstalledApp()
    {
        if (!OperatingSystem.IsWindows() || !VelopackLocator.IsCurrentSet)
        {
            return;
        }

        try
        {
            var locator = VelopackLocator.Current;
            if (locator.IsPortable || locator.CurrentlyInstalledVersion is null)
            {
                return;
            }

            // Velopack installers create these automatically. We intentionally
            // keep this compatibility API as a post-update self-healing guard
            // because Windows can lose shortcut identity/cache across upgrades.
#pragma warning disable CS0618
            var shortcuts = new Shortcuts(locator);
            shortcuts.CreateShortcutForThisExe(ShortcutLocation.Desktop);
            shortcuts.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot);
#pragma warning restore CS0618
        }
        catch (Exception error)
        {
            // A shortcut failure must not prevent the application or an update
            // from starting. Keep a diagnostic breadcrumb for support.
            Trace.TraceWarning($"Unable to repair AI Sales OS shortcuts: {error}");
        }
    }
}
