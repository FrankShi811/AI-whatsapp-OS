namespace WAFlow.Core.Infrastructure;

public sealed record UpdateCacheCleanupResult(
    int DeletedFiles,
    int DeletedDirectories,
    long ReleasedBytes)
{
    public static UpdateCacheCleanupResult Empty { get; } = new(0, 0, 0);

    public UpdateCacheCleanupResult Add(UpdateCacheCleanupResult other) =>
        new(
            DeletedFiles + other.DeletedFiles,
            DeletedDirectories + other.DeletedDirectories,
            ReleasedBytes + other.ReleasedBytes);
}

public static class UpdateCacheRetention
{
    public const int RollbackVersionLimit = 3;

    public static UpdateCacheCleanupResult PruneInstalledPackages(
        string packageDirectory,
        string currentVersion,
        int rollbackVersionLimit = RollbackVersionLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        if (rollbackVersionLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(rollbackVersionLimit));
        if (!Version.TryParse(currentVersion, out var installedVersion))
            throw new ArgumentException("Current version must use x.y.z format.", nameof(currentVersion));

        var root = NormalizeExistingDirectory(packageDirectory);
        if (root is null) return UpdateCacheCleanupResult.Empty;

        var packages = Directory.EnumerateFiles(root, "AISalesOS-*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(path => TryParsePackage(path, out var version) ? new CachedPackage(path, version) : null)
            .Where(item => item is not null)
            .Cast<CachedPackage>()
            .ToArray();
        var rollbackVersions = packages
            .Select(item => item.Version)
            .Where(version => version < installedVersion)
            .Distinct()
            .OrderByDescending(version => version)
            .Take(rollbackVersionLimit)
            .ToHashSet();

        var deletedFiles = 0;
        long releasedBytes = 0;
        foreach (var package in packages)
        {
            if (package.Version >= installedVersion || rollbackVersions.Contains(package.Version))
                continue;

            var fullPath = EnsureDirectChild(root, package.Path);
            var file = new FileInfo(fullPath);
            var length = file.Exists ? file.Length : 0;
            file.Delete();
            deletedFiles++;
            releasedBytes += length;
        }

        return new UpdateCacheCleanupResult(deletedFiles, 0, releasedBytes);
    }

    public static UpdateCacheCleanupResult PruneVersionDirectories(
        string cacheDirectory,
        int retainedVersionCount = RollbackVersionLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (retainedVersionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(retainedVersionCount));

        var root = NormalizeExistingDirectory(cacheDirectory);
        if (root is null) return UpdateCacheCleanupResult.Empty;

        var candidates = Directory.EnumerateDirectories(root, "v*", SearchOption.TopDirectoryOnly)
            .Select(path => TryParseVersionDirectory(path, out var version)
                ? new CachedVersionDirectory(path, version)
                : null)
            .Where(item => item is not null)
            .Cast<CachedVersionDirectory>()
            .OrderByDescending(item => item.Version)
            .ToArray();
        var retained = candidates
            .Take(retainedVersionCount)
            .Select(item => item.Version)
            .ToHashSet();

        var result = UpdateCacheCleanupResult.Empty;
        foreach (var candidate in candidates.Where(item => !retained.Contains(item.Version)))
            result = result.Add(DeleteCacheDirectory(root, candidate.Path));
        return result;
    }

    public static UpdateCacheCleanupResult DeleteStaleChildren(
        string cacheDirectory,
        DateTime staleBeforeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (staleBeforeUtc.Kind == DateTimeKind.Local)
            staleBeforeUtc = staleBeforeUtc.ToUniversalTime();

        var root = NormalizeExistingDirectory(cacheDirectory);
        if (root is null) return UpdateCacheCleanupResult.Empty;

        var result = UpdateCacheCleanupResult.Empty;
        foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var fullPath = EnsureDirectChild(root, filePath);
            var file = new FileInfo(fullPath);
            if (file.LastWriteTimeUtc >= staleBeforeUtc) continue;
            var length = file.Length;
            file.Delete();
            result = result.Add(new UpdateCacheCleanupResult(1, 0, length));
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var directory = new DirectoryInfo(EnsureDirectChild(root, directoryPath));
            if (directory.LastWriteTimeUtc >= staleBeforeUtc) continue;
            result = result.Add(DeleteCacheDirectory(root, directory.FullName));
        }

        return result;
    }

    private static string? NormalizeExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath)
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : null;
    }

    private static string EnsureDirectChild(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cache cleanup refused a path outside its exact root: {fullPath}");
        return fullPath;
    }

    private static UpdateCacheCleanupResult DeleteCacheDirectory(string root, string path)
    {
        var fullPath = EnsureDirectChild(root, path);
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists) return UpdateCacheCleanupResult.Empty;
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return UpdateCacheCleanupResult.Empty;

        var files = directory.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        var releasedBytes = files.Sum(file => file.Length);
        var deletedFiles = files.Length;
        directory.Delete(recursive: true);
        return new UpdateCacheCleanupResult(deletedFiles, 1, releasedBytes);
    }

    private static bool TryParsePackage(string path, out Version version)
    {
        version = new Version();
        const string prefix = "AISalesOS-";
        var name = Path.GetFileName(path);
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return false;

        var packageName = name[prefix.Length..^".nupkg".Length];
        var separator = packageName.LastIndexOf('-');
        if (separator <= 0) return false;
        var kind = packageName[(separator + 1)..];
        if (!kind.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !kind.Equals("delta", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Version.TryParse(packageName[..separator], out var parsedVersion))
            return false;
        version = parsedVersion;
        return true;
    }

    private static bool TryParseVersionDirectory(string path, out Version version)
    {
        version = new Version();
        var name = Path.GetFileName(path);
        if (name.Length <= 1 ||
            name[0] is not ('v' or 'V') ||
            !Version.TryParse(name[1..], out var parsedVersion))
            return false;
        version = parsedVersion;
        return true;
    }

    private sealed record CachedPackage(string Path, Version Version);
    private sealed record CachedVersionDirectory(string Path, Version Version);
}
