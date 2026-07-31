using System.Net;
using System.Runtime.InteropServices;
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

        if (TryResolveWindowsAutoProxy(destination, out var windowsRoute))
            return windowsRoute;

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
        return $"{(route.Source.StartsWith("windows", StringComparison.OrdinalIgnoreCase) ? "Windows 系统代理" : "环境代理")} ({proxy.Scheme}://{proxy.Host}:{EffectivePort(proxy)})";
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

    private static bool TryResolveWindowsAutoProxy(Uri destination, out NetworkProxyRoute route)
    {
        route = new NetworkProxyRoute("", "direct", false);
        if (!OperatingSystem.IsWindows()) return false;

        WinHttpCurrentUserIeProxyConfig config = default;
        var hasConfig = false;
        IntPtr session = IntPtr.Zero;
        try
        {
            hasConfig = WinHttpGetIEProxyConfigForCurrentUser(out config);
            var manualProxy = PointerText(config.Proxy);
            if (TryNormalizeWindowsProxyList(manualProxy, destination.Scheme, out var manualProxyUrl))
            {
                route = new NetworkProxyRoute(manualProxyUrl, "windows:manual", true);
                return true;
            }

            var autoConfigUrl = PointerText(config.AutoConfigUrl);
            if (!hasConfig || (!config.AutoDetect && string.IsNullOrWhiteSpace(autoConfigUrl)))
                return false;

            session = WinHttpOpen(
                "AI Sales OS/WAFlow",
                WinHttpAccessTypeNoProxy,
                null,
                null,
                0);
            if (session == IntPtr.Zero) return false;
            _ = WinHttpSetTimeouts(session, 3000, 3000, 5000, 5000);

            var options = new WinHttpAutoProxyOptions
            {
                Flags = (config.AutoDetect ? WinHttpAutoproxyAutoDetect : 0)
                    | (!string.IsNullOrWhiteSpace(autoConfigUrl) ? WinHttpAutoproxyConfigUrl : 0),
                AutoDetectFlags = WinHttpAutoDetectTypeDhcp | WinHttpAutoDetectTypeDnsA,
                AutoConfigUrl = config.AutoConfigUrl,
                AutoLogonIfChallenged = true
            };
            if (options.Flags == 0
                || !WinHttpGetProxyForUrl(session, destination.AbsoluteUri, ref options, out var proxyInfo))
                return false;
            try
            {
                if (proxyInfo.AccessType != WinHttpAccessTypeNamedProxy
                    || !TryNormalizeWindowsProxyList(PointerText(proxyInfo.Proxy), destination.Scheme, out var proxyUrl))
                    return false;
                route = new NetworkProxyRoute(proxyUrl, "windows:auto", true);
                return true;
            }
            finally
            {
                FreeGlobal(proxyInfo.Proxy);
                FreeGlobal(proxyInfo.ProxyBypass);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (session != IntPtr.Zero) WinHttpCloseHandle(session);
            if (hasConfig)
            {
                FreeGlobal(config.AutoConfigUrl);
                FreeGlobal(config.Proxy);
                FreeGlobal(config.ProxyBypass);
            }
        }
    }

    private static bool TryNormalizeWindowsProxyList(string? value, string destinationScheme, out string proxyUrl)
    {
        proxyUrl = "";
        var entries = new List<string>();
        foreach (var segment in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = segment.Trim();
            if (entry.StartsWith("PROXY ", StringComparison.OrdinalIgnoreCase))
                entry = entry[6..].Trim();
            if (entry.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) || entry.Length == 0)
                continue;

            if (entry.Contains('='))
            {
                entries.Add(entry);
                continue;
            }

            var firstProxy = entry
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(item => !item.Equals("DIRECT", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(firstProxy))
                entries.Add(firstProxy);
        }
        if (entries.Count == 0) return false;

        string? selected = null;
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;
            var scheme = entry[..separator].Trim();
            if (scheme.Equals(destinationScheme, StringComparison.OrdinalIgnoreCase))
            {
                selected = entry[(separator + 1)..].Trim();
                break;
            }
        }
        selected ??= entries.FirstOrDefault(entry => !entry.Contains('='));
        selected ??= entries
            .Select(entry => entry.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[1])
            .FirstOrDefault();
        return TryNormalizeProxy(selected, out proxyUrl);
    }

    private static string PointerText(IntPtr pointer) =>
        pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pointer) ?? "";

    private static void FreeGlobal(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) _ = GlobalFree(pointer);
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

    private const uint WinHttpAccessTypeNoProxy = 1;
    private const uint WinHttpAccessTypeNamedProxy = 3;
    private const uint WinHttpAutoproxyAutoDetect = 0x00000001;
    private const uint WinHttpAutoproxyConfigUrl = 0x00000002;
    private const uint WinHttpAutoDetectTypeDhcp = 0x00000001;
    private const uint WinHttpAutoDetectTypeDnsA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinHttpCurrentUserIeProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect;
        public IntPtr AutoConfigUrl;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinHttpAutoProxyOptions
    {
        public uint Flags;
        public uint AutoDetectFlags;
        public IntPtr AutoConfigUrl;
        public IntPtr Reserved;
        public uint ReservedFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool AutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinHttpProxyInfo
    {
        public uint AccessType;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out WinHttpCurrentUserIeProxyConfig proxyConfig);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(
        string userAgent,
        uint accessType,
        string? proxyName,
        string? proxyBypass,
        uint flags);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpSetTimeouts(
        IntPtr session,
        int resolveTimeout,
        int connectTimeout,
        int sendTimeout,
        int receiveTimeout);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(
        IntPtr session,
        string url,
        ref WinHttpAutoProxyOptions options,
        out WinHttpProxyInfo proxyInfo);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpCloseHandle(IntPtr internet);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
