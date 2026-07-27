using System.Collections.Concurrent;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class MessagingSyncService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly WhatsAppConnectionManager _whatsApp;
    private readonly EmailService _email;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWhatsAppSync = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifetimeLock = new();
    private CancellationTokenSource? _lifetime;
    private Task? _supervisor;

    public MessagingSyncService(
        LocalRepository repository,
        WhatsAppConnectionManager whatsApp,
        EmailService email)
    {
        _repository = repository;
        _whatsApp = whatsApp;
        _email = email;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _email.StartBackgroundSyncAsync(cancellationToken);
        lock (_lifetimeLock)
        {
            if (_supervisor is { IsCompleted: false }) return;
            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _supervisor = RunWhatsAppSupervisorAsync(_lifetime.Token);
        }
    }

    private async Task RunWhatsAppSupervisorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var accounts = await _repository.GetWhatsAppAccountsAsync(cancellationToken);
                foreach (var account in accounts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(account.LinkedPhone)
                        || !_whatsApp.HasStoredSession(account.Id)
                        || !_whatsApp.IsAutoReconnectEnabled(account.Id))
                    {
                        _lastWhatsAppSync.TryRemove(account.Id, out _);
                        continue;
                    }

                    try
                    {
                        await _whatsApp.EnsureConnectedAsync(account.Id, cancellationToken);
                        if (!_whatsApp.IsConnectedFor(account.Id))
                        {
                            _lastWhatsAppSync.TryRemove(account.Id, out _);
                            continue;
                        }

                        var now = DateTimeOffset.Now;
                        if (!_lastWhatsAppSync.TryGetValue(account.Id, out var lastSync)
                            || now - lastSync >= TimeSpan.FromMinutes(5))
                        {
                            await _whatsApp.SyncNowAsync(account.Id, cancellationToken);
                            _lastWhatsAppSync[account.Id] = now;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        _lastWhatsAppSync.TryRemove(account.Id, out _);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A transient repository failure must not terminate the application-wide sync supervisor.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? supervisor;
        lock (_lifetimeLock)
        {
            lifetime = _lifetime;
            supervisor = _supervisor;
            _lifetime = null;
            _supervisor = null;
        }
        lifetime?.Cancel();
        if (supervisor is not null)
        {
            try { await supervisor; }
            catch (OperationCanceledException) { }
        }
        lifetime?.Dispose();
        _lastWhatsAppSync.Clear();
    }
}
