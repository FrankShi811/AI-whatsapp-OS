using System.Collections.Concurrent;
using System.Text.Json;

namespace WAFlow.Core.Services;

public sealed class WhatsAppConnectionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WhatsAppBridgeClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<WhatsAppBridgeEvent>? EventReceived;
    public string ActiveAccountId { get; private set; } = "primary";
    public bool IsConnected => IsConnectedFor(ActiveAccountId);
    public string ConnectionState => ConnectionStateFor(ActiveAccountId);

    public void SetActiveAccount(string accountId) => ActiveAccountId = Normalize(accountId);
    public bool IsConnectedFor(string accountId) => _clients.TryGetValue(Normalize(accountId), out var client) && client.IsConnected;
    public string ConnectionStateFor(string accountId) => _clients.TryGetValue(Normalize(accountId), out var client) ? client.ConnectionState : "disconnected";

    public async Task StartAsync(string accountId = "primary", CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId); ActiveAccountId = accountId;
        await GetClient(accountId).StartAsync(accountId, cancellationToken);
    }

    public Task<JsonElement> ConnectAsync(CancellationToken cancellationToken = default) => ConnectAsync(ActiveAccountId, cancellationToken);
    public Task<JsonElement> PingAsync(CancellationToken cancellationToken = default) => GetClient(ActiveAccountId).PingAsync(cancellationToken);
    public async Task<JsonElement> ConnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId); ActiveAccountId = accountId;
        var client = GetClient(accountId);
        await client.StartAsync(accountId, cancellationToken);
        return await client.ConnectAsync(cancellationToken);
    }

    public Task<JsonElement> DisconnectAsync(CancellationToken cancellationToken = default) => GetClient(ActiveAccountId).DisconnectAsync(cancellationToken);
    public Task<JsonElement> LogoutAsync(CancellationToken cancellationToken = default) => GetClient(ActiveAccountId).LogoutAsync(cancellationToken);
    public Task<JsonElement> SendTextAsync(string phone, string text, CancellationToken cancellationToken = default) => SendTextAsync(ActiveAccountId, phone, text, cancellationToken);
    public Task<JsonElement> SendTextAsync(string accountId, string phone, string text, CancellationToken cancellationToken = default) => GetClient(accountId).SendTextAsync(phone, text, cancellationToken);
    public Task<JsonElement> SyncNowAsync(CancellationToken cancellationToken = default) => GetClient(ActiveAccountId).SyncNowAsync(cancellationToken);

    private WhatsAppBridgeClient GetClient(string accountId)
    {
        accountId = Normalize(accountId);
        return _clients.GetOrAdd(accountId, id =>
        {
            var client = new WhatsAppBridgeClient();
            client.EventReceived += (_, e) => EventReceived?.Invoke(this, string.IsNullOrWhiteSpace(e.AccountId) ? e with { AccountId = id } : e);
            return client;
        });
    }

    private static string Normalize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "primary" : value.Trim();
        if (normalized.Length > 64 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-')) throw new InvalidOperationException("WhatsApp 账号 ID 无效。");
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values) await client.DisposeAsync();
        _clients.Clear();
    }
}
