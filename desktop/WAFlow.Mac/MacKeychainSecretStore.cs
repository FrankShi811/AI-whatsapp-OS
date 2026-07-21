using System.Diagnostics;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Mac;

public sealed class MacKeychainSecretStore(string service = "AI Sales OS", string account = "AIProviderApiKey") : ISecretStore
{
    public void Save(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        Run(false, "add-generic-password", "-U", "-s", service, "-a", account, "-w", secret.Trim());
    }

    public string? Read()
    {
        var result = Run(true, "find-generic-password", "-s", service, "-a", account, "-w");
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private static ProcessResult Run(bool allowFailure, params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法调用 macOS 钥匙串。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "无法写入 macOS 钥匙串。" : error.Trim());
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
