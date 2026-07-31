using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WAFlow.Core.Infrastructure;

public sealed record DataWorkspaceLocation(
    string RootDirectory,
    string DatabasePath,
    bool IsEnvironmentOverride);

public sealed record DataWorkspaceUsage(
    string RootDirectory,
    long UsedBytes,
    long AvailableBytes,
    string DriveName,
    bool IsEnvironmentOverride);

public sealed record DataWorkspaceMigrationPreview(
    string SourceRoot,
    string TargetRoot,
    long SourceBytes,
    long TargetAvailableBytes,
    string TargetDriveName);

public sealed record DataWorkspaceMigrationResult(
    bool Attempted,
    bool Succeeded,
    string Message,
    string SourceRoot = "",
    string TargetRoot = "",
    bool SourceRetained = false);

public sealed class DataWorkspaceLease : IDisposable
{
    private readonly FileStream _stream;
    public string Path { get; }

    internal DataWorkspaceLease(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(Path); }
        catch { }
    }
}

/// <summary>
/// Resolves and migrates the complete local WAFlow workspace. The locator is
/// intentionally kept outside the workspace so moving the data never creates
/// a circular dependency on the old drive.
/// </summary>
public sealed class DataWorkspaceManager
{
    private const int FormatVersion = 1;
    private const string DatabaseFileName = "waflow.db";
    private const string SuggestedFolderName = "AI Sales OS Data";
    private readonly string _locatorDirectory;
    private readonly string _defaultRoot;

    public DataWorkspaceManager(string? locatorDirectory = null, string? defaultRoot = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _locatorDirectory = NormalizeDirectory(locatorDirectory
            ?? Path.Combine(localAppData, "AI Sales OS"));
        _defaultRoot = NormalizeDirectory(defaultRoot
            ?? Path.Combine(localAppData, "WAFlow"));
    }

    public string LocatorFilePath => Path.Combine(_locatorDirectory, "data-workspace.json");
    public string PendingMigrationFilePath => Path.Combine(_locatorDirectory, "data-workspace-migration.json");

