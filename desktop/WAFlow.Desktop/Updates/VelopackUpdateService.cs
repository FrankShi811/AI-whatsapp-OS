using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace WAFlow.Desktop.Updates;

public sealed class VelopackUpdateService : IApplicationUpdateService
{
    private readonly UpdateConfiguration _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private UpdateManager? _manager;
    private VelopackAsset? _downloadedRelease;
    private string? _portableInstallerPath;

    public VelopackUpdateService(UpdateConfiguration? configuration = null)
    {
        _configuration = configuration ?? UpdateConfiguration.Load();
        State = ApplicationUpdateState.Initial(ReleaseCatalog.CurrentVersion);
        if (!_configuration.IsConfigured)
            State = State with { Stage = ApplicationUpdateStage.Disabled, Message = "尚未配置 GitHub Release 仓库", CanCheck = false };
    }

    public ApplicationUpdateState State { get; private set; }
    public event EventHandler<ApplicationUpdateState>? StateChanged;

    public async Task CheckAndDownloadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_configuration.IsConfigured) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (!force && State.Stage is ApplicationUpdateStage.Checking or ApplicationUpdateStage.Downloading or ApplicationUpdateStage.ReadyToInstall)
                return;

            _manager ??= CreateManager();
            if (!_manager.IsInstalled)
            {
                await CheckPortableReleaseAsync(cancellationToken);
                return;
            }

            if (_manager.UpdatePendingRestart is { } pending)
            {
                _downloadedRelease = pending;
                PublishReady(pending);
                return;
            }

            Publish(State with
            {
                Stage = ApplicationUpdateStage.Checking,
                IsInstalled = true,
                CanCheck = false,
                CanInstall = false,
                DownloadProgress = 0,
                Message = "正在检查 GitHub Release…"
            });

            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Publish(State with
                {
                    Stage = ApplicationUpdateStage.UpToDate,
                    LatestVersion = State.CurrentVersion,
                    ReleaseNotes = string.Empty,
                    DownloadProgress = 100,
                    Message = "当前已是最新版本",
                    CanCheck = true,
                    CanInstall = false
                });
                return;
            }

            var target = update.TargetFullRelease;
            Publish(State with
            {
                Stage = ApplicationUpdateStage.Downloading,
                LatestVersion = target.Version.ToString(),
                ReleaseNotes = target.NotesMarkdown ?? string.Empty,
                DownloadProgress = 0,
                Message = $"发现 v{target.Version}，正在自动下载…",
                CanCheck = false,
                CanInstall = false
            });

            var progress = new Action<int>(value => Publish(State with
            {
                Stage = ApplicationUpdateStage.Downloading,
                DownloadProgress = Math.Clamp(value, 0, 100),
                Message = $"正在下载 v{target.Version} · {Math.Clamp(value, 0, 100)}%"
            }));
            await _manager.DownloadUpdatesAsync(update, progress, cancellationToken);
            _downloadedRelease = target;
            PublishReady(target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(State with { Stage = ApplicationUpdateStage.Idle, CanCheck = true, Message = "更新检查已取消" });
        }
        catch (Exception error)
        {
            Publish(State with
            {
                Stage = ApplicationUpdateStage.Failed,
                CanCheck = true,
                CanInstall = false,
                Message = ToUserMessage(error)
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ApplyAndRestart()
    {
        if (State.Stage != ApplicationUpdateStage.ReadyToInstall)
            throw new InvalidOperationException("更新尚未下载完成。");

        if (!string.IsNullOrWhiteSpace(_portableInstallerPath))
        {
            if (!File.Exists(_portableInstallerPath))
                throw new FileNotFoundException("正式安装包已被移动或删除，请重新检查更新。", _portableInstallerPath);
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(_portableInstallerPath) { UseShellExecute = true });
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                start.ArgumentList.Add(_portableInstallerPath);
                Process.Start(start);
                return;
            }
            throw new PlatformNotSupportedException("当前平台不支持自动启动正式安装包。");
        }

        if (_manager is null || _downloadedRelease is null)
            throw new InvalidOperationException("更新尚未下载完成。");
        _manager.WaitExitThenApplyUpdates(_downloadedRelease, silent: false, restart: true, restartArgs: null);
    }

    private async Task CheckPortableReleaseAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration.GitHubRepositoryUrl))
        {
            Publish(State with
            {
                Stage = ApplicationUpdateStage.Disabled,
                IsInstalled = false,
                CanCheck = false,
                Message = "当前便携版没有可用的 GitHub Release 更新源。"
            });
            return;
        }

        Publish(State with
        {
            Stage = ApplicationUpdateStage.Checking,
            IsInstalled = false,
            CanCheck = false,
            CanInstall = false,
            DownloadProgress = 0,
            Message = "正在为便携版检查正式安装包…"
        });

        // GitHub's anonymous Releases API has a small shared rate limit and can
        // return HTTP 403 even when the actual release assets are healthy. The
        // Velopack feed is a normal static Release asset and needs no API call.
        var releaseBaseUrl = UpdateConfiguration.BuildLatestReleaseDownloadBaseUrl(_configuration.GitHubRepositoryUrl);
        var feedUrl = $"{releaseBaseUrl}/releases.{_configuration.Channel}.json";
        using var response = await _http.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("Assets", out var feedAssets) || feedAssets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub Release 更新清单缺少 Assets。");
        var fullAsset = feedAssets.EnumerateArray()
            .Where(item => item.TryGetProperty("Type", out var type) && type.GetString()?.Equals("Full", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => new
            {
                Version = item.TryGetProperty("Version", out var version) ? version.GetString() ?? "" : "",
                Notes = item.TryGetProperty("NotesMarkdown", out var notesNode) ? notesNode.GetString() ?? "" : ""
            })
            .OrderByDescending(item => Version.TryParse(item.Version, out var version) ? version : new Version())
            .FirstOrDefault() ?? throw new InvalidOperationException("GitHub Release 更新清单没有完整安装包记录。");
        var latestVersion = fullAsset.Version;
        var notes = fullAsset.Notes;
        if (!TryCompareVersion(latestVersion, State.CurrentVersion, out var versionComparison))
            throw new InvalidOperationException("GitHub Release 返回的版本号无效。");
        if (versionComparison < 0)
        {
            Publish(State with
            {
                Stage = ApplicationUpdateStage.UpToDate,
                LatestVersion = string.IsNullOrWhiteSpace(latestVersion) ? State.CurrentVersion : latestVersion,
                ReleaseNotes = notes,
                DownloadProgress = 100,
                Message = "当前便携版版本高于公开 Release，暂不执行安装。",
                IsInstalled = false,
                CanCheck = true,
                CanInstall = false
            });
            return;
        }

        var installerName = PortableInstallerAssetName();
        var asset = new PortableReleaseAsset(installerName, $"{releaseBaseUrl}/{installerName}", 0, "");

        Publish(State with
        {
            Stage = ApplicationUpdateStage.Downloading,
            LatestVersion = latestVersion,
            ReleaseNotes = notes,
            DownloadProgress = 0,
            Message = $"发现 v{latestVersion}，正在下载正式安装包…",
            IsInstalled = false,
            CanCheck = false,
            CanInstall = false
        });

        var downloadDirectory = Path.Combine(Path.GetTempPath(), "AI Sales OS Updates", $"v{latestVersion}");
        Directory.CreateDirectory(downloadDirectory);
        var targetPath = Path.Combine(downloadDirectory, asset.Name);
        using var download = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        download.EnsureSuccessStatusCode();
        var totalBytes = download.Content.Headers.ContentLength ?? asset.Size;
        await using (var input = await download.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                var percentage = totalBytes > 0 ? (int)Math.Clamp(received * 100 / totalBytes, 0, 100) : 0;
                Publish(State with
                {
                    Stage = ApplicationUpdateStage.Downloading,
                    DownloadProgress = percentage,
                    Message = $"正在下载正式安装包 v{latestVersion} · {percentage}%"
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(asset.Digest) && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var checksumStream = File.OpenRead(targetPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(checksumStream, cancellationToken));
            var expected = asset.Digest["sha256:".Length..].Trim();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(targetPath);
                throw new InvalidDataException("更新包 SHA-256 校验失败，已拒绝安装。请稍后重试。");
            }
        }

        _portableInstallerPath = targetPath;
        Publish(State with
        {
            Stage = ApplicationUpdateStage.ReadyToInstall,
            LatestVersion = latestVersion,
            ReleaseNotes = notes,
            DownloadProgress = 100,
            IsInstalled = false,
            CanCheck = true,
            CanInstall = true,
            Message = $"正式安装包 v{latestVersion} 已下载；安装一次后，后续版本即可在程序内自动更新。"
        });
    }

    private UpdateManager CreateManager()
    {
        var options = new UpdateOptions { ExplicitChannel = _configuration.Channel };
        if (!string.IsNullOrWhiteSpace(_configuration.LocalSourceDirectory))
            return new UpdateManager(_configuration.LocalSourceDirectory, options);
        // Use the static releases.<channel>.json feed. This avoids GitHub API
        // rate limits and keeps package checksum verification inside Velopack.
        var releaseBaseUrl = UpdateConfiguration.BuildLatestReleaseDownloadBaseUrl(_configuration.GitHubRepositoryUrl!);
        return new UpdateManager(new SimpleWebSource(releaseBaseUrl, null, 30), options);
    }

    private void PublishReady(VelopackAsset release)
    {
        Publish(State with
        {
            Stage = ApplicationUpdateStage.ReadyToInstall,
            LatestVersion = release.Version.ToString(),
            ReleaseNotes = release.NotesMarkdown ?? State.ReleaseNotes,
            DownloadProgress = 100,
            IsInstalled = true,
            CanCheck = true,
            CanInstall = true,
            Message = $"v{release.Version} 已下载，点击安装并重启"
        });
    }

    private void Publish(ApplicationUpdateState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static string ToUserMessage(Exception error) => error switch
    {
        NotInstalledException => "当前不是 Velopack 安装版本，无法自动更新。",
        ChecksumFailedException => "更新包校验失败，已拒绝安装；请稍后重试。",
        AcquireLockFailedException => "另一个更新任务正在运行，请稍后重试。",
        _ => $"更新失败：{error.Message}"
    };

    private static bool TryCompareVersion(string candidate, string current, out int comparison)
    {
        comparison = 0;
        if (!Version.TryParse(candidate, out var candidateVersion) || !Version.TryParse(current, out var currentVersion)) return false;
        comparison = candidateVersion.CompareTo(currentVersion);
        return true;
    }

    private static string PortableInstallerAssetName()
    {
        if (OperatingSystem.IsWindows()) return "AI.Sales.OS.Setup.exe";
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("当前平台没有可用的正式安装包。");
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "AI.Sales.OS.macOS.Apple-Silicon.Chinese.Preview.pkg"
            : "AI.Sales.OS.macOS.Intel.Chinese.Preview.pkg";
    }

    private sealed record PortableReleaseAsset(string Name, string DownloadUrl, long Size, string Digest);
}
