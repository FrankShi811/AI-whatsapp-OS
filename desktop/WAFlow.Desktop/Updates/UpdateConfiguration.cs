using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;

namespace WAFlow.Desktop.Updates;

public sealed record UpdateConfiguration(string? GitHubRepositoryUrl, string? LocalSourceDirectory, string Channel)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(LocalSourceDirectory) || !string.IsNullOrWhiteSpace(GitHubRepositoryUrl);

    public static UpdateConfiguration Load()
    {
        var localSource = Environment.GetEnvironmentVariable("AI_SALES_OS_UPDATE_SOURCE")?.Trim();
        if (!string.IsNullOrWhiteSpace(localSource)) localSource = Path.GetFullPath(localSource);

        var repository = Environment.GetEnvironmentVariable("AI_SALES_OS_GITHUB_REPOSITORY")?.Trim();
        if (string.IsNullOrWhiteSpace(repository))
        {
            repository = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key.Equals("GitHubRepositoryUrl", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();
        }

        if (!string.IsNullOrWhiteSpace(repository)) repository = NormalizeGitHubRepositoryUrl(repository);
        var channel = OperatingSystem.IsMacOS()
            ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64"
            : "win";
        return new UpdateConfiguration(repository, localSource, channel);
    }

    public static string NormalizeGitHubRepositoryUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Segments.Length < 3)
            throw new InvalidOperationException("GitHub 更新仓库必须使用 https://github.com/owner/repository 格式。");
        return $"https://github.com/{uri.Segments[1].TrimEnd('/')}/{uri.Segments[2].TrimEnd('/')}";
    }
}
