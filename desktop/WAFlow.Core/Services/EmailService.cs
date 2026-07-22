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

public sealed class EmailService
{
    private readonly LocalRepository _repository;

    public EmailService(LocalRepository repository) => _repository = repository;

    public static IReadOnlyList<EmailProviderPreset> ProviderPresets { get; } =
    [
        new(EmailProviderKind.Gmail, "Gmail", "imap.gmail.com", 993, "smtp.gmail.com", 465, true),
        new(EmailProviderKind.Microsoft365, "Outlook / Microsoft 365", "outlook.office365.com", 993, "smtp.office365.com", 587, true),
        new(EmailProviderKind.Yahoo, "Yahoo Mail", "imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, true),
        new(EmailProviderKind.ICloud, "iCloud Mail", "imap.mail.me.com", 993, "smtp.mail.me.com", 587, true),
        new(EmailProviderKind.Custom, "自定义 IMAP / SMTP", "", 993, "", 465, false)
    ];

    public static EmailProviderPreset Preset(EmailProviderKind provider) =>
        ProviderPresets.First(item => item.Provider == provider);

    public async Task SaveAndTestAccountAsync(EmailAccount account, string password, CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        if (!string.IsNullOrWhiteSpace(password)) PasswordStore(account.Id).Save(password);
        var storedPassword = PasswordStore(account.Id).Read();
        if (string.IsNullOrWhiteSpace(storedPassword)) throw new InvalidOperationException("请输入邮箱密码或应用专用密码。");

        try
        {
            await TestConnectionsAsync(account, storedPassword, cancellationToken);
            account.Status = EmailConnectionStatus.Connected;
            account.LastError = "";
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            await _repository.LogEventAsync("email_account_connected", null, null, $"account_id={account.Id};provider={account.Provider};email={account.EmailAddress}", cancellationToken);
        }
        catch (Exception error)
        {
            account.Status = EmailConnectionStatus.Error;
            account.LastError = Safe(error.Message);
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            throw new InvalidOperationException($"邮箱连接失败：{account.LastError}", error);
        }
    }

    public async Task DeleteAccountAsync(EmailAccount account, CancellationToken cancellationToken = default)
    {
        PasswordStore(account.Id).Delete();
        await _repository.DeleteEmailAccountAsync(account.Id, cancellationToken);
        await _repository.LogEventAsync("email_account_deleted", null, null, $"account_id={account.Id};email={account.EmailAddress}", cancellationToken);
    }

    public async Task<int> SyncInboxAsync(string accountId, int maxMessages = 500, CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(accountId, cancellationToken);
        var password = RequirePassword(account);
        var imported = 0;
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(account.ImapHost, account.ImapPort, SocketOptions(account.ImapPort, account.ImapUseSsl), cancellationToken);
            await client.AuthenticateAsync(account.UserName, password, cancellationToken);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var start = Math.Max(0, inbox.Count - Math.Clamp(maxMessages, 1, 2_000));
            for (var index = start; index < inbox.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = await inbox.GetMessageAsync(index, cancellationToken);
                if (await StoreIncomingAsync(account, index.ToString(), message, cancellationToken)) imported++;
            }
            await client.DisconnectAsync(true, cancellationToken);
            account.Status = EmailConnectionStatus.Connected;
            account.LastSyncAt = DateTimeOffset.Now;
            account.LastError = "";
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            await _repository.LogEventAsync("email_inbox_synced", null, null, $"account_id={account.Id};messages={imported}", cancellationToken);
            return imported;
        }
        catch (Exception error)
        {
            account.Status = EmailConnectionStatus.Error;
            account.LastError = Safe(error.Message);
            await _repository.SaveEmailAccountAsync(account, cancellationToken);
            throw new InvalidOperationException($"邮件同步失败：{account.LastError}", error);
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

    private async Task<bool> StoreIncomingAsync(EmailAccount account, string uid, MimeMessage source, CancellationToken cancellationToken)
    {
        var sender = source.From.Mailboxes.FirstOrDefault();
        if (sender is null || string.IsNullOrWhiteSpace(sender.Address)) return false;
        var peer = NormalizeEmail(sender.Address);
        var providerId = string.IsNullOrWhiteSpace(source.MessageId) ? $"imap:{uid}" : source.MessageId;
        var messageId = $"{account.Id}:{providerId}";
        if (await _repository.GetEmailMessageAsync(messageId, cancellationToken) is not null) return false;
        var lead = await _repository.GetLeadByEmailAsync(peer, cancellationToken);
        var conversation = await BuildConversationAsync(account, peer, sender.Name, source.Subject, source.TextBody, source.Date, lead, true, cancellationToken);
        var item = new EmailMessage
        {
            Id = messageId, ProviderMessageId = providerId, AccountId = account.Id,
            ConversationId = conversation.Id, LeadId = lead?.Id ?? "", Direction = EmailMessageDirection.Incoming,
            Status = EmailMessageStatus.Received, FromAddress = peer, FromName = sender.Name ?? "",
            ToAddresses = source.To.Mailboxes.Select(address => NormalizeEmail(address.Address)).Where(value => value.Length > 0).ToList(),
            CcAddresses = source.Cc.Mailboxes.Select(address => NormalizeEmail(address.Address)).Where(value => value.Length > 0).ToList(),
            Subject = source.Subject ?? "", TextBody = source.TextBody ?? "", HtmlBody = source.HtmlBody ?? "",
            InReplyTo = source.InReplyTo ?? "", Timestamp = source.Date == default ? DateTimeOffset.Now : source.Date
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
        if (incrementUnread) conversation.UnreadCount++;
        await _repository.UpsertEmailConversationAsync(conversation, cancellationToken);
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
        !useSsl ? SecureSocketOptions.Auto : port == 465 || port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

    private static void ValidateAccount(EmailAccount account)
    {
        if (!MailboxAddress.TryParse(account.EmailAddress, out _)) throw new InvalidOperationException("邮箱地址格式无效。");
        if (string.IsNullOrWhiteSpace(account.ImapHost) || account.ImapPort is < 1 or > 65535) throw new InvalidOperationException("IMAP 服务器配置无效。");
        if (string.IsNullOrWhiteSpace(account.SmtpHost) || account.SmtpPort is < 1 or > 65535) throw new InvalidOperationException("SMTP 服务器配置无效。");
        if (string.IsNullOrWhiteSpace(account.UserName)) account.UserName = account.EmailAddress.Trim();
    }

    private static string NormalizeEmail(string? value) => (value ?? "").Trim().ToLowerInvariant();
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
}