    public DataWorkspaceLocation Resolve()
    {
        var databaseOverride = Environment.GetEnvironmentVariable("WAFLOW_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(databaseOverride))
        {
            var databasePath = Path.GetFullPath(databaseOverride);
            var root = Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("WAFLOW_DATABASE_PATH 缺少有效目录。");
            return new DataWorkspaceLocation(root, databasePath, true);
        }

        if (!File.Exists(LocatorFilePath))
            return FromRoot(_defaultRoot, false);

        WorkspaceLocator locator;
        try
        {
            locator = Json.Deserialize<WorkspaceLocator>(
                File.ReadAllText(LocatorFilePath, Encoding.UTF8))
                ?? throw new InvalidDataException("工作区位置索引为空。");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidDataException(
                $"本地数据工作区位置索引无法读取：{LocatorFilePath}",
                error);
        }

        if (locator.Version != FormatVersion || string.IsNullOrWhiteSpace(locator.RootDirectory))
            throw new InvalidDataException("本地数据工作区位置索引版本无效。");

        var location = FromRoot(locator.RootDirectory, false);
        if (!Directory.Exists(location.RootDirectory) || !File.Exists(location.DatabasePath))
            throw new DirectoryNotFoundException(
                $"已设置的数据工作区不可用：{location.RootDirectory}。请恢复该磁盘后重试。");
        return location;
    }

    public DataWorkspaceLocation FromDatabasePath(string databasePath, bool isEnvironmentOverride = false)
    {
        var fullPath = Path.GetFullPath(databasePath);
        return new DataWorkspaceLocation(
            Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("数据库路径缺少有效目录。"),
            fullPath,
            isEnvironmentOverride);
    }

    public async Task<DataWorkspaceUsage> GetUsageAsync(
        DataWorkspaceLocation location,
        CancellationToken cancellationToken = default)
    {
        var usedBytes = await Task.Run(
            () => EnumerateWorkspaceFiles(location.RootDirectory)
                .Sum(file => file.Length),
            cancellationToken);
        var drive = RequireFixedDrive(location.RootDirectory);
        return new DataWorkspaceUsage(
            location.RootDirectory,
            usedBytes,
            drive.AvailableFreeSpace,
            drive.Name,
            location.IsEnvironmentOverride);
    }

    public string BuildSuggestedTargetRoot(string selectedDirectory)
    {
        var selected = NormalizeDirectory(selectedDirectory);
        var installedRoot = TryGetInstalledApplicationRoot();
        if (!string.IsNullOrWhiteSpace(installedRoot)
            && PathsEqual(selected, installedRoot))
        {
            selected = Path.GetPathRoot(selected)
                ?? throw new InvalidOperationException("无法识别安装目录所在磁盘。");
        }
        var leaf = new DirectoryInfo(selected).Name;
        return leaf.Equals(SuggestedFolderName, StringComparison.OrdinalIgnoreCase)
            ? selected
            : NormalizeDirectory(Path.Combine(selected, SuggestedFolderName));
    }

    public async Task<DataWorkspaceMigrationPreview> PreviewMigrationAsync(
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var source = Resolve();
        if (source.IsEnvironmentOverride)
            throw new InvalidOperationException("测试数据库覆盖模式下不能迁移正式工作区。");

        var normalizedTarget = NormalizeDirectory(targetRoot);
        ValidateRootRelationship(source.RootDirectory, normalizedTarget);
        if (!Directory.Exists(source.RootDirectory) || !File.Exists(source.DatabasePath))
            throw new DirectoryNotFoundException("当前工作区或数据库不存在，已停止迁移。");
        if (Directory.Exists(normalizedTarget)
            && Directory.EnumerateFileSystemEntries(normalizedTarget).Any())
            throw new IOException($"目标文件夹必须为空：{normalizedTarget}");

        var sourceBytes = await Task.Run(
            () => EnumerateWorkspaceFiles(source.RootDirectory).Sum(file => file.Length),
            cancellationToken);
        var drive = RequireFixedDrive(normalizedTarget);
        var safetyMargin = Math.Max(128L * 1024 * 1024, sourceBytes / 10);
        if (drive.AvailableFreeSpace < sourceBytes + safetyMargin)
            throw new IOException(
                $"目标磁盘空间不足。至少需要 {FormatBytes(sourceBytes + safetyMargin)}，" +
                $"当前可用 {FormatBytes(drive.AvailableFreeSpace)}。");

        return new DataWorkspaceMigrationPreview(
            source.RootDirectory,
            normalizedTarget,
            sourceBytes,
            drive.AvailableFreeSpace,
            drive.Name);
    }

    public async Task ScheduleMigrationAsync(
        DataWorkspaceMigrationPreview preview,
        CancellationToken cancellationToken = default)
    {
        var verified = await PreviewMigrationAsync(preview.TargetRoot, cancellationToken);
        if (!PathsEqual(verified.SourceRoot, preview.SourceRoot))
            throw new InvalidOperationException("迁移期间当前工作区发生变化，请重新选择目标磁盘。");

        var manifest = new WorkspaceMigrationManifest
        {
            Version = FormatVersion,
            Id = Guid.NewGuid().ToString("N"),
            SourceRoot = verified.SourceRoot,
            TargetRoot = verified.TargetRoot,
            SourceBytes = verified.SourceBytes,
            CreatedAt = DateTimeOffset.Now,
            State = WorkspaceMigrationStates.Scheduled
        };
        await WriteManifestAsync(manifest, cancellationToken);
    }

    public async Task CancelScheduledMigrationAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(cancellationToken);
        if (manifest?.State == WorkspaceMigrationStates.Scheduled)
        {
            manifest.State = WorkspaceMigrationStates.Cancelled;
            manifest.Error = "迁移重启进程未能启动。";
            await WriteManifestAsync(manifest, cancellationToken);
        }
    }

