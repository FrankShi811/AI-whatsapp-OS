using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record EmailProviderPreset(
    EmailProviderKind Provider,
    string Label,
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort,
    bool UsesAppPassword);

public sealed record EmailProviderGuide(
    EmailProviderKind Provider,
    string Title,
    string Badge,
    string Summary,
    IReadOnlyList<string> Steps,
    string EmailHint,
    string UserNameHint,
    string PasswordLabel,
    string PasswordHint,
    string SetupButtonLabel,
    string SetupUrl,
    string HelpButtonLabel,
    string HelpUrl,
    string CompatibilityNote);

public sealed record EmailSynchronizationState(
    string AccountId,
    string State,
    int Imported = 0,
    string Error = "");

public sealed class EmailService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly ConcurrentDictionary<string, BackgroundAccountMonitor> _backgroundMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _syncGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backgroundLock = new();
    private CancellationTokenSource? _backgroundLifetime;
    private Task? _backgroundSupervisor;

    public EmailService(LocalRepository repository) => _repository = repository;

    public event EventHandler<EmailSynchronizationState>? SynchronizationChanged;

    public static IReadOnlyList<EmailProviderPreset> ProviderPresets { get; } =
    [
        new(EmailProviderKind.Gmail, "Gmail", "imap.gmail.com", 993, "smtp.gmail.com", 465, true),
        new(EmailProviderKind.Microsoft365, "Outlook / Microsoft 365", "outlook.office365.com", 993, "smtp.office365.com", 587, true),
        new(EmailProviderKind.Yahoo, "Yahoo Mail", "imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, true),
        new(EmailProviderKind.ICloud, "iCloud Mail", "imap.mail.me.com", 993, "smtp.mail.me.com", 587, true),
        new(EmailProviderKind.Custom, "自定义 IMAP / SMTP", "", 993, "", 465, false)
    ];

    public static IReadOnlyList<EmailProviderGuide> ProviderGuides { get; } =
    [
        new(
            EmailProviderKind.Gmail,
            "Gmail 三步连接",
            "应用专用密码",
            "IMAP / SMTP 服务器已经自动填好；你只需准备 Gmail 地址和 Google 生成的 16 位应用专用密码。",
            [
                "填写完整 Gmail 地址；“登录用户名”会自动同步，无需另找账号名。",
                "打开 Google 账号“安全性”，先确认已开启两步验证。",
                "点击下方入口生成应用专用密码，粘贴到密码框，然后点击“测试连接并保存”。"
            ],
            "例如 frank@gmail.com 或公司 Google Workspace 邮箱",
            "使用完整邮箱地址；通常与上方邮箱地址相同",
            "Gmail 16 位应用专用密码",
            "不要填写 Gmail 日常登录密码。复制结果中即使带空格也可以，保存前会自动清理空格。",
            "生成 Gmail 应用专用密码",
            "https://myaccount.google.com/apppasswords",
            "查看 Gmail 官方连接说明",
            "https://support.google.com/mail/answer/7126229?hl=zh-Hans",
            "个人 Gmail 自 2025 年起默认开启 IMAP，不需要再寻找“开启 IMAP”开关。若看不到应用专用密码入口，请先确认两步验证已开启；仅安全密钥、组织策略或高级保护也可能隐藏该入口。"),
        new(
            EmailProviderKind.Microsoft365,
            "Outlook / Microsoft 365 连接说明",
            "OAuth2 限制",
            "服务器参数会自动填入，但 Microsoft 当前对 Outlook.com 明确要求 OAuth2 / Modern Auth，连接前请先确认账号策略。",
            [
                "填写完整 Outlook / Microsoft 365 邮箱地址，登录用户名保持为完整邮箱。",
                "在 Outlook 网页设置的“邮件 → 转发和 IMAP”中允许 IMAP（若该选项可见）。",
                "阅读下方兼容性说明；企业账号请向管理员确认是否允许 IMAP / SMTP 密码认证。"
            ],
            "例如 name@outlook.com 或 name@company.com",
            "使用完整 Microsoft 邮箱地址",
            "Microsoft 密码 / 应用密码",
            "只有账号或组织仍允许密码认证时才可使用；OAuth-only 账号反复更换普通密码也不会成功。",
            "打开 Outlook 邮件设置",
            "https://outlook.live.com/mail/0/options/mail/forwarding",
            "查看 Microsoft 官方服务器说明",
            "https://support.microsoft.com/en-US/Outlook/pop-imap-and-smtp-settings-for-outlook-com",
            "重要：Outlook.com 官方当前要求 OAuth2 / Modern Auth。本版尚未集成 Microsoft OAuth，因此 OAuth-only 账号暂时无法连接；企业管理员若提供可用的密码认证参数，可在下方高级服务器设置中调整后测试。"),
        new(
            EmailProviderKind.Yahoo,
            "Yahoo Mail 三步连接",
            "第三方应用密码",
            "Yahoo 要求未使用 Yahoo 登录页面的第三方邮件程序使用单独生成的应用密码。",
            [
                "填写完整 Yahoo 邮箱地址；登录用户名保持与邮箱地址一致。",
                "打开 Yahoo“账号安全”，在“外部连接”下选择“创建应用密码”。",
                "应用名称可填写 AI Sales OS；将生成的密码粘贴到下方并测试保存。"
            ],
            "例如 name@yahoo.com",
            "使用完整 Yahoo 邮箱地址",
            "Yahoo 第三方应用密码",
            "不要填写 Yahoo 日常登录密码；请粘贴“账号安全”页面生成的第三方应用密码。",
            "生成 Yahoo 应用密码",
            "https://login.yahoo.com/account/security",
            "查看 Yahoo 官方连接说明",
            "https://help.yahoo.com/kb/imap-internet-message-access-protocol-sln4075.html",
            "Yahoo 可能根据账号安全状态决定是否显示应用密码入口；若入口暂不可用，请先用常用浏览器正常登录 Yahoo 邮箱后再试。"),
        new(
            EmailProviderKind.ICloud,
            "iCloud Mail 三步连接",
            "App 专用密码",
            "iCloud Mail 需要已开启双重认证的 Apple 账户，并使用单独生成的 App 专用密码。",
            [
                "填写完整 @icloud.com、@me.com 或 @mac.com 邮箱地址。",
                "登录 account.apple.com，在“登录与安全性 → App 专用密码”中生成新密码。",
                "登录用户名先使用完整 iCloud 邮箱；粘贴专用密码后测试保存。"
            ],
            "例如 name@icloud.com",
            "建议使用完整 iCloud 邮箱地址",
            "iCloud App 专用密码",
            "不要填写 Apple 账户主密码；请粘贴 account.apple.com 生成的 App 专用密码。",
            "生成 iCloud App 专用密码",
            "https://account.apple.com/account/manage/section/security",
            "查看 Apple 官方服务器说明",
            "https://support.apple.com/102525",
            "Apple 官方说明 IMAP 用户名通常可用邮箱 @ 前的名称；为同时满足 SMTP，本页默认使用完整邮箱。若 IMAP 验证失败，可再尝试仅填写 @ 前的名称。"),
        new(
            EmailProviderKind.Custom,
            "自定义企业邮箱连接清单",
            "向服务商索取参数",
            "适用于腾讯企业邮、阿里企业邮、Zoho、域名邮箱或其他支持 IMAP / SMTP 密码认证的服务。",
            [
                "向邮箱服务商或企业管理员索取 IMAP 主机、端口和加密方式。",
                "同时索取 SMTP 主机、端口、加密方式，以及应使用完整邮箱还是独立用户名。",
                "确认密码框应填写登录密码、应用密码还是客户端授权码，再测试保存。"
            ],
            "填写要收发邮件的完整地址",
            "按服务商说明填写；多数情况为完整邮箱地址",
            "邮箱密码 / 应用密码 / 客户端授权码",
            "不同服务商要求不同。若网页邮箱提供“客户端授权码”，请填写授权码而不是网页登录密码。",
            "",
            "",
            "了解需要向服务商索取哪些参数",
            "https://support.microsoft.com/en-us/outlook/install-mobile/server-settings-you-ll-need-from-your-email-provider",
            "若服务商强制 OAuth2 且不提供应用密码或客户端授权码，本版无法连接；请联系管理员确认可用的 IMAP / SMTP 认证方式。")
    ];

    public static EmailProviderPreset Preset(EmailProviderKind provider) =>
        ProviderPresets.First(item => item.Provider == provider);

    public static EmailProviderGuide Guide(EmailProviderKind provider) =>
        ProviderGuides.First(item => item.Provider == provider);

    public async Task SaveAndTestAccountAsync(EmailAccount account, string password, CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        if (!string.IsNullOrWhiteSpace(password)) PasswordStore(account.Id).Save(NormalizeCredential(account.Provider, password));
        var storedPassword = PasswordStore(account.Id).Read();
        if (string.IsNullOrWhiteSpace(storedPassword)) throw new InvalidOperationException($"请输入{Guide(account.Provider).PasswordLabel}。");

        try
        {
            await TestConnectionsAsync(account, storedPassword, cancellationToken);
            account.Status = EmailConnectionStatus.Connected;
            account.LastError = "";
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            await _repository.LogEventAsync("email_account_connected", null, null, $"account_id={account.Id};provider={account.Provider};email={account.EmailAddress}", cancellationToken);
            EnsureBackgroundMonitor(account.Id);
        }
        catch (Exception error)
        {
            account.Status = EmailConnectionStatus.Error;
            account.LastError = FriendlyConnectionError(account.Provider, error);
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            throw new InvalidOperationException($"邮箱连接失败：{account.LastError}", error);
        }
    }

    public async Task DeleteAccountAsync(EmailAccount account, CancellationToken cancellationToken = default)
    {
        StopBackgroundMonitor(account.Id);
        PasswordStore(account.Id).Delete();
        await _repository.DeleteEmailAccountAsync(account.Id, cancellationToken);
        await _repository.LogEventAsync("email_account_deleted", null, null, $"account_id={account.Id};email={account.EmailAddress}", cancellationToken);
    }

    public async Task<int> SyncInboxAsync(string accountId, int maxMessages = 500, CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(accountId, cancellationToken);
        var password = RequirePassword(account);
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(account.ImapHost, account.ImapPort, SocketOptions(account.ImapPort, account.ImapUseSsl), cancellationToken);
            await client.AuthenticateAsync(account.UserName, password, cancellationToken);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var imported = await ImportInboxAsync(account, inbox, maxMessages, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            account.Status = EmailConnectionStatus.Connected;
            account.LastSyncAt = DateTimeOffset.Now;
            account.LastError = "";
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            await _repository.LogEventAsync("email_inbox_synced", null, null, $"account_id={account.Id};messages={imported}", cancellationToken);
            SynchronizationChanged?.Invoke(this, new EmailSynchronizationState(account.Id, "connected", imported));
            return imported;
        }
        catch (Exception error)
        {
            account.Status = EmailConnectionStatus.Error;
            account.LastError = Safe(error.Message);
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            SynchronizationChanged?.Invoke(this, new EmailSynchronizationState(account.Id, "error", Error: account.LastError));
            throw new InvalidOperationException($"邮件同步失败：{account.LastError}", error);
        }
    }

    public Task StartBackgroundSyncAsync(CancellationToken cancellationToken = default)
    {
        lock (_backgroundLock)
        {
            if (_backgroundSupervisor is { IsCompleted: false }) return Task.CompletedTask;
            _backgroundLifetime?.Dispose();
            _backgroundLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _backgroundSupervisor = RunBackgroundSupervisorAsync(_backgroundLifetime.Token);
        }
        return Task.CompletedTask;
    }

    private async Task RunBackgroundSupervisorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var accounts = await _repository.GetEmailAccountsAsync(cancellationToken);
                var eligibleIds = accounts
                    .Where(IsBackgroundEligible)
                    .Select(account => account.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var accountId in eligibleIds) EnsureBackgroundMonitor(accountId);
                foreach (var accountId in _backgroundMonitors.Keys.Where(id => !eligibleIds.Contains(id)).ToArray())
                    StopBackgroundMonitor(accountId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                SynchronizationChanged?.Invoke(this, new EmailSynchronizationState("", "supervisor_error", Error: Safe(error.Message)));
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

    private bool IsBackgroundEligible(EmailAccount account) =>
        account.Status != EmailConnectionStatus.NotConfigured
        && !string.IsNullOrWhiteSpace(account.ImapHost)
        && !string.IsNullOrWhiteSpace(account.UserName)
        && !string.IsNullOrWhiteSpace(PasswordStore(account.Id).Read());

    private void EnsureBackgroundMonitor(string accountId)
    {
        var parent = _backgroundLifetime;
        if (parent is null || parent.IsCancellationRequested) return;
        _backgroundMonitors.GetOrAdd(accountId, id =>
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
            return new BackgroundAccountMonitor(lifetime, MonitorAccountAsync(id, lifetime.Token));
        });
    }

    private void StopBackgroundMonitor(string accountId)
    {
        if (!_backgroundMonitors.TryRemove(accountId, out var monitor)) return;
        monitor.Lifetime.Cancel();
        _ = monitor.Worker.ContinueWith(
            _ => monitor.Lifetime.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task MonitorAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var account = await RequireAccountAsync(accountId, cancellationToken);
                if (!IsBackgroundEligible(account)) return;
                await RunConnectedAccountSessionAsync(account, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                var message = Safe(error.Message);
                try
                {
                    var account = await _repository.GetEmailAccountAsync(accountId, cancellationToken);
                    if (account is not null)
                    {
                        account.Status = EmailConnectionStatus.Error;
                        account.LastError = message;
                        await _repository.SaveEmailAccountAsync(account, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // The monitor will retry even if status persistence is temporarily unavailable.
                }
                SynchronizationChanged?.Invoke(this, new EmailSynchronizationState(accountId, "error", Error: message));
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 300));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunConnectedAccountSessionAsync(EmailAccount account, CancellationToken cancellationToken)
    {
        var password = RequirePassword(account);
        using var client = new ImapClient();
        await client.ConnectAsync(account.ImapHost, account.ImapPort, SocketOptions(account.ImapPort, account.ImapUseSsl), cancellationToken);
        await client.AuthenticateAsync(account.UserName, password, cancellationToken);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var imported = await ImportInboxAsync(account, inbox, 500, cancellationToken);
        account.Status = EmailConnectionStatus.Connected;
        account.LastSyncAt = DateTimeOffset.Now;
        account.LastError = "";
        await _repository.SaveEmailAccountAsync(account, cancellationToken);
        SynchronizationChanged?.Invoke(this, new EmailSynchronizationState(account.Id, "connected", imported));

        while (!cancellationToken.IsCancellationRequested && client.IsConnected)
        {
            if ((client.Capabilities & ImapCapabilities.Idle) != 0)
            {
                using var idleDone = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                void InboxChanged(object? _, EventArgs __) => idleDone.Cancel();
                inbox.CountChanged += InboxChanged;
                try
                {
                    await client.IdleAsync(idleDone.Token, cancellationToken);
                }
                catch (OperationCanceledException) when (idleDone.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // New mail or the IDLE renewal interval ended.
                }
                finally
                {
                    inbox.CountChanged -= InboxChanged;
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await client.NoOpAsync(cancellationToken);
            }

            var newMessages = await ImportInboxAsync(account, inbox, 150, cancellationToken);
            account.Status = EmailConnectionStatus.Connected;
            account.LastSyncAt = DateTimeOffset.Now;
            account.LastError = "";
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            if (newMessages > 0)
                SynchronizationChanged?.Invoke(this, new EmailSynchronizationState(account.Id, "messages", newMessages));
        }

        if (client.IsConnected)
            await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task<int> ImportInboxAsync(
        EmailAccount account,
        IMailFolder inbox,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        var syncGate = _syncGates.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await syncGate.WaitAsync(cancellationToken);
        try
        {
            var imported = 0;
            if (inbox.Count == 0) return imported;
            var start = Math.Max(0, inbox.Count - Math.Clamp(maxMessages, 1, 2_000));
            var summaries = await inbox.FetchAsync(
                start,
                -1,
                new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags),
                cancellationToken);
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var providerId = string.IsNullOrWhiteSpace(summary.Envelope?.MessageId)
                    ? $"imap:{summary.UniqueId.Id}"
                    : summary.Envelope.MessageId;
                if (await _repository.GetEmailMessageAsync($"{account.Id}:{providerId}", cancellationToken) is not null)
                    continue;
                var message = await inbox.GetMessageAsync(summary.UniqueId, cancellationToken);
                var incrementUnread = !summary.Flags.HasValue || !summary.Flags.Value.HasFlag(MessageFlags.Seen);
                if (await StoreIncomingAsync(account, summary.UniqueId.Id.ToString(), message, incrementUnread, cancellationToken))
                    imported++;
            }
            return imported;
        }
        finally
        {
            syncGate.Release();
        }
    }

    public async Task<EmailMessage> SendAsync(
        string accountId,
        string toAddress,
        string subject,
        string body,
        string? leadId = null,
        string? inReplyTo = null,
        CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(accountId, cancellationToken);
        var password = RequirePassword(account);
        if (!MailboxAddress.TryParse(toAddress, out var recipient)) throw new InvalidOperationException("收件邮箱格式无效。");
        if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("请填写邮件主题。");
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("请填写邮件正文。");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
        mime.To.Add(recipient);
        mime.Subject = subject.Trim();
        mime.Body = new TextPart("plain") { Text = body };
        if (!string.IsNullOrWhiteSpace(inReplyTo)) mime.InReplyTo = inReplyTo;
        mime.MessageId = MimeUtils.GenerateMessageId();

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(account.SmtpHost, account.SmtpPort, SocketOptions(account.SmtpPort, account.SmtpUseSsl), cancellationToken);
            await client.AuthenticateAsync(account.UserName, password, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            var stored = await StoreOutgoingAsync(account, recipient, mime, leadId, cancellationToken);
            await _repository.LogEventAsync("email_message_sent", stored.LeadId, null, $"account_id={account.Id};message_id={stored.ProviderMessageId};to={recipient.Address}", cancellationToken);
            return stored;
        }
        catch (Exception error)
        {
            try { await StoreFailedOutgoingAsync(account, recipient, mime, leadId, error.Message, cancellationToken); }
            catch { /* Preserve the SMTP error even when local failure-history persistence also fails. */ }
            await _repository.LogEventAsync("email_message_failed", leadId, null, $"account_id={account.Id};to={recipient.Address};error={Safe(error.Message)}", cancellationToken);
            throw new InvalidOperationException($"邮件发送失败：{Safe(error.Message)}", error);
        }
    }

    private async Task StoreFailedOutgoingAsync(
        EmailAccount account,
        MailboxAddress recipient,
        MimeMessage source,
        string? leadId,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var peer = NormalizeEmail(recipient.Address);
        var lead = string.IsNullOrWhiteSpace(leadId)
            ? await _repository.GetLeadByEmailAsync(peer, cancellationToken)
            : await _repository.GetLeadAsync(leadId, cancellationToken);
        var now = DateTimeOffset.Now;
        var conversation = await BuildConversationAsync(account, peer, recipient.Name, source.Subject, source.TextBody, now, lead, false, cancellationToken);
        var providerMessageId = string.IsNullOrWhiteSpace(source.MessageId) ? MimeUtils.GenerateMessageId() : source.MessageId;
        await _repository.UpsertEmailMessageAsync(new EmailMessage
        {
            Id = $"{account.Id}:{providerMessageId}", ProviderMessageId = providerMessageId,
            AccountId = account.Id, ConversationId = conversation.Id, LeadId = lead?.Id ?? "",
            Direction = EmailMessageDirection.Outgoing, Status = EmailMessageStatus.Failed,
            FromAddress = account.EmailAddress, FromName = account.DisplayName, ToAddresses = [peer],
            Subject = source.Subject ?? "", TextBody = source.TextBody ?? "", HtmlBody = source.HtmlBody ?? "",
            InReplyTo = source.InReplyTo ?? "", Timestamp = now, FailureReason = Safe(failureReason)
        }, cancellationToken);
    }

    private async Task<bool> StoreIncomingAsync(
        EmailAccount account,
        string uid,
        MimeMessage source,
        bool incrementUnread,
        CancellationToken cancellationToken)
    {
        var sender = source.From.Mailboxes.FirstOrDefault();
        if (sender is null || string.IsNullOrWhiteSpace(sender.Address)) return false;
        var peer = NormalizeEmail(sender.Address);
        var providerId = string.IsNullOrWhiteSpace(source.MessageId) ? $"imap:{uid}" : source.MessageId;
        var messageId = $"{account.Id}:{providerId}";
        if (await _repository.GetEmailMessageAsync(messageId, cancellationToken) is not null) return false;
        var lead = await _repository.GetLeadByEmailAsync(peer, cancellationToken);
        var timestamp = source.Date == default ? DateTimeOffset.Now : source.Date;
        var conversation = await BuildConversationAsync(account, peer, sender.Name, source.Subject, source.TextBody, timestamp, lead, incrementUnread, cancellationToken);
        var item = new EmailMessage
        {
            Id = messageId, ProviderMessageId = providerId, AccountId = account.Id,
            ConversationId = conversation.Id, LeadId = lead?.Id ?? "", Direction = EmailMessageDirection.Incoming,
            Status = EmailMessageStatus.Received, FromAddress = peer, FromName = sender.Name ?? "",
            ToAddresses = source.To.Mailboxes.Select(address => NormalizeEmail(address.Address)).Where(value => value.Length > 0).ToList(),
            CcAddresses = source.Cc.Mailboxes.Select(address => NormalizeEmail(address.Address)).Where(value => value.Length > 0).ToList(),
            Subject = source.Subject ?? "", TextBody = source.TextBody ?? "", HtmlBody = source.HtmlBody ?? "",
            InReplyTo = source.InReplyTo ?? "", Timestamp = timestamp
        };
        return await _repository.UpsertEmailMessageAsync(item, cancellationToken);
    }

    private async Task<EmailMessage> StoreOutgoingAsync(EmailAccount account, MailboxAddress recipient, MimeMessage source, string? leadId, CancellationToken cancellationToken)
    {
        var peer = NormalizeEmail(recipient.Address);
        var lead = string.IsNullOrWhiteSpace(leadId) ? await _repository.GetLeadByEmailAsync(peer, cancellationToken) : await _repository.GetLeadAsync(leadId, cancellationToken);
        var now = DateTimeOffset.Now;
        var conversation = await BuildConversationAsync(account, peer, recipient.Name, source.Subject, source.TextBody, now, lead, false, cancellationToken);
        var providerMessageId = string.IsNullOrWhiteSpace(source.MessageId) ? MimeUtils.GenerateMessageId() : source.MessageId;
        var item = new EmailMessage
        {
            Id = $"{account.Id}:{providerMessageId}", ProviderMessageId = providerMessageId,
            AccountId = account.Id, ConversationId = conversation.Id, LeadId = lead?.Id ?? "",
            Direction = EmailMessageDirection.Outgoing, Status = EmailMessageStatus.Sent,
            FromAddress = account.EmailAddress, FromName = account.DisplayName,
            ToAddresses = [peer], Subject = source.Subject ?? "", TextBody = source.TextBody ?? "",
            HtmlBody = source.HtmlBody ?? "", InReplyTo = source.InReplyTo ?? "", Timestamp = now
        };
        await _repository.UpsertEmailMessageAsync(item, cancellationToken);
        if (lead is not null)
        {
            lead.LastContactAt = now;
            lead.LatestMessage = item.TextBody;
            if (lead.Stage == LeadStage.New) lead.Stage = LeadStage.Contacted;
            await _repository.UpsertLeadAsync(lead, cancellationToken);
        }
        return item;
    }

    private async Task<EmailConversation> BuildConversationAsync(
        EmailAccount account,
        string peerEmail,
        string? peerName,
        string? subject,
        string? body,
        DateTimeOffset timestamp,
        Lead? lead,
        bool incrementUnread,
        CancellationToken cancellationToken)
    {
        var id = $"{account.Id}:{peerEmail}";
        var existing = (await _repository.GetEmailConversationsAsync(account.Id, cancellationToken))
            .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var conversation = existing ?? new EmailConversation { Id = id, AccountId = account.Id, PeerEmail = peerEmail };
        conversation.LeadId = lead?.Id ?? conversation.LeadId;
        conversation.PeerName = !string.IsNullOrWhiteSpace(lead?.DisplayName) ? lead.DisplayName : (peerName ?? conversation.PeerName);
        conversation.Subject = subject ?? conversation.Subject;
        conversation.LastMessage = Snippet(body);
        conversation.LastMessageAt = timestamp;
        await _repository.UpsertEmailConversationAsync(conversation, cancellationToken, incrementUnread);
        return conversation;
    }

    private static async Task TestConnectionsAsync(EmailAccount account, string password, CancellationToken cancellationToken)
    {
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(account.ImapHost, account.ImapPort, SocketOptions(account.ImapPort, account.ImapUseSsl), cancellationToken);
            await imap.AuthenticateAsync(account.UserName, password, cancellationToken);
            await imap.DisconnectAsync(true, cancellationToken);
        }
        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, SocketOptions(account.SmtpPort, account.SmtpUseSsl), cancellationToken);
            await smtp.AuthenticateAsync(account.UserName, password, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
    }

    private async Task<EmailAccount> RequireAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = await _repository.GetEmailAccountAsync(accountId, cancellationToken);
        return account ?? throw new InvalidOperationException("邮件账号不存在，请先连接邮箱。");
    }

    private static string RequirePassword(EmailAccount account) =>
        PasswordStore(account.Id).Read() ?? throw new InvalidOperationException("邮箱凭据不存在，请重新连接邮箱。");

    private static WindowsCredentialStore PasswordStore(string accountId) => new($"WAFlow/EmailPassword/{accountId}");

    private static SecureSocketOptions SocketOptions(int port, bool useSsl) =>
        !useSsl ? SecureSocketOptions.Auto : port == 465 || port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

    private static void ValidateAccount(EmailAccount account)
    {
        if (!MailboxAddress.TryParse(account.EmailAddress, out _)) throw new InvalidOperationException("邮箱地址格式无效。");
        if (string.IsNullOrWhiteSpace(account.ImapHost) || account.ImapPort is < 1 or > 65535) throw new InvalidOperationException("IMAP 服务器配置无效。");
        if (string.IsNullOrWhiteSpace(account.SmtpHost) || account.SmtpPort is < 1 or > 65535) throw new InvalidOperationException("SMTP 服务器配置无效。");
        if (string.IsNullOrWhiteSpace(account.UserName)) account.UserName = account.EmailAddress.Trim();
    }

    private static string NormalizeEmail(string? value) => (value ?? "").Trim().ToLowerInvariant();
    private static string NormalizeCredential(EmailProviderKind provider, string value)
    {
        var trimmed = value.Trim();
        return provider is EmailProviderKind.Gmail or EmailProviderKind.Yahoo or EmailProviderKind.ICloud
            ? string.Concat(trimmed.Where(character => !char.IsWhiteSpace(character)))
            : trimmed;
    }

    private static string FriendlyConnectionError(EmailProviderKind provider, Exception error)
    {
        var technical = Safe(error.Message);
        var guidance = provider switch
        {
            EmailProviderKind.Gmail => "Gmail 连接或登录未通过。请确认已开启两步验证，并填写 Google 生成的 16 位应用专用密码，而不是日常登录密码。",
            EmailProviderKind.Microsoft365 => "Microsoft 连接或登录未通过。Outlook.com 当前要求 OAuth2 / Modern Auth；若账号为 OAuth-only，本版暂时无法连接。企业账号请让管理员确认 IMAP / SMTP 密码认证策略。",
            EmailProviderKind.Yahoo => "Yahoo 连接或登录未通过。请在“账号安全 → 外部连接”创建第三方应用密码，不要填写日常登录密码。",
            EmailProviderKind.ICloud => "iCloud 连接或登录未通过。请确认 Apple 账户已开启双重认证，并填写 account.apple.com 生成的 App 专用密码。",
            _ => "服务器拒绝连接。请向邮箱服务商核对主机、端口、加密方式、用户名和客户端授权码。"
        };
        return $"{guidance} 技术信息：{technical}";
    }

    private static string Snippet(string? value)
    {
        var compact = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..180] + "…";
    }
    private static string Safe(string? value)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? supervisorLifetime;
        Task? supervisor;
        lock (_backgroundLock)
        {
            supervisorLifetime = _backgroundLifetime;
            supervisor = _backgroundSupervisor;
            _backgroundLifetime = null;
            _backgroundSupervisor = null;
        }
        supervisorLifetime?.Cancel();
        var monitors = _backgroundMonitors.Values.ToArray();
        foreach (var monitor in monitors) monitor.Lifetime.Cancel();
        _backgroundMonitors.Clear();
        if (supervisor is not null)
        {
            try { await supervisor; }
            catch (OperationCanceledException) { }
        }
        if (monitors.Length > 0)
        {
            try { await Task.WhenAll(monitors.Select(item => item.Worker)); }
            catch (OperationCanceledException) { }
        }
        foreach (var monitor in monitors) monitor.Lifetime.Dispose();
        supervisorLifetime?.Dispose();
        foreach (var gate in _syncGates.Values) gate.Dispose();
        _syncGates.Clear();
    }

    private sealed record BackgroundAccountMonitor(CancellationTokenSource Lifetime, Task Worker);
}
