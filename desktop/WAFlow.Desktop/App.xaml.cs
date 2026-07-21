using System.Windows;
using System.Windows.Threading;
using System.IO;
using Velopack;
using WAFlow.Core;
using WAFlow.Desktop.Updates;

namespace WAFlow.Desktop;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;
    public IApplicationUpdateService Updates { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAppUserModelId(WindowsTaskbarIdentity.AppUserModelId)
            .SetAutoApplyOnStartup(false)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        WindowsTaskbarIdentity.InitializeProcess();
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            Services = new AppServices();
            await Services.InitializeAsync();
            Updates = new VelopackUpdateService();
            ThemeManager.Apply((await Services.Repository.GetAppSettingsAsync()).ThemeMode);
            var main = new MainWindow(Services, Updates);
            MainWindow = main;
            main.Show();
            if (Services.Repository.LastRecoveryNotice is { } recovery)
            {
                MessageBox.Show(
                    $"检测到本地数据库损坏，AI Sales OS 已自动恢复并保留可读取数据。\n\n" +
                    $"客户：{recovery.LeadCount} 条\nWhatsApp 会话：{recovery.ConversationCount} 个\n消息：{recovery.MessageCount} 条\n\n" +
                    $"损坏原件和恢复副本已保存在：\n{recovery.BackupDirectory}",
                    "数据库已安全恢复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            await Services.LeadAutomation.StartAsync();
            await Services.Campaigns.StartAsync();
        }
        catch (Exception error)
        {
            LogException("startup", error);
            MessageBox.Show($"AI Sales OS 初始化失败：\n{error.Message}", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("dispatcher", e.Exception);
        MessageBox.Show($"操作失败：\n{e.Exception.Message}", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogException(string area, Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Sales OS", "logs");
            Directory.CreateDirectory(directory);
            var entry = $"[{DateTimeOffset.Now:O}] {area}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "app-errors.log"), entry);
        }
        catch
        {
            // Logging must never hide the original application error.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is not null)
        {
            Services.LeadAutomation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Services.Campaigns.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Services.WhatsApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnExit(e);
    }
}
