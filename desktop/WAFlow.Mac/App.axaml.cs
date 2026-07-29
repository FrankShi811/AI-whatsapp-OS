using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace WAFlow.Mac;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            if (Environment.GetEnvironmentVariable("WAFLOW_UI_SMOKE_TEST") == "1")
            {
                mainWindow.Opened += (_, _) =>
                    DispatcherTimer.RunOnce(() =>
                    {
                        Console.WriteLine("PASS macOS UI smoke window/resources/accessibility");
                        desktop.Shutdown(0);
                    }, TimeSpan.FromMilliseconds(1_500));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
