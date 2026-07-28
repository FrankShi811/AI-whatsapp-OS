using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record WhatsAppNumberRegistrationLookupResult(bool Exists, string Jid);

public interface IWhatsAppNumberRegistrationLookup
{
    bool IsConnectedFor(string accountId);
    Task<WhatsAppNumberRegistrationLookupResult> LookupRegistrationAsync(
        string accountId,
        string phone,
        CancellationToken cancellationToken = default);
}

public sealed record WhatsAppNumberValidationChanged(
    string LeadId,
    string Phone,
    WhatsAppRegistrationStatus Status,
    string StateLabel,
    DateTimeOffset? CheckedAt,
    string Error);

public sealed class WhatsAppNumberValidationService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly IWhatsAppNumberRegistrationLookup _lookup;
    private readonly TimeSpan _requestInterval;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lifetimeLock = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;

    public event EventHandler<WhatsAppNumberValidationChanged>? StatusChanged;

    public WhatsAppNumberValidationService(
        LocalRepository repository,
        IWhatsAppNumberRegistrationLookup lookup,
        TimeSpan? requestInterval = null)
    {
        _repository = repository;
        _lookup = lookup;
        _requestInterval = requestInterval ?? TimeSpan.FromMilliseconds(900);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifetimeLock)
        {
            if (_worker is { IsCompleted: false }) return Task.CompletedTask;
            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = RunAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    public void NotifyPendingWork()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task<int> ProcessPendingAsync(int maxCount = 25, CancellationToken cancellationToken = default)
    {
        maxCount = Math.Clamp(maxCount, 1, 100);
        var account = (await _repository.GetWhatsAppAccountsAsync(cancellationToken))
            .FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.LinkedPhone)
                && _lookup.IsConnectedFor(item.Id));
        if (account is null) return 0;

        var now = DateTimeOffset.Now;
        var pending = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(NeedsCheck)
            .OrderBy(item => item.WhatsAppRegistrationNextRetryAt ?? item.WhatsAppRegistrationLastAttemptAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.UpdatedAt)
            .Take(maxCount)
            .Select(item => item.Id)
            .ToList();
        var processed = 0;
        foreach (var leadId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CheckLeadAsync(account.Id, leadId, cancellationToken)) processed++;
            if (_requestInterval > TimeSpan.Zero)
                await Task.Delay(_requestInterval, cancellationToken);
        }
        return processed;

        bool NeedsCheck(Lead lead)
        {
            if (!lead.PhoneValid || string.IsNullOrWhiteSpace(lead.PhoneE164)) return false;
            return lead.WhatsAppRegistrationStatus switch
            {
                WhatsAppRegistrationStatus.Pending => true,
                WhatsAppRegistrationStatus.Checking =>
                    lead.WhatsAppRegistrationLastAttemptAt is null
                    || now - lead.WhatsAppRegistrationLastAttemptAt >= TimeSpan.FromMinutes(2),
                WhatsAppRegistrationStatus.RetryableFailed =>
                    lead.WhatsAppRegistrationNextRetryAt is null
                    || lead.WhatsAppRegistrationNextRetryAt <= now,
                WhatsAppRegistrationStatus.Registered or WhatsAppRegistrationStatus.NotRegistered =>
                    !lead.WhatsAppRegistrationMatchesCurrentPhone,
                _ => false
            };
        }
    }

    private async Task<bool> CheckLeadAsync(string accountId, string leadId, CancellationToken cancellationToken)
    {
        var lead = await _repository.GetLeadAsync(leadId, cancellationToken);
        if (lead is null || !lead.PhoneValid || string.IsNullOrWhiteSpace(lead.PhoneE164)) return false;
        var phone = lead.PhoneE164;
        lead.WhatsAppRegistrationStatus = WhatsAppRegistrationStatus.Checking;
        lead.WhatsAppRegistrationLastAttemptAt = DateTimeOffset.Now;
        lead.WhatsAppRegistrationNextRetryAt = null;
        lead.WhatsAppRegistrationAttemptCount++;
        lead.WhatsAppRegistrationError = "";
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        Raise(lead);

        try
        {
            var result = await _lookup.LookupRegistrationAsync(accountId, phone, cancellationToken);
            var current = await _repository.GetLeadAsync(leadId, cancellationToken);
            if (current is null) return true;
            if (!SamePhone(current.PhoneE164, phone))
            {
                current.QueueWhatsAppRegistrationCheck();
                await _repository.UpsertLeadAsync(current, cancellationToken);
                Raise(current);
                return true;
            }
            current.WhatsAppRegistrationStatus = result.Exists
                ? WhatsAppRegistrationStatus.Registered
                : WhatsAppRegistrationStatus.NotRegistered;
            current.WhatsAppRegistrationPhone = current.PhoneE164;
            current.WhatsAppRegistrationCheckedAt = DateTimeOffset.Now;
            current.WhatsAppRegistrationNextRetryAt = null;
            current.WhatsAppRegistrationError = "";
            await _repository.UpsertLeadAsync(current, cancellationToken);
            await _repository.LogEventAsync(
                result.Exists ? "whatsapp_number_registered" : "whatsapp_number_not_registered",
                current.Id,
                null,
                $"phone={current.PhoneE164};account={accountId};jid={result.Jid}",
                cancellationToken);
            Raise(current);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var current = await _repository.GetLeadAsync(leadId, cancellationToken);
            if (current is null) return true;
            if (!SamePhone(current.PhoneE164, phone))
            {
                current.QueueWhatsAppRegistrationCheck();
            }
            else
            {
                current.WhatsAppRegistrationStatus = WhatsAppRegistrationStatus.RetryableFailed;
                current.WhatsAppRegistrationPhone = "";
                current.WhatsAppRegistrationCheckedAt = null;
                current.WhatsAppRegistrationError = ErrorCode(error);
                var retryMinutes = Math.Min(30, Math.Max(2, 1 << Math.Min(4, Math.Max(1, current.WhatsAppRegistrationAttemptCount))));
                current.WhatsAppRegistrationNextRetryAt = DateTimeOffset.Now.AddMinutes(retryMinutes);
            }
            await _repository.UpsertLeadAsync(current, cancellationToken);
            await _repository.LogEventAsync(
                "whatsapp_number_check_retryable",
                current.Id,
                null,
                $"phone={phone};account={accountId};error={current.WhatsAppRegistrationError}",
                cancellationToken);
            Raise(current);
            return true;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessPendingAsync(25, cancellationToken);
                if (processed > 0) continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Durable lead state remains the source of truth. A later cycle retries safely.
            }

            try { await _wake.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private void Raise(Lead lead) => StatusChanged?.Invoke(
        this,
        new WhatsAppNumberValidationChanged(
            lead.Id,
            lead.PhoneE164,
            lead.WhatsAppRegistrationStatus,
            lead.PhoneState,
            lead.WhatsAppRegistrationCheckedAt,
            lead.WhatsAppRegistrationError));

    private static bool SamePhone(string left, string right) =>
        Digits(left).Equals(Digits(right), StringComparison.Ordinal);

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());

    private static string ErrorCode(Exception error) => error switch
    {
        WhatsAppBridgeException bridge => bridge.Code,
        TimeoutException => "whatsapp_check_timeout",
        _ when error is JsonException => "whatsapp_check_invalid_response",
        _ => "whatsapp_check_unavailable"
    };

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? worker;
        lock (_lifetimeLock)
        {
            lifetime = _lifetime;
            worker = _worker;
            _lifetime = null;
            _worker = null;
        }
        lifetime?.Cancel();
        NotifyPendingWork();
        if (worker is not null)
        {
            try { await worker; }
            catch (OperationCanceledException) { }
        }
        lifetime?.Dispose();
        _wake.Dispose();
    }
}
