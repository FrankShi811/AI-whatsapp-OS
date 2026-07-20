using Microsoft.Data.Sqlite;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Infrastructure;

public sealed class LocalRepository
{
    private static readonly string[] DemoLeadIds = ["lead_elena", "lead_ahmed", "lead_maria", "lead_james", "lead_invalid"];
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public LocalRepository(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFlow", "waflow.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = true }.ToString();
    }

    private SqliteConnection Open() => new(_connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        var sql = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS settings (
              key TEXT PRIMARY KEY,
              value_json TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS leads (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              company TEXT NOT NULL,
              country TEXT NOT NULL,
              phone_e164 TEXT NOT NULL,
              phone_valid INTEGER NOT NULL,
              opted_out INTEGER NOT NULL DEFAULT 0,
              grade TEXT NOT NULL,
              stage TEXT NOT NULL,
              score INTEGER NOT NULL,
              owner TEXT NOT NULL,
              analysis_status TEXT NOT NULL,
              next_follow_up_at TEXT,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            DROP INDEX IF EXISTS ux_leads_phone;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_leads_phone ON leads(phone_e164) WHERE phone_valid=1 AND phone_e164 <> '';
            CREATE INDEX IF NOT EXISTS ix_leads_filters ON leads(grade, stage, owner, updated_at DESC);
            CREATE TABLE IF NOT EXISTS analysis_runs (
              id TEXT PRIMARY KEY,
              lead_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              status TEXT NOT NULL,
              model TEXT NOT NULL,
              error TEXT,
              result_json TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analysis_lead ON analysis_runs(lead_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS drafts (
              id TEXT PRIMARY KEY,
              lead_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              status TEXT NOT NULL,
              purpose TEXT NOT NULL,
              language TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_drafts_lead ON drafts(lead_id, updated_at DESC);
            CREATE TABLE IF NOT EXISTS draft_versions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              draft_id TEXT NOT NULL REFERENCES drafts(id) ON DELETE CASCADE,
              body TEXT NOT NULL,
              action TEXT NOT NULL,
              actor TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              event_type TEXT NOT NULL,
              lead_id TEXT,
              draft_id TEXT,
              detail TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS import_jobs (
              id TEXT PRIMARY KEY,
              file_name TEXT NOT NULL,
              status TEXT NOT NULL,
              total_rows INTEGER NOT NULL,
              created INTEGER NOT NULL,
              updated INTEGER NOT NULL,
              invalid_phones INTEGER NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS whatsapp_conversations (
              id TEXT PRIMARY KEY,
              account_id TEXT NOT NULL,
              phone TEXT NOT NULL,
              lead_id TEXT,
              last_message_at TEXT NOT NULL,
              unread_count INTEGER NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, phone)
            );
            CREATE INDEX IF NOT EXISTS ix_whatsapp_conversations_recent ON whatsapp_conversations(account_id, last_message_at DESC);
            CREATE INDEX IF NOT EXISTS ix_whatsapp_conversations_lead ON whatsapp_conversations(lead_id, last_message_at DESC);
            CREATE TABLE IF NOT EXISTS whatsapp_contacts (
              id TEXT PRIMARY KEY,
              account_id TEXT NOT NULL,
              jid TEXT NOT NULL,
              phone TEXT NOT NULL,
              display_name TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, jid)
            );
            CREATE INDEX IF NOT EXISTS ix_whatsapp_contacts_search ON whatsapp_contacts(account_id, display_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_whatsapp_contacts_phone ON whatsapp_contacts(account_id, phone) WHERE phone <> '';
            CREATE TABLE IF NOT EXISTS whatsapp_messages (
              id TEXT PRIMARY KEY,
              provider_message_id TEXT NOT NULL,
              account_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL REFERENCES whatsapp_conversations(id) ON DELETE CASCADE,
              lead_id TEXT,
              phone TEXT NOT NULL,
              direction TEXT NOT NULL,
              status TEXT NOT NULL,
              timestamp TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, provider_message_id)
            );
            CREATE INDEX IF NOT EXISTS ix_whatsapp_messages_timeline ON whatsapp_messages(conversation_id, timestamp DESC);
            CREATE INDEX IF NOT EXISTS ix_whatsapp_messages_lead ON whatsapp_messages(lead_id, timestamp DESC);
            CREATE TABLE IF NOT EXISTS whatsapp_campaigns (
              id TEXT PRIMARY KEY,
              account_id TEXT NOT NULL,
              status TEXT NOT NULL,
              starts_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_whatsapp_campaigns_queue ON whatsapp_campaigns(account_id, status, starts_at);
            CREATE TABLE IF NOT EXISTS whatsapp_campaign_recipients (
              id TEXT PRIMARY KEY,
              campaign_id TEXT NOT NULL REFERENCES whatsapp_campaigns(id) ON DELETE CASCADE,
              lead_id TEXT NOT NULL REFERENCES leads(id) ON DELETE RESTRICT,
              account_id TEXT NOT NULL,
              phone TEXT NOT NULL,
              status TEXT NOT NULL,
              scheduled_at TEXT NOT NULL,
              next_attempt_at TEXT NOT NULL,
              sent_at TEXT,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(campaign_id, lead_id)
            );
            CREATE INDEX IF NOT EXISTS ix_whatsapp_campaign_recipients_due ON whatsapp_campaign_recipients(account_id, status, next_attempt_at);
            CREATE INDEX IF NOT EXISTS ix_whatsapp_campaign_recipients_campaign ON whatsapp_campaign_recipients(campaign_id, scheduled_at);
            CREATE INDEX IF NOT EXISTS ix_whatsapp_campaign_recipients_sent ON whatsapp_campaign_recipients(account_id, sent_at) WHERE status='Sent';
            """;
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SeedIfEmptyAsync(db, cancellationToken);
        await using var cleanup = await db.BeginTransactionAsync(cancellationToken);
        await RemoveDemoLeadsIfRealDataExistsInternalAsync(db, cleanup as SqliteTransaction, cancellationToken);
        await cleanup.CommitAsync(cancellationToken);
    }

    private static async Task SeedIfEmptyAsync(SqliteConnection db, CancellationToken cancellationToken)
    {
        await using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM leads";
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) > 0) return;
        var scorer = new Services.LeadScoringService();
        var samples = new[]
        {
            new Lead { Id="lead_elena", Name="Elena Rossi", Company="Nordline Living", Country="Italy", PhoneE164="+393491234567", PhoneValid=true, Email="elena@nordline.example", PreferredLanguage="it", ProductInterest="Oak dining chair · Model DC-18", EstimatedOrderValue=24800, Currency="EUR", CompanyScale=.8, PurchasePower=.9, ExplicitDemand=true, RegisteredOrConsulted=true, Source="Global buyer discovery", Tags=["Distributor","Furniture","EU"], Owner="Olivia Chen", Stage=LeadStage.Negotiation, LatestMessage="Could you confirm the lead time for 300 units?", LastContactAt=DateTimeOffset.Now.AddHours(-2), NextFollowUpAt=DateTimeOffset.Now.AddHours(3) },
            new Lead { Id="lead_ahmed", Name="Ahmed Mansour", Company="Nile Trade Co.", Country="Egypt", PhoneE164="+201001234567", PhoneValid=true, Email="ahmed@niletrade.example", PreferredLanguage="ar", ProductInterest="Solar garden lights", EstimatedOrderValue=18200, Currency="USD", CompanyScale=.7, PurchasePower=.8, ExplicitDemand=true, RegisteredOrConsulted=true, Source="Import directory", Tags=["Importer","Lighting"], Owner="Olivia Chen", Stage=LeadStage.Interested, LatestMessage="Please send your FOB price list.", LastContactAt=DateTimeOffset.Now.AddHours(-3), NextFollowUpAt=DateTimeOffset.Now.AddHours(6) },
            new Lead { Id="lead_maria", Name="María Torres", Company="Casa Nova Retail", Country="Mexico", PhoneE164="+525512345678", PhoneValid=true, Email="maria@casanova.example", PreferredLanguage="es", ProductInterest="Kitchen storage set", EstimatedOrderValue=9600, Currency="USD", CompanyScale=.55, PurchasePower=.6, ExplicitDemand=false, RegisteredOrConsulted=true, Source="Trade show QR", Tags=["Retailer","LATAM"], Owner="Olivia Chen", Stage=LeadStage.Interested, LatestMessage="I will review the catalogue today.", LastContactAt=DateTimeOffset.Now.AddHours(-5), NextFollowUpAt=DateTimeOffset.Now.AddDays(1) },
            new Lead { Id="lead_james", Name="James Cole", Company="Brighton Supply", Country="United Kingdom", PhoneE164="+447700900123", PhoneValid=true, Email="james@brighton.example", PreferredLanguage="en", ProductInterest="Reusable water bottles", EstimatedOrderValue=7400, Currency="GBP", CompanyScale=.5, PurchasePower=.5, ExplicitDemand=true, RegisteredOrConsulted=true, Source="Website inquiry", Tags=["Wholesaler","UK"], Stage=LeadStage.New, LatestMessage="Do you support private labels?", LastContactAt=DateTimeOffset.Now.AddHours(-7) },
            new Lead { Id="lead_invalid", Name="Test Invalid", Company="Example Trading", Country="Unknown", PhoneE164="12345", PhoneValid=false, PreferredLanguage="en", ProductInterest="Sample catalogue", EstimatedOrderValue=2000, CompanyScale=.3, PurchasePower=.3, Source="Test import", Tags=["Risk"], Stage=LeadStage.New }
        };
        foreach (var lead in samples)
        {
            scorer.Score(lead);
            if (lead.Id == "lead_elena") { lead.Score = 88; lead.Grade = "A"; lead.ProfileSummary = "欧洲家具分销商，询问 300 件交期，采购意图明确。"; lead.CustomerSegment = "高价值经销商"; lead.NextAction = "确认交期并提供阶梯报价。"; lead.AnalysisStatus = AnalysisStatus.Succeeded; }
            await UpsertLeadInternalAsync(db, lead, cancellationToken);
        }
    }

    public async Task<SalesProfile?> GetSalesProfileAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<SalesProfile>("sales_profile", cancellationToken);

    public Task SaveSalesProfileAsync(SalesProfile profile, CancellationToken cancellationToken = default) =>
        SaveSettingAsync("sales_profile", profile, cancellationToken);

    public async Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<AppSettings>("app_settings", cancellationToken) ?? new AppSettings();

    public Task SaveAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        SaveSettingAsync("app_settings", settings, cancellationToken);

    public async Task<OnboardingState> GetOnboardingStateAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<OnboardingState>("onboarding_state", cancellationToken) ?? new OnboardingState();

    public Task SaveOnboardingStateAsync(OnboardingState state, CancellationToken cancellationToken = default) =>
        SaveSettingAsync("onboarding_state", state, cancellationToken);

    public async Task<List<CampaignMessageTemplate>> GetCampaignMessageTemplatesAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<List<CampaignMessageTemplate>>("campaign_message_templates", cancellationToken) ?? [];

    public async Task SaveCampaignMessageTemplateAsync(CampaignMessageTemplate template, CancellationToken cancellationToken = default)
    {
        var templates = await GetCampaignMessageTemplatesAsync(cancellationToken);
        var existing = templates.FindIndex(item => item.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
        template.UpdatedAt = DateTimeOffset.Now;
        if (existing >= 0) templates[existing] = template; else templates.Add(template);
        await SaveSettingAsync("campaign_message_templates", templates.OrderByDescending(item => item.UpdatedAt).ToList(), cancellationToken);
    }

    public async Task DeleteCampaignMessageTemplateAsync(string id, CancellationToken cancellationToken = default)
    {
        var templates = await GetCampaignMessageTemplatesAsync(cancellationToken);
        templates.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        await SaveSettingAsync("campaign_message_templates", templates, cancellationToken);
    }

    public async Task<WhatsAppIpState?> GetWhatsAppIpStateAsync(string accountId, CancellationToken cancellationToken = default) =>
        await GetSettingAsync<WhatsAppIpState>($"whatsapp_ip_state:{accountId}", cancellationToken);

    public Task SaveWhatsAppIpStateAsync(WhatsAppIpState state, CancellationToken cancellationToken = default) =>
        SaveSettingAsync($"whatsapp_ip_state:{state.AccountId}", state, cancellationToken);

    public async Task<List<WhatsAppAccount>> GetWhatsAppAccountsAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<List<WhatsAppAccount>>("whatsapp_accounts", cancellationToken) ?? [new WhatsAppAccount()];

    public Task SaveWhatsAppAccountsAsync(IReadOnlyList<WhatsAppAccount> accounts, CancellationToken cancellationToken = default)
    {
        if (accounts.Count == 0) throw new InvalidOperationException("至少需要保留一个 WhatsApp 账号。");
        if (accounts.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Count) throw new InvalidOperationException("WhatsApp 账号 ID 不能重复。");
        if (accounts.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Id.Length is < 1 or > 64 || x.Id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-')))
            throw new InvalidOperationException("WhatsApp 账号名称或 ID 无效。");
        return SaveSettingAsync("whatsapp_accounts", accounts.ToList(), cancellationToken);
    }

    private async Task<T?> GetSettingAsync<T>(string key, CancellationToken cancellationToken)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT value_json FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return Json.Deserialize<T>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    private async Task SaveSettingAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value_json,updated_at) VALUES($key,$json,$at) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$json", Json.Serialize(value));
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<Lead>> GetLeadsAsync(string? search = null, string? grade = null, LeadStage? stage = null, CancellationToken cancellationToken = default)
    {
        var items = new List<Lead>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) { filters.Add("(name LIKE $search OR company LIKE $search OR phone_e164 LIKE $search OR data_json LIKE $search)"); command.Parameters.AddWithValue("$search", $"%{search.Trim()}%"); }
        if (!string.IsNullOrWhiteSpace(grade) && grade != "全部") { filters.Add("grade=$grade"); command.Parameters.AddWithValue("$grade", grade); }
        if (stage is not null) { filters.Add("stage=$stage"); command.Parameters.AddWithValue("$stage", stage.Value.ToString()); }
        command.CommandText = $"SELECT data_json FROM leads {(filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "")} ORDER BY score DESC, updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<Lead>(reader.GetString(0)) is { } lead) items.Add(lead);
        return items;
    }

    public async Task<Lead?> GetLeadAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand(); command.CommandText = "SELECT data_json FROM leads WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        return Json.Deserialize<Lead>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertLeadAsync(Lead lead, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await UpsertLeadInternalAsync(db, lead, cancellationToken);
    }

    public async Task<bool> DeleteLeadAsync(string leadId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leadId)) return false;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var deleted = await DeleteLeadInternalAsync(db, transaction as SqliteTransaction, leadId, "customer_deleted", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<int> RemoveDemoLeadsIfRealDataExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var removed = await RemoveDemoLeadsIfRealDataExistsInternalAsync(db, transaction as SqliteTransaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static async Task<int> RemoveDemoLeadsIfRealDataExistsInternalAsync(SqliteConnection db, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var count = db.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"SELECT COUNT(*) FROM leads WHERE id NOT IN ({string.Join(',', DemoLeadIds.Select((_, index) => $"$demo{index}"))})";
        for (var index = 0; index < DemoLeadIds.Length; index++) count.Parameters.AddWithValue($"$demo{index}", DemoLeadIds[index]);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) == 0) return 0;

        var removed = 0;
        foreach (var leadId in DemoLeadIds)
            if (await DeleteLeadInternalAsync(db, transaction, leadId, "demo_customer_removed", cancellationToken)) removed++;
        return removed;
    }

    private static async Task<bool> DeleteLeadInternalAsync(SqliteConnection db, SqliteTransaction? transaction, string leadId, string eventType, CancellationToken cancellationToken)
    {
        var conversations = new List<WhatsAppConversation>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_conversations WHERE lead_id=$lead";
            select.Parameters.AddWithValue("$lead", leadId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppConversation>(reader.GetString(0)) is { } item) conversations.Add(item);
        }
        foreach (var item in conversations)
        {
            item.LeadId = "";
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE whatsapp_conversations SET lead_id=NULL,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", item.Id); update.Parameters.AddWithValue("$json", Json.Serialize(item));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var messages = new List<WhatsAppMessage>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_messages WHERE lead_id=$lead";
            select.Parameters.AddWithValue("$lead", leadId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppMessage>(reader.GetString(0)) is { } item) messages.Add(item);
        }
        foreach (var item in messages)
        {
            item.LeadId = "";
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE whatsapp_messages SET lead_id=NULL,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", item.Id); update.Parameters.AddWithValue("$json", Json.Serialize(item));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var queued = db.CreateCommand())
        {
            queued.Transaction = transaction;
            queued.CommandText = "DELETE FROM whatsapp_campaign_recipients WHERE lead_id=$lead";
            queued.Parameters.AddWithValue("$lead", leadId);
            await queued.ExecuteNonQueryAsync(cancellationToken);
        }
        int affected;
        await using (var delete = db.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM leads WHERE id=$lead";
            delete.Parameters.AddWithValue("$lead", leadId);
            affected = await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected == 0) return false;
        await using var audit = db.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = "INSERT INTO audit_events(event_type,lead_id,draft_id,detail,created_at) VALUES($type,$lead,NULL,$detail,$at)";
        audit.Parameters.AddWithValue("$type", eventType); audit.Parameters.AddWithValue("$lead", leadId);
        audit.Parameters.AddWithValue("$detail", "lead deleted; WhatsApp history retained and unlinked"); audit.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await audit.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task UpsertLeadsAsync(IReadOnlyList<Lead> leads, int batchSize = 500, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (leads.Count == 0) { progress?.Report(0); return; }
        batchSize = Math.Clamp(batchSize, 50, 2_000);
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        for (var offset = 0; offset < leads.Count; offset += batchSize)
        {
            await using var transaction = await db.BeginTransactionAsync(cancellationToken);
            var end = Math.Min(offset + batchSize, leads.Count);
            for (var index = offset; index < end; index++)
                await UpsertLeadInternalAsync(db, leads[index], cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            progress?.Report(end);
        }
    }

    private static async Task UpsertLeadInternalAsync(SqliteConnection db, Lead lead, CancellationToken cancellationToken, System.Data.Common.DbTransaction? transaction = null)
    {
        lead.UpdatedAt = DateTimeOffset.Now;
        await using var command = db.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = """
            INSERT INTO leads(id,name,company,country,phone_e164,phone_valid,opted_out,grade,stage,score,owner,analysis_status,next_follow_up_at,updated_at,data_json)
            VALUES($id,$name,$company,$country,$phone,$valid,$opted,$grade,$stage,$score,$owner,$analysis,$follow,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,company=excluded.company,country=excluded.country,phone_e164=excluded.phone_e164,
            phone_valid=excluded.phone_valid,opted_out=excluded.opted_out,grade=excluded.grade,stage=excluded.stage,score=excluded.score,owner=excluded.owner,
            analysis_status=excluded.analysis_status,next_follow_up_at=excluded.next_follow_up_at,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", lead.Id); command.Parameters.AddWithValue("$name", lead.Name); command.Parameters.AddWithValue("$company", lead.Company);
        command.Parameters.AddWithValue("$country", lead.Country); command.Parameters.AddWithValue("$phone", lead.PhoneE164); command.Parameters.AddWithValue("$valid", lead.PhoneValid ? 1 : 0);
        command.Parameters.AddWithValue("$opted", lead.OptedOut ? 1 : 0); command.Parameters.AddWithValue("$grade", lead.Grade); command.Parameters.AddWithValue("$stage", lead.Stage.ToString());
        command.Parameters.AddWithValue("$score", lead.Score); command.Parameters.AddWithValue("$owner", lead.Owner); command.Parameters.AddWithValue("$analysis", lead.AnalysisStatus.ToString());
        command.Parameters.AddWithValue("$follow", (object?)lead.NextFollowUpAt?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("$updated", lead.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(lead));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Lead?> GetLeadByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        var digits = Services.PhoneIdentity.Digits(phone);
        if (digits.Length == 0) return null;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM leads WHERE phone_e164=$phone LIMIT 1";
        command.Parameters.AddWithValue("$phone", "+" + digits);
        var exact = Json.Deserialize<Lead>(await command.ExecuteScalarAsync(cancellationToken) as string);
        if (exact is not null) return exact;
        return Services.PhoneIdentity.FindUniqueLead(await GetLeadsAsync(cancellationToken: cancellationToken), digits);
    }

    public async Task<int> SynchronizeLeadConnectionsFromInboxAsync(IReadOnlyList<Lead> leads, CancellationToken cancellationToken = default)
    {
        var byPhone = leads
            .SelectMany(lead => Services.PhoneIdentity.LeadPhoneCandidates(lead).Select(phone => (Phone: phone, Lead: lead)))
            .Where(item => item.Phone.Length > 0)
            .GroupBy(item => item.Phone, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Lead, StringComparer.OrdinalIgnoreCase);
        if (byPhone.Count == 0) return 0;

        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var latestMessages = new Dictionary<string, WhatsAppMessage>(StringComparer.OrdinalIgnoreCase);
        var latestConversations = new Dictionary<string, WhatsAppConversation>(StringComparer.OrdinalIgnoreCase);

        var conversations = new List<WhatsAppConversation>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction as SqliteTransaction;
            select.CommandText = "SELECT data_json FROM whatsapp_conversations";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppConversation>(reader.GetString(0)) is { } item) conversations.Add(item);
        }
        var messages = new List<WhatsAppMessage>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction as SqliteTransaction;
            select.CommandText = "SELECT data_json FROM whatsapp_messages";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppMessage>(reader.GetString(0)) is { } item) messages.Add(item);
        }
        var resolvedPhones = conversations.Select(item => item.Phone).Concat(messages.Select(item => item.Phone))
            .Select(Services.PhoneIdentity.Digits).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)
            .ToDictionary(value => value, value => byPhone.TryGetValue(value, out var exactLead) ? exactLead : Services.PhoneIdentity.FindUniqueLead(leads, value), StringComparer.Ordinal);
        foreach (var conversation in conversations)
        {
            var digits = Services.PhoneIdentity.Digits(conversation.Phone);
            if (!resolvedPhones.TryGetValue(digits, out var lead) || lead is null) continue;
            conversation.LeadId = lead.Id;
            if (!string.IsNullOrWhiteSpace(lead.DisplayName)) conversation.DisplayName = lead.DisplayName;
            linked.Add(lead.Id);
            if (!latestConversations.TryGetValue(lead.Id, out var previous) || conversation.LastMessageAt > previous.LastMessageAt) latestConversations[lead.Id] = conversation;
            await using var update = db.CreateCommand();
            update.Transaction = transaction as SqliteTransaction;
            update.CommandText = "UPDATE whatsapp_conversations SET lead_id=$lead,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$lead", lead.Id); update.Parameters.AddWithValue("$json", Json.Serialize(conversation)); update.Parameters.AddWithValue("$id", conversation.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var message in messages)
        {
            var digits = Services.PhoneIdentity.Digits(message.Phone);
            if (!resolvedPhones.TryGetValue(digits, out var lead) || lead is null) continue;
            message.LeadId = lead.Id;
            linked.Add(lead.Id);
            if (!latestMessages.TryGetValue(lead.Id, out var previous) || message.Timestamp > previous.Timestamp) latestMessages[lead.Id] = message;
            await using var update = db.CreateCommand();
            update.Transaction = transaction as SqliteTransaction;
            update.CommandText = "UPDATE whatsapp_messages SET lead_id=$lead,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$lead", lead.Id); update.Parameters.AddWithValue("$json", Json.Serialize(message)); update.Parameters.AddWithValue("$id", message.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var leadId in linked)
        {
            var lead = byPhone.Values.First(item => item.Id.Equals(leadId, StringComparison.OrdinalIgnoreCase));
            if (latestMessages.TryGetValue(leadId, out var message)) Services.LeadConnectionStatus.ApplyFromMessage(lead, message);
            else if (latestConversations.TryGetValue(leadId, out var conversation))
            {
                lead.LastContactAt = conversation.LastMessageAt;
                if (!string.IsNullOrWhiteSpace(conversation.LastMessage)) lead.LatestMessage = conversation.LastMessage;
                Services.LeadConnectionStatus.Apply(lead, "\u5df2\u5efa\u8054", conversation.LastMessageAt);
            }
            await UpsertLeadInternalAsync(db, lead, cancellationToken, transaction);
        }
        await transaction.CommitAsync(cancellationToken);
        return linked.Count;
    }

    public async Task<List<WhatsAppConversation>> GetWhatsAppConversationsAsync(string accountId = "primary", CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppConversation>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_conversations WHERE account_id=$account ORDER BY last_message_at DESC";
        command.Parameters.AddWithValue("$account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<WhatsAppConversation>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<WhatsAppConversation?> GetWhatsAppConversationAsync(string accountId, string phone, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_conversations WHERE account_id=$account AND phone=$phone LIMIT 1";
        command.Parameters.AddWithValue("$account", accountId); command.Parameters.AddWithValue("$phone", new string(phone.Where(char.IsDigit).ToArray()));
        return Json.Deserialize<WhatsAppConversation>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<WhatsAppContact>> GetWhatsAppContactsAsync(string accountId = "primary", CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppContact>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_contacts WHERE account_id=$account ORDER BY display_name COLLATE NOCASE, updated_at DESC";
        command.Parameters.AddWithValue("$account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<WhatsAppContact>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertWhatsAppContactAsync(WhatsAppContact contact, CancellationToken cancellationToken = default)
    {
        contact.AccountId = string.IsNullOrWhiteSpace(contact.AccountId) ? "primary" : contact.AccountId;
        contact.Jid = contact.Jid.Trim();
        if (string.IsNullOrWhiteSpace(contact.Id)) contact.Id = $"{contact.AccountId}:{contact.Jid}";
        contact.Phone = new string(contact.Phone.Where(char.IsDigit).ToArray());
        contact.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO whatsapp_contacts(id,account_id,jid,phone,display_name,updated_at,data_json)
            VALUES($id,$account,$jid,$phone,$name,$updated,$json)
            ON CONFLICT DO UPDATE SET phone=excluded.phone,display_name=excluded.display_name,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", contact.Id); command.Parameters.AddWithValue("$account", contact.AccountId);
        command.Parameters.AddWithValue("$jid", contact.Jid); command.Parameters.AddWithValue("$phone", contact.Phone);
        command.Parameters.AddWithValue("$name", contact.DisplayName); command.Parameters.AddWithValue("$updated", contact.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(contact));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertWhatsAppConversationAsync(WhatsAppConversation conversation, CancellationToken cancellationToken = default)
    {
        conversation.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO whatsapp_conversations(id,account_id,phone,lead_id,last_message_at,unread_count,updated_at,data_json)
            VALUES($id,$account,$phone,$lead,$last,$unread,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET lead_id=excluded.lead_id,last_message_at=excluded.last_message_at,unread_count=excluded.unread_count,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", conversation.Id); command.Parameters.AddWithValue("$account", conversation.AccountId); command.Parameters.AddWithValue("$phone", conversation.Phone);
        command.Parameters.AddWithValue("$lead", string.IsNullOrWhiteSpace(conversation.LeadId) ? DBNull.Value : conversation.LeadId); command.Parameters.AddWithValue("$last", conversation.LastMessageAt.ToString("O"));
        command.Parameters.AddWithValue("$unread", conversation.UnreadCount); command.Parameters.AddWithValue("$updated", conversation.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$json", Json.Serialize(conversation));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpsertWhatsAppMessageAsync(WhatsAppMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO whatsapp_messages(id,provider_message_id,account_id,conversation_id,lead_id,phone,direction,status,timestamp,data_json)
            VALUES($id,$provider,$account,$conversation,$lead,$phone,$direction,$status,$timestamp,$json)
            """;
        command.Parameters.AddWithValue("$id", message.Id); command.Parameters.AddWithValue("$provider", message.ProviderMessageId); command.Parameters.AddWithValue("$account", message.AccountId);
        command.Parameters.AddWithValue("$conversation", message.ConversationId); command.Parameters.AddWithValue("$lead", string.IsNullOrWhiteSpace(message.LeadId) ? DBNull.Value : message.LeadId); command.Parameters.AddWithValue("$phone", message.Phone);
        command.Parameters.AddWithValue("$direction", message.Direction.ToString()); command.Parameters.AddWithValue("$status", message.Status.ToString()); command.Parameters.AddWithValue("$timestamp", message.Timestamp.ToString("O")); command.Parameters.AddWithValue("$json", Json.Serialize(message));
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (!inserted)
        {
            await using var select = db.CreateCommand();
            select.CommandText = "SELECT data_json FROM whatsapp_messages WHERE id=$id";
            select.Parameters.AddWithValue("$id", message.Id);
            if (Json.Deserialize<WhatsAppMessage>(await select.ExecuteScalarAsync(cancellationToken) as string) is { } existing && !CanAdvanceStatus(existing.Status, message.Status))
                message.Status = existing.Status;
            await using var update = db.CreateCommand();
            update.CommandText = "UPDATE whatsapp_messages SET status=$status,lead_id=$lead,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", message.Id); update.Parameters.AddWithValue("$status", message.Status.ToString()); update.Parameters.AddWithValue("$lead", string.IsNullOrWhiteSpace(message.LeadId) ? DBNull.Value : message.LeadId); update.Parameters.AddWithValue("$json", Json.Serialize(message));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return inserted;
    }

    public async Task<List<WhatsAppMessage>> GetWhatsAppMessagesAsync(string conversationId, int limit = 500, CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppMessage>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_messages WHERE conversation_id=$conversation ORDER BY timestamp DESC LIMIT $limit";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<WhatsAppMessage>(reader.GetString(0)) is { } item) items.Add(item);
        items.Reverse();
        return items;
    }

    public async Task<WhatsAppMessage?> UpdateWhatsAppMessageStatusAsync(
        string accountId,
        string providerMessageId,
        WhatsAppMessageStatus status,
        DateTimeOffset? statusAt = null,
        DateTimeOffset? deliveredAt = null,
        DateTimeOffset? readAt = null,
        string failureReason = "",
        CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var select = db.CreateCommand();
        select.CommandText = "SELECT id,data_json FROM whatsapp_messages WHERE account_id=$account AND provider_message_id=$provider LIMIT 1";
        select.Parameters.AddWithValue("$account", accountId); select.Parameters.AddWithValue("$provider", providerMessageId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var id = reader.GetString(0); var message = Json.Deserialize<WhatsAppMessage>(reader.GetString(1));
        await reader.DisposeAsync();
        if (message is null) return null;
        var canAdvance = CanAdvanceStatus(message.Status, status);
        if (canAdvance) message.Status = status;
        if (statusAt is not null && (message.StatusUpdatedAt is null || statusAt > message.StatusUpdatedAt)) message.StatusUpdatedAt = statusAt;
        if (deliveredAt is not null && (message.DeliveredAt is null || deliveredAt < message.DeliveredAt)) message.DeliveredAt = deliveredAt;
        if (readAt is not null && (message.ReadAt is null || readAt < message.ReadAt)) message.ReadAt = readAt;
        if (message.Status == WhatsAppMessageStatus.Read && message.DeliveredAt is null) message.DeliveredAt = message.ReadAt ?? statusAt;
        if (status == WhatsAppMessageStatus.Failed && canAdvance)
        {
            message.FailedAt = statusAt ?? DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(failureReason)) message.FailureReason = failureReason;
        }
        await using var update = db.CreateCommand();
        update.CommandText = "UPDATE whatsapp_messages SET status=$status,data_json=$json WHERE id=$id";
        update.Parameters.AddWithValue("$status", message.Status.ToString()); update.Parameters.AddWithValue("$json", Json.Serialize(message)); update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return message;
    }

    public async Task MarkWhatsAppConversationReadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var all = await GetWhatsAppConversationsAsync(cancellationToken: cancellationToken);
        var conversation = all.FirstOrDefault(x => x.Id == conversationId);
        if (conversation is null || conversation.UnreadCount == 0) return;
        conversation.UnreadCount = 0;
        await UpsertWhatsAppConversationAsync(conversation, cancellationToken);
    }

    private static bool CanAdvanceStatus(WhatsAppMessageStatus current, WhatsAppMessageStatus next)
    {
        if (current == next) return true;
        if (next == WhatsAppMessageStatus.Failed) return current == WhatsAppMessageStatus.Pending;
        if (current == WhatsAppMessageStatus.Failed) return next is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
        static int Rank(WhatsAppMessageStatus value) => value switch
        {
            WhatsAppMessageStatus.Pending => 0, WhatsAppMessageStatus.Sent => 1,
            WhatsAppMessageStatus.Delivered => 2, WhatsAppMessageStatus.Read => 3,
            WhatsAppMessageStatus.Received => 3, _ => -1
        };
        return Rank(next) >= Rank(current);
    }

    public async Task<List<WhatsAppCampaign>> GetCampaignsAsync(string? accountId = "primary", CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppCampaign>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_campaigns" + (accountId is null ? "" : " WHERE account_id=$account") + " ORDER BY updated_at DESC";
        if (accountId is not null) command.Parameters.AddWithValue("$account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<WhatsAppCampaign>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<WhatsAppCampaign?> GetCampaignAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_campaigns WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return Json.Deserialize<WhatsAppCampaign>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task SaveCampaignAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        campaign.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO whatsapp_campaigns(id,account_id,status,starts_at,updated_at,data_json)
            VALUES($id,$account,$status,$starts,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET account_id=excluded.account_id,status=excluded.status,starts_at=excluded.starts_at,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", campaign.Id); command.Parameters.AddWithValue("$account", campaign.AccountId);
        command.Parameters.AddWithValue("$status", campaign.Status.ToString()); command.Parameters.AddWithValue("$starts", campaign.StartsAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", campaign.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$json", Json.Serialize(campaign));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceCampaignRecipientsAsync(string campaignId, IReadOnlyList<CampaignRecipient> recipients, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        await using (var delete = db.CreateCommand())
        {
            delete.Transaction = transaction as SqliteTransaction;
            delete.CommandText = "DELETE FROM whatsapp_campaign_recipients WHERE campaign_id=$campaign";
            delete.Parameters.AddWithValue("$campaign", campaignId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var recipient in recipients)
            await SaveCampaignRecipientInternalAsync(db, recipient, transaction as SqliteTransaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<CampaignRecipient>> GetCampaignRecipientsAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        var items = new List<CampaignRecipient>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_campaign_recipients WHERE campaign_id=$campaign ORDER BY scheduled_at";
        command.Parameters.AddWithValue("$campaign", campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<CampaignRecipient>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<CampaignRecipient?> GetNextDueCampaignRecipientAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.data_json FROM whatsapp_campaign_recipients r
            JOIN whatsapp_campaigns c ON c.id=r.campaign_id
            WHERE r.status='Queued' AND r.next_attempt_at <= $now AND c.status IN ('Scheduled','Running')
            ORDER BY r.next_attempt_at LIMIT 1
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return Json.Deserialize<CampaignRecipient>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task SaveCampaignRecipientAsync(CampaignRecipient recipient, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await SaveCampaignRecipientInternalAsync(db, recipient, null, cancellationToken);
    }

    private static async Task SaveCampaignRecipientInternalAsync(SqliteConnection db, CampaignRecipient recipient, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        recipient.UpdatedAt = DateTimeOffset.Now;
        await using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO whatsapp_campaign_recipients(id,campaign_id,lead_id,account_id,phone,status,scheduled_at,next_attempt_at,sent_at,updated_at,data_json)
            VALUES($id,$campaign,$lead,$account,$phone,$status,$scheduled,$next,$sent,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,next_attempt_at=excluded.next_attempt_at,sent_at=excluded.sent_at,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", recipient.Id); command.Parameters.AddWithValue("$campaign", recipient.CampaignId);
        command.Parameters.AddWithValue("$lead", recipient.LeadId); command.Parameters.AddWithValue("$account", recipient.AccountId); command.Parameters.AddWithValue("$phone", recipient.Phone);
        command.Parameters.AddWithValue("$status", recipient.Status.ToString()); command.Parameters.AddWithValue("$scheduled", recipient.ScheduledAt.ToString("O"));
        command.Parameters.AddWithValue("$next", recipient.NextAttemptAt.ToString("O")); command.Parameters.AddWithValue("$sent", (object?)recipient.SentAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", recipient.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$json", Json.Serialize(recipient));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountCampaignMessagesSentAsync(string accountId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM whatsapp_campaign_recipients WHERE account_id=$account AND status='Sent' AND sent_at >= $since";
        command.Parameters.AddWithValue("$account", accountId); command.Parameters.AddWithValue("$since", since.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task RecoverInterruptedCampaignRecipientsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var select = db.CreateCommand();
        select.CommandText = "SELECT data_json FROM whatsapp_campaign_recipients WHERE status='Sending'";
        var items = new List<CampaignRecipient>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<CampaignRecipient>(reader.GetString(0)) is { } item) items.Add(item);
        foreach (var item in items)
        {
            item.Status = CampaignRecipientStatus.Failed;
            item.LastError = "应用上次在发送确认前退出，发送结果未知；为避免重复触达，系统不会自动重发。";
            await SaveCampaignRecipientAsync(item, cancellationToken);
        }
    }

    public async Task PauseActiveCampaignsAsync(string accountId, string reason, CancellationToken cancellationToken = default)
    {
        foreach (var campaign in (await GetCampaignsAsync(accountId, cancellationToken)).Where(x => x.Status is CampaignStatus.Scheduled or CampaignStatus.Running))
        {
            campaign.Status = CampaignStatus.Paused; campaign.PauseReason = reason;
            await SaveCampaignAsync(campaign, cancellationToken);
        }
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var leads = await GetLeadsAsync(cancellationToken: cancellationToken);
        var campaigns = await GetCampaignsAsync(null, cancellationToken);
        var lastImport = await GetLastImportTextAsync(cancellationToken);
        return new DashboardSnapshot
        {
            TotalLeads = leads.Count,
            Grades = new[] { "A", "B", "C", "D" }.ToDictionary(x => x, x => leads.Count(l => l.Grade == x)),
            Stages = Enum.GetValues<LeadStage>().ToDictionary(x => x, x => leads.Count(l => l.Stage == x)),
            PendingFollowUps = leads.Count(l => l.NextFollowUpAt is not null && l.NextFollowUpAt <= DateTimeOffset.Now.AddDays(1)),
            ActiveCampaigns = campaigns.Count(item => item.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused),
            FailedAnalyses = leads.Count(l => l.AnalysisStatus == AnalysisStatus.RetryableFailed),
            PriorityLeads = leads.Where(l => l.Grade is "A" or "B").Take(5).ToList(),
            LastImportText = lastImport
        };
    }

    private async Task<string> GetLastImportTextAsync(CancellationToken cancellationToken)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT file_name,total_rows,created,updated,invalid_phones,created_at FROM import_jobs ORDER BY created_at DESC LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return "暂无导入记录";
        var time = DateTimeOffset.TryParse(reader.GetString(5), out var parsed) ? parsed.LocalDateTime.ToString("MM-dd HH:mm") : "";
        return $"{reader.GetString(0)} · {reader.GetInt32(1)} 行 · 新建 {reader.GetInt32(2)} · 更新 {reader.GetInt32(3)} · 号码风险 {reader.GetInt32(4)} · {time}";
    }

    public async Task SaveAnalysisRunAsync(string id, string leadId, string status, string model, LeadAnalysis? result, string? error, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO analysis_runs(id,lead_id,status,model,error,result_json,created_at,updated_at) VALUES($id,$lead,$status,$model,$error,$result,$at,$at) ON CONFLICT(id) DO UPDATE SET status=excluded.status,error=excluded.error,result_json=excluded.result_json,updated_at=excluded.updated_at";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$lead", leadId); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$result", result is null ? DBNull.Value : Json.Serialize(result)); command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<OutreachDraft>> GetDraftsAsync(string? leadId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<OutreachDraft>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM drafts" + (leadId is null ? "" : " WHERE lead_id=$lead") + " ORDER BY updated_at DESC";
        if (leadId is not null) command.Parameters.AddWithValue("$lead", leadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<OutreachDraft>(reader.GetString(0)) is { } draft) items.Add(draft);
        return items;
    }

    public async Task SaveDraftAsync(OutreachDraft draft, string action, string actor = "当前用户", CancellationToken cancellationToken = default)
    {
        draft.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        using var transaction = db.BeginTransaction();
        await using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO drafts(id,lead_id,status,purpose,language,updated_at,data_json) VALUES($id,$lead,$status,$purpose,$language,$at,$json) ON CONFLICT(id) DO UPDATE SET status=excluded.status,purpose=excluded.purpose,language=excluded.language,updated_at=excluded.updated_at,data_json=excluded.data_json";
            command.Parameters.AddWithValue("$id", draft.Id); command.Parameters.AddWithValue("$lead", draft.LeadId); command.Parameters.AddWithValue("$status", draft.Status.ToString()); command.Parameters.AddWithValue("$purpose", draft.Purpose); command.Parameters.AddWithValue("$language", draft.Language); command.Parameters.AddWithValue("$at", draft.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$json", Json.Serialize(draft));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var version = db.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO draft_versions(draft_id,body,action,actor,created_at) VALUES($draft,$body,$action,$actor,$at)";
            version.Parameters.AddWithValue("$draft", draft.Id); version.Parameters.AddWithValue("$body", draft.Body); version.Parameters.AddWithValue("$action", action); version.Parameters.AddWithValue("$actor", actor); version.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await version.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task LogEventAsync(string eventType, string? leadId, string? draftId, string detail = "", CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO audit_events(event_type,lead_id,draft_id,detail,created_at) VALUES($type,$lead,$draft,$detail,$at)";
        command.Parameters.AddWithValue("$type", eventType); command.Parameters.AddWithValue("$lead", (object?)leadId ?? DBNull.Value); command.Parameters.AddWithValue("$draft", (object?)draftId ?? DBNull.Value); command.Parameters.AddWithValue("$detail", detail); command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveImportSummaryAsync(string fileName, int total, int created, int updated, int invalid, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO import_jobs(id,file_name,status,total_rows,created,updated,invalid_phones,created_at) VALUES($id,$file,'completed',$total,$created,$updated,$invalid,$at)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$file", fileName); command.Parameters.AddWithValue("$total", total); command.Parameters.AddWithValue("$created", created); command.Parameters.AddWithValue("$updated", updated); command.Parameters.AddWithValue("$invalid", invalid); command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