    public async Task<DataWorkspaceMigrationResult> ApplyPendingMigrationAsync(
        int? waitForProcessId = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(cancellationToken);
        if (manifest is null)
            return new DataWorkspaceMigrationResult(false, true, "");

        var stagingRoot = manifest.TargetRoot + $".migrating-{manifest.Id}";
        if (manifest.State is WorkspaceMigrationStates.Switched
            or WorkspaceMigrationStates.CleaningSource)
        {
            try
            {
                await VerifyDatabaseAsync(
                    Path.Combine(manifest.TargetRoot, DatabaseFileName),
                    cancellationToken);
                await WriteLocatorAsync(manifest.TargetRoot, cancellationToken);
                if (manifest.State == WorkspaceMigrationStates.CleaningSource)
                {
                    manifest.State = WorkspaceMigrationStates.CompletedWithSourceRetained;
                    manifest.SourceRetained = true;
                    manifest.Error = "上次启动在清理旧位置时中断；新工作区正常，旧位置已安全保留。";
                    await WriteManifestAsync(manifest, cancellationToken);
                }
                return new DataWorkspaceMigrationResult(
                    true,
                    true,
                    "本地数据已复制并校验，正在从新工作区启动。",
                    manifest.SourceRoot,
                    manifest.TargetRoot,
                    SourceRetained: true);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (File.Exists(Path.Combine(manifest.SourceRoot, DatabaseFileName)))
                    await WriteLocatorOrDefaultAsync(manifest.SourceRoot, cancellationToken);
                manifest.State = WorkspaceMigrationStates.Failed;
                manifest.SourceRetained = true;
                manifest.Error = SafeError(error);
                await WriteManifestAsync(manifest, cancellationToken);
                return new DataWorkspaceMigrationResult(
                    true,
                    false,
                    $"工作区迁移恢复检查未通过，程序继续使用原位置。{SafeError(error)}",
                    manifest.SourceRoot,
                    manifest.TargetRoot,
                    SourceRetained: true);
            }
        }

        if (manifest.State == WorkspaceMigrationStates.Copying)
        {
            if (File.Exists(Path.Combine(manifest.SourceRoot, DatabaseFileName)))
                await WriteLocatorOrDefaultAsync(manifest.SourceRoot, cancellationToken);
            DeleteDirectoryIfPresent(stagingRoot);
            manifest.State = WorkspaceMigrationStates.Failed;
            manifest.SourceRetained = true;
            manifest.Error = "上次迁移在复制阶段中断，未切换工作区。";
            await WriteManifestAsync(manifest, cancellationToken);
            return new DataWorkspaceMigrationResult(
                true,
                false,
                "上次工作区迁移在复制阶段中断，程序继续使用原位置，可在设置中重新迁移。",
                manifest.SourceRoot,
                manifest.TargetRoot,
                SourceRetained: true);
        }

        if (manifest.State != WorkspaceMigrationStates.Scheduled)
            return new DataWorkspaceMigrationResult(false, true, "");

        FileStream? migrationLock = null;
        try
        {
            if (waitForProcessId is > 0 && waitForProcessId != Environment.ProcessId)
                await WaitForProcessExitAsync(waitForProcessId.Value, cancellationToken);

            EnsureNoActiveWorkspaceUsers(manifest.SourceRoot);
            migrationLock = AcquireMigrationLock(manifest.SourceRoot);
            EnsureWorkspaceDatabaseNotInUse(manifest.SourceRoot);
            var preview = await PreviewMigrationAsync(manifest.TargetRoot, cancellationToken);
            if (!PathsEqual(preview.SourceRoot, manifest.SourceRoot))
                throw new InvalidOperationException("当前工作区与迁移计划不一致，已停止切换。");

            manifest.State = WorkspaceMigrationStates.Copying;
            manifest.Error = "";
            await WriteManifestAsync(manifest, cancellationToken);

            DeleteDirectoryIfPresent(stagingRoot);
            Directory.CreateDirectory(stagingRoot);
            var sourceBefore = CaptureMetadata(manifest.SourceRoot);
            await CopyWorkspaceAsync(manifest.SourceRoot, stagingRoot, cancellationToken);
            await VerifyWorkspaceCopyAsync(manifest.SourceRoot, stagingRoot, cancellationToken);
            var stagedDatabase = Path.Combine(stagingRoot, DatabaseFileName);
            await VerifyDatabaseAsync(stagedDatabase, cancellationToken);
            await RewriteInternalWorkspacePathsAsync(
                stagedDatabase,
                manifest.SourceRoot,
                manifest.TargetRoot,
                cancellationToken);
            await VerifyDatabaseAsync(stagedDatabase, cancellationToken);
            var sourceAfter = CaptureMetadata(manifest.SourceRoot);
            if (!MetadataEquals(sourceBefore, sourceAfter))
                throw new IOException("迁移期间源工作区仍在变化，已停止切换。请关闭其他 AI Sales OS 进程后重试。");

            manifest.SourceFingerprint = await BuildFingerprintAsync(
                manifest.SourceRoot,
                cancellationToken);
            if (Directory.Exists(manifest.TargetRoot))
                Directory.Delete(manifest.TargetRoot, recursive: false);
            Directory.Move(stagingRoot, manifest.TargetRoot);

            manifest.State = WorkspaceMigrationStates.Switched;
            manifest.SwitchedAt = DateTimeOffset.Now;
            await WriteManifestAsync(manifest, cancellationToken);
            await WriteLocatorAsync(manifest.TargetRoot, cancellationToken);
            return new DataWorkspaceMigrationResult(
                true,
                true,
                "本地数据已复制并校验，正在从新工作区启动。",
                manifest.SourceRoot,
                manifest.TargetRoot,
                SourceRetained: true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try
            {
                if (File.Exists(Path.Combine(manifest.SourceRoot, DatabaseFileName)))
                    await WriteLocatorOrDefaultAsync(manifest.SourceRoot, cancellationToken);
            }
            catch { }
            DeleteDirectoryIfPresent(stagingRoot);
            manifest.State = WorkspaceMigrationStates.Failed;
            manifest.Error = SafeError(error);
            await WriteManifestAsync(manifest, cancellationToken);
            return new DataWorkspaceMigrationResult(
                true,
                false,
                $"工作区迁移未完成，程序继续使用原位置。{SafeError(error)}",
                manifest.SourceRoot,
                manifest.TargetRoot,
                SourceRetained: true);
        }
        finally
        {
            migrationLock?.Dispose();
            TryDeleteFile(Path.Combine(manifest.SourceRoot, ".migration.lock"));
        }
    }

    public async Task<DataWorkspaceMigrationResult> CompletePendingMigrationAsync(
        string activeRoot,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(cancellationToken);
        if (manifest is null
            || manifest.State != WorkspaceMigrationStates.Switched
            || !PathsEqual(manifest.TargetRoot, activeRoot))
            return new DataWorkspaceMigrationResult(false, true, "");

        try
        {
            await VerifyDatabaseAsync(
                Path.Combine(manifest.TargetRoot, DatabaseFileName),
                cancellationToken);
            manifest.State = WorkspaceMigrationStates.CleaningSource;
            await WriteManifestAsync(manifest, cancellationToken);

            var currentFingerprint = Directory.Exists(manifest.SourceRoot)
                ? await BuildFingerprintAsync(manifest.SourceRoot, cancellationToken)
                : manifest.SourceFingerprint;
            var sourceRetained = !string.Equals(
                currentFingerprint,
                manifest.SourceFingerprint,
                StringComparison.Ordinal);
            if (!sourceRetained)
                await Task.Run(
                    () => DeleteVerifiedSourceWorkspace(manifest.SourceRoot, manifest.TargetRoot),
                    cancellationToken);

            manifest.State = WorkspaceMigrationStates.Completed;
            manifest.CompletedAt = DateTimeOffset.Now;
            manifest.SourceRetained = sourceRetained;
            manifest.Error = sourceRetained
                ? "源工作区在切换后发生变化，为避免丢失数据已保留。"
                : "";
            await WriteManifestAsync(manifest, cancellationToken);
            return new DataWorkspaceMigrationResult(
                true,
                true,
                sourceRetained
                    ? $"新工作区已启用。旧位置检测到新的文件变化，已安全保留：{manifest.SourceRoot}"
                    : $"本地数据工作区已迁移到：{manifest.TargetRoot}",
                manifest.SourceRoot,
                manifest.TargetRoot,
                sourceRetained);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            manifest.State = WorkspaceMigrationStates.CompletedWithSourceRetained;
            manifest.SourceRetained = true;
            manifest.Error = SafeError(error);
            await WriteManifestAsync(manifest, cancellationToken);
            return new DataWorkspaceMigrationResult(
                true,
                true,
                $"新工作区已启用，但旧位置未能自动清理，可稍后人工处理：{manifest.SourceRoot}",
                manifest.SourceRoot,
                manifest.TargetRoot,
                SourceRetained: true);
        }
    }

    public async Task RollbackAfterStartupFailureAsync(
        string activeRoot,
        Exception error,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(cancellationToken);
        if (manifest is null
            || manifest.State != WorkspaceMigrationStates.Switched
            || !PathsEqual(manifest.TargetRoot, activeRoot))
            return;

        await WriteLocatorOrDefaultAsync(manifest.SourceRoot, cancellationToken);
        manifest.State = WorkspaceMigrationStates.RolledBack;
        manifest.Error = $"新工作区启动校验失败，已恢复原位置。{SafeError(error)}";
        await WriteManifestAsync(manifest, cancellationToken);
    }

    public DataWorkspaceLease AcquireLease(string rootDirectory)
    {
        var root = NormalizeDirectory(rootDirectory);
        Directory.CreateDirectory(root);
        var leasePath = Path.Combine(root, $".workspace-use-{Environment.ProcessId}.lock");
        var stream = new FileStream(
            leasePath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read);
        using var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true);
        writer.Write($"{Environment.ProcessId}|{DateTimeOffset.Now:O}|{Environment.ProcessPath}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return new DataWorkspaceLease(leasePath, stream);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.#} {units[unit]}";
    }

    private DataWorkspaceLocation FromRoot(string rootDirectory, bool isEnvironmentOverride)
    {
        var root = NormalizeDirectory(rootDirectory);
        return new DataWorkspaceLocation(
            root,
            Path.Combine(root, DatabaseFileName),
            isEnvironmentOverride);
    }

    private async Task WriteLocatorOrDefaultAsync(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        if (PathsEqual(rootDirectory, _defaultRoot))
        {
            TryDeleteFile(LocatorFilePath);
            return;
        }
        await WriteLocatorAsync(rootDirectory, cancellationToken);
    }

    private async Task WriteLocatorAsync(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_locatorDirectory);
        var locator = new WorkspaceLocator
        {
            Version = FormatVersion,
            RootDirectory = NormalizeDirectory(rootDirectory),
            UpdatedAt = DateTimeOffset.Now
        };
        await WriteJsonAtomicallyAsync(LocatorFilePath, locator, cancellationToken);
    }

