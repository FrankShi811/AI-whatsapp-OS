using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record CustomerSearchRequest(
    string Query,
    int MaxResults = 8,
    string? Language = null,
    string? Country = null,
    int? MaximumAttempts = null);

public sealed class CustomerSearchProviderOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public int MaximumAttempts { get; init; } = 3;
    public int CircuitFailureThreshold { get; init; } = 3;
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromMinutes(2);
    public int MaximumResponseBytes { get; init; } = 1_048_576;
}

public interface ICustomerSearchProvider
{
    string Id { get; }
    bool RequiresApiKey { get; }

    Task<IReadOnlyList<CustomerEnrichmentSearchResult>> SearchAsync(
        CustomerSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerSearchProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);
}

public interface IMeteredCustomerSearchProvider
{
    int MaximumAttempts { get; }
    int LastAttemptCount { get; }
}

/// <summary>
/// Common reliability boundary for user-configured search providers. The class
/// deliberately owns no API key value: every request reads the injected local
/// secret store, and errors never include request headers or bodies.
/// </summary>
public abstract class CustomerSearchProviderBase : ICustomerSearchProvider, IMeteredCustomerSearchProvider
{
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly ISecretStore? _secretStore;
    private readonly HttpClient _httpClient;
    private readonly CustomerSearchProviderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _stateGate = new();
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _lastAttemptCount;

    protected CustomerSearchProviderBase(
        ISecretStore? secretStore,
        HttpClient? httpClient = null,
        CustomerSearchProviderOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _secretStore = secretStore;
        _httpClient = httpClient ?? SharedHttpClient;
        _options = options ?? new CustomerSearchProviderOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        ValidateOptions(_options);
    }

    public abstract string Id { get; }
    public abstract bool RequiresApiKey { get; }
    public int MaximumAttempts => _options.MaximumAttempts;
    public int LastAttemptCount => Volatile.Read(ref _lastAttemptCount);

    protected virtual string ConnectivityQuery => "AI Sales OS business information";

