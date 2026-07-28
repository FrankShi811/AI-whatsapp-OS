namespace WAFlow.Desktop.Updates;

public enum ApplicationUpdateStage
{
    Disabled,
    Idle,
    Checking,
    Downloading,
    ReadyToInstall,
    UpToDate,
    Failed
}

public sealed record ApplicationUpdateState(
    ApplicationUpdateStage Stage,
    string CurrentVersion,
    string? LatestVersion,
    string ReleaseNotes,
    int DownloadProgress,
    string Message,
    bool IsInstalled,
    bool CanCheck,
    bool CanInstall)
{
    public static ApplicationUpdateState Initial(string currentVersion) => new(
        ApplicationUpdateStage.Idle,
        currentVersion,
        null,
        string.Empty,
        0,
        "启动后持续监控 GitHub Release",
        false,
        true,
        false);
}

public interface IApplicationUpdateService
    : IAsyncDisposable
{
    ApplicationUpdateState State { get; }
    event EventHandler<ApplicationUpdateState>? StateChanged;
    void StartMonitoring();
    Task CheckAndDownloadAsync(bool force = false, CancellationToken cancellationToken = default);
    void ApplyAndRestart();
}
