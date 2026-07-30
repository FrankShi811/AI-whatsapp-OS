using System.Diagnostics;
using System.IO;
using Velopack.Locators;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Desktop.Updates;

internal static class LocalUpdateCacheMaintenance
{
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromDays(1);
    private static int _running;

    internal static void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var result = CleanupInstalledPackages()
                .Add(CleanupPortableInstallerCache());
            if (result.DeletedFiles > 0 || result.DeletedDirectories > 0)
            {
                Trace.TraceInformation(
                    $"Update cache cleanup removed {result.DeletedFiles} files and " +
                    $"{result.DeletedDirectories} directories; released {result.ReleasedBytes} bytes.");
            }
        }
        catch (Exception error)
        {
            Trace.TraceWarning($"Update cache cleanup skipped after a safe failure: {error.Message}");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static UpdateCacheCleanupResult CleanupInstalledPackages()
    {
        if (!VelopackLocator.IsCurrentSet) return UpdateCacheCleanupResult.Empty;
        var locator = VelopackLocator.Current;
        if (locator.IsPortable ||
            locator.CurrentlyInstalledVersion is null ||
            string.IsNullOrWhiteSpace(locator.PackagesDir))
            return UpdateCacheCleanupResult.Empty;

        var result = UpdateCacheRetention.PruneInstalledPackages(
            locator.PackagesDir,
            locator.CurrentlyInstalledVersion.ToString(),
            UpdateCacheRetention.RollbackVersionLimit);
        return result.Add(UpdateCacheRetention.DeleteStaleChildren(
            Path.Combine(locator.PackagesDir, "VelopackTemp"),
            DateTime.UtcNow - StaleTemporaryAge));
    }

    private static UpdateCacheCleanupResult CleanupPortableInstallerCache() =>
        UpdateCacheRetention.PruneVersionDirectories(
            Path.Combine(Path.GetTempPath(), "AI Sales OS Updates"),
            UpdateCacheRetention.RollbackVersionLimit);
}
