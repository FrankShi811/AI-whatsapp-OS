using System.Threading.Channels;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record LeadAnalysisAutomationEventArgs(string LeadId, AnalysisStatus Status, string Message);

public sealed class LeadIntelligenceAutomationService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly DeepSeekService _provider;
    private readonly WhatsAppSyncService _sync;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _worker;

    public event EventHandler<LeadAnalysisAutomationEventArgs>? AnalysisChanged;

    public LeadIntelligenceAutomationService(LocalRepository repository, DeepSeekService provider, WhatsAppSyncService sync)
    {
        _repository = repository;
        _provider = provider;
        _sync = sync;
        _sync.MessageSynchronized += Sync_MessageSynchronized;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _worker ??= Task.Run(() => ProcessQueueAsync(_lifetime.Token), CancellationToken.None);
        var pending = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(lead => lead.AnalysisStatus == AnalysisStatus.Queued)
            .Select(lead => lead.Id);
        foreach (var leadId in pending) _queue.Writer.TryWrite(leadId);
    }

    public async Task QueueLeadForReplyAsync(WhatsAppMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Direction != WhatsAppMessageDirection.Incoming || string.IsNullOrWhiteSpace(message.LeadId) || string.IsNullOrWhiteSpace(message.Body)) return;
        var lead = await _repository.GetLeadAsync(message.LeadId, cancellationToken);
        if (lead is null) return;

        var messages = await _repository.GetWhatsAppMessagesForLeadAsync(lead, cancellationToken: cancellationToken);
        lead.LatestReplySignals = WhatsAppReplySignalExtractor.Extract(messages);
        lead.AnalysisStatus = AnalysisStatus.Queued;
        lead.AnalysisTrigger = "whatsapp_reply";
        lead.AnalysisRequestedAt = message.Timestamp;
        lead.AnalysisError = _provider.HasApiKey() ? "" : "等待配置 AI API 与模型后自动分析。";
        if (!lead.AiScoreApplied)
        {
            lead.Score = 0;
            lead.Grade = "D";
            lead.ScoreBreakdown = [];
            lead.ScoreReasons = [];
            lead.ProfileSummary = "已捕捉 WhatsApp 回复，等待 AI 分析";
            lead.NextAction = "配置 AI API 后将自动分析该客户回复。";
        }
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.LogEventAsync("lead_analysis_queued", lead.Id, null, $"trigger=whatsapp_reply; message_id={message.ProviderMessageId}; signals={string.Join('|', lead.LatestReplySignals)}", cancellationToken);
        AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(lead.Id, AnalysisStatus.Queued, "WhatsApp 新回复已进入 AI 分析队列"));
        _queue.Writer.TryWrite(lead.Id);
    }

    public async Task NotifyProviderConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var pending = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(lead => lead.AnalysisStatus == AnalysisStatus.Queued)
            .Select(lead => lead.Id);
        foreach (var leadId in pending) _queue.Writer.TryWrite(leadId);
    }

    private void Sync_MessageSynchronized(object? sender, WhatsAppMessage message)
    {
        if (message.Direction != WhatsAppMessageDirection.Incoming) return;
        _ = QueueReplySafelyAsync(message);
    }

    private async Task QueueReplySafelyAsync(WhatsAppMessage message)
    {
        try { await QueueLeadForReplyAsync(message, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(message.LeadId, AnalysisStatus.RetryableFailed, error.Message));
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var leadId in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            if (!_provider.HasApiKey()) continue;
            var lead = await _repository.GetLeadAsync(leadId, cancellationToken);
            if (lead is null || lead.AnalysisStatus != AnalysisStatus.Queued) continue;
            var requestMarker = lead.AnalysisRequestedAt;
            AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(lead.Id, AnalysisStatus.Running, "AI 正在分析 WhatsApp 回复"));
            try
            {
                var analyzed = await _provider.AnalyzeLeadAsync(lead, cancellationToken);
                AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(analyzed.Id, AnalysisStatus.Succeeded, "AI 已更新商机画像与等级"));
                if (analyzed.AnalysisRequestedAt is not null && requestMarker is not null && analyzed.AnalysisRequestedAt > requestMarker)
                {
                    analyzed.AnalysisStatus = AnalysisStatus.Queued;
                    analyzed.AnalysisError = "分析期间收到新回复，已再次排队。";
                    await _repository.UpsertLeadAsync(analyzed, cancellationToken);
                    _queue.Writer.TryWrite(analyzed.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(lead.Id, AnalysisStatus.RetryableFailed, error.Message));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sync.MessageSynchronized -= Sync_MessageSynchronized;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
