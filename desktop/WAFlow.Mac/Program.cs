using Avalonia;
using Velopack;

namespace WAFlow.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            return MacSelfTest.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("WAFLOW_UI_SMOKE_TEST", "1");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        VelopackApp.Build().Run();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
