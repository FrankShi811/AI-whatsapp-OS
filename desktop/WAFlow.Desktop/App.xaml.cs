using System.Windows;
using System.Windows.Threading;
using System.IO;
using WAFlow.Core;

namespace WAFlow.Desktop;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        WindowsTaskbarIdentity.InitializeProcess();
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            Services = new AppServices();
            await Services.InitializeAsync();
            var main = new MainWindow(Services);
            MainWindow = main;
            main.Show();
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
            Services.Campaigns.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Services.WhatsApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnExit(e);
    }
}
