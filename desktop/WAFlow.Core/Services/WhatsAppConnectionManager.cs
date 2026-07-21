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
    public Task<JsonElement> SendReplyTextAsync(string accountId, string phone, string text, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) => GetClient(accountId).SendReplyTextAsync(phone, text, quotedMessageId, quotedText, quotedFromMe, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string phone, string path, string caption = "", CancellationToken cancellationToken = default) => SendMediaAsync(ActiveAccountId, phone, path, caption, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string accountId, string phone, string path, string caption, CancellationToken cancellationToken = default) => GetClient(accountId).SendMediaAsync(phone, path, caption, cancellationToken);
    public Task<JsonElement> SendReplyMediaAsync(string accountId, string phone, string path, string caption, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) => GetClient(accountId).SendReplyMediaAsync(phone, path, caption, quotedMessageId, quotedText, quotedFromMe, cancellationToken);
    public Task<JsonElement> RevokeMessageAsync(string accountId, string phone, string messageId, CancellationToken cancellationToken = default) => GetClient(accountId).RevokeMessageAsync(phone, messageId, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string phone, bool pinned, CancellationToken cancellationToken = default) => SetChatPinnedAsync(ActiveAccountId, phone, pinned, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string accountId, string phone, bool pinned, CancellationToken cancellationToken = default) => GetClient(accountId).SetChatPinnedAsync(phone, pinned, cancellationToken);
    public async Task<WhatsAppGroupCreateResult> CreateGroupAsync(string accountId, WhatsAppGroupCreateRequest request, CancellationToken cancellationToken = default)
    {
        var result = await GetClient(accountId).CreateGroupAsync(request, cancellationToken);
        var jid = result.TryGetProperty("jid", out var jidElement) ? jidElement.GetString() ?? "" : "";
        var subject = result.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() ?? request.Subject : request.Subject;
        var participants = result.TryGetProperty("participants", out var participantsElement) && participantsElement.ValueKind == JsonValueKind.Array
            ? participantsElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList()
            : request.ParticipantPhones.ToList();
        var count = result.TryGetProperty("participantCount", out var countElement) && countElement.TryGetInt32(out var parsedCount) ? parsedCount : participants.Count;
        if (string.IsNullOrWhiteSpace(jid)) throw new WhatsAppBridgeException("group_create_missing_id", "WhatsApp 未返回新群组 ID。");
        return new WhatsAppGroupCreateResult(jid, subject, count, participants);
    }
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
