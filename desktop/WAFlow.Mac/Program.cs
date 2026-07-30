using Avalonia;
using Velopack;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            return MacSelfTest.RunAsync().GetAwaiter().GetResult();
        var isUiSmokeTest = args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase);
        if (isUiSmokeTest)
        {
            Environment.SetEnvironmentVariable("WAFLOW_UI_SMOKE_TEST", "1");
        }

        if (!isUiSmokeTest)
            VelopackApp.Build().Run();
        var workspaceManager = new DataWorkspaceManager();
        try
        {
            var migration = workspaceManager
                .ApplyPendingMigrationAsync(ParseWaitForProcessId(args))
                .GetAwaiter()
                .GetResult();
            App.ConfigureWorkspace(workspaceManager, migration);
        }
        catch (Exception error)
        {
            App.ConfigureWorkspaceFailure(workspaceManager, error);
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int? ParseWaitForProcessId(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!args[index].Equals("--wait-for-pid", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(args[index + 1], out var processId) && processId > 0)
                return processId;
        }
        return null;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
