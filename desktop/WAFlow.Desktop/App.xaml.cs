using System.Windows;
using System.Windows.Threading;
using WAFlow.Core;

namespace WAFlow.Desktop;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
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
            MessageBox.Show($"AI Sales OS 初始化失败：\n{error.Message}", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"操作失败：\n{e.Exception.Message}", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
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
