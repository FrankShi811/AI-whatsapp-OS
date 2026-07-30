using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Mac;

public sealed class App : Application
{
    private static DataWorkspaceManager _workspaceManager = new();
    private static DataWorkspaceMigrationResult _startupMigration = new(false, true, "");
    private static Exception? _workspaceStartupError;

    internal static void ConfigureWorkspace(
        DataWorkspaceManager manager,
        DataWorkspaceMigrationResult migration)
    {
        _workspaceManager = manager;
        _startupMigration = migration;
        _workspaceStartupError = null;
    }

    internal static void ConfigureWorkspaceFailure(DataWorkspaceManager manager, Exception error)
    {
        _workspaceManager = manager;
        _workspaceStartupError = error;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow mainWindow;
            try
            {
                mainWindow = new MainWindow(
                    _workspaceManager,
                    _startupMigration,
                    _workspaceStartupError);
            }
            catch (Exception error)
            {
                var failureWindow = BuildStartupFailureWindow(error);
                desktop.MainWindow = failureWindow;
                if (Environment.GetEnvironmentVariable("WAFLOW_UI_SMOKE_TEST") == "1")
                {
                    failureWindow.Opened += (_, _) =>
                        DispatcherTimer.RunOnce(() =>
                        {
                            WriteUiSmokeResult($"FAIL macOS startup: {error}");
                            desktop.Shutdown(1);
                        }, TimeSpan.FromMilliseconds(100));
                }
                base.OnFrameworkInitializationCompleted();
                return;
            }
            desktop.MainWindow = mainWindow;
            if (Environment.GetEnvironmentVariable("WAFLOW_UI_SMOKE_TEST") == "1")
            {
                mainWindow.Opened += (_, _) =>
                    DispatcherTimer.RunOnce(async () =>
                    {
                        try
                        {
                            var modules = await mainWindow.RunUiSmokeAsync();
                            var result =
                                $"PASS macOS UI smoke window/resources/accessibility/theme/scale/navigation " +
                                $"modules={string.Join(',', modules)}";
                            Console.WriteLine(result);
                            WriteUiSmokeResult(result);
                            desktop.Shutdown(0);
                        }
                        catch (Exception error)
                        {
                            Console.Error.WriteLine($"FAIL macOS UI smoke: {error}");
                            WriteUiSmokeResult($"FAIL macOS UI smoke: {error}");
                            desktop.Shutdown(1);
                        }
                    }, TimeSpan.FromMilliseconds(250));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static Window BuildStartupFailureWindow(Exception error)
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(28),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = "AI Sales OS 无法读取本地工作区",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = error.Message,
            FontSize = 13,
            Foreground = Brushes.DarkRed,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "程序尚未修改客户数据。请恢复原数据磁盘，或保留此提示用于排查。",
            FontSize = 12,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });
        return new Window
        {
            Title = "AI Sales OS",
            Width = 620,
            Height = 360,
            MinWidth = 480,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = panel
        };
    }

    private static void WriteUiSmokeResult(string result)
    {
        var path = Environment.GetEnvironmentVariable("WAFLOW_UI_SMOKE_RESULT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, result);
        }
        catch
        {
            // Test evidence must never interrupt normal application shutdown.
        }
    }
}