    public async Task<IReadOnlyList<CustomerEnrichmentSearchResult>> SearchAsync(
        CustomerSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        Interlocked.Exchange(ref _lastAttemptCount, 0);
        ThrowIfCircuitOpen();
        var apiKey = ReadApiKey();

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfCircuitOpen();
            CustomerEnrichmentException? lastError = null;
            var attemptLimit = Math.Clamp(normalized.MaximumAttempts ?? _options.MaximumAttempts, 1, _options.MaximumAttempts);
            for (var attempt = 0; attempt < attemptLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForRateLimitAsync(cancellationToken);
                try
                {
                    using var requestMessage = CreateHttpRequest(normalized, apiKey);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_options.RequestTimeout);
                    HttpResponseMessage response;
                    try
                    {
                        Interlocked.Increment(ref _lastAttemptCount);
                        response = await _httpClient.SendAsync(
                            requestMessage,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException error)
                    {
                        throw CreateProviderException(
                            ProviderUnavailableCode,
                            $"{DisplayName} 搜索请求超时，请稍后重试。",
                            retryable: true,
                            error);
                    }
                    catch (HttpRequestException error)
                    {
                        throw CreateProviderException(
                            ProviderUnavailableCode,
                            UnavailableMessage,
                            retryable: true,
                            error);
                    }

                    using (response)
                    {
                        if (!response.IsSuccessStatusCode)
                            throw CreateHttpFailure(response.StatusCode);

                        string payload;
                        try
                        {
                            payload = await ReadLimitedContentAsync(
                                response.Content,
                                _options.MaximumResponseBytes,
                                timeout.Token);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException error)
                        {
                            throw CreateProviderException(
                                ProviderUnavailableCode,
                                $"{DisplayName} 搜索响应读取超时，请稍后重试。",
                                retryable: true,
                                error);
                        }
                        catch (Exception error) when (error is HttpRequestException or IOException)
                        {
                            throw CreateProviderException(
                                ProviderUnavailableCode,
                                UnavailableMessage,
                                retryable: true,
                                error);
                        }
                        IReadOnlyList<CustomerEnrichmentSearchResult> parsed;
                        try
                        {
                            using var document = JsonDocument.Parse(payload);
                            parsed = ParseResults(document.RootElement, normalized, _timeProvider.GetUtcNow());
                        }
                        catch (CustomerEnrichmentException)
                        {
                            throw;
                        }
                        catch (Exception error) when (error is JsonException or InvalidOperationException)
                        {
                            throw CreateProviderException(
                                ProviderUnavailableCode,
                                $"{DisplayName} 返回了无法识别的搜索结果。",
                                retryable: true,
                                error);
                        }

                        RecordSuccess();
                        return Deduplicate(parsed, normalized.MaxResults);
                    }
                }
                catch (CustomerEnrichmentException error) when (error.Retryable)
                {
                    lastError = error;
                    if (attempt + 1 >= attemptLimit) break;
                    await _delay(ResolveRetryDelay(error, attempt), cancellationToken);
                }
            }

            var finalError = lastError ?? CreateProviderException(
                ProviderUnavailableCode,
                UnavailableMessage,
                retryable: true);
            RecordFailure();
            throw finalError;
        }
        catch (CustomerEnrichmentException error) when (!error.Retryable)
        {
            // Authentication, quota and configuration failures should not make a
            // transient circuit look unhealthy; the caller can switch provider.
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<CustomerSearchProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await SearchAsync(new CustomerSearchRequest(ConnectivityQuery, 1), cancellationToken);
            return new CustomerSearchProviderHealth(
                Id,
                true,
                $"{DisplayName} 连接正常。测试会计入该 Provider 的调用量。",
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CustomerEnrichmentException error)
        {
            return new CustomerSearchProviderHealth(Id, false, error.Message, _timeProvider.GetUtcNow());
        }
        catch
        {
            return new CustomerSearchProviderHealth(
                Id,
                false,
                $"{DisplayName} 暂时不可用。",
                _timeProvider.GetUtcNow());
        }
    }

    protected abstract string DisplayName { get; }
    protected abstract HttpRequestMessage CreateHttpRequest(CustomerSearchRequest request, string apiKey);
    protected abstract IReadOnlyList<CustomerEnrichmentSearchResult> ParseResults(
        JsonElement root,
        CustomerSearchRequest request,
        DateTimeOffset retrievedAt);

    protected virtual string ProviderUnavailableCode => CustomerEnrichmentErrorCodes.SearchProviderUnavailable;
    protected virtual string UnavailableMessage => $"{DisplayName} 暂时不可用，系统可切换到下一搜索 Provider。";

    protected static CustomerEnrichmentSearchResult CreateResult(
        string provider,
        CustomerSearchRequest request,
        string? title,
        string? url,
        string? snippet,
        DateTimeOffset? publishedAt,
        DateTimeOffset retrievedAt,
        int rank) => new()
    {
        Provider = provider,
        Query = request.Query,
        Title = CleanText(title),
        Url = NormalizeResultUrl(url),
        Snippet = CleanText(snippet),
        PublishedAt = publishedAt,
        RetrievedAt = retrievedAt,
        Rank = rank
    };

