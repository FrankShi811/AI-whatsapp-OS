using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record PublicIpCheckResult(WhatsAppIpState State, bool Changed, string Error = "");

public sealed class PublicIpMonitor
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly LocalRepository _repository;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublicIpMonitor(LocalRepository repository, HttpClient? client = null)
    {
        _repository = repository;
        _client = client ?? SharedClient;
    }

    public async Task<PublicIpCheckResult> CheckAsync(string accountId, CancellationToken cancellationToken = default) =>
        await CheckAsync(accountId, false, cancellationToken);

    public async Task<PublicIpCheckResult> CheckAsync(string accountId, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        accountId = string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await _repository.GetWhatsAppIpStateAsync(accountId, cancellationToken) ?? new WhatsAppIpState { AccountId = accountId };
            if (!forceRefresh && !string.IsNullOrWhiteSpace(state.CurrentIp) && state.LastCheckedAt is { } checkedAt && DateTimeOffset.Now - checkedAt < TimeSpan.FromSeconds(8))
                return new PublicIpCheckResult(state, false);
            try
            {
                var currentIp = await GetCurrentIpAsync(cancellationToken);
                var previousCurrent = state.CurrentIp;
                var changed = !string.IsNullOrWhiteSpace(previousCurrent) && !previousCurrent.Equals(currentIp, StringComparison.OrdinalIgnoreCase);
                var needsLocation = changed || string.IsNullOrWhiteSpace(state.Country) || !state.CurrentIp.Equals(currentIp, StringComparison.OrdinalIgnoreCase);
                if (changed)
                {
                    state.PreviousIp = previousCurrent;
                    state.ChangedAt = DateTimeOffset.Now;
                }
                state.CurrentIp = currentIp;
                state.LastCheckedAt = DateTimeOffset.Now;
                if (needsLocation) await FillLocationAsync(state, cancellationToken);
                await _repository.SaveWhatsAppIpStateAsync(state, cancellationToken);
                if (changed)
                    await _repository.LogEventAsync("whatsapp_public_ip_changed", null, null, $"account={accountId};from={state.PreviousIp};to={state.CurrentIp};location={state.LocationLabel}", cancellationToken);
                return new PublicIpCheckResult(state, changed);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                return new PublicIpCheckResult(state, false, error is TaskCanceledException ? "公网 IP 检测超时" : "暂时无法检测公网 IP");
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<string> GetCurrentIpAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("https://api64.ipify.org?format=json", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var ip = json.RootElement.TryGetProperty("ip", out var value) ? value.GetString() ?? "" : "";
        if (!IPAddress.TryParse(ip, out var address) || IPAddress.IsLoopback(address)) throw new InvalidDataException("公网 IP 响应无效");
        return address.ToString();
    }

    private async Task FillLocationAsync(WhatsAppIpState state, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"https://ipwho.is/{Uri.EscapeDataString(state.CurrentIp)}", cancellationToken);
        if (!response.IsSuccessStatusCode) return;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return;
        state.CountryCode = Text(root, "country_code");
        state.Country = Text(root, "country");
        state.Region = Text(root, "region");
        state.City = Text(root, "city");
        if (root.TryGetProperty("connection", out var connection) && connection.ValueKind == JsonValueKind.Object)
            state.Isp = Text(connection, "isp");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AI-Sales-OS", "1.0"));
        return client;
    }
}
