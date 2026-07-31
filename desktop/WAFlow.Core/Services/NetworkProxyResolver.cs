using System.Net;
using MailKit.Net.Proxy;

namespace WAFlow.Core.Services;

public sealed record NetworkProxyRoute(
    string ProxyUrl,
    string Source,
    bool AllowDirectFallback)
{
    public bool HasProxy => !string.IsNullOrWhiteSpace(ProxyUrl);
}

public static class NetworkProxyResolver
{
    private static readonly string[] ProxyEnvironmentVariables =
    [
        "WAFLOW_PROXY_URL",
        "HTTPS_PROXY",
        "https_proxy",
        "ALL_PROXY",
        "all_proxy",
        "HTTP_PROXY",
        "http_proxy"
    ];

    public static NetworkProxyRoute Resolve(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        foreach (var variable in ProxyEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (TryNormalizeProxy(value, out var proxyUrl))
                return new NetworkProxyRoute(proxyUrl, $"environment:{variable}", true);
        }

        try
        {
            var systemProxy = WebRequest.DefaultWebProxy;
            if (systemProxy is not null && !systemProxy.IsBypassed(destination))
            {
                var proxy = systemProxy.GetProxy(destination);
                if (proxy is not null
                    && !Uri.Compare(proxy, destination, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase).Equals(0)
                    && TryNormalizeProxy(proxy.AbsoluteUri, out var proxyUrl))
                    return new NetworkProxyRoute(proxyUrl, "windows", true);
            }
        }
        catch
        {
            // A malformed PAC or unavailable Windows proxy service must not prevent direct access.
        }

        return new NetworkProxyRoute("", "direct", false);
    }

    public static IProxyClient? CreateMailKitProxy(NetworkProxyRoute route)
    {
        if (!route.HasProxy || !Uri.TryCreate(route.ProxyUrl, UriKind.Absolute, out var proxy)) return null;

        var credentials = ParseCredentials(proxy);
        return proxy.Scheme.ToLowerInvariant() switch
        {
            "http" => credentials is null
                ? new HttpProxyClient(proxy.Host, EffectivePort(proxy))
                : new HttpProxyClient(proxy.Host, EffectivePort(proxy), credentials),
            "https" => credentials is null
                ? new HttpsProxyClient(proxy.Host, EffectivePort(proxy))
                : new HttpsProxyClient(proxy.Host, EffectivePort(proxy), credentials),
            "socks" or "socks5" or "socks5h" => credentials is null
                ? new Socks5Client(proxy.Host, EffectivePort(proxy))
                : new Socks5Client(proxy.Host, EffectivePort(proxy), credentials),
            _ => null
        };
    }

    public static string SafeRouteLabel(NetworkProxyRoute route)
    {
        if (!route.HasProxy) return "直连";
        if (!Uri.TryCreate(route.ProxyUrl, UriKind.Absolute, out var proxy)) return "系统代理";
        return $"{(route.Source == "windows" ? "Windows 系统代理" : "环境代理")} ({proxy.Scheme}://{proxy.Host}:{EffectivePort(proxy)})";
    }

    public static string FriendlyNetworkFailure(Exception error, string serviceName)
    {
        var current = error;
        while (current.InnerException is not null) current = current.InnerException;
        var technical = Safe(current.Message);

        if (error is OperationCanceledException or TimeoutException
            || technical.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return $"{serviceName}连接超时。程序已尝试 Windows 系统代理与直连，将在后台继续重试。";

        if (technical.Contains("name or service not known", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("host not known", StringComparison.OrdinalIgnoreCase))
            return $"{serviceName}域名无法解析。请检查此电脑的 DNS、代理或公司网络策略。";

        if (technical.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("ssl", StringComparison.OrdinalIgnoreCase)
            || technical.Contains("tls", StringComparison.OrdinalIgnoreCase))
            return $"{serviceName}安全连接被中断。请检查杀毒软件、公司网关或代理是否正在拦截 TLS。";

        return $"{serviceName}网络连接暂时不可用。程序已自动尝试 Windows 系统代理与直连，将在后台继续重试。技术信息：{technical}";
    }

    private static bool TryNormalizeProxy(string? value, out string proxyUrl)
    {
        proxyUrl = "";
        var candidate = (value ?? "").Trim();
        if (candidate.Length == 0) return false;
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = $"http://{candidate}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Scheme.ToLowerInvariant() is not ("http" or "https" or "socks" or "socks5" or "socks5h"))
            return false;
        proxyUrl = uri.AbsoluteUri;
        return true;
    }

    private static NetworkCredential? ParseCredentials(Uri proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy.UserInfo)) return null;
        var parts = proxy.UserInfo.Split(':', 2);
        return new NetworkCredential(
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    private static int EffectivePort(Uri proxy) =>
        proxy.IsDefaultPort
            ? proxy.Scheme.ToLowerInvariant() switch
            {
                "http" => 80,
                "https" => 443,
                _ => 1080
            }
            : proxy.Port;

    private static string Safe(string? value)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 240 ? text : text[..240];
    }
}