    private async Task<WorkspaceMigrationManifest?> ReadManifestAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(PendingMigrationFilePath)) return null;
        var text = await File.ReadAllTextAsync(
            PendingMigrationFilePath,
            Encoding.UTF8,
            cancellationToken);
        return Json.Deserialize<WorkspaceMigrationManifest>(text);
    }

    private async Task WriteManifestAsync(
        WorkspaceMigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_locatorDirectory);
        await WriteJsonAtomicallyAsync(
            PendingMigrationFilePath,
            manifest,
            cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(value));
        await using (var stream = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task WaitForProcessExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        }
        catch (ArgumentException)
        {
            // The source process already exited.
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                "原程序未能在 3 分钟内安全退出，工作区尚未复制或切换。请安装修复版本后重新迁移。",
                error);
        }
    }

    private static string? TryGetInstalledApplicationRoot()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !Path.GetFileName(processPath).Equals("AISalesOS.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var currentDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(currentDirectory)
            || !new DirectoryInfo(currentDirectory).Name.Equals("current", StringComparison.OrdinalIgnoreCase))
            return null;
        return Directory.GetParent(currentDirectory)?.FullName;
    }

    private static FileStream AcquireMigrationLock(string sourceRoot)
    {
        var path = Path.Combine(sourceRoot, ".migration.lock");
        return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    }

    private static void EnsureWorkspaceDatabaseNotInUse(string sourceRoot)
    {
        var databasePath = Path.Combine(sourceRoot, DatabaseFileName);
        try
        {
            using var database = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException(
                "仍有其他 AI Sales OS 进程正在使用当前数据库。请关闭其他程序窗口后重新迁移。",
                error);
        }
    }

    private static void EnsureNoActiveWorkspaceUsers(string sourceRoot)
    {
        foreach (var lease in Directory.EnumerateFiles(
                     sourceRoot,
                     ".workspace-use-*.lock",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var probe = new FileStream(
                    lease,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                probe.Dispose();
                File.Delete(lease);
            }
            catch (IOException)
            {
                throw new IOException(
                    "仍有其他 AI Sales OS 进程正在使用当前工作区。请关闭其他程序窗口后重新迁移。");
            }
        }
    }

    private static async Task CopyWorkspaceAsync(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        foreach (var directory in EnumerateWorkspaceDirectories(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, directory.FullName);
            Directory.CreateDirectory(Path.Combine(targetRoot, relative));
        }

        foreach (var file in EnumerateWorkspaceFiles(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, file.FullName);
            var destination = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
        }
    }

    private static async Task VerifyWorkspaceCopyAsync(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var sourceFiles = EnumerateWorkspaceFiles(sourceRoot)
            .ToDictionary(
                file => Path.GetRelativePath(sourceRoot, file.FullName),
                StringComparer.OrdinalIgnoreCase);
        var targetFiles = EnumerateWorkspaceFiles(targetRoot)
            .ToDictionary(
                file => Path.GetRelativePath(targetRoot, file.FullName),
                StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count != targetFiles.Count
            || sourceFiles.Keys.Except(targetFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new IOException("目标工作区文件数量或路径校验失败。");

        foreach (var pair in sourceFiles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targetFiles[pair.Key];
            if (pair.Value.Length != target.Length)
                throw new IOException($"文件大小校验失败：{pair.Key}");
            var sourceHash = await HashFileAsync(pair.Value.FullName, cancellationToken);
            var targetHash = await HashFileAsync(target.FullName, cancellationToken);
            if (!sourceHash.AsSpan().SequenceEqual(targetHash))
                throw new IOException($"文件哈希校验失败：{pair.Key}");
        }
    }

    private static async Task VerifyDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("迁移副本缺少 waflow.db。", databasePath);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"迁移副本未通过 SQLite 完整性检查：{result}");
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("迁移副本存在外键完整性错误。");
    }

    private static async Task RewriteInternalWorkspacePathsAsync(
        string databasePath,
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeDirectory(sourceRoot);
        var normalizedTarget = NormalizeDirectory(targetRoot);
        var jsonSource = normalizedSource.Replace(@"\", @"\\", StringComparison.Ordinal);
        var jsonTarget = normalizedTarget.Replace(@"\", @"\\", StringComparison.Ordinal);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var textColumns = new List<(string Table, string Column)>();
        await using (var tables = connection.CreateCommand())
        {
            tables.Transaction = (SqliteTransaction)transaction;
            tables.CommandText =
                "SELECT name FROM sqlite_schema " +
                "WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var tableReader = await tables.ExecuteReaderAsync(cancellationToken);
            var tableNames = new List<string>();
            while (await tableReader.ReadAsync(cancellationToken))
                tableNames.Add(tableReader.GetString(0));
            await tableReader.DisposeAsync();

            foreach (var table in tableNames)
            {
                await using var columns = connection.CreateCommand();
                columns.Transaction = (SqliteTransaction)transaction;
                columns.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
                await using var columnReader = await columns.ExecuteReaderAsync(cancellationToken);
                while (await columnReader.ReadAsync(cancellationToken))
                {
                    var column = columnReader.GetString(1);
                    var type = columnReader.IsDBNull(2) ? "" : columnReader.GetString(2);
                    if (type.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                        textColumns.Add((table, column));
                }
            }
        }

        foreach (var (table, column) in textColumns)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                $"UPDATE {QuoteIdentifier(table)} " +
                $"SET {QuoteIdentifier(column)} = " +
                $"replace(replace({QuoteIdentifier(column)}, $jsonSource, $jsonTarget), $source, $target) " +
                $"WHERE instr({QuoteIdentifier(column)}, $source) > 0 " +
                $"OR instr({QuoteIdentifier(column)}, $jsonSource) > 0";
            update.Parameters.AddWithValue("$source", normalizedSource);
            update.Parameters.AddWithValue("$target", normalizedTarget);
            update.Parameters.AddWithValue("$jsonSource", jsonSource);
            update.Parameters.AddWithValue("$jsonTarget", jsonTarget);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<string> BuildFingerprintAsync(
        string root,
        CancellationToken cancellationToken)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateWorkspaceFiles(root)
                     .OrderBy(file => Path.GetRelativePath(root, file.FullName), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes($"{relative}|{file.Length}|"));
            aggregate.AppendData(await HashFileAsync(file.FullName, cancellationToken));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    private static async Task<byte[]> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static Dictionary<string, FileMetadata> CaptureMetadata(string root) =>
        EnumerateWorkspaceFiles(root).ToDictionary(
            file => Path.GetRelativePath(root, file.FullName),
            file => new FileMetadata(file.Length, file.LastWriteTimeUtc.Ticks),
            StringComparer.OrdinalIgnoreCase);

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, FileMetadata> left,
        IReadOnlyDictionary<string, FileMetadata> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static IEnumerable<DirectoryInfo> EnumerateWorkspaceDirectories(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in current.EnumerateDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                yield return directory;
                pending.Push(directory);
            }
        }
    }

    private static IEnumerable<FileInfo> EnumerateWorkspaceFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));
        while (directories.Count > 0)
        {
            var current = directories.Pop();
            foreach (var file in current.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0
                    || IsEphemeralWorkspaceFile(file.Name))
                    continue;
                yield return file;
            }
            foreach (var directory in current.EnumerateDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    directories.Push(directory);
            }
        }
    }

    private static bool IsEphemeralWorkspaceFile(string fileName) =>
        fileName.Equals(".migration.lock", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(".workspace-use-", StringComparison.OrdinalIgnoreCase)
           && fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    private static void DeleteVerifiedSourceWorkspace(string sourceRoot, string targetRoot)
    {
        ValidateRootRelationship(sourceRoot, targetRoot);
        var source = NormalizeDirectory(sourceRoot);
        if (!File.Exists(Path.Combine(source, DatabaseFileName)))
            throw new IOException("旧工作区缺少数据库，已停止自动清理。");
        var root = Path.GetPathRoot(source);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (PathsEqual(source, root)
            || PathsEqual(source, profile)
            || PathsEqual(source, local)
            || PathsEqual(source, Path.GetDirectoryName(local) ?? ""))
            throw new IOException("旧工作区范围过宽，已停止自动清理。");
        Directory.Delete(source, recursive: true);
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void ValidateRootRelationship(string sourceRoot, string targetRoot)
    {
        var source = NormalizeDirectory(sourceRoot);
        var target = NormalizeDirectory(targetRoot);
        if (PathsEqual(source, target))
            throw new InvalidOperationException("目标工作区不能与当前位置相同。");
        if (IsNested(target, source) || IsNested(source, target))
            throw new InvalidOperationException("新旧工作区不能互相包含，请选择其他磁盘上的独立文件夹。");
    }

    private static bool IsNested(string candidate, string parent) =>
        candidate.StartsWith(
            parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static DriveInfo RequireFixedDrive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("数据工作区只能迁移到本机固定磁盘，不能使用网络共享。");
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("目标路径缺少磁盘根目录。");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            throw new InvalidOperationException("数据工作区只能迁移到已就绪的本机固定磁盘。");
        return drive;
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("工作区路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return root;
        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeError(Exception error)
    {
        var message = error.Message.Replace(Environment.NewLine, " ").Trim();
        return message.Length <= 600 ? message : message[..600];
    }

    private sealed record FileMetadata(long Length, long LastWriteTicks);

    private sealed class WorkspaceLocator
    {
        public int Version { get; set; }
        public string RootDirectory { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class WorkspaceMigrationManifest
    {
        public int Version { get; set; }
        public string Id { get; set; } = "";
        public string SourceRoot { get; set; } = "";
        public string TargetRoot { get; set; } = "";
        public long SourceBytes { get; set; }
        public string SourceFingerprint { get; set; } = "";
        public string State { get; set; } = WorkspaceMigrationStates.Scheduled;
        public string Error { get; set; } = "";
        public bool SourceRetained { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? SwitchedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private static class WorkspaceMigrationStates
    {
        public const string Scheduled = "scheduled";
        public const string Copying = "copying";
        public const string Switched = "switched";
        public const string CleaningSource = "cleaning_source";
        public const string Completed = "completed";
        public const string CompletedWithSourceRetained = "completed_source_retained";
        public const string RolledBack = "rolled_back";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }
}
