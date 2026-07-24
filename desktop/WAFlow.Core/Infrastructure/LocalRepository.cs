using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Core.Infrastructure;

public sealed class LocalRepository
{
    private static readonly string[] DemoLeadIds = ["lead_elena", "lead_ahmed", "lead_maria", "lead_james", "lead_invalid"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConversationWriteGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _conversationWriteGate;
    public string DatabasePath { get; }
    public DatabaseRecoveryNotice? LastRecoveryNotice { get; private set; }

    public LocalRepository(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFlow", "waflow.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = true }.ToString();
        _conversationWriteGate = ConversationWriteGates.GetOrAdd(Path.GetFullPath(DatabasePath), _ => new SemaphoreSlim(1, 1));
    }

    private SqliteConnection Open() => new(_connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LastRecoveryNotice = await DatabaseStartupGuard.PrepareAsync(DatabasePath, cancellationToken);
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
            CREATE INDEX IF NOT EXISTS ix_leads_phone ON leads(phone_e164) WHERE phone_valid=1 AND phone_e164 <> '';
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
            CREATE TABLE IF NOT EXISTS customer_analysis_reports (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              ai_model TEXT NOT NULL,
              source_snapshot TEXT NOT NULL,
              report_json TEXT NOT NULL,
              created_time TEXT NOT NULL,
              version INTEGER NOT NULL,
              export_history TEXT NOT NULL,
              status TEXT NOT NULL,
              error TEXT NOT NULL,
              updated_time TEXT NOT NULL,
              UNIQUE(customer_id, version)
            );
            CREATE INDEX IF NOT EXISTS ix_customer_analysis_reports_customer ON customer_analysis_reports(customer_id, version DESC);
            CREATE INDEX IF NOT EXISTS ix_customer_analysis_reports_recent ON customer_analysis_reports(created_time DESC);
            CREATE TABLE IF NOT EXISTS customer_analysis_report_steps (
              id TEXT PRIMARY KEY,
              report_id TEXT NOT NULL REFERENCES customer_analysis_reports(id) ON DELETE CASCADE,
              step_key TEXT NOT NULL,
              sequence INTEGER NOT NULL,
              status TEXT NOT NULL,
              result_json TEXT NOT NULL,
              error TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              UNIQUE(report_id, step_key)
            );
            CREATE INDEX IF NOT EXISTS ix_customer_analysis_steps_report ON customer_analysis_report_steps(report_id, sequence);
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
            CREATE TABLE IF NOT EXISTS email_accounts (
              id TEXT PRIMARY KEY,
              email_address TEXT NOT NULL COLLATE NOCASE,
              status TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(email_address)
            );
            CREATE INDEX IF NOT EXISTS ix_email_accounts_recent ON email_accounts(updated_at DESC);
            CREATE TABLE IF NOT EXISTS email_conversations (
              id TEXT PRIMARY KEY,
              account_id TEXT NOT NULL REFERENCES email_accounts(id) ON DELETE CASCADE,
              lead_id TEXT,
              peer_email TEXT NOT NULL COLLATE NOCASE,
              last_message_at TEXT NOT NULL,
              unread_count INTEGER NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, peer_email)
            );
            CREATE INDEX IF NOT EXISTS ix_email_conversations_recent ON email_conversations(account_id, last_message_at DESC);
            CREATE INDEX IF NOT EXISTS ix_email_conversations_lead ON email_conversations(lead_id, last_message_at DESC);
            CREATE TABLE IF NOT EXISTS email_messages (
              id TEXT PRIMARY KEY,
              provider_message_id TEXT NOT NULL,
              account_id TEXT NOT NULL REFERENCES email_accounts(id) ON DELETE CASCADE,
              conversation_id TEXT NOT NULL REFERENCES email_conversations(id) ON DELETE CASCADE,
              lead_id TEXT,
              direction TEXT NOT NULL,
              status TEXT NOT NULL,
              timestamp TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, provider_message_id)
            );
            CREATE INDEX IF NOT EXISTS ix_email_messages_timeline ON email_messages(conversation_id, timestamp DESC);
            CREATE INDEX IF NOT EXISTS ix_email_messages_lead ON email_messages(lead_id, timestamp DESC);
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
            CREATE TABLE IF NOT EXISTS customer_intelligence_profiles (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL UNIQUE REFERENCES leads(id) ON DELETE CASCADE,
              version INTEGER NOT NULL,
              confidence REAL NOT NULL,
              source_snapshot_hash TEXT NOT NULL,
              source_captured_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_customer_intelligence_profiles_recent ON customer_intelligence_profiles(updated_at DESC);
            CREATE TABLE IF NOT EXISTS ai_recommendation_history (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              recommendation_type TEXT NOT NULL,
              status TEXT NOT NULL,
              source_profile_id TEXT NOT NULL,
              source_profile_version INTEGER NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_ai_recommendations_customer ON ai_recommendation_history(customer_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_ai_recommendations_status ON ai_recommendation_history(status, updated_at DESC);
            CREATE TABLE IF NOT EXISTS customer_behavior_timeline (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              channel TEXT NOT NULL,
              event_type TEXT NOT NULL,
              source_type TEXT NOT NULL,
              source_id TEXT NOT NULL,
              occurred_at TEXT NOT NULL,
              created_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(customer_id, source_type, source_id, event_type)
            );
            CREATE INDEX IF NOT EXISTS ix_customer_behavior_timeline_customer ON customer_behavior_timeline(customer_id, occurred_at DESC);
            CREATE TABLE IF NOT EXISTS sales_action_logs (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              recommendation_id TEXT,
              action_type TEXT NOT NULL,
              status TEXT NOT NULL,
              due_at TEXT,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sales_action_logs_customer ON sales_action_logs(customer_id, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_sales_action_logs_due ON sales_action_logs(status, due_at) WHERE due_at IS NOT NULL;
            CREATE TABLE IF NOT EXISTS ai_learning_feedback (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              recommendation_id TEXT,
              action_id TEXT,
              outcome TEXT NOT NULL,
              helpful INTEGER NOT NULL,
              created_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_ai_learning_feedback_customer ON ai_learning_feedback(customer_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS customer_brain_runs (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              status TEXT NOT NULL,
              ai_model TEXT NOT NULL,
              source_snapshot_hash TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              completed_at TEXT,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_customer_brain_runs_customer ON customer_brain_runs(customer_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_customer_brain_runs_status ON customer_brain_runs(status, updated_at DESC);
            CREATE TABLE IF NOT EXISTS follow_up_tasks (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              recommendation_id TEXT,
              status TEXT NOT NULL,
              priority TEXT NOT NULL,
              due_at TEXT NOT NULL,
              source_type TEXT NOT NULL,
              source_id TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(customer_id, source_type, source_id)
            );
            CREATE INDEX IF NOT EXISTS ix_follow_up_tasks_customer ON follow_up_tasks(customer_id, due_at);
            CREATE INDEX IF NOT EXISTS ix_follow_up_tasks_due ON follow_up_tasks(status, due_at);
            CREATE TABLE IF NOT EXISTS customer_event_log (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              event_type TEXT NOT NULL,
              source_type TEXT NOT NULL,
              source_id TEXT NOT NULL,
              occurred_at TEXT NOT NULL,
              created_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(customer_id, event_type, source_type, source_id)
            );
            CREATE INDEX IF NOT EXISTS ix_customer_event_log_customer ON customer_event_log(customer_id, occurred_at DESC);
            CREATE TABLE IF NOT EXISTS global_customer_identities (
              customer_id TEXT PRIMARY KEY REFERENCES leads(id) ON DELETE CASCADE,
              canonical_name TEXT NOT NULL DEFAULT '',
              primary_account_id TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS customer_phone_identities (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              digits TEXT NOT NULL,
              e164 TEXT NOT NULL,
              jid TEXT NOT NULL,
              lid TEXT NOT NULL,
              source_account_id TEXT NOT NULL,
              manually_confirmed INTEGER NOT NULL DEFAULT 0,
              confidence REAL NOT NULL DEFAULT 0,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_customer_phone_identity_digits ON customer_phone_identities(digits);
            CREATE INDEX IF NOT EXISTS ix_customer_phone_identity_jid ON customer_phone_identities(jid) WHERE jid <> '';
            CREATE INDEX IF NOT EXISTS ix_customer_phone_identity_customer ON customer_phone_identities(customer_id, updated_at DESC);
            CREATE TABLE IF NOT EXISTS whatsapp_identity_links (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              account_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              contact_jid TEXT NOT NULL,
              match_result TEXT NOT NULL,
              is_active INTEGER NOT NULL DEFAULT 1,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, conversation_id)
            );
            CREATE INDEX IF NOT EXISTS ix_whatsapp_identity_link_customer ON whatsapp_identity_links(customer_id, updated_at DESC);
            CREATE TABLE IF NOT EXISTS account_personas (
              account_id TEXT PRIMARY KEY,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS account_relationship_memories (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              account_id TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(customer_id, account_id)
            );
            CREATE TABLE IF NOT EXISTS customer_identity_match_logs (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL,
              account_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              result TEXT NOT NULL,
              created_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_customer_identity_match_logs_conversation ON customer_identity_match_logs(account_id, conversation_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS global_customer_agent_locks (
              customer_id TEXT PRIMARY KEY REFERENCES leads(id) ON DELETE CASCADE,
              active_account_id TEXT NOT NULL DEFAULT '',
              account_id TEXT NOT NULL DEFAULT '',
              conversation_id TEXT NOT NULL DEFAULT '',
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversation_agent_states (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL,
              account_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              mode TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(account_id, conversation_id)
            );
            CREATE INDEX IF NOT EXISTS ix_conversation_agent_states_customer ON conversation_agent_states(customer_id, mode, updated_at DESC);
            CREATE TABLE IF NOT EXISTS relationship_memories (
              customer_id TEXT PRIMARY KEY REFERENCES leads(id) ON DELETE CASCADE,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sourcing_requests (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              version INTEGER NOT NULL,
              status TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL,
              UNIQUE(customer_id, version)
            );
            CREATE INDEX IF NOT EXISTS ix_sourcing_requests_customer ON sourcing_requests(customer_id, version DESC);
            CREATE TABLE IF NOT EXISTS human_handoff_events (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              account_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              status TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_human_handoff_customer ON human_handoff_events(customer_id, status, updated_at DESC);
            CREATE TABLE IF NOT EXISTS pending_questions (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
              account_id TEXT NOT NULL,
              safety TEXT NOT NULL,
              is_resolved INTEGER NOT NULL,
              updated_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_pending_questions_customer ON pending_questions(customer_id, is_resolved, updated_at DESC);
            CREATE TABLE IF NOT EXISTS customer_merge_audits (
              id TEXT PRIMARY KEY,
              source_customer_id TEXT NOT NULL,
              target_customer_id TEXT NOT NULL,
              identity_link_id TEXT NOT NULL,
              action TEXT NOT NULL,
              created_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agent_turn_logs (
              id TEXT PRIMARY KEY,
              customer_id TEXT NOT NULL,
              account_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              source_message_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              data_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_agent_turn_logs_customer ON agent_turn_logs(customer_id, created_at DESC);
            """;
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        // CREATE TABLE IF NOT EXISTS does not evolve tables created by an earlier
        // preview build. 4.1 is an in-place upgrade, so add every promoted column
        // idempotently before backfill touches it.
        await EnsureColumnAsync(db, "global_customer_identities", "canonical_name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "customer_phone_identities", "manually_confirmed", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "customer_phone_identities", "confidence", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "whatsapp_identity_links", "is_active", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "global_customer_agent_locks", "active_account_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "global_customer_agent_locks", "account_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "global_customer_agent_locks", "conversation_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await using (var removeLegacySalesProfile = db.CreateCommand())
        {
            removeLegacySalesProfile.CommandText = "DELETE FROM settings WHERE key='sales_profile'";
            await removeLegacySalesProfile.ExecuteNonQueryAsync(cancellationToken);
        }
        await SeedIfEmptyAsync(db, cancellationToken);
        await using var cleanup = await db.BeginTransactionAsync(cancellationToken);
        await RemoveDemoLeadsIfRealDataExistsInternalAsync(db, cleanup as SqliteTransaction, cancellationToken);
        await AlignLeadAiBaselineInternalAsync(db, cleanup as SqliteTransaction, cancellationToken);
        await RepairWhatsAppTextEncodingInternalAsync(db, cleanup as SqliteTransaction, cancellationToken);
        await BackfillCustomerSuccessIdentityInternalAsync(db, cleanup as SqliteTransaction, cancellationToken);
        await cleanup.CommitAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection db,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken)
    {
        await using var inspect = db.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;

        await using var alter = db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RepairWhatsAppTextEncodingInternalAsync(SqliteConnection db, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var conversations = new List<WhatsAppConversation>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_conversations";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppConversation>(reader.GetString(0)) is { } item) conversations.Add(item);
        }
        foreach (var item in conversations)
        {
            var displayName = WhatsAppTextEncodingRepair.Repair(item.DisplayName);
            var lastMessage = WhatsAppTextEncodingRepair.Repair(item.LastMessage);
            if (displayName == item.DisplayName && lastMessage == item.LastMessage) continue;
            item.DisplayName = displayName;
            item.LastMessage = lastMessage;
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE whatsapp_conversations SET data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", item.Id);
            update.Parameters.AddWithValue("$json", Json.Serialize(item));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var contacts = new List<WhatsAppContact>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_contacts";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppContact>(reader.GetString(0)) is { } item) contacts.Add(item);
        }
        foreach (var item in contacts)
        {
            var values = new[] { item.DisplayName, item.SavedName, item.NotifyName, item.VerifiedName, item.Username };
            var repaired = values.Select(WhatsAppTextEncodingRepair.Repair).ToArray();
            if (values.SequenceEqual(repaired, StringComparer.Ordinal)) continue;
            item.DisplayName = repaired[0]; item.SavedName = repaired[1]; item.NotifyName = repaired[2]; item.VerifiedName = repaired[3]; item.Username = repaired[4];
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE whatsapp_contacts SET display_name=$name,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", item.Id);
            update.Parameters.AddWithValue("$name", item.DisplayName);
            update.Parameters.AddWithValue("$json", Json.Serialize(item));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var messages = new List<WhatsAppMessage>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_messages";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppMessage>(reader.GetString(0)) is { } item) messages.Add(item);
        }
        foreach (var item in messages)
        {
            var body = WhatsAppTextEncodingRepair.Repair(item.Body);
            var fileName = WhatsAppTextEncodingRepair.Repair(item.FileName);
            var pushName = WhatsAppTextEncodingRepair.Repair(item.PushName);
            if (body == item.Body && fileName == item.FileName && pushName == item.PushName) continue;
            item.Body = body; item.FileName = fileName; item.PushName = pushName;
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE whatsapp_messages SET data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", item.Id);
            update.Parameters.AddWithValue("$json", Json.Serialize(item));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

    }

    private static async Task BackfillCustomerSuccessIdentityInternalAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var leads = new Dictionary<string, Lead>(StringComparer.OrdinalIgnoreCase);
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM leads";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<Lead>(reader.GetString(0)) is { } lead)
                    leads[lead.Id] = lead;
        }

        foreach (var lead in leads.Values)
        {
            var identity = new GlobalCustomerIdentity
            {
                CustomerId = lead.Id,
                CanonicalName = lead.DisplayName,
                ConfirmedAliases = string.IsNullOrWhiteSpace(lead.Name) ? [] : [lead.Name],
                CreatedAt = lead.CreatedAt == default ? DateTimeOffset.Now : lead.CreatedAt,
                UpdatedAt = DateTimeOffset.Now
            };
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO global_customer_identities(customer_id,canonical_name,primary_account_id,updated_at,data_json)
                VALUES($customer,$name,'',$updated,$json)
                ON CONFLICT(customer_id) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$customer", identity.CustomerId);
            insert.Parameters.AddWithValue("$name", identity.CanonicalName);
            insert.Parameters.AddWithValue("$updated", identity.UpdatedAt.ToString("O"));
            insert.Parameters.AddWithValue("$json", Json.Serialize(identity));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var conversations = new List<WhatsAppConversation>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_conversations WHERE lead_id IS NOT NULL AND lead_id <> ''";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppConversation>(reader.GetString(0)) is { } conversation)
                    conversations.Add(conversation);
        }

        var contacts = new List<WhatsAppContact>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM whatsapp_contacts";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<WhatsAppContact>(reader.GetString(0)) is { } contact)
                    contacts.Add(contact);
        }

        foreach (var conversation in conversations.Where(item => leads.ContainsKey(item.LeadId)))
        {
            var lead = leads[conversation.LeadId];
            var digits = PhoneIdentity.Digits(
                string.IsNullOrWhiteSpace(conversation.Phone) ? lead.PhoneE164 : conversation.Phone);
            var contact = contacts.FirstOrDefault(item =>
                item.AccountId.Equals(conversation.AccountId, StringComparison.OrdinalIgnoreCase) &&
                (PhoneIdentity.Digits(item.Phone) == digits ||
                 item.Jid.Equals(conversation.Phone, StringComparison.OrdinalIgnoreCase)));
            var phoneId = StableId("phone", lead.Id, conversation.AccountId, digits, contact?.Jid ?? "");
            var linkId = StableId("link", conversation.AccountId, conversation.Id);
            var now = DateTimeOffset.Now;
            var phone = new CustomerPhoneIdentity
            {
                Id = phoneId,
                CustomerId = lead.Id,
                RawValue = string.IsNullOrWhiteSpace(conversation.Phone) ? lead.PhoneE164 : conversation.Phone,
                Digits = digits,
                E164 = PhoneNormalizer.Normalize(lead.PhoneE164, null).Valid ? lead.PhoneE164 : "",
                Jid = contact?.Jid ?? "",
                Lid = contact?.SourceJid?.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) == true
                    ? contact.SourceJid : "",
                SourceAccountId = conversation.AccountId,
                SourceConversationId = conversation.Id,
                ManuallyConfirmed = true,
                Confidence = 1,
                Method = CustomerIdentityMatchMethod.ManualBinding,
                UpdatedAt = now
            };
            await using (var insert = db.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO customer_phone_identities(id,customer_id,digits,e164,jid,lid,source_account_id,manually_confirmed,confidence,updated_at,data_json)
                    VALUES($id,$customer,$digits,$e164,$jid,$lid,$account,1,1,$updated,$json)
                    ON CONFLICT(id) DO UPDATE SET updated_at=excluded.updated_at,data_json=excluded.data_json
                    """;
                insert.Parameters.AddWithValue("$id", phone.Id);
                insert.Parameters.AddWithValue("$customer", phone.CustomerId);
                insert.Parameters.AddWithValue("$digits", phone.Digits);
                insert.Parameters.AddWithValue("$e164", phone.E164);
                insert.Parameters.AddWithValue("$jid", phone.Jid);
                insert.Parameters.AddWithValue("$lid", phone.Lid);
                insert.Parameters.AddWithValue("$account", phone.SourceAccountId);
                insert.Parameters.AddWithValue("$updated", now.ToString("O"));
                insert.Parameters.AddWithValue("$json", Json.Serialize(phone));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var link = new WhatsAppIdentityLink
            {
                Id = linkId,
                CustomerId = lead.Id,
                AccountId = conversation.AccountId,
                ConversationId = conversation.Id,
                ContactJid = contact?.Jid ?? "",
                ContactLid = phone.Lid,
                PhoneIdentityId = phone.Id,
                MatchResult = CustomerIdentityMatchResult.ExactMatch,
                MatchMethod = CustomerIdentityMatchMethod.ManualBinding,
                Confidence = 1,
                ManuallyConfirmed = true,
                UpdatedAt = now
            };
            await using (var insert = db.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO whatsapp_identity_links(id,customer_id,account_id,conversation_id,contact_jid,match_result,is_active,updated_at,data_json)
                    VALUES($id,$customer,$account,$conversation,$jid,$result,1,$updated,$json)
                    ON CONFLICT(account_id,conversation_id) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("$id", link.Id);
                insert.Parameters.AddWithValue("$customer", link.CustomerId);
                insert.Parameters.AddWithValue("$account", link.AccountId);
                insert.Parameters.AddWithValue("$conversation", link.ConversationId);
                insert.Parameters.AddWithValue("$jid", link.ContactJid);
                insert.Parameters.AddWithValue("$result", link.MatchResult.ToString());
                insert.Parameters.AddWithValue("$updated", now.ToString("O"));
                insert.Parameters.AddWithValue("$json", Json.Serialize(link));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var state = new ConversationAgentState
            {
                Id = StableId("agent-state", conversation.AccountId, conversation.Id),
                CustomerId = lead.Id,
                AccountId = conversation.AccountId,
                ConversationId = conversation.Id,
                Mode = ConversationAgentMode.SuggestOnly,
                StateReason = "由已有 CRM 与 WhatsApp 明确关联无损回填。",
                UpdatedAt = now
            };
            await using (var insert = db.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO conversation_agent_states(id,customer_id,account_id,conversation_id,mode,updated_at,data_json)
                    VALUES($id,$customer,$account,$conversation,$mode,$updated,$json)
                    ON CONFLICT(account_id,conversation_id) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("$id", state.Id);
                insert.Parameters.AddWithValue("$customer", state.CustomerId);
                insert.Parameters.AddWithValue("$account", state.AccountId);
                insert.Parameters.AddWithValue("$conversation", state.ConversationId);
                insert.Parameters.AddWithValue("$mode", state.Mode.ToString());
                insert.Parameters.AddWithValue("$updated", now.ToString("O"));
                insert.Parameters.AddWithValue("$json", Json.Serialize(state));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var lead in leads.Values)
        {
            await using var select = db.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM global_customer_identities WHERE customer_id=$customer";
            select.Parameters.AddWithValue("$customer", lead.Id);
            var identity = Json.Deserialize<GlobalCustomerIdentity>(await select.ExecuteScalarAsync(cancellationToken) as string);
            if (identity is null) continue;

            // Rebuild the account projection from the durable identity links, not
            // only from conversations currently present in the local inbox. A
            // manually confirmed cross-account alias can legitimately exist before
            // that account's history has been synchronized; dropping it here would
            // split one customer back into multiple identities after every restart.
            var linkedAccounts = new List<string>();
            await using (var linked = db.CreateCommand())
            {
                linked.Transaction = transaction;
                linked.CommandText = """
                    SELECT account_id
                    FROM whatsapp_identity_links
                    WHERE customer_id=$customer AND is_active=1
                    ORDER BY account_id COLLATE NOCASE
                    """;
                linked.Parameters.AddWithValue("$customer", lead.Id);
                await using var reader = await linked.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var accountId = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(accountId)) linkedAccounts.Add(accountId);
                }
            }

            identity.LinkedAccountIds = linkedAccounts
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!identity.LinkedAccountIds.Contains(identity.PrimaryAccountId, StringComparer.OrdinalIgnoreCase))
                identity.PrimaryAccountId = identity.LinkedAccountIds.FirstOrDefault() ?? "";
            identity.UpdatedAt = DateTimeOffset.Now;
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE global_customer_identities SET canonical_name=$name,primary_account_id=$primary,updated_at=$updated,data_json=$json WHERE customer_id=$customer";
            update.Parameters.AddWithValue("$name", identity.CanonicalName);
            update.Parameters.AddWithValue("$primary", identity.PrimaryAccountId);
            update.Parameters.AddWithValue("$updated", identity.UpdatedAt.ToString("O"));
            update.Parameters.AddWithValue("$json", Json.Serialize(identity));
            update.Parameters.AddWithValue("$customer", identity.CustomerId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string StableId(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts)));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static async Task SeedIfEmptyAsync(SqliteConnection db, CancellationToken cancellationToken)
    {
        await using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM leads";
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) > 0) return;
        var samples = new[]
        {
            new Lead { Id="lead_elena", Name="Elena Rossi", Company="Nordline Living", Country="Italy", PhoneE164="+393491234567", PhoneValid=true, Email="elena@nordline.example", PreferredLanguage="it", ProductInterest="Oak dining chair · Model DC-18", EstimatedOrderValue=24800, Currency="EUR", CompanyScale=.8, PurchasePower=.9, ExplicitDemand=true, RegisteredOrConsulted=true, Source="Global buyer discovery", Tags=["Distributor","Furniture","EU"], Owner="Olivia Chen", Stage=LeadStage.Negotiation, LatestMessage="Could you confirm the lead time for 300 units?", LastContactAt=DateTimeOffset.Now.AddHours(-2), NextFollowUpAt=DateTimeOffset.Now.AddHours(3) },
            new Lead { Id="lead_ahmed", Name="Ahmed Mansour", Company="Nile Trade Co.", Country="Egypt", PhoneE164="+201001234567", PhoneValid=true, Email="ahmed@niletrade.example", PreferredLanguage="ar", ProductInterest="Solar garden lights", EstimatedOrderValue=18200, Currency="USD", CompanyScale=.7, PurchasePower=.8, ExplicitDemand=true, RegisteredOrConsulted=true, Source="Import directory", Tags=["Importer","Lighting"], Owner="Olivia Chen", Stage=LeadStage.Interested, LatestMessage="Please send your FOB price list.", LastContactAt=DateTimeOffset.Now.AddHours(-3), NextFollowUpAt=DateTimeOffset.Now.AddHours(6) },
            new Lead { Id="lead_maria", Name="María Torres", Company="Casa Nova Retail", Country="Mexico", PhoneE164="+525512345678", PhoneValid=true, Email="maria@casanova.example", PreferredLanguage="es", ProductInterest="Kitchen storage set", EstimatedOrderValue=9600, Currency="USD", CompanyScale=.55, PurchasePower=.6, ExplicitDemand=false, RegisteredOrConsulted=true, Source="Trade show QR", Tags=["Retailer","LATAM"], Owner="Olivia Chen", Stage=LeadStage.Interested, LatestMessage="I will review the catalogue today.", LastContactAt=DateTimeOffset.Now.AddHours(-5), NextFollowUpAt=DateTimeOffset.Now.AddDays(1) },
            new Lead { Id="lead_james", Name="James Cole", Company="Brighton Supply", Country="United Kingdom", PhoneE164="+447700900123", PhoneValid=true, Email="james@brighton.example", PreferredLanguage="en", ProductInterest="Reusable water bottles", EstimatedOrderValue=7400, Currency="GBP", CompanyScale=.5, PurchasePower=.5, ExplicitDemand=true, RegisteredOrConsulted=true, Source="Website inquiry", Tags=["Wholesaler","UK"], Stage=LeadStage.New, LatestMessage="Do you support private labels?", LastContactAt=DateTimeOffset.Now.AddHours(-7) },
            new Lead { Id="lead_invalid", Name="Test Invalid", Company="Example Trading", Country="Unknown", PhoneE164="12345", PhoneValid=false, PreferredLanguage="en", ProductInterest="Sample catalogue", EstimatedOrderValue=2000, CompanyScale=.3, PurchasePower=.3, Source="Test import", Tags=["Risk"], Stage=LeadStage.New }
        };
        foreach (var lead in samples)
            await UpsertLeadInternalAsync(db, lead, cancellationToken);
    }

    private static async Task AlignLeadAiBaselineInternalAsync(SqliteConnection db, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var leads = new List<Lead>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM leads";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<Lead>(reader.GetString(0)) is { } lead) leads.Add(lead);
        }

        foreach (var lead in leads)
        {
            var changed = false;
            var hasCurrentV2Score = lead.HasCurrentAiScore;
            var hasLegacyOrUntrustedScore = !hasCurrentV2Score &&
                (lead.AiScoreApplied || lead.AnalysisStatus == AnalysisStatus.Succeeded || lead.Score != 0 || lead.Grade != "D" ||
                 lead.AnalysisContractVersion != 0 || lead.BaseProfileScore != 0 || lead.BehaviorSignalScore != 0 ||
                 lead.ScoreBreakdown.Count > 0 || lead.ScoreReasons.Count > 0 || lead.ScoreFactors.Count > 0 || lead.BehaviorSignals.Count > 0);
            if (hasLegacyOrUntrustedScore)
            {
                LeadScoringService.ResetToAiBaseline(lead, "等待 Lead Intelligence V2 分析", "请使用当前 AI 模型运行 V2 分析。");
                lead.AnalysisStatus = AnalysisStatus.NotRun;
                lead.AnalysisError = "旧评分契约已停用；客户原始资料与历史分析记录均已保留，请运行 V2 分析。";
                lead.LastAnalyzedAt = null;
                changed = true;
            }
            else if (!hasCurrentV2Score)
            {
                if (lead.AnalysisStatus == AnalysisStatus.Running)
                {
                    lead.AnalysisStatus = AnalysisStatus.Queued;
                    lead.AnalysisError = "上次分析被程序退出中断，已重新排队。";
                    changed = true;
                }
            }
            if (changed) await UpsertLeadInternalAsync(db, lead, cancellationToken, transaction);
        }
    }

    public async Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<AppSettings>("app_settings", cancellationToken) ?? new AppSettings();

    public Task SaveAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        SaveSettingAsync("app_settings", settings, cancellationToken);

    public async Task<LeadBulkAnalysisRunState?> GetLeadBulkAnalysisRunStateAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync<LeadBulkAnalysisRunState>("lead_bulk_analysis_run_state", cancellationToken);

    public Task SaveLeadBulkAnalysisRunStateAsync(LeadBulkAnalysisRunState state, CancellationToken cancellationToken = default) =>
        SaveSettingAsync("lead_bulk_analysis_run_state", state, cancellationToken);

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

    public async Task<int> DeleteLeadsAsync(IEnumerable<string> leadIds, CancellationToken cancellationToken = default)
    {
        var ids = leadIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return 0;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var deleted = 0;
        foreach (var leadId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DeleteLeadInternalAsync(db, transaction as SqliteTransaction, leadId, "customers_bulk_deleted", cancellationToken)) deleted++;
        }
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

        var emailConversations = new List<EmailConversation>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM email_conversations WHERE lead_id=$lead";
            select.Parameters.AddWithValue("$lead", leadId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<EmailConversation>(reader.GetString(0)) is { } item) emailConversations.Add(item);
        }
        foreach (var item in emailConversations)
        {
            item.LeadId = "";
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE email_conversations SET lead_id=NULL,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", item.Id); update.Parameters.AddWithValue("$json", Json.Serialize(item));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var emailMessages = new List<EmailMessage>();
        await using (var select = db.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT data_json FROM email_messages WHERE lead_id=$lead";
            select.Parameters.AddWithValue("$lead", leadId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<EmailMessage>(reader.GetString(0)) is { } item) emailMessages.Add(item);
        }
        foreach (var item in emailMessages)
        {
            item.LeadId = "";
            await using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE email_messages SET lead_id=NULL,data_json=$json WHERE id=$id";
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
        audit.Parameters.AddWithValue("$detail", "lead deleted; WhatsApp and email history retained and unlinked"); audit.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
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
        command.CommandText = "SELECT data_json FROM leads WHERE phone_e164=$phone LIMIT 2";
        command.Parameters.AddWithValue("$phone", "+" + digits);
        var exact = new List<Lead>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                if (Json.Deserialize<Lead>(reader.GetString(0)) is { } lead) exact.Add(lead);
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        return Services.PhoneIdentity.FindUniqueLead(await GetLeadsAsync(cancellationToken: cancellationToken), digits);
    }

    public async Task<Lead?> GetLeadByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0) return null;
        var matches = (await GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(lead => NormalizeEmail(lead.Email).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public async Task<int> SynchronizeLeadConnectionsFromInboxAsync(IReadOnlyList<Lead> leads, CancellationToken cancellationToken = default)
    {
        var byPhone = leads
            .SelectMany(lead => Services.PhoneIdentity.LeadPhoneCandidates(lead).Select(phone => (Phone: phone, Lead: lead)))
            .Where(item => item.Phone.Length > 0)
            .GroupBy(item => item.Phone, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { group.Key, Leads = group.Select(item => item.Lead).DistinctBy(lead => lead.Id).ToList() })
            .Where(group => group.Leads.Count == 1)
            .ToDictionary(group => group.Key, group => group.Leads[0], StringComparer.OrdinalIgnoreCase);
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
        await _conversationWriteGate.WaitAsync(cancellationToken);
        try
        {
            conversation.UpdatedAt = DateTimeOffset.Now;
            await using var db = Open(); await db.OpenAsync(cancellationToken);
            await using (var existingCommand = db.CreateCommand())
            {
                existingCommand.CommandText = "SELECT data_json FROM whatsapp_conversations WHERE id=$id";
                existingCommand.Parameters.AddWithValue("$id", conversation.Id);
                if (Json.Deserialize<WhatsAppConversation>(await existingCommand.ExecuteScalarAsync(cancellationToken) as string) is { } existing &&
                    existing.LastReadAt is { } persistedReadAt &&
                    (conversation.LastReadAt is null || conversation.LastReadAt <= persistedReadAt))
                {
                    // A history/contact sync may finish after the user opened the chat.
                    // Read cursors are monotonic: an older snapshot must never restore
                    // a badge that the newer local read action already cleared.
                    conversation.LastReadAt = persistedReadAt;
                    conversation.UnreadCount = existing.UnreadCount;
                }
            }
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
        finally
        {
            _conversationWriteGate.Release();
        }
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
            if (Json.Deserialize<WhatsAppMessage>(await select.ExecuteScalarAsync(cancellationToken) as string) is { } existing)
            {
                if (!CanAdvanceStatus(existing.Status, message.Status)) message.Status = existing.Status;
                if (string.IsNullOrWhiteSpace(message.MediaPath))
                {
                    if (!string.IsNullOrWhiteSpace(existing.MediaPath)) message.MediaPath = existing.MediaPath;
                    else if (string.IsNullOrWhiteSpace(message.MediaDownloadError) && !string.IsNullOrWhiteSpace(existing.MediaDownloadError)) message.MediaDownloadError = existing.MediaDownloadError;
                }
                if (string.IsNullOrWhiteSpace(message.QuotedMessageId))
                {
                    message.QuotedMessageId = existing.QuotedMessageId;
                    message.QuotedFromMe = existing.QuotedFromMe;
                }
                if (string.IsNullOrWhiteSpace(message.QuotedText)) message.QuotedText = existing.QuotedText;
                if (existing.IsRevoked)
                {
                    message.IsRevoked = true;
                    message.RevokedAt ??= existing.RevokedAt;
                }
            }
            await using var update = db.CreateCommand();
            update.CommandText = "UPDATE whatsapp_messages SET status=$status,lead_id=$lead,data_json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", message.Id); update.Parameters.AddWithValue("$status", message.Status.ToString()); update.Parameters.AddWithValue("$lead", string.IsNullOrWhiteSpace(message.LeadId) ? DBNull.Value : message.LeadId); update.Parameters.AddWithValue("$json", Json.Serialize(message));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return inserted;
    }

    public async Task<WhatsAppMessage?> MarkWhatsAppMessageRevokedAsync(
        string accountId,
        string providerMessageId,
        DateTimeOffset? revokedAt = null,
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
        message.IsRevoked = true;
        message.RevokedAt ??= revokedAt ?? DateTimeOffset.Now;
        await using var update = db.CreateCommand();
        update.CommandText = "UPDATE whatsapp_messages SET data_json=$json WHERE id=$id";
        update.Parameters.AddWithValue("$json", Json.Serialize(message)); update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return message;
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

    public async Task<List<WhatsAppMessage>> GetWhatsAppMessagesForLeadAsync(Lead lead, int limit = 40, CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppMessage>();
        var phone = Services.PhoneIdentity.Digits(lead.PhoneE164);
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_messages WHERE lead_id=$lead OR ($phone <> '' AND phone=$phone) ORDER BY timestamp DESC LIMIT $limit";
        command.Parameters.AddWithValue("$lead", lead.Id);
        command.Parameters.AddWithValue("$phone", phone);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (Json.Deserialize<WhatsAppMessage>(reader.GetString(0)) is { } item) items.Add(item);
        items.Reverse();
        return items;
    }

    public async Task<WhatsAppMessage?> GetWhatsAppMessageByProviderIdAsync(
        string accountId,
        string providerMessageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId)) return null;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_messages WHERE account_id=$account AND provider_message_id=$provider ORDER BY timestamp DESC LIMIT 1";
        command.Parameters.AddWithValue("$account", string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId);
        command.Parameters.AddWithValue("$provider", providerMessageId);
        return Json.Deserialize<WhatsAppMessage>(await command.ExecuteScalarAsync(cancellationToken) as string);
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
        if (conversation is null) return;
        conversation.UnreadCount = 0;
        conversation.LastReadAt = DateTimeOffset.Now;
        await UpsertWhatsAppConversationAsync(conversation, cancellationToken);
    }

    public async Task<int> CountUnreadWhatsAppMessagesAsync(
        string conversationId,
        DateTimeOffset? lastReadAt,
        CancellationToken cancellationToken = default)
    {
        if (lastReadAt is null) return 0;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_messages WHERE conversation_id=$conversation AND direction=$direction AND julianday(timestamp)>julianday($read)";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$direction", WhatsAppMessageDirection.Incoming.ToString());
        command.Parameters.AddWithValue("$read", lastReadAt.Value.ToString("O"));
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = Json.Deserialize<WhatsAppMessage>(reader.GetString(0));
            if (message is { IsRevoked: false, IsStatusUpdate: false }) count++;
        }
        return count;
    }

    private static bool CanAdvanceStatus(WhatsAppMessageStatus current, WhatsAppMessageStatus next)
    {
        if (current == next) return true;
        // WhatsApp can accept a local send request and only report the transport
        // error afterwards. A server error must therefore be allowed to correct
        // both Pending and Sent, but never a message already delivered/read.
        if (next == WhatsAppMessageStatus.Failed) return current is WhatsAppMessageStatus.Pending or WhatsAppMessageStatus.Sent;
        if (current == WhatsAppMessageStatus.Failed) return next is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
        static int Rank(WhatsAppMessageStatus value) => value switch
        {
            WhatsAppMessageStatus.Pending => 0, WhatsAppMessageStatus.Sent => 1,
            WhatsAppMessageStatus.Delivered => 2, WhatsAppMessageStatus.Read => 3,
            WhatsAppMessageStatus.Received => 3, _ => -1
        };
        return Rank(next) >= Rank(current);
    }

    public async Task<List<EmailAccount>> GetEmailAccountsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<EmailAccount>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM email_accounts ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<EmailAccount>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<EmailAccount?> GetEmailAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM email_accounts WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return Json.Deserialize<EmailAccount>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task SaveEmailAccountAsync(EmailAccount account, CancellationToken cancellationToken = default)
    {
        account.EmailAddress = NormalizeEmail(account.EmailAddress);
        account.UserName = string.IsNullOrWhiteSpace(account.UserName) ? account.EmailAddress : account.UserName.Trim();
        account.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO email_accounts(id,email_address,status,updated_at,data_json)
            VALUES($id,$email,$status,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET email_address=excluded.email_address,status=excluded.status,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", account.Id); command.Parameters.AddWithValue("$email", account.EmailAddress);
        command.Parameters.AddWithValue("$status", account.Status.ToString()); command.Parameters.AddWithValue("$updated", account.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(account));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteEmailAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand(); command.CommandText = "DELETE FROM email_accounts WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<EmailConversation>> GetEmailConversationsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var items = new List<EmailConversation>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM email_conversations WHERE account_id=$account ORDER BY last_message_at DESC";
        command.Parameters.AddWithValue("$account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<EmailConversation>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertEmailConversationAsync(EmailConversation conversation, CancellationToken cancellationToken = default)
    {
        conversation.PeerEmail = NormalizeEmail(conversation.PeerEmail);
        conversation.UpdatedAt = DateTimeOffset.Now;
        if (string.IsNullOrWhiteSpace(conversation.Id)) conversation.Id = $"{conversation.AccountId}:{conversation.PeerEmail}";
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO email_conversations(id,account_id,lead_id,peer_email,last_message_at,unread_count,updated_at,data_json)
            VALUES($id,$account,$lead,$peer,$last,$unread,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET lead_id=excluded.lead_id,peer_email=excluded.peer_email,last_message_at=excluded.last_message_at,
              unread_count=excluded.unread_count,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", conversation.Id); command.Parameters.AddWithValue("$account", conversation.AccountId);
        command.Parameters.AddWithValue("$lead", string.IsNullOrWhiteSpace(conversation.LeadId) ? DBNull.Value : conversation.LeadId);
        command.Parameters.AddWithValue("$peer", conversation.PeerEmail); command.Parameters.AddWithValue("$last", conversation.LastMessageAt.ToString("O"));
        command.Parameters.AddWithValue("$unread", conversation.UnreadCount); command.Parameters.AddWithValue("$updated", conversation.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(conversation)); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpsertEmailMessageAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        message.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO email_messages(id,provider_message_id,account_id,conversation_id,lead_id,direction,status,timestamp,updated_at,data_json)
            VALUES($id,$provider,$account,$conversation,$lead,$direction,$status,$timestamp,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET lead_id=excluded.lead_id,status=excluded.status,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", message.Id); command.Parameters.AddWithValue("$provider", message.ProviderMessageId);
        command.Parameters.AddWithValue("$account", message.AccountId); command.Parameters.AddWithValue("$conversation", message.ConversationId);
        command.Parameters.AddWithValue("$lead", string.IsNullOrWhiteSpace(message.LeadId) ? DBNull.Value : message.LeadId);
        command.Parameters.AddWithValue("$direction", message.Direction.ToString()); command.Parameters.AddWithValue("$status", message.Status.ToString());
        command.Parameters.AddWithValue("$timestamp", message.Timestamp.ToString("O")); command.Parameters.AddWithValue("$updated", message.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(message));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<EmailMessage?> GetEmailMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM email_messages WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return Json.Deserialize<EmailMessage>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<EmailMessage>> GetEmailMessagesAsync(string conversationId, int limit = 500, CancellationToken cancellationToken = default)
    {
        var items = new List<EmailMessage>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM email_messages WHERE conversation_id=$conversation ORDER BY timestamp DESC LIMIT $limit";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2_000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<EmailMessage>(reader.GetString(0)) is { } item) items.Add(item);
        items.Reverse();
        return items;
    }

    public async Task<List<EmailMessage>> GetEmailMessagesForLeadAsync(string leadId, int limit = 100, CancellationToken cancellationToken = default)
    {
        var items = new List<EmailMessage>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM email_messages WHERE lead_id=$lead ORDER BY timestamp DESC LIMIT $limit";
        command.Parameters.AddWithValue("$lead", leadId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1_000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<EmailMessage>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
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

    public async Task<CampaignRecipient?> GetCampaignRecipientByProviderMessageIdAsync(
        string accountId,
        string providerMessageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId)) return null;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT data_json
            FROM whatsapp_campaign_recipients
            WHERE account_id=$account
              AND json_extract(data_json, '$.providerMessageId')=$provider
            ORDER BY updated_at DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$account", string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId);
        command.Parameters.AddWithValue("$provider", providerMessageId);
        return Json.Deserialize<CampaignRecipient>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<CampaignRecipient>> GetCampaignRecipientsAwaitingConfirmationAsync(
        DateTimeOffset updatedBefore,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CampaignRecipient>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_campaign_recipients WHERE status='Sending' AND updated_at <= $before ORDER BY updated_at";
        command.Parameters.AddWithValue("$before", updatedBefore.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CampaignRecipient>(reader.GetString(0)) is { } item) items.Add(item);
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

    public async Task<List<WhatsAppCampaign>> GetActiveCampaignsAsync(CancellationToken cancellationToken = default) =>
        (await GetCampaignsAsync(null, cancellationToken))
        .Where(campaign => campaign.Status is CampaignStatus.Scheduled or CampaignStatus.Running)
        .ToList();

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var leads = await GetLeadsAsync(cancellationToken: cancellationToken);
        var campaigns = await GetCampaignsAsync(null, cancellationToken);
        var followUpTasks = await GetFollowUpTasksAsync(null, cancellationToken);
        var recipients = new List<CampaignRecipient>();
        foreach (var campaign in campaigns) recipients.AddRange(await GetCampaignRecipientsAsync(campaign.Id, cancellationToken));
        var lastImport = await GetLastImportTextAsync(cancellationToken);
        return new DashboardSnapshot
        {
            TotalLeads = leads.Count,
            Grades = new[] { "A", "B", "C", "D" }.ToDictionary(
                grade => grade,
                grade => leads.Count(lead => (lead.HasCurrentAiScore ? lead.Grade : "D") == grade)),
            Stages = Enum.GetValues<LeadStage>().ToDictionary(x => x, x => leads.Count(l => l.Stage == x)),
            PendingFollowUps = followUpTasks.Count(task =>
                    (task.Status is FollowUpTaskStatus.Proposed or FollowUpTaskStatus.Open or FollowUpTaskStatus.InProgress)
                    && task.DueAt <= DateTimeOffset.Now.AddDays(1))
                + leads.Count(lead => lead.NextFollowUpAt is not null
                    && lead.NextFollowUpAt <= DateTimeOffset.Now.AddDays(1)
                    && !followUpTasks.Any(task => task.CustomerId == lead.Id
                        && (task.Status is FollowUpTaskStatus.Proposed or FollowUpTaskStatus.Open or FollowUpTaskStatus.InProgress))),
            ActiveCampaigns = campaigns.Count(item => item.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused or CampaignStatus.SafetyStopped),
            FailedAnalyses = leads.Count(l => l.AnalysisStatus == AnalysisStatus.RetryableFailed),
            AnalyzedLeads = leads.Count(l => l.HasCurrentAiScore),
            QueuedAnalyses = leads.Count(l => l.AnalysisStatus is AnalysisStatus.Queued or AnalysisStatus.Running),
            CampaignSent = recipients.Count(item => item.Status == CampaignRecipientStatus.Sent),
            CampaignFailed = recipients.Count(item => item.Status == CampaignRecipientStatus.Failed),
            CampaignQueued = recipients.Count(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending),
            SafetyStoppedCampaigns = campaigns.Count(item => item.Status == CampaignStatus.SafetyStopped),
            PriorityLeads = leads
                .Where(lead => lead.HasCurrentAiScore && lead.Grade is "A" or "B")
                .OrderBy(lead => lead.Grade == "A" ? 0 : 1)
                .ThenByDescending(lead => lead.Score)
                .ThenBy(lead => lead.NextFollowUpAt ?? DateTimeOffset.MaxValue)
                .Take(8)
                .ToList(),
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

    public async Task<List<CustomerHistoryEvent>> GetCustomerHistoryAsync(string leadId, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerHistoryEvent>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT event_type,detail,created_at FROM audit_events WHERE lead_id=$lead ORDER BY created_at";
        command.Parameters.AddWithValue("$lead", leadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CustomerHistoryEvent
            {
                Type = reader.GetString(0), Detail = reader.GetString(1),
                CreatedAt = DateTimeOffset.TryParse(reader.GetString(2), out var at) ? at : DateTimeOffset.MinValue
            });
        return items;
    }

    public async Task<List<LeadAnalysisRunSnapshot>> GetLeadAnalysisHistoryAsync(string leadId, CancellationToken cancellationToken = default)
    {
        var items = new List<LeadAnalysisRunSnapshot>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT status,model,error,result_json,created_at FROM analysis_runs WHERE lead_id=$lead ORDER BY created_at";
        command.Parameters.AddWithValue("$lead", leadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new LeadAnalysisRunSnapshot
            {
                Status = reader.GetString(0), Model = reader.GetString(1), Error = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Result = reader.IsDBNull(3) ? null : Json.Deserialize<LeadAnalysis>(reader.GetString(3)),
                CreatedAt = DateTimeOffset.TryParse(reader.GetString(4), out var at) ? at : DateTimeOffset.MinValue
            });
        return items;
    }

    public async Task<int> GetNextCustomerReportVersionAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM customer_analysis_reports WHERE customer_id=$customer";
        command.Parameters.AddWithValue("$customer", customerId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task SaveCustomerAnalysisReportAsync(CustomerAnalysisReport report, CancellationToken cancellationToken = default)
    {
        report.UpdatedTime = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_analysis_reports(id,customer_id,ai_model,source_snapshot,report_json,created_time,version,export_history,status,error,updated_time)
            VALUES($id,$customer,$model,$snapshot,$report,$created,$version,$exports,$status,$error,$updated)
            ON CONFLICT(id) DO UPDATE SET ai_model=excluded.ai_model,source_snapshot=excluded.source_snapshot,report_json=excluded.report_json,
              export_history=excluded.export_history,status=excluded.status,error=excluded.error,updated_time=excluded.updated_time
            """;
        command.Parameters.AddWithValue("$id", report.Id); command.Parameters.AddWithValue("$customer", report.CustomerId);
        command.Parameters.AddWithValue("$model", report.AiModel); command.Parameters.AddWithValue("$snapshot", Json.Serialize(report.SourceSnapshot));
        command.Parameters.AddWithValue("$report", Json.Serialize(report.Report)); command.Parameters.AddWithValue("$created", report.CreatedTime.ToString("O"));
        command.Parameters.AddWithValue("$version", report.Version); command.Parameters.AddWithValue("$exports", Json.Serialize(report.ExportHistory));
        command.Parameters.AddWithValue("$status", report.Status.ToString()); command.Parameters.AddWithValue("$error", report.Error);
        command.Parameters.AddWithValue("$updated", report.UpdatedTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CustomerAnalysisReport?> GetCustomerAnalysisReportAsync(string reportId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,customer_id,ai_model,source_snapshot,report_json,created_time,version,export_history,status,error,updated_time FROM customer_analysis_reports WHERE id=$id";
        command.Parameters.AddWithValue("$id", reportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCustomerAnalysisReport(reader) : null;
    }

    public async Task<List<CustomerAnalysisReport>> GetCustomerAnalysisReportsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerAnalysisReport>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,customer_id,ai_model,source_snapshot,report_json,created_time,version,export_history,status,error,updated_time FROM customer_analysis_reports WHERE customer_id=$customer ORDER BY version DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadCustomerAnalysisReport(reader));
        return items;
    }

    private static CustomerAnalysisReport ReadCustomerAnalysisReport(SqliteDataReader reader)
    {
        var status = Enum.TryParse<CustomerReportStatus>(reader.GetString(8), out var parsedStatus) ? parsedStatus : CustomerReportStatus.RetryableFailed;
        return new CustomerAnalysisReport
        {
            Id = reader.GetString(0), CustomerId = reader.GetString(1), AiModel = reader.GetString(2),
            SourceSnapshot = Json.Deserialize<CustomerIntelligenceSourceSnapshot>(reader.GetString(3)) ?? new(),
            Report = Json.Deserialize<CustomerIntelligenceReportContent>(reader.GetString(4)) ?? new(),
            CreatedTime = DateTimeOffset.TryParse(reader.GetString(5), out var created) ? created : DateTimeOffset.MinValue,
            Version = reader.GetInt32(6), ExportHistory = Json.Deserialize<List<CustomerReportExportRecord>>(reader.GetString(7)) ?? [],
            Status = status, Error = reader.GetString(9), UpdatedTime = DateTimeOffset.TryParse(reader.GetString(10), out var updated) ? updated : DateTimeOffset.MinValue,
            CustomerName = (Json.Deserialize<CustomerIntelligenceSourceSnapshot>(reader.GetString(3))?.Lead.DisplayName) ?? ""
        };
    }

    public async Task SaveCustomerAnalysisStepAsync(CustomerAnalysisReportStep step, CancellationToken cancellationToken = default)
    {
        step.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_analysis_report_steps(id,report_id,step_key,sequence,status,result_json,error,created_at,updated_at)
            VALUES($id,$report,$key,$sequence,$status,$result,$error,$created,$updated)
            ON CONFLICT(report_id,step_key) DO UPDATE SET status=excluded.status,result_json=excluded.result_json,error=excluded.error,updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", step.Id); command.Parameters.AddWithValue("$report", step.ReportId);
        command.Parameters.AddWithValue("$key", step.StepKey); command.Parameters.AddWithValue("$sequence", step.Sequence);
        command.Parameters.AddWithValue("$status", step.Status.ToString()); command.Parameters.AddWithValue("$result", step.ResultJson);
        command.Parameters.AddWithValue("$error", step.Error); command.Parameters.AddWithValue("$created", step.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", step.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<CustomerAnalysisReportStep>> GetCustomerAnalysisStepsAsync(string reportId, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerAnalysisReportStep>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,report_id,step_key,sequence,status,result_json,error,created_at,updated_at FROM customer_analysis_report_steps WHERE report_id=$report ORDER BY sequence";
        command.Parameters.AddWithValue("$report", reportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CustomerAnalysisReportStep
            {
                Id = reader.GetString(0), ReportId = reader.GetString(1), StepKey = reader.GetString(2), Sequence = reader.GetInt32(3),
                Status = Enum.TryParse<CustomerReportStepStatus>(reader.GetString(4), out var status) ? status : CustomerReportStepStatus.RetryableFailed,
                ResultJson = reader.GetString(5), Error = reader.GetString(6),
                CreatedAt = DateTimeOffset.TryParse(reader.GetString(7), out var created) ? created : DateTimeOffset.MinValue,
                UpdatedAt = DateTimeOffset.TryParse(reader.GetString(8), out var updated) ? updated : DateTimeOffset.MinValue
            });
        return items;
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

    public async Task<CustomerIntelligenceProfile?> GetCustomerIntelligenceProfileAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_intelligence_profiles WHERE customer_id=$customer";
        command.Parameters.AddWithValue("$customer", customerId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return Json.Deserialize<CustomerIntelligenceProfile>(json);
    }

    public async Task SaveCustomerIntelligenceProfileAsync(CustomerIntelligenceProfile profile, CancellationToken cancellationToken = default)
    {
        profile.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_intelligence_profiles(id,customer_id,version,confidence,source_snapshot_hash,source_captured_at,updated_at,data_json)
            VALUES($id,$customer,$version,$confidence,$hash,$captured,$updated,$json)
            ON CONFLICT(customer_id) DO UPDATE SET id=excluded.id,version=excluded.version,confidence=excluded.confidence,
              source_snapshot_hash=excluded.source_snapshot_hash,source_captured_at=excluded.source_captured_at,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$customer", profile.CustomerId);
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$confidence", profile.Confidence);
        command.Parameters.AddWithValue("$hash", profile.SourceSnapshotHash);
        command.Parameters.AddWithValue("$captured", profile.SourceCapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(profile));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<AiRecommendationRecord>> GetAiRecommendationHistoryAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<AiRecommendationRecord>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM ai_recommendation_history WHERE customer_id=$customer ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<AiRecommendationRecord>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task SaveAiRecommendationAsync(AiRecommendationRecord recommendation, CancellationToken cancellationToken = default)
    {
        recommendation.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_recommendation_history(id,customer_id,recommendation_type,status,source_profile_id,source_profile_version,created_at,updated_at,data_json)
            VALUES($id,$customer,$type,$status,$profile,$version,$created,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", recommendation.Id);
        command.Parameters.AddWithValue("$customer", recommendation.CustomerId);
        command.Parameters.AddWithValue("$type", recommendation.RecommendationType);
        command.Parameters.AddWithValue("$status", recommendation.Status.ToString());
        command.Parameters.AddWithValue("$profile", recommendation.SourceProfileId);
        command.Parameters.AddWithValue("$version", recommendation.SourceProfileVersion);
        command.Parameters.AddWithValue("$created", recommendation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", recommendation.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(recommendation));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpsertCustomerBehaviorEventAsync(CustomerBehaviorEvent behaviorEvent, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_behavior_timeline(id,customer_id,channel,event_type,source_type,source_id,occurred_at,created_at,data_json)
            VALUES($id,$customer,$channel,$type,$sourceType,$source,$occurred,$created,$json)
            ON CONFLICT(customer_id,source_type,source_id,event_type) DO UPDATE SET
              channel=excluded.channel,occurred_at=excluded.occurred_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", behaviorEvent.Id);
        command.Parameters.AddWithValue("$customer", behaviorEvent.CustomerId);
        command.Parameters.AddWithValue("$channel", behaviorEvent.Channel);
        command.Parameters.AddWithValue("$type", behaviorEvent.EventType);
        command.Parameters.AddWithValue("$sourceType", behaviorEvent.SourceType);
        command.Parameters.AddWithValue("$source", behaviorEvent.SourceId);
        command.Parameters.AddWithValue("$occurred", behaviorEvent.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$created", behaviorEvent.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(behaviorEvent));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<List<CustomerBehaviorEvent>> GetCustomerBehaviorTimelineAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerBehaviorEvent>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_behavior_timeline WHERE customer_id=$customer ORDER BY occurred_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerBehaviorEvent>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task SaveSalesActionAsync(SalesActionRecord action, CancellationToken cancellationToken = default)
    {
        action.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO sales_action_logs(id,customer_id,recommendation_id,action_type,status,due_at,updated_at,data_json)
            VALUES($id,$customer,$recommendation,$type,$status,$due,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,due_at=excluded.due_at,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", action.Id);
        command.Parameters.AddWithValue("$customer", action.CustomerId);
        command.Parameters.AddWithValue("$recommendation", string.IsNullOrWhiteSpace(action.RecommendationId) ? DBNull.Value : action.RecommendationId);
        command.Parameters.AddWithValue("$type", action.ActionType);
        command.Parameters.AddWithValue("$status", action.Status.ToString());
        command.Parameters.AddWithValue("$due", action.DueAt is null ? DBNull.Value : action.DueAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$updated", action.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(action));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<SalesActionRecord>> GetSalesActionsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<SalesActionRecord>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM sales_action_logs WHERE customer_id=$customer ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<SalesActionRecord>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<List<SalesActionRecord>> GetAllSalesActionsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<SalesActionRecord>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM sales_action_logs ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<SalesActionRecord>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task SaveAiLearningFeedbackAsync(AiLearningFeedback feedback, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_learning_feedback(id,customer_id,recommendation_id,action_id,outcome,helpful,created_at,data_json)
            VALUES($id,$customer,$recommendation,$action,$outcome,$helpful,$created,$json)
            ON CONFLICT(id) DO UPDATE SET outcome=excluded.outcome,helpful=excluded.helpful,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", feedback.Id);
        command.Parameters.AddWithValue("$customer", feedback.CustomerId);
        command.Parameters.AddWithValue("$recommendation", string.IsNullOrWhiteSpace(feedback.RecommendationId) ? DBNull.Value : feedback.RecommendationId);
        command.Parameters.AddWithValue("$action", string.IsNullOrWhiteSpace(feedback.ActionId) ? DBNull.Value : feedback.ActionId);
        command.Parameters.AddWithValue("$outcome", feedback.Outcome);
        command.Parameters.AddWithValue("$helpful", feedback.Helpful ? 1 : 0);
        command.Parameters.AddWithValue("$created", feedback.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(feedback));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<AiLearningFeedback>> GetAiLearningFeedbackAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<AiLearningFeedback>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM ai_learning_feedback WHERE customer_id=$customer ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<AiLearningFeedback>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<List<AiLearningFeedback>> GetAllAiLearningFeedbackAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<AiLearningFeedback>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM ai_learning_feedback ORDER BY created_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<AiLearningFeedback>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task SaveCustomerBrainRunAsync(CustomerBrainRun run, CancellationToken cancellationToken = default)
    {
        run.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_brain_runs(id,customer_id,status,ai_model,source_snapshot_hash,created_at,updated_at,completed_at,data_json)
            VALUES($id,$customer,$status,$model,$hash,$created,$updated,$completed,$json)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,ai_model=excluded.ai_model,
              source_snapshot_hash=excluded.source_snapshot_hash,updated_at=excluded.updated_at,
              completed_at=excluded.completed_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$customer", run.CustomerId);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$model", run.AiModel);
        command.Parameters.AddWithValue("$hash", run.SourceSnapshotHash);
        command.Parameters.AddWithValue("$created", run.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", run.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed", run.CompletedAt is null ? DBNull.Value : run.CompletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(run));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<CustomerBrainRun>> GetCustomerBrainRunsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerBrainRun>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_brain_runs WHERE customer_id=$customer ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerBrainRun>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertFollowUpTaskAsync(FollowUpTask task, CancellationToken cancellationToken = default)
    {
        task.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO follow_up_tasks(id,customer_id,recommendation_id,status,priority,due_at,source_type,source_id,updated_at,data_json)
            VALUES($id,$customer,$recommendation,$status,$priority,$due,$sourceType,$source,$updated,$json)
            ON CONFLICT(customer_id,source_type,source_id) DO UPDATE SET
              recommendation_id=excluded.recommendation_id,status=excluded.status,priority=excluded.priority,
              due_at=excluded.due_at,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$customer", task.CustomerId);
        command.Parameters.AddWithValue("$recommendation", string.IsNullOrWhiteSpace(task.RecommendationId) ? DBNull.Value : task.RecommendationId);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$priority", task.Priority.ToString());
        command.Parameters.AddWithValue("$due", task.DueAt.ToString("O"));
        command.Parameters.AddWithValue("$sourceType", task.SourceType);
        command.Parameters.AddWithValue("$source", task.SourceId);
        command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(task));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<FollowUpTask>> GetFollowUpTasksAsync(string? customerId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<FollowUpTask>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = customerId is null
            ? "SELECT data_json FROM follow_up_tasks ORDER BY due_at, updated_at DESC"
            : "SELECT data_json FROM follow_up_tasks WHERE customer_id=$customer ORDER BY due_at, updated_at DESC";
        if (customerId is not null) command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<FollowUpTask>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<bool> UpsertCustomerEventAsync(CustomerEventLogEntry customerEvent, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_event_log(id,customer_id,event_type,source_type,source_id,occurred_at,created_at,data_json)
            VALUES($id,$customer,$type,$sourceType,$source,$occurred,$created,$json)
            ON CONFLICT(customer_id,event_type,source_type,source_id) DO UPDATE SET
              occurred_at=excluded.occurred_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", customerEvent.Id);
        command.Parameters.AddWithValue("$customer", customerEvent.CustomerId);
        command.Parameters.AddWithValue("$type", customerEvent.EventType);
        command.Parameters.AddWithValue("$sourceType", customerEvent.SourceType);
        command.Parameters.AddWithValue("$source", customerEvent.SourceId);
        command.Parameters.AddWithValue("$occurred", customerEvent.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$created", customerEvent.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(customerEvent));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<List<CustomerEventLogEntry>> GetCustomerEventsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerEventLogEntry>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_event_log WHERE customer_id=$customer ORDER BY occurred_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEventLogEntry>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task LogEventAsync(string eventType, string? leadId, string? draftId, string detail = "", CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO audit_events(event_type,lead_id,draft_id,detail,created_at) VALUES($type,$lead,$draft,$detail,$at)";
        command.Parameters.AddWithValue("$type", eventType); command.Parameters.AddWithValue("$lead", (object?)leadId ?? DBNull.Value); command.Parameters.AddWithValue("$draft", (object?)draftId ?? DBNull.Value); command.Parameters.AddWithValue("$detail", detail); command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GlobalCustomerIdentity?> GetGlobalCustomerIdentityAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM global_customer_identities WHERE customer_id=$customer";
        command.Parameters.AddWithValue("$customer", customerId);
        return Json.Deserialize<GlobalCustomerIdentity>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertGlobalCustomerIdentityAsync(GlobalCustomerIdentity identity, CancellationToken cancellationToken = default)
    {
        identity.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO global_customer_identities(customer_id,canonical_name,primary_account_id,updated_at,data_json)
            VALUES($customer,$name,$primary,$updated,$json)
            ON CONFLICT(customer_id) DO UPDATE SET canonical_name=excluded.canonical_name,
              primary_account_id=excluded.primary_account_id,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$customer", identity.CustomerId);
        command.Parameters.AddWithValue("$name", identity.CanonicalName);
        command.Parameters.AddWithValue("$primary", identity.PrimaryAccountId);
        command.Parameters.AddWithValue("$updated", identity.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(identity));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<CustomerPhoneIdentity>> GetCustomerPhoneIdentitiesAsync(string? customerId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<CustomerPhoneIdentity>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = customerId is null
            ? "SELECT data_json FROM customer_phone_identities ORDER BY updated_at DESC"
            : "SELECT data_json FROM customer_phone_identities WHERE customer_id=$customer ORDER BY updated_at DESC";
        if (customerId is not null) command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerPhoneIdentity>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertCustomerPhoneIdentityAsync(CustomerPhoneIdentity identity, CancellationToken cancellationToken = default)
    {
        identity.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_phone_identities(id,customer_id,digits,e164,jid,lid,source_account_id,manually_confirmed,confidence,updated_at,data_json)
            VALUES($id,$customer,$digits,$e164,$jid,$lid,$account,$confirmed,$confidence,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET customer_id=excluded.customer_id,digits=excluded.digits,e164=excluded.e164,
              jid=excluded.jid,lid=excluded.lid,source_account_id=excluded.source_account_id,manually_confirmed=excluded.manually_confirmed,
              confidence=excluded.confidence,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", identity.Id);
        command.Parameters.AddWithValue("$customer", identity.CustomerId);
        command.Parameters.AddWithValue("$digits", identity.Digits);
        command.Parameters.AddWithValue("$e164", identity.E164);
        command.Parameters.AddWithValue("$jid", identity.Jid);
        command.Parameters.AddWithValue("$lid", identity.Lid);
        command.Parameters.AddWithValue("$account", identity.SourceAccountId);
        command.Parameters.AddWithValue("$confirmed", identity.ManuallyConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$confidence", identity.Confidence);
        command.Parameters.AddWithValue("$updated", identity.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(identity));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WhatsAppIdentityLink?> GetWhatsAppIdentityLinkAsync(string accountId, string conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_identity_links WHERE account_id=$account AND conversation_id=$conversation";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        return Json.Deserialize<WhatsAppIdentityLink>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<WhatsAppIdentityLink>> GetWhatsAppIdentityLinksAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppIdentityLink>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM whatsapp_identity_links WHERE customer_id=$customer AND is_active=1 ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<WhatsAppIdentityLink>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertWhatsAppIdentityLinkAsync(WhatsAppIdentityLink link, CancellationToken cancellationToken = default)
    {
        link.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO whatsapp_identity_links(id,customer_id,account_id,conversation_id,contact_jid,match_result,is_active,updated_at,data_json)
            VALUES($id,$customer,$account,$conversation,$jid,$result,$active,$updated,$json)
            ON CONFLICT(account_id,conversation_id) DO UPDATE SET id=excluded.id,customer_id=excluded.customer_id,
              contact_jid=excluded.contact_jid,match_result=excluded.match_result,is_active=excluded.is_active,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", link.Id);
        command.Parameters.AddWithValue("$customer", link.CustomerId);
        command.Parameters.AddWithValue("$account", link.AccountId);
        command.Parameters.AddWithValue("$conversation", link.ConversationId);
        command.Parameters.AddWithValue("$jid", link.ContactJid);
        command.Parameters.AddWithValue("$result", link.MatchResult.ToString());
        command.Parameters.AddWithValue("$active", link.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$updated", link.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(link));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveIdentityMatchLogAsync(CustomerIdentityMatchLog log, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_identity_match_logs(id,customer_id,account_id,conversation_id,result,created_at,data_json)
            VALUES($id,$customer,$account,$conversation,$result,$created,$json)
            """;
        command.Parameters.AddWithValue("$id", log.Id);
        command.Parameters.AddWithValue("$customer", log.CustomerId);
        command.Parameters.AddWithValue("$account", log.AccountId);
        command.Parameters.AddWithValue("$conversation", log.ConversationId);
        command.Parameters.AddWithValue("$result", log.Result.ToString());
        command.Parameters.AddWithValue("$created", log.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(log));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccountPersona?> GetAccountPersonaAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM account_personas WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", accountId);
        return Json.Deserialize<AccountPersona>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertAccountPersonaAsync(AccountPersona persona, CancellationToken cancellationToken = default)
    {
        persona.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO account_personas(account_id,updated_at,data_json) VALUES($account,$updated,$json)
            ON CONFLICT(account_id) DO UPDATE SET updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$account", persona.AccountId);
        command.Parameters.AddWithValue("$updated", persona.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(persona));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccountRelationshipMemory?> GetAccountRelationshipMemoryAsync(string customerId, string accountId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM account_relationship_memories WHERE customer_id=$customer AND account_id=$account";
        command.Parameters.AddWithValue("$customer", customerId);
        command.Parameters.AddWithValue("$account", accountId);
        return Json.Deserialize<AccountRelationshipMemory>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertAccountRelationshipMemoryAsync(AccountRelationshipMemory memory, CancellationToken cancellationToken = default)
    {
        memory.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO account_relationship_memories(id,customer_id,account_id,updated_at,data_json)
            VALUES($id,$customer,$account,$updated,$json)
            ON CONFLICT(customer_id,account_id) DO UPDATE SET updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", memory.Id);
        command.Parameters.AddWithValue("$customer", memory.CustomerId);
        command.Parameters.AddWithValue("$account", memory.AccountId);
        command.Parameters.AddWithValue("$updated", memory.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(memory));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConversationAgentState?> GetConversationAgentStateAsync(string accountId, string conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM conversation_agent_states WHERE account_id=$account AND conversation_id=$conversation";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        return Json.Deserialize<ConversationAgentState>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<ConversationAgentState>> GetCustomerAgentStatesAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<ConversationAgentState>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM conversation_agent_states WHERE customer_id=$customer ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<ConversationAgentState>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<List<ConversationAgentState>> GetAgentStatesAsync(
        ConversationAgentMode? mode = null, CancellationToken cancellationToken = default)
    {
        var items = new List<ConversationAgentState>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = mode is null
            ? "SELECT data_json FROM conversation_agent_states ORDER BY updated_at DESC"
            : "SELECT data_json FROM conversation_agent_states WHERE mode=$mode ORDER BY updated_at DESC";
        if (mode is not null) command.Parameters.AddWithValue("$mode", mode.Value.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<ConversationAgentState>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertConversationAgentStateAsync(ConversationAgentState state, CancellationToken cancellationToken = default)
    {
        state.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_agent_states(id,customer_id,account_id,conversation_id,mode,updated_at,data_json)
            VALUES($id,$customer,$account,$conversation,$mode,$updated,$json)
            ON CONFLICT(account_id,conversation_id) DO UPDATE SET customer_id=excluded.customer_id,
              mode=excluded.mode,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", state.Id);
        command.Parameters.AddWithValue("$customer", state.CustomerId);
        command.Parameters.AddWithValue("$account", state.AccountId);
        command.Parameters.AddWithValue("$conversation", state.ConversationId);
        command.Parameters.AddWithValue("$mode", state.Mode.ToString());
        command.Parameters.AddWithValue("$updated", state.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(state));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GlobalCustomerAgentLock?> GetGlobalCustomerAgentLockAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM global_customer_agent_locks WHERE customer_id=$customer";
        command.Parameters.AddWithValue("$customer", customerId);
        return Json.Deserialize<GlobalCustomerAgentLock>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<bool> TryAcquireGlobalCustomerAgentLockAsync(GlobalCustomerAgentLock agentLock, CancellationToken cancellationToken = default)
    {
        agentLock.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO global_customer_agent_locks(customer_id,active_account_id,account_id,conversation_id,updated_at,data_json)
            VALUES($customer,$account,$account,$conversation,$updated,$json)
            ON CONFLICT(customer_id) DO UPDATE SET
              account_id=excluded.account_id,conversation_id=excluded.conversation_id,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            WHERE global_customer_agent_locks.account_id=excluded.account_id
              AND global_customer_agent_locks.conversation_id=excluded.conversation_id
            """;
        command.Parameters.AddWithValue("$customer", agentLock.CustomerId);
        command.Parameters.AddWithValue("$account", agentLock.ActiveAccountId);
        command.Parameters.AddWithValue("$conversation", agentLock.ActiveConversationId);
        command.Parameters.AddWithValue("$updated", agentLock.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(agentLock));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task SwitchGlobalCustomerAgentLockAsync(GlobalCustomerAgentLock agentLock, CancellationToken cancellationToken = default)
    {
        agentLock.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO global_customer_agent_locks(customer_id,active_account_id,account_id,conversation_id,updated_at,data_json)
            VALUES($customer,$account,$account,$conversation,$updated,$json)
            ON CONFLICT(customer_id) DO UPDATE SET account_id=excluded.account_id,conversation_id=excluded.conversation_id,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$customer", agentLock.CustomerId);
        command.Parameters.AddWithValue("$account", agentLock.ActiveAccountId);
        command.Parameters.AddWithValue("$conversation", agentLock.ActiveConversationId);
        command.Parameters.AddWithValue("$updated", agentLock.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(agentLock));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseGlobalCustomerAgentLockAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM global_customer_agent_locks WHERE customer_id=$customer";
        command.Parameters.AddWithValue("$customer", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RelationshipMemory?> GetRelationshipMemoryAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM relationship_memories WHERE customer_id=$customer";
        command.Parameters.AddWithValue("$customer", customerId);
        return Json.Deserialize<RelationshipMemory>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertRelationshipMemoryAsync(RelationshipMemory memory, CancellationToken cancellationToken = default)
    {
        memory.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO relationship_memories(customer_id,updated_at,data_json) VALUES($customer,$updated,$json)
            ON CONFLICT(customer_id) DO UPDATE SET updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$customer", memory.CustomerId);
        command.Parameters.AddWithValue("$updated", memory.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(memory));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SourcingRequest?> GetLatestSourcingRequestAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM sourcing_requests WHERE customer_id=$customer ORDER BY version DESC,updated_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$customer", customerId);
        return Json.Deserialize<SourcingRequest>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<SourcingRequest>> GetLatestSourcingRequestsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<SourcingRequest>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT data_json FROM sourcing_requests source
            WHERE NOT EXISTS (
              SELECT 1 FROM sourcing_requests newer
              WHERE newer.customer_id=source.customer_id
                AND (newer.version>source.version OR
                  (newer.version=source.version AND newer.updated_at>source.updated_at)))
            ORDER BY updated_at DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<SourcingRequest>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertSourcingRequestAsync(SourcingRequest request, CancellationToken cancellationToken = default)
    {
        request.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO sourcing_requests(id,customer_id,version,status,updated_at,data_json)
            VALUES($id,$customer,$version,$status,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET version=excluded.version,status=excluded.status,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$customer", request.CustomerId);
        command.Parameters.AddWithValue("$version", request.Version);
        command.Parameters.AddWithValue("$status", request.Status.ToString());
        command.Parameters.AddWithValue("$updated", request.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(request));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HumanHandoffEvent?> GetOpenHumanHandoffAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM human_handoff_events WHERE customer_id=$customer AND status IN ('Open','TakenOver') ORDER BY updated_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$customer", customerId);
        return Json.Deserialize<HumanHandoffEvent>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<HumanHandoffEvent?> GetLatestHumanHandoffAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM human_handoff_events WHERE customer_id=$customer ORDER BY updated_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$customer", customerId);
        return Json.Deserialize<HumanHandoffEvent>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<HumanHandoffEvent>> GetOpenHumanHandoffsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<HumanHandoffEvent>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM human_handoff_events WHERE status IN ('Open','TakenOver') ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<HumanHandoffEvent>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertHumanHandoffAsync(HumanHandoffEvent handoff, CancellationToken cancellationToken = default)
    {
        handoff.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO human_handoff_events(id,customer_id,account_id,conversation_id,status,updated_at,data_json)
            VALUES($id,$customer,$account,$conversation,$status,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", handoff.Id);
        command.Parameters.AddWithValue("$customer", handoff.CustomerId);
        command.Parameters.AddWithValue("$account", handoff.AccountId);
        command.Parameters.AddWithValue("$conversation", handoff.ConversationId);
        command.Parameters.AddWithValue("$status", handoff.Status.ToString());
        command.Parameters.AddWithValue("$updated", handoff.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(handoff));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<PendingQuestion>> GetPendingQuestionsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var items = new List<PendingQuestion>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM pending_questions WHERE customer_id=$customer AND is_resolved=0 ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<PendingQuestion>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertPendingQuestionAsync(PendingQuestion question, CancellationToken cancellationToken = default)
    {
        question.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_questions(id,customer_id,account_id,safety,is_resolved,updated_at,data_json)
            VALUES($id,$customer,$account,$safety,$resolved,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET safety=excluded.safety,is_resolved=excluded.is_resolved,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", question.Id);
        command.Parameters.AddWithValue("$customer", question.CustomerId);
        command.Parameters.AddWithValue("$account", question.AccountId);
        command.Parameters.AddWithValue("$safety", question.Safety.ToString());
        command.Parameters.AddWithValue("$resolved", question.IsResolved ? 1 : 0);
        command.Parameters.AddWithValue("$updated", question.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(question));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCustomerMergeAuditAsync(CustomerMergeAudit audit, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_merge_audits(id,source_customer_id,target_customer_id,identity_link_id,action,created_at,data_json)
            VALUES($id,$source,$target,$link,$action,$created,$json)
            """;
        command.Parameters.AddWithValue("$id", audit.Id);
        command.Parameters.AddWithValue("$source", audit.SourceCustomerId);
        command.Parameters.AddWithValue("$target", audit.TargetCustomerId);
        command.Parameters.AddWithValue("$link", audit.IdentityLinkId);
        command.Parameters.AddWithValue("$action", audit.Action);
        command.Parameters.AddWithValue("$created", audit.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(audit));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAgentTurnLogAsync(AgentTurnLog log, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_turn_logs(id,customer_id,account_id,conversation_id,source_message_id,created_at,data_json)
            VALUES($id,$customer,$account,$conversation,$message,$created,$json)
            """;
        command.Parameters.AddWithValue("$id", log.Id);
        command.Parameters.AddWithValue("$customer", log.CustomerId);
        command.Parameters.AddWithValue("$account", log.AccountId);
        command.Parameters.AddWithValue("$conversation", log.ConversationId);
        command.Parameters.AddWithValue("$message", log.SourceMessageId);
        command.Parameters.AddWithValue("$created", log.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(log));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<AgentTurnLog>> GetAgentTurnLogsAsync(string customerId, int limit = 100, CancellationToken cancellationToken = default)
    {
        var items = new List<AgentTurnLog>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM agent_turn_logs WHERE customer_id=$customer ORDER BY created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$customer", customerId);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<AgentTurnLog>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<List<WhatsAppMessage>> GetWhatsAppMessagesForCustomerAsync(string customerId, int limit = 500, CancellationToken cancellationToken = default)
    {
        var items = new List<WhatsAppMessage>();
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT m.data_json
            FROM whatsapp_messages m
            LEFT JOIN whatsapp_identity_links l
              ON l.account_id=m.account_id AND l.conversation_id=m.conversation_id AND l.is_active=1
            WHERE m.lead_id=$customer OR l.customer_id=$customer
            ORDER BY m.timestamp DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$customer", customerId);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<WhatsAppMessage>(reader.GetString(0)) is { } item && seen.Add(item.Id)) items.Add(item);
        items.Reverse();
        return items;
    }

    public async Task SaveImportSummaryAsync(string fileName, int total, int created, int updated, int invalid, CancellationToken cancellationToken = default)
    {
        await using var db = Open(); await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO import_jobs(id,file_name,status,total_rows,created,updated,invalid_phones,created_at) VALUES($id,$file,'completed',$total,$created,$updated,$invalid,$at)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$file", fileName); command.Parameters.AddWithValue("$total", total); command.Parameters.AddWithValue("$created", created); command.Parameters.AddWithValue("$updated", updated); command.Parameters.AddWithValue("$invalid", invalid); command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
