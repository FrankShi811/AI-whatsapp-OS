using System.Windows;
using System.Windows.Media;
using WAFlow.Desktop.Updates;

namespace WAFlow.Desktop.Windows;

public partial class VersionHistoryWindow : Window
{
    private readonly IApplicationUpdateService _updates;

    public VersionHistoryWindow(IApplicationUpdateService updates)
    {
        InitializeComponent();
        _updates = updates;
        CurrentVersionText.Text = $"AI Sales OS v{ReleaseCatalog.CurrentVersion}";
        ReleaseList.ItemsSource = ReleaseCatalog.History.Select(note => new ReleaseNoteItem(
            note.Version, note.Date, note.Title, note.Changes,
            note.Version == ReleaseCatalog.CurrentVersion ? Visibility.Visible : Visibility.Collapsed));
        Loaded += VersionHistoryWindow_Loaded;
        Closed += VersionHistoryWindow_Closed;
        _updates.StateChanged += Updates_StateChanged;
        ApplyUpdateState(_updates.State);
    }

    private async void VersionHistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_updates.State.Stage is ApplicationUpdateStage.Idle or ApplicationUpdateStage.Failed)
            await _updates.CheckAndDownloadAsync(force: true);
    }

    private void VersionHistoryWindow_Closed(object? sender, EventArgs e) =>
        _updates.StateChanged -= Updates_StateChanged;

    private void Updates_StateChanged(object? sender, ApplicationUpdateState state) =>
        _ = Dispatcher.InvokeAsync(() => ApplyUpdateState(state));

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        await _updates.CheckAndDownloadAsync(force: true);

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _updates.ApplyAndRestart();
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "无法安装更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyUpdateState(ApplicationUpdateState state)
    {
        InstalledVersionValue.Text = $"v{state.CurrentVersion}";
        LatestVersionValue.Text = string.IsNullOrWhiteSpace(state.LatestVersion) ? "检查后显示" : $"v{state.LatestVersion}";
        UpdateStatusText.Text = state.Message;
        UpdateProgress.Value = state.DownloadProgress;
        UpdateProgress.Visibility = state.Stage == ApplicationUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = state.CanCheck;
        InstallUpdateButton.IsEnabled = state.CanInstall;
        InstallUpdateButton.Content = state.IsInstalled ? "安装更新并重启" : "安装正式版";
        ReleaseNotesPanel.Visibility = string.IsNullOrWhiteSpace(state.ReleaseNotes) ? Visibility.Collapsed : Visibility.Visible;
        RemoteReleaseNotesText.Text = state.ReleaseNotes;

        var (label, backgroundKey, foregroundKey) = state.Stage switch
        {
            ApplicationUpdateStage.Checking => ("正在检查", "InfoSoft", "Info"),
            ApplicationUpdateStage.Downloading => ($"下载 {state.DownloadProgress}%", "InfoSoft", "Info"),
            ApplicationUpdateStage.ReadyToInstall => ("等待安装", "WarningSoft", "Warning"),
            ApplicationUpdateStage.UpToDate => ("已是最新", "SuccessSoft", "Success"),
            ApplicationUpdateStage.Failed => ("检查失败", "DangerSoft", "Danger"),
            ApplicationUpdateStage.Disabled => ("当前不可用", "CanvasDeep", "Muted"),
            _ => ("准备检查", "PrimarySoft", "PrimaryDark")
        };
        UpdateStatusBadgeText.Text = label;
        UpdateStatusBadge.Background = (Brush)FindResource(backgroundKey);
        UpdateStatusBadgeText.Foreground = (Brush)FindResource(foregroundKey);
    }

    private sealed record ReleaseNoteItem(string Version, string Date, string Title, IReadOnlyList<string> Changes, Visibility CurrentVisibility)
    {
        public string VersionLabel => $"v{Version}";
    }
}
