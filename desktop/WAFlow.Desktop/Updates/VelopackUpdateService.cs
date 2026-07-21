using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace WAFlow.Desktop.Updates;

public sealed class VelopackUpdateService : IApplicationUpdateService
{
    private readonly UpdateConfiguration _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _manager;
    private VelopackAsset? _downloadedRelease;

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
                Publish(State with
                {
                    Stage = ApplicationUpdateStage.Disabled,
                    IsInstalled = false,
                    CanCheck = false,
                    Message = "便携版不执行更新；请安装 Velopack Setup 后使用自动更新"
                });
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
        if (_manager is null || _downloadedRelease is null || State.Stage != ApplicationUpdateStage.ReadyToInstall)
            throw new InvalidOperationException("更新尚未下载完成。");

        _manager.WaitExitThenApplyUpdates(_downloadedRelease, silent: false, restart: true, restartArgs: null);
    }

    private UpdateManager CreateManager()
    {
        var options = new UpdateOptions { ExplicitChannel = _configuration.Channel };
        if (!string.IsNullOrWhiteSpace(_configuration.LocalSourceDirectory))
            return new UpdateManager(_configuration.LocalSourceDirectory, options);
        return new UpdateManager(new GithubSource(_configuration.GitHubRepositoryUrl!, accessToken: null, prerelease: false), options);
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
}
