using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public interface IPublicWebDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

public sealed class SystemPublicWebDnsResolver : IPublicWebDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(host).WaitAsync(cancellationToken);
}

public sealed class PublicWebReaderOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaximumRedirects { get; init; } = 4;
    public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;
    public int MaximumExtractedCharacters { get; init; } = 60_000;
    public int MaximumTitleCharacters { get; init; } = 500;
    public string UserAgent { get; init; } = "AI-Sales-OS-PublicWebReader/1.0";
}

public sealed record PublicWebReadResult(
    Uri OriginalUrl,
    Uri FinalUrl,
    Uri CanonicalUrl,
    string? Title,
    string ContentText,
    string ContentHash,
    DateTimeOffset? PublishedAt,
    DateTimeOffset RetrievedAt,
    HttpStatusCode StatusCode,
    string? ContentType,
    int RedirectCount);

/// <summary>
/// Reads public HTML/text pages through a deliberately narrow network boundary.
/// Redirect destinations and DNS answers are validated on every hop, response
/// size is bounded, and no cookie, credential or referrer is attached.
/// </summary>
public sealed class PublicWebReader : IDisposable
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex CommentRegex = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex InactiveElementRegex = new(
        @"<(script|style|noscript|svg|template|form|iframe|object|canvas)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex MainElementRegex = new(
        @"<(main|article)\b[^>]*>(?<content>.*?)</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex BodyElementRegex = new(
        @"<body\b[^>]*>(?<content>.*?)</body\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex TitleElementRegex = new(
        @"<title\b[^>]*>(?<content>.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex TimeElementRegex = new(
        @"<time\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex MetaTagRegex = new(
        @"<meta\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex LinkTagRegex = new(
        @"<link\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex AttributeRegex = new(
        @"(?<name>[\w:-]+)\s*=\s*(?:\""(?<double>[^\""\r\n]*)\""|'(?<single>[^'\r\n]*)'|(?<bare>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex BreakRegex = new(
        @"<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex BlockEndRegex = new(
        @"</(?:p|div|section|article|main|header|footer|aside|h[1-6]|li|ul|ol|table|tr|td|th|blockquote|pre|address|figure|figcaption|details|summary)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex ListStartRegex = new(
        @"<li\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex InlineWhitespaceRegex = new(
        @"[^\S\r\n]+",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex ExcessNewlineRegex = new(
        @"(?:\s*\r?\n\s*){3,}",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IPublicWebDnsResolver _dnsResolver;
    private readonly PublicWebReaderOptions _options;
    private readonly TimeProvider _timeProvider;

    static PublicWebReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public PublicWebReader(
        HttpClient? httpClient = null,
        IPublicWebDnsResolver? dnsResolver = null,
        PublicWebReaderOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _dnsResolver = dnsResolver ?? new SystemPublicWebDnsResolver();
        _options = options ?? new PublicWebReaderOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateSafeHttpClient(_dnsResolver, _options);
    }

    public Task<PublicWebReadResult> ReadAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri))
            throw Blocked("网页地址无效，已停止读取。");
        return ReadAsync(uri, cancellationToken);
    }

    public async Task<PublicWebReadResult> ReadAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var originalUrl = NormalizeRequestUri(url);
        var currentUrl = originalUrl;
        var redirectCount = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            while (true)
            {
                await ValidatePublicDestinationAsync(currentUrl, timeout.Token);
                using var request = CreateRequest(currentUrl);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= _options.MaximumRedirects)
                        throw Blocked("网页重定向次数过多，已停止读取。");
                    if (response.Headers.Location is not Uri location)
                        throw Blocked("网页返回了无目标地址的重定向，已停止读取。");

                    currentUrl = NormalizeRequestUri(
                        location.IsAbsoluteUri ? location : new Uri(currentUrl, location));
                    redirectCount++;
                    continue;
                }

                EnsureSuccessfulStatus(response.StatusCode);
                var mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
                if (!IsSupportedContentType(mediaType))
                    throw Blocked($"网页内容类型 {mediaType ?? "未知"} 不支持安全文本提取。");

                var bytes = await ReadLimitedContentAsync(
                    response.Content,
                    _options.MaximumResponseBytes,
                    timeout.Token);
                var htmlOrText = DecodeContent(bytes, response.Content.Headers.ContentType?.CharSet);
                var isPlainText = string.Equals(mediaType, "text/plain", StringComparison.OrdinalIgnoreCase);
                var finalUrl = NormalizeRequestUri(response.RequestMessage?.RequestUri ?? currentUrl);
                var extracted = isPlainText
                    ? new ExtractedPage(
                        null,
                        NormalizeExtractedText(htmlOrText, _options.MaximumExtractedCharacters),
                        null,
                        null)
                    : ExtractHtml(htmlOrText, finalUrl);
                if (string.IsNullOrWhiteSpace(extracted.Content))
                    throw Blocked("网页没有可安全提取的公开文本。");

                var contentHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(extracted.Content))).ToLowerInvariant();
                return new PublicWebReadResult(
                    originalUrl,
                    finalUrl,
                    extracted.CanonicalUrl ?? finalUrl,
                    extracted.Title,
                    extracted.Content,
                    contentHash,
                    extracted.PublishedAt,
                    _timeProvider.GetUtcNow(),
                    response.StatusCode,
                    mediaType,
                    redirectCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.WebFetchTimeout,
                "读取公开网页超时，已保留搜索结果摘要。",
                retryable: true,
                error);
        }
        catch (CustomerEnrichmentException)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.WebFetchBlocked,
                "公开网页当前无法访问，已保留搜索结果摘要。",
                retryable: true,
                error);
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.WebFetchBlocked,
                "网页结构过于复杂，已停止本次文本提取。",
                retryable: false,
                error);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private HttpRequestMessage CreateRequest(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,text/plain;q=0.9");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.8,en;q=0.6");
        return request;
    }

    private async Task ValidatePublicDestinationAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateHttpUri(uri);
        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw Blocked("安全策略不允许读取本机或内网地址。");

        if (IPAddress.TryParse(host, out var literal))
        {
            EnsurePublicAddresses([literal]);
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _dnsResolver.ResolveAsync(host, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.WebFetchBlocked,
                "公开网页域名当前无法解析，已保留搜索结果摘要。",
                retryable: true,
                error);
        }
        EnsurePublicAddresses(addresses);
    }

    private static void EnsurePublicAddresses(IReadOnlyCollection<IPAddress>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
            throw Blocked("网页域名没有可访问的公开地址。");
        if (addresses.Any(IsPrivateOrReservedAddress))
            throw Blocked("安全策略不允许读取本机、内网或保留地址。");
    }

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                || (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;
        if (address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
            return true;
        if (address.IsIPv4MappedToIPv6)
            return IsPrivateOrReservedAddress(address.MapToIPv4());

        var ipv6 = address.GetAddressBytes();
        if ((ipv6[0] & 0xfe) == 0xfc) return true; // fc00::/7
        if (ipv6[0] == 0xfe && (ipv6[1] & 0xc0) is 0x80 or 0xc0) return true;
        if (ipv6[0] == 0xff) return true;
        if (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0d && ipv6[3] == 0xb8)
            return true; // documentation prefix
        if (ipv6[0] == 0x00 && ipv6[1] == 0x64 && ipv6[2] == 0xff && ipv6[3] == 0x9b)
            return true; // NAT64 prefixes can conceal a private IPv4 target
        if (ipv6[0] == 0x20 && ipv6[1] == 0x02)
            return true; // 6to4 embeds an IPv4 destination
        if (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x00
            && ((ipv6[3] & 0xf0) == 0x10 || (ipv6[3] & 0xf0) == 0x20))
            return true; // ORCHID / ORCHIDv2

        var firstTwelveAreZero = ipv6.Take(12).All(value => value == 0);
        if (firstTwelveAreZero)
            return IsPrivateOrReservedAddress(new IPAddress(ipv6.AsSpan(12, 4)));
        return false;
    }

    private ExtractedPage ExtractHtml(string html, Uri finalUrl)
    {
        var cleaned = CommentRegex.Replace(html, " ");
        cleaned = InactiveElementRegex.Replace(cleaned, " ");

        var title = ExtractTitle(cleaned);
        var canonical = ExtractCanonicalUrl(cleaned, finalUrl);
        var publishedAt = ExtractPublishedAt(cleaned);
        var mainMatches = MainElementRegex.Matches(cleaned);
        var primary = mainMatches
            .Select(match => match.Groups["content"].Value)
            .OrderByDescending(value => value.Length)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primary))
            primary = BodyElementRegex.Match(cleaned).Groups["content"].Value;
        if (string.IsNullOrWhiteSpace(primary)) primary = cleaned;

        return new ExtractedPage(
            title,
            NormalizeExtractedText(HtmlToText(primary), _options.MaximumExtractedCharacters),
            canonical,
            publishedAt);
    }

    private string? ExtractTitle(string html)
    {
        string? title = TitleElementRegex.Match(html).Groups["content"].Value;
        if (string.IsNullOrWhiteSpace(title))
            title = FindMetaContent(html, "og:title", "twitter:title");
        title = NormalizeExtractedText(HtmlToText(title ?? ""), _options.MaximumTitleCharacters);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static Uri? ExtractCanonicalUrl(string html, Uri finalUrl)
    {
        foreach (Match match in LinkTagRegex.Matches(html))
        {
            var attributes = ReadAttributes(match.Groups["attributes"].Value);
            if (!attributes.TryGetValue("rel", out var rel)
                || !rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("canonical", StringComparer.OrdinalIgnoreCase)
                || !attributes.TryGetValue("href", out var href)
                || !Uri.TryCreate(finalUrl, WebUtility.HtmlDecode(href), out var candidate))
                continue;
            if (!IsHttpScheme(candidate.Scheme)
                || !string.IsNullOrEmpty(candidate.UserInfo)
                || !candidate.IdnHost.Equals(finalUrl.IdnHost, StringComparison.OrdinalIgnoreCase))
                continue;
            return NormalizeRequestUri(candidate);
        }
        return null;
    }

    private static DateTimeOffset? ExtractPublishedAt(string html)
    {
        var value = FindMetaContent(
            html,
            "article:published_time",
            "datepublished",
            "date",
            "publishdate",
            "pubdate",
            "og:published_time");
        if (TryParseDate(value, out var parsed)) return parsed;

        foreach (Match match in TimeElementRegex.Matches(html))
        {
            var attributes = ReadAttributes(match.Groups["attributes"].Value);
            if (attributes.TryGetValue("datetime", out value) && TryParseDate(value, out parsed))
                return parsed;
        }
        return null;
    }

    private static string? FindMetaContent(string html, params string[] names)
    {
        foreach (Match match in MetaTagRegex.Matches(html))
        {
            var attributes = ReadAttributes(match.Groups["attributes"].Value);
            var key = attributes.GetValueOrDefault("property")
                ?? attributes.GetValueOrDefault("name")
                ?? attributes.GetValueOrDefault("itemprop");
            if (key is null || !names.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase)) continue;
            if (attributes.TryGetValue("content", out var content))
                return WebUtility.HtmlDecode(content).Trim();
        }
        return null;
    }

    private static Dictionary<string, string> ReadAttributes(string attributes)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(attributes))
        {
            var value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            output[match.Groups["name"].Value] = value;
        }
        return output;
    }

    private static string HtmlToText(string html)
    {
        var text = BreakRegex.Replace(html, "\n");
        text = BlockEndRegex.Replace(text, "\n");
        text = ListStartRegex.Replace(text, "\n• ");
        text = HtmlTagRegex.Replace(text, " ");
        return WebUtility.HtmlDecode(text);
    }

    private static string NormalizeExtractedText(string value, int maximumCharacters)
    {
        var normalized = (value ?? "").Replace('\0', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = new string(normalized
            .Where(character => character == '\n' || character == '\t' || !char.IsControl(character))
            .ToArray());
        normalized = InlineWhitespaceRegex.Replace(normalized, " ");
        normalized = string.Join('\n', normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
        normalized = ExcessNewlineRegex.Replace(normalized, "\n\n").Trim();
        if (normalized.Length <= maximumCharacters) return normalized;

        var truncated = normalized[..maximumCharacters];
        var boundary = truncated.LastIndexOfAny(['\n', '。', '.', '!', '?', '！', '？']);
        return (boundary >= maximumCharacters * 3 / 4 ? truncated[..(boundary + 1)] : truncated).Trim();
    }

    private static string DecodeContent(byte[] bytes, string? headerCharset)
    {
        var encoding = ResolveEncoding(bytes, headerCharset);
        return encoding.GetString(bytes).TrimStart('\uFEFF');
    }

    private static Encoding ResolveEncoding(byte[] bytes, string? headerCharset)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8;
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode;
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode;

        var charset = (headerCharset ?? "").Trim().Trim('"', '\'');
        if (charset.Length == 0)
        {
            var headerText = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 8192));
            foreach (Match match in MetaTagRegex.Matches(headerText))
            {
                var attributes = ReadAttributes(match.Groups["attributes"].Value);
                if (attributes.TryGetValue("charset", out charset)) break;
                if (attributes.TryGetValue("http-equiv", out var equiv)
                    && equiv.Equals("content-type", StringComparison.OrdinalIgnoreCase)
                    && attributes.TryGetValue("content", out var content))
                {
                    var charsetIndex = content.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                    if (charsetIndex >= 0) charset = content[(charsetIndex + 8)..].Split(';')[0].Trim();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim().Trim('"', '\'')); }
            catch (ArgumentException) { }
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }

    private static async Task<byte[]> ReadLimitedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
            throw Blocked("网页内容超过安全读取上限。");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > maximumBytes)
                throw Blocked("网页内容超过安全读取上限。");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static HttpClient CreateSafeHttpClient(
        IPublicWebDnsResolver dnsResolver,
        PublicWebReaderOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, options.RequestTimeout.TotalSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                    ? new[] { literal }
                    : await dnsResolver.ResolveAsync(context.DnsEndPoint.Host, cancellationToken);
                EnsurePublicAddresses(addresses);

                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(address, context.DnsEndPoint.Port),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception error) when (error is SocketException or OperationCanceledException)
                    {
                        lastError = error;
                        socket.Dispose();
                        if (error is OperationCanceledException) throw;
                    }
                }
                throw new HttpRequestException("No validated public endpoint could be reached.", lastError);
            }
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static Uri NormalizeRequestUri(Uri uri)
    {
        ValidateHttpUri(uri);
        return new UriBuilder(uri) { Fragment = "" }.Uri;
    }

    private static void ValidateHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !IsHttpScheme(uri.Scheme)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw Blocked("只允许读取不含凭据的公开 HTTP 或 HTTPS 网页。");
    }

    private static bool IsHttpScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static void EnsureSuccessfulStatus(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        if (numeric is >= 200 and <= 299) return;
        if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.WebFetchTimeout,
                "公开网页响应超时，已保留搜索结果摘要。",
                retryable: true);
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || numeric == 407)
            throw Blocked("公开网页拒绝访问，系统不会尝试绕过登录或权限限制。");
        throw new CustomerEnrichmentException(
            CustomerEnrichmentErrorCodes.WebFetchBlocked,
            $"公开网页返回 HTTP {numeric}，已保留搜索结果摘要。",
            retryable: numeric >= 500);
    }

    private static bool IsSupportedContentType(string? mediaType) =>
        string.IsNullOrWhiteSpace(mediaType)
        || mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDate(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out parsed);

    private static CustomerEnrichmentException Blocked(string message) => new(
        CustomerEnrichmentErrorCodes.WebFetchBlocked,
        message,
        retryable: false);

    private static void ValidateOptions(PublicWebReaderOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero
            || options.MaximumRedirects is < 0 or > 12
            || options.MaximumResponseBytes is < 4096 or > 20 * 1024 * 1024
            || options.MaximumExtractedCharacters is < 1000 or > 1_000_000
            || options.MaximumTitleCharacters is < 20 or > 5000
            || string.IsNullOrWhiteSpace(options.UserAgent))
            throw new ArgumentOutOfRangeException(nameof(options), "PublicWebReader 配置超出安全范围。");
    }

    private sealed record ExtractedPage(
        string? Title,
        string Content,
        Uri? CanonicalUrl,
        DateTimeOffset? PublishedAt);
}