    protected static string? String(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }
        return null;
    }

    protected static DateTimeOffset? Timestamp(JsonElement element, params string[] names)
    {
        var value = String(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    protected CustomerEnrichmentException CreateProviderException(
        string code,
        string message,
        bool retryable,
        Exception? inner = null) => new(code, message, retryable, inner);

    private CustomerSearchRequest NormalizeRequest(CustomerSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = (request.Query ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (query.Length == 0)
            throw CreateProviderException(
                CustomerEnrichmentErrorCodes.CustomerIdentityMissing,
                "搜索关键词为空，已停止本次调查。",
                retryable: false);
        if (query.Length > 400 || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 50)
            throw CreateProviderException(
                CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                "搜索关键词超过 Provider 允许的长度，请缩短后重试。",
                retryable: false);

        return new CustomerSearchRequest(
            query,
            Math.Clamp(request.MaxResults, 1, 20),
            NormalizeLocale(request.Language),
            NormalizeLocale(request.Country)?.ToUpperInvariant(),
            request.MaximumAttempts is null ? null : Math.Clamp(request.MaximumAttempts.Value, 1, _options.MaximumAttempts));
    }

    private string ReadApiKey()
    {
        if (!RequiresApiKey) return "";
        string? key;
        try
        {
            key = _secretStore?.Read();
        }
        catch (Exception error)
        {
            throw CreateProviderException(
                CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                $"无法从本地安全存储读取 {DisplayName} API Key。",
                retryable: false,
                error);
        }

        if (string.IsNullOrWhiteSpace(key))
            throw CreateProviderException(
                CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                $"尚未配置 {DisplayName} API Key。",
                retryable: false);
        return key.Trim();
    }

    private void ThrowIfCircuitOpen()
    {
        lock (_stateGate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_circuitOpenUntil <= now)
            {
                if (_circuitOpenUntil != DateTimeOffset.MinValue)
                {
                    _circuitOpenUntil = DateTimeOffset.MinValue;
                    _consecutiveFailures = 0;
                }
                return;
            }

            throw CreateProviderException(
                ProviderUnavailableCode,
                $"{DisplayName} 连续失败后已暂时熔断，系统可切换到下一 Provider。",
                retryable: false);
        }
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_stateGate)
        {
            var now = _timeProvider.GetUtcNow();
            wait = _nextRequestAt > now ? _nextRequestAt - now : TimeSpan.Zero;
            var scheduledAt = wait > TimeSpan.Zero ? _nextRequestAt : now;
            _nextRequestAt = scheduledAt + _options.MinimumRequestInterval;
        }
        if (wait > TimeSpan.Zero) await _delay(wait, cancellationToken);
    }

    private void RecordSuccess()
    {
        lock (_stateGate)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntil = DateTimeOffset.MinValue;
        }
    }

    private void RecordFailure()
    {
        lock (_stateGate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitFailureThreshold)
                _circuitOpenUntil = _timeProvider.GetUtcNow() + _options.CircuitOpenDuration;
        }
    }

    private CustomerEnrichmentException CreateHttpFailure(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return CreateProviderException(
                ProviderUnavailableCode,
                $"{DisplayName} API Key 无效、无权限或服务未启用。",
                retryable: false);
        if (statusCode == HttpStatusCode.TooManyRequests)
            return CreateProviderException(
                CustomerEnrichmentErrorCodes.ProviderQuotaExhausted,
                $"{DisplayName} 免费额度或速率额度已用完，系统不会自动产生付费请求。",
                retryable: false);

        var retryable = statusCode == HttpStatusCode.RequestTimeout || (int)statusCode >= 500;
        return CreateProviderException(
            ProviderUnavailableCode,
            $"{DisplayName} 请求失败（HTTP {(int)statusCode}），系统可切换到下一 Provider。",
            retryable);
    }

    private TimeSpan ResolveRetryDelay(CustomerEnrichmentException error, int attempt)
    {
        if (error.Code == CustomerEnrichmentErrorCodes.ProviderQuotaExhausted)
            return TimeSpan.Zero;
        var multiplier = Math.Pow(2, Math.Clamp(attempt, 0, 5));
        return TimeSpan.FromMilliseconds(Math.Min(
            _options.BaseRetryDelay.TotalMilliseconds * multiplier,
            5000));
    }

    private static IReadOnlyList<CustomerEnrichmentSearchResult> Deduplicate(
        IEnumerable<CustomerEnrichmentSearchResult> candidates,
        int limit)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<CustomerEnrichmentSearchResult>();
        foreach (var item in candidates)
        {
            if (string.IsNullOrWhiteSpace(item.Url) || !urls.Add(item.Url)) continue;
            var contentKey = ContentKey(item.Title, item.Snippet);
            if (contentKey.Length > 0 && !contents.Add(contentKey)) continue;
            item.Rank = output.Count + 1;
            output.Add(item);
            if (output.Count >= limit) break;
        }
        return output;
    }

    private static string ContentKey(string title, string snippet)
    {
        var normalized = WhitespaceRegex.Replace($"{title} {snippet}".Trim().ToLowerInvariant(), " ");
        if (normalized.Length < 40) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string CleanText(string? value)
    {
        var withoutTags = HtmlTagRegex.Replace(value ?? "", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static string NormalizeResultUrl(string? value)
    {
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return "";
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    private static string? NormalizeLocale(string? value)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length is < 2 or > 12) return null;
        return normalized.All(character => char.IsLetter(character) || character == '-')
            ? normalized
            : null;
    }

    private static async Task<string> ReadLimitedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                "搜索 Provider 返回的数据过大，已停止读取。",
                retryable: false);
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new CustomerEnrichmentException(
                    CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                    "搜索 Provider 返回的数据过大，已停止读取。",
                    retryable: false);
            destination.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    private static void ValidateOptions(CustomerSearchProviderOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero
            || options.MinimumRequestInterval < TimeSpan.Zero
            || options.BaseRetryDelay < TimeSpan.Zero
            || options.MaximumAttempts is < 1 or > 5
            || options.CircuitFailureThreshold is < 1 or > 20
            || options.CircuitOpenDuration <= TimeSpan.Zero
            || options.MaximumResponseBytes is < 1024 or > 10 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "搜索 Provider 运行参数无效。");
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

public sealed class TavilySearchProvider : CustomerSearchProviderBase
{
    private static readonly Uri Endpoint = new("https://api.tavily.com/search");

    public TavilySearchProvider(
        ISecretStore secretStore,
        HttpClient? httpClient = null,
        CustomerSearchProviderOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(secretStore, httpClient, options, timeProvider, delay) { }

    public override string Id => "tavily";
    public override bool RequiresApiKey => true;
    protected override string DisplayName => "Tavily";

    protected override HttpRequestMessage CreateHttpRequest(CustomerSearchRequest request, string apiKey)
    {
        var body = new JsonObject
        {
            ["query"] = request.Query,
            ["search_depth"] = "basic",
            ["max_results"] = request.MaxResults,
            ["include_answer"] = false,
            ["include_raw_content"] = false,
            ["include_images"] = false
        };
        if (!string.IsNullOrWhiteSpace(request.Country)) body["country"] = request.Country;

        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(body.ToJsonString(Json.Options), Encoding.UTF8, "application/json");
        return message;
    }

    protected override IReadOnlyList<CustomerEnrichmentSearchResult> ParseResults(
        JsonElement root,
        CustomerSearchRequest request,
        DateTimeOffset retrievedAt)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        var output = new List<CustomerEnrichmentSearchResult>();
        var rank = 0;
        foreach (var item in results.EnumerateArray())
        {
            rank++;
            output.Add(CreateResult(
                Id,
                request,
                String(item, "title"),
                String(item, "url"),
                String(item, "content", "snippet"),
                Timestamp(item, "published_date", "published_at"),
                retrievedAt,
                rank));
        }
        return output;
    }
}

public sealed class BraveSearchProvider : CustomerSearchProviderBase
{
    private static readonly Uri Endpoint = new("https://api.search.brave.com/res/v1/web/search");

    public BraveSearchProvider(
        ISecretStore secretStore,
        HttpClient? httpClient = null,
        CustomerSearchProviderOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(secretStore, httpClient, options, timeProvider, delay) { }

    public override string Id => "brave";
    public override bool RequiresApiKey => true;
    protected override string DisplayName => "Brave Search";

    protected override HttpRequestMessage CreateHttpRequest(CustomerSearchRequest request, string apiKey)
    {
        var query = new List<string>
        {
            $"q={Uri.EscapeDataString(request.Query)}",
            $"count={request.MaxResults}",
            "safesearch=strict",
            "text_decorations=false"
        };
        if (!string.IsNullOrWhiteSpace(request.Language))
            query.Add($"search_lang={Uri.EscapeDataString(request.Language)}");
        if (!string.IsNullOrWhiteSpace(request.Country))
            query.Add($"country={Uri.EscapeDataString(request.Country)}");

        var builder = new UriBuilder(Endpoint) { Query = string.Join('&', query) };
        var message = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey);
        return message;
    }

    protected override IReadOnlyList<CustomerEnrichmentSearchResult> ParseResults(
        JsonElement root,
        CustomerSearchRequest request,
        DateTimeOffset retrievedAt)
    {
        if (!root.TryGetProperty("web", out var web)
            || web.ValueKind != JsonValueKind.Object
            || !web.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            return [];
        var output = new List<CustomerEnrichmentSearchResult>();
        var rank = 0;
        foreach (var item in results.EnumerateArray())
        {
            rank++;
            var snippet = String(item, "description", "snippet") ?? "";
            if (item.TryGetProperty("extra_snippets", out var extras) && extras.ValueKind == JsonValueKind.Array)
                snippet = string.Join(" ", new[] { snippet }.Concat(
                    extras.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? "")));
            output.Add(CreateResult(
                Id,
                request,
                String(item, "title"),
                String(item, "url"),
                snippet,
                Timestamp(item, "page_age", "published_at"),
                retrievedAt,
                rank));
        }
        return output;
    }
}

public sealed class SearXngSearchProvider : CustomerSearchProviderBase
{
    private readonly Uri _endpoint;

    public SearXngSearchProvider(
        string baseUrl = "http://127.0.0.1:8080",
        HttpClient? httpClient = null,
        CustomerSearchProviderOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(null, httpClient, options ?? new CustomerSearchProviderOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(12),
            MinimumRequestInterval = TimeSpan.FromMilliseconds(100)
        }, timeProvider, delay)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("SearXNG 地址必须是有效的 HTTP 或 HTTPS 地址。", nameof(baseUrl));
        var root = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _endpoint = new Uri(root, "search");
    }

    public override string Id => "searxng";
    public override bool RequiresApiKey => false;
    protected override string DisplayName => "本地 SearXNG";
    protected override string ProviderUnavailableCode => CustomerEnrichmentErrorCodes.SearXngNotRunning;
    protected override string UnavailableMessage => "本地 SearXNG 未启动或未启用 JSON 输出。";

    protected override HttpRequestMessage CreateHttpRequest(CustomerSearchRequest request, string apiKey)
    {
        var query = new List<string>
        {
            $"q={Uri.EscapeDataString(request.Query)}",
            "format=json",
            $"pageno=1",
            "safesearch=2"
        };
        if (!string.IsNullOrWhiteSpace(request.Language))
            query.Add($"language={Uri.EscapeDataString(request.Language)}");
        var builder = new UriBuilder(_endpoint) { Query = string.Join('&', query) };
        var message = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    protected override IReadOnlyList<CustomerEnrichmentSearchResult> ParseResults(
        JsonElement root,
        CustomerSearchRequest request,
        DateTimeOffset retrievedAt)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        var output = new List<CustomerEnrichmentSearchResult>();
        var rank = 0;
        foreach (var item in results.EnumerateArray())
        {
            rank++;
            output.Add(CreateResult(
                Id,
                request,
                String(item, "title"),
                String(item, "url"),
                String(item, "content", "snippet"),
                Timestamp(item, "publishedDate", "published_date", "published_at"),
                retrievedAt,
                rank));
            if (output.Count >= request.MaxResults) break;
        }
        return output;
    }
}
