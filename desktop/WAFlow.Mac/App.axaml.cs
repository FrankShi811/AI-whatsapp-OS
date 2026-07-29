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
                    DispatcherTimer.RunOnce(async () =>
                    {
                        try
                        {
                            var modules = await mainWindow.RunUiSmokeAsync();
                            Console.WriteLine(
                                $"PASS macOS UI smoke window/resources/accessibility/theme/scale/navigation " +
                                $"modules={string.Join(',', modules)}");
                            desktop.Shutdown(0);
                        }
                        catch (Exception error)
                        {
                            Console.Error.WriteLine($"FAIL macOS UI smoke: {error}");
                            desktop.Shutdown(1);
                        }
                    }, TimeSpan.FromMilliseconds(250));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
