namespace WAFlow.Core.Infrastructure;

public static class PlatformDataPaths
{
    public static string LocalApplicationDataRoot
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable("WAFLOW_LOCAL_APP_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
                return Path.GetFullPath(overrideRoot.Trim());

            return OperatingSystem.IsMacOS()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    public static string WAFlowDataDirectory =>
        Path.Combine(LocalApplicationDataRoot, "WAFlow");

    public static string DatabasePath =>
        Path.Combine(WAFlowDataDirectory, "waflow.db");

    public static string WhatsAppSessionsDirectory =>
        Path.Combine(WAFlowDataDirectory, "whatsapp-sessions");
}
