using Microsoft.Data.Sqlite;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Core.Infrastructure;

public sealed partial class LocalRepository
{
    private const int CustomerEnrichmentSchemaVersion = 1;

    private static async Task InitializeCustomerEnrichmentSchemaAsync(
        SqliteConnection db,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS customer_enrichment_jobs (
                  id TEXT PRIMARY KEY,
                  customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                  trigger_type TEXT NOT NULL,
                  status TEXT NOT NULL,
                  provider TEXT NOT NULL,
                  started_at TEXT,
                  completed_at TEXT,
                  failed_at TEXT,
                  error_code TEXT NOT NULL,
                  error_message TEXT NOT NULL,
                  queries_count INTEGER NOT NULL,
                  sources_count INTEGER NOT NULL,
                  facts_count INTEGER NOT NULL,
                  cost_usd TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  data_json TEXT NOT NULL,
                  UNIQUE(id, customer_id)
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_jobs_customer
                  ON customer_enrichment_jobs(customer_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_jobs_queue
                  ON customer_enrichment_jobs(status, updated_at);

                CREATE TABLE IF NOT EXISTS customer_enrichment_queries (
                  id TEXT PRIMARY KEY,
                  job_id TEXT NOT NULL,
                  customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                  query_hash TEXT NOT NULL,
                  query_text TEXT NOT NULL,
                  provider TEXT NOT NULL,
                  status TEXT NOT NULL,
                  results_count INTEGER NOT NULL,
                  created_at TEXT NOT NULL,
                  retrieved_at TEXT,
                  data_json TEXT NOT NULL,
                  FOREIGN KEY(job_id, customer_id) REFERENCES customer_enrichment_jobs(id, customer_id) ON DELETE CASCADE,
                  UNIQUE(id, job_id, customer_id),
                  UNIQUE(job_id, query_hash)
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_queries_cache
                  ON customer_enrichment_queries(customer_id, query_hash, retrieved_at DESC);

                CREATE TABLE IF NOT EXISTS customer_enrichment_sources (
                  id TEXT PRIMARY KEY,
                  job_id TEXT NOT NULL,
                  query_id TEXT NOT NULL,
                  customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                  url TEXT NOT NULL,
                  canonical_url TEXT NOT NULL,
                  title TEXT NOT NULL,
                  domain TEXT NOT NULL,
                  snippet TEXT NOT NULL,
                  content_text TEXT NOT NULL,
                  content_hash TEXT NOT NULL,
                  published_at TEXT,
                  retrieved_at TEXT NOT NULL,
                  provider TEXT NOT NULL,
                  identity_match_score INTEGER NOT NULL,
                  identity_match_status TEXT NOT NULL,
                  data_json TEXT NOT NULL,
                  FOREIGN KEY(job_id, customer_id) REFERENCES customer_enrichment_jobs(id, customer_id) ON DELETE CASCADE,
                  FOREIGN KEY(query_id, job_id, customer_id) REFERENCES customer_enrichment_queries(id, job_id, customer_id) ON DELETE CASCADE,
                  UNIQUE(id, job_id, customer_id),
                  UNIQUE(job_id, canonical_url)
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_sources_customer
                  ON customer_enrichment_sources(customer_id, retrieved_at DESC);
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_sources_hash
                  ON customer_enrichment_sources(content_hash) WHERE content_hash <> '';

                CREATE TABLE IF NOT EXISTS customer_enrichment_facts (
                  id TEXT PRIMARY KEY,
                  customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                  job_id TEXT NOT NULL,
                  field_type TEXT NOT NULL,
                  field_value TEXT NOT NULL,
                  normalized_value TEXT NOT NULL,
                  confidence_score INTEGER NOT NULL,
                  verification_status TEXT NOT NULL,
                  source_count INTEGER NOT NULL,
                  first_discovered_at TEXT NOT NULL,
                  last_verified_at TEXT,
                  expires_at TEXT,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  data_json TEXT NOT NULL,
                  FOREIGN KEY(job_id, customer_id) REFERENCES customer_enrichment_jobs(id, customer_id) ON DELETE CASCADE,
                  UNIQUE(id, job_id, customer_id),
                  UNIQUE(job_id, field_type, normalized_value)
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_facts_customer
                  ON customer_enrichment_facts(customer_id, verification_status, updated_at DESC);

                CREATE TABLE IF NOT EXISTS customer_enrichment_fact_sources (
                  fact_id TEXT NOT NULL,
                  source_id TEXT NOT NULL,
                  job_id TEXT NOT NULL,
                  customer_id TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  PRIMARY KEY(fact_id, source_id),
                  FOREIGN KEY(fact_id, job_id, customer_id) REFERENCES customer_enrichment_facts(id, job_id, customer_id) ON DELETE CASCADE,
                  FOREIGN KEY(source_id, job_id, customer_id) REFERENCES customer_enrichment_sources(id, job_id, customer_id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_fact_sources_source
                  ON customer_enrichment_fact_sources(source_id, fact_id);

                CREATE TABLE IF NOT EXISTS customer_enrichment_reviews (
                  id TEXT PRIMARY KEY,
                  customer_id TEXT NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                  job_id TEXT NOT NULL,
                  fact_id TEXT NOT NULL,
                  action TEXT NOT NULL,
                  actor TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  data_json TEXT NOT NULL,
                  FOREIGN KEY(job_id, customer_id) REFERENCES customer_enrichment_jobs(id, customer_id) ON DELETE CASCADE,
                  FOREIGN KEY(fact_id, job_id, customer_id) REFERENCES customer_enrichment_facts(id, job_id, customer_id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_reviews_fact
                  ON customer_enrichment_reviews(fact_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS customer_enrichment_provider_usage (
                  id TEXT PRIMARY KEY,
                  provider TEXT NOT NULL,
                  job_id TEXT REFERENCES customer_enrichment_jobs(id) ON DELETE SET NULL,
                  request_day TEXT NOT NULL,
                  request_month TEXT NOT NULL,
                  requests INTEGER NOT NULL,
                  estimated_cost_usd TEXT NOT NULL,
                  succeeded INTEGER NOT NULL,
                  error_code TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  data_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_customer_enrichment_usage_month
                  ON customer_enrichment_provider_usage(request_month, provider, created_at DESC);

                CREATE TABLE IF NOT EXISTS customer_enrichment_settings (
                  id TEXT PRIMARY KEY,
                  schema_version INTEGER NOT NULL,
                  updated_at TEXT NOT NULL,
                  data_json TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using (var marker = db.CreateCommand())
            {
                marker.Transaction = transaction;
                marker.CommandText = """
                    INSERT INTO customer_enrichment_settings(id,schema_version,updated_at,data_json)
                    VALUES('default',$version,$updated,$json)
                    ON CONFLICT(id) DO UPDATE SET
                      schema_version=MAX(schema_version,excluded.schema_version)
                    """;
                marker.Parameters.AddWithValue("$version", CustomerEnrichmentSchemaVersion);
                marker.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
                marker.Parameters.AddWithValue("$json", Json.Serialize(new CustomerEnrichmentSettings()));
                await marker.ExecuteNonQueryAsync(cancellationToken);
            }

            var expected = new[]
            {
                "customer_enrichment_jobs",
                "customer_enrichment_queries",
                "customer_enrichment_sources",
                "customer_enrichment_facts",
                "customer_enrichment_fact_sources",
                "customer_enrichment_reviews",
                "customer_enrichment_provider_usage",
                "customer_enrichment_settings"
            };
            await using (var verify = db.CreateCommand())
            {
                verify.Transaction = transaction;
                verify.CommandText = $"SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ({string.Join(',', expected.Select((_, index) => $"$t{index}"))})";
                for (var index = 0; index < expected.Length; index++)
                    verify.Parameters.AddWithValue($"$t{index}", expected[index]);
                var count = Convert.ToInt32(await verify.ExecuteScalarAsync(cancellationToken));
                if (count != expected.Length)
                    throw new InvalidDataException("客户外部调查数据库迁移未创建全部数据表。");
            }
            await using (var foreignKeys = db.CreateCommand())
            {
                foreignKeys.Transaction = transaction;
                foreignKeys.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                    throw new InvalidDataException("客户外部调查数据库迁移产生了外键完整性错误。");
            }
            await ValidateCustomerEnrichmentSchemaContractAsync(db, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ValidateCustomerEnrichmentSchemaContractAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var contracts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer_enrichment_jobs"] = ["id", "customer_id", "trigger_type", "status", "provider", "data_json"],
            ["customer_enrichment_queries"] = ["id", "job_id", "customer_id", "query_hash", "query_text", "data_json"],
            ["customer_enrichment_sources"] = ["id", "job_id", "query_id", "customer_id", "canonical_url", "content_hash", "data_json"],
            ["customer_enrichment_facts"] = ["id", "customer_id", "job_id", "field_type", "normalized_value", "verification_status", "data_json"],
            ["customer_enrichment_fact_sources"] = ["fact_id", "source_id", "job_id", "customer_id", "created_at"],
            ["customer_enrichment_reviews"] = ["id", "customer_id", "job_id", "fact_id", "action", "data_json"],
            ["customer_enrichment_provider_usage"] = ["id", "provider", "request_day", "request_month", "estimated_cost_usd", "data_json"],
            ["customer_enrichment_settings"] = ["id", "schema_version", "updated_at", "data_json"]
        };
        foreach (var contract in contracts)
        {
            await using var inspect = db.CreateCommand();
            inspect.Transaction = transaction;
            inspect.CommandText = $"PRAGMA table_info(\"{contract.Key}\")";
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) actual.Add(reader.GetString(1));
            var missing = contract.Value.Where(column => !actual.Contains(column)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"客户外部调查表 {contract.Key} 缺少列：{string.Join(',', missing)}。");
        }

        var foreignKeyContracts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer_enrichment_jobs"] = ["customer_id|leads|id|CASCADE"],
            ["customer_enrichment_queries"] =
            [
                "customer_id|leads|id|CASCADE", "job_id|customer_enrichment_jobs|id|CASCADE",
                "customer_id|customer_enrichment_jobs|customer_id|CASCADE"
            ],
            ["customer_enrichment_sources"] =
            [
                "customer_id|leads|id|CASCADE", "job_id|customer_enrichment_jobs|id|CASCADE",
                "customer_id|customer_enrichment_jobs|customer_id|CASCADE",
                "query_id|customer_enrichment_queries|id|CASCADE",
                "job_id|customer_enrichment_queries|job_id|CASCADE",
                "customer_id|customer_enrichment_queries|customer_id|CASCADE"
            ],
            ["customer_enrichment_facts"] =
            [
                "customer_id|leads|id|CASCADE", "job_id|customer_enrichment_jobs|id|CASCADE",
                "customer_id|customer_enrichment_jobs|customer_id|CASCADE"
            ],
            ["customer_enrichment_fact_sources"] =
            [
                "fact_id|customer_enrichment_facts|id|CASCADE",
                "job_id|customer_enrichment_facts|job_id|CASCADE",
                "customer_id|customer_enrichment_facts|customer_id|CASCADE",
                "source_id|customer_enrichment_sources|id|CASCADE",
                "job_id|customer_enrichment_sources|job_id|CASCADE",
                "customer_id|customer_enrichment_sources|customer_id|CASCADE"
            ],
            ["customer_enrichment_reviews"] =
            [
                "customer_id|leads|id|CASCADE", "job_id|customer_enrichment_jobs|id|CASCADE",
                "customer_id|customer_enrichment_jobs|customer_id|CASCADE",
                "fact_id|customer_enrichment_facts|id|CASCADE",
                "job_id|customer_enrichment_facts|job_id|CASCADE",
                "customer_id|customer_enrichment_facts|customer_id|CASCADE"
            ],
            ["customer_enrichment_provider_usage"] = ["job_id|customer_enrichment_jobs|id|SET NULL"]
        };
        foreach (var contract in foreignKeyContracts)
        {
            await using var inspect = db.CreateCommand();
            inspect.Transaction = transaction;
            inspect.CommandText = $"PRAGMA foreign_key_list(\"{contract.Key}\")";
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                actual.Add($"{reader.GetString(3)}|{reader.GetString(2)}|{reader.GetString(4)}|{reader.GetString(6)}");
            var missing = contract.Value.Where(item => !actual.Contains(item)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"客户外部调查表 {contract.Key} 缺少目标明确的外键：{string.Join(',', missing)}。");
        }

        var requiredIndexes = new[]
        {
            "ix_customer_enrichment_jobs_customer",
            "ix_customer_enrichment_jobs_queue",
            "ix_customer_enrichment_queries_cache",
            "ix_customer_enrichment_sources_customer",
            "ix_customer_enrichment_sources_hash",
            "ix_customer_enrichment_facts_customer",
            "ix_customer_enrichment_fact_sources_source",
            "ix_customer_enrichment_reviews_fact",
            "ix_customer_enrichment_usage_month"
        };
        await using var indexes = db.CreateCommand();
        indexes.Transaction = transaction;
        indexes.CommandText = $"SELECT name FROM sqlite_schema WHERE type='index' AND name IN ({string.Join(',', requiredIndexes.Select((_, index) => $"$i{index}"))})";
        for (var index = 0; index < requiredIndexes.Length; index++)
            indexes.Parameters.AddWithValue($"$i{index}", requiredIndexes[index]);
        var actualIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await indexes.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) actualIndexes.Add(reader.GetString(0));
        var missingIndexes = requiredIndexes.Where(index => !actualIndexes.Contains(index)).ToArray();
        if (missingIndexes.Length > 0)
            throw new InvalidDataException($"客户外部调查数据库缺少索引：{string.Join(',', missingIndexes)}。");
    }

    public async Task<CustomerEnrichmentSettings> GetCustomerEnrichmentSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_settings WHERE id='default'";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json && Json.Deserialize<CustomerEnrichmentSettings>(json) is { } settings
            ? settings
            : new CustomerEnrichmentSettings();
    }

    public async Task SaveCustomerEnrichmentSettingsAsync(
        CustomerEnrichmentSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_enrichment_settings(id,schema_version,updated_at,data_json)
            VALUES('default',$version,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET
              schema_version=excluded.schema_version,
              updated_at=excluded.updated_at,
              data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$version", CustomerEnrichmentSchemaVersion);
        command.Parameters.AddWithValue("$updated", settings.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(settings));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PruneCustomerEnrichmentDataAsync(
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow().AddDays(-Math.Clamp(retentionDays, 30, 3650)).ToString("O");
        var currentLocalMonth = _timeProvider.GetLocalNow().ToString("yyyy-MM");
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        await using var jobs = db.CreateCommand();
        jobs.Transaction = transaction;
        jobs.CommandText = """
            DELETE FROM customer_enrichment_jobs
            WHERE created_at < $cutoff AND status NOT IN ('Queued','Running')
            """;
        jobs.Parameters.AddWithValue("$cutoff", cutoff);
        var removedJobs = await jobs.ExecuteNonQueryAsync(cancellationToken);
        await using var usage = db.CreateCommand();
        usage.Transaction = transaction;
        // The local usage estimate follows the user's calendar month. Never
        // prune that month's ledger even when retention overlaps a month edge.
        usage.CommandText = "DELETE FROM customer_enrichment_provider_usage WHERE created_at < $cutoff AND request_month < $currentMonth";
        usage.Parameters.AddWithValue("$cutoff", cutoff);
        usage.Parameters.AddWithValue("$currentMonth", currentLocalMonth);
        await usage.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removedJobs;
    }

    public async Task SaveCustomerEnrichmentJobAsync(
        CustomerEnrichmentJob job,
        CancellationToken cancellationToken = default)
    {
        job.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_enrichment_jobs(
              id,customer_id,trigger_type,status,provider,started_at,completed_at,failed_at,
              error_code,error_message,queries_count,sources_count,facts_count,cost_usd,
              created_at,updated_at,data_json)
            VALUES($id,$customer,$trigger,$status,$provider,$started,$completed,$failed,
              $errorCode,$errorMessage,$queries,$sources,$facts,$cost,$created,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET
              status=excluded.status,provider=excluded.provider,started_at=excluded.started_at,
              completed_at=excluded.completed_at,failed_at=excluded.failed_at,
              error_code=excluded.error_code,error_message=excluded.error_message,
              queries_count=excluded.queries_count,sources_count=excluded.sources_count,
              facts_count=excluded.facts_count,cost_usd=excluded.cost_usd,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$customer", job.CustomerId);
        command.Parameters.AddWithValue("$trigger", job.TriggerType.ToString());
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$provider", job.Provider);
        command.Parameters.AddWithValue("$started", Db(job.StartedAt));
        command.Parameters.AddWithValue("$completed", Db(job.CompletedAt));
        command.Parameters.AddWithValue("$failed", Db(job.FailedAt));
        command.Parameters.AddWithValue("$errorCode", job.ErrorCode);
        command.Parameters.AddWithValue("$errorMessage", job.ErrorMessage);
        command.Parameters.AddWithValue("$queries", job.QueriesCount);
        command.Parameters.AddWithValue("$sources", job.SourcesCount);
        command.Parameters.AddWithValue("$facts", job.FactsCount);
        command.Parameters.AddWithValue("$cost", job.CostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(job));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CustomerEnrichmentJob?> GetCustomerEnrichmentJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", jobId);
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? Json.Deserialize<CustomerEnrichmentJob>(json)
            : null;
    }

    public async Task<IReadOnlyList<CustomerEnrichmentJob>> GetCustomerEnrichmentJobsAsync(
        string? customerId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_jobs" +
                              (string.IsNullOrWhiteSpace(customerId) ? "" : " WHERE customer_id=$customer") +
                              " ORDER BY created_at DESC";
        if (!string.IsNullOrWhiteSpace(customerId)) command.Parameters.AddWithValue("$customer", customerId);
        var results = new List<CustomerEnrichmentJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentJob>(reader.GetString(0)) is { } item) results.Add(item);
        return results;
    }

    public async Task<IReadOnlyDictionary<string, CustomerEnrichmentQueueSummary>>
        GetCustomerEnrichmentQueueSummariesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT l.id AS customer_id,
                   l.data_json AS lead_json,
                   'job' AS row_kind,
                   job.data_json AS job_json,
                   NULL AS field_type,
                   NULL AS normalized_value,
                   NULL AS fact_job_json
            FROM leads AS l
            LEFT JOIN customer_enrichment_jobs AS job
              ON job.customer_id = l.id

            UNION ALL

            SELECT l.id AS customer_id,
                   l.data_json AS lead_json,
                   'fact' AS row_kind,
                   NULL AS job_json,
                   fact.field_type,
                   fact.normalized_value,
                   fact_job.data_json AS fact_job_json
            FROM leads AS l
            LEFT JOIN customer_enrichment_facts AS fact
              ON fact.customer_id = l.id
            LEFT JOIN customer_enrichment_jobs AS fact_job
              ON fact_job.id = fact.job_id
             AND fact_job.customer_id = fact.customer_id
            """;

        var customerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobsByCustomer = new Dictionary<string, Dictionary<string, CustomerEnrichmentJob>>(StringComparer.OrdinalIgnoreCase);
        var currentIdentityHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentFactKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var customerId = reader.GetString(0);
            customerIds.Add(customerId);
            if (!currentIdentityHashes.ContainsKey(customerId)
                && Json.Deserialize<Lead>(reader.GetString(1)) is { } lead)
            {
                currentIdentityHashes[customerId] = CustomerEnrichmentIdentityService.Build(lead).IdentityHash;
            }

            var rowKind = reader.GetString(2);
            if (rowKind == "job" && !reader.IsDBNull(3)
                && Json.Deserialize<CustomerEnrichmentJob>(reader.GetString(3)) is { } job)
            {
                if (!jobsByCustomer.TryGetValue(customerId, out var jobs))
                {
                    jobs = new Dictionary<string, CustomerEnrichmentJob>(StringComparer.OrdinalIgnoreCase);
                    jobsByCustomer[customerId] = jobs;
                }
                jobs[job.Id] = job;
            }

            if (rowKind != "fact"
                || reader.IsDBNull(4)
                || reader.IsDBNull(5)
                || reader.IsDBNull(6)
                || !currentIdentityHashes.TryGetValue(customerId, out var currentIdentityHash)
                || Json.Deserialize<CustomerEnrichmentJob>(reader.GetString(6)) is not { } factJob
                || !string.Equals(factJob.IdentityHash, currentIdentityHash, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!currentFactKeys.TryGetValue(customerId, out var factKeys))
            {
                factKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                currentFactKeys[customerId] = factKeys;
            }
            factKeys.Add($"{reader.GetString(4)}|{reader.GetString(5)}");
        }

        return customerIds.ToDictionary(
            customerId => customerId,
            customerId =>
            {
                var jobs = jobsByCustomer.TryGetValue(customerId, out var jobsById)
                    ? jobsById.Values.OrderByDescending(job => job.CreatedAt).ToList()
                    : [];
                var latestHistoricalJob = jobs.FirstOrDefault();
                var latestCurrentJob = currentIdentityHashes.TryGetValue(customerId, out var currentIdentityHash)
                    ? jobs.FirstOrDefault(job => string.Equals(
                        job.IdentityHash,
                        currentIdentityHash,
                        StringComparison.OrdinalIgnoreCase))
                    : null;
                var factCount = currentFactKeys.TryGetValue(customerId, out var factKeys) ? factKeys.Count : 0;
                return new CustomerEnrichmentQueueSummary(
                    customerId,
                    latestCurrentJob,
                    factCount,
                    latestHistoricalJob);
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<CustomerEnrichmentJob>> GetRecoverableCustomerEnrichmentJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_jobs WHERE status IN ('Queued','Running') ORDER BY created_at";
        var results = new List<CustomerEnrichmentJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentJob>(reader.GetString(0)) is { } item) results.Add(item);
        return results;
    }

    public async Task SaveCustomerEnrichmentQueryAsync(
        CustomerEnrichmentQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using (var existing = db.CreateCommand())
        {
            existing.CommandText = "SELECT id FROM customer_enrichment_queries WHERE job_id=$job AND query_hash=$hash";
            existing.Parameters.AddWithValue("$job", query.JobId);
            existing.Parameters.AddWithValue("$hash", query.QueryHash);
            if (await existing.ExecuteScalarAsync(cancellationToken) is string existingId)
                query.Id = existingId;
        }
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_enrichment_queries(
              id,job_id,customer_id,query_hash,query_text,provider,status,results_count,
              created_at,retrieved_at,data_json)
            VALUES($id,$job,$customer,$hash,$query,$provider,$status,$count,$created,$retrieved,$json)
            ON CONFLICT(id) DO UPDATE SET
              provider=excluded.provider,status=excluded.status,results_count=excluded.results_count,
              retrieved_at=excluded.retrieved_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", query.Id);
        command.Parameters.AddWithValue("$job", query.JobId);
        command.Parameters.AddWithValue("$customer", query.CustomerId);
        command.Parameters.AddWithValue("$hash", query.QueryHash);
        command.Parameters.AddWithValue("$query", query.QueryText);
        command.Parameters.AddWithValue("$provider", query.Provider);
        command.Parameters.AddWithValue("$status", query.Status);
        command.Parameters.AddWithValue("$count", query.ResultsCount);
        command.Parameters.AddWithValue("$created", query.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$retrieved", Db(query.RetrievedAt));
        command.Parameters.AddWithValue("$json", Json.Serialize(query));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetCustomerEnrichmentJobWorkAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        await using (var facts = db.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = "DELETE FROM customer_enrichment_facts WHERE job_id=$job";
            facts.Parameters.AddWithValue("$job", jobId);
            await facts.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var queries = db.CreateCommand())
        {
            queries.Transaction = transaction;
            queries.CommandText = "DELETE FROM customer_enrichment_queries WHERE job_id=$job";
            queries.Parameters.AddWithValue("$job", jobId);
            await queries.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerEnrichmentQuery>> GetCustomerEnrichmentQueriesAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_queries WHERE job_id=$job ORDER BY created_at";
        command.Parameters.AddWithValue("$job", jobId);
        var results = new List<CustomerEnrichmentQuery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentQuery>(reader.GetString(0)) is { } item) results.Add(item);
        return results;
    }

    public async Task SaveCustomerEnrichmentSourcesAsync(
        IEnumerable<CustomerEnrichmentSource> sources,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        foreach (var source in sources)
        {
            await using (var existing = db.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT id FROM customer_enrichment_sources WHERE job_id=$job AND canonical_url=$canonical";
                existing.Parameters.AddWithValue("$job", source.JobId);
                existing.Parameters.AddWithValue("$canonical", source.CanonicalUrl);
                if (await existing.ExecuteScalarAsync(cancellationToken) is string existingId)
                    source.Id = existingId;
            }
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO customer_enrichment_sources(
                  id,job_id,query_id,customer_id,url,canonical_url,title,domain,snippet,content_text,
                  content_hash,published_at,retrieved_at,provider,identity_match_score,
                  identity_match_status,data_json)
                VALUES($id,$job,$query,$customer,$url,$canonical,$title,$domain,$snippet,$content,
                  $hash,$published,$retrieved,$provider,$score,$status,$json)
                ON CONFLICT(job_id,canonical_url) DO UPDATE SET
                  title=excluded.title,snippet=excluded.snippet,content_text=excluded.content_text,
                  content_hash=excluded.content_hash,published_at=excluded.published_at,
                  retrieved_at=excluded.retrieved_at,identity_match_score=excluded.identity_match_score,
                  identity_match_status=excluded.identity_match_status,data_json=excluded.data_json
                """;
            command.Parameters.AddWithValue("$id", source.Id);
            command.Parameters.AddWithValue("$job", source.JobId);
            command.Parameters.AddWithValue("$query", source.QueryId);
            command.Parameters.AddWithValue("$customer", source.CustomerId);
            command.Parameters.AddWithValue("$url", source.Url);
            command.Parameters.AddWithValue("$canonical", source.CanonicalUrl);
            command.Parameters.AddWithValue("$title", source.Title);
            command.Parameters.AddWithValue("$domain", source.Domain);
            command.Parameters.AddWithValue("$snippet", source.Snippet);
            command.Parameters.AddWithValue("$content", source.ContentText);
            command.Parameters.AddWithValue("$hash", source.ContentHash);
            command.Parameters.AddWithValue("$published", Db(source.PublishedAt));
            command.Parameters.AddWithValue("$retrieved", source.RetrievedAt.ToString("O"));
            command.Parameters.AddWithValue("$provider", source.Provider);
            command.Parameters.AddWithValue("$score", source.IdentityMatchScore);
            command.Parameters.AddWithValue("$status", source.IdentityMatchStatus.ToString());
            command.Parameters.AddWithValue("$json", Json.Serialize(source));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerEnrichmentSource>> GetCustomerEnrichmentSourcesAsync(
        string customerId,
        string? jobId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_sources WHERE customer_id=$customer" +
                              (string.IsNullOrWhiteSpace(jobId) ? "" : " AND job_id=$job") +
                              " ORDER BY retrieved_at DESC, identity_match_score DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        if (!string.IsNullOrWhiteSpace(jobId)) command.Parameters.AddWithValue("$job", jobId);
        var results = new List<CustomerEnrichmentSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentSource>(reader.GetString(0)) is { } item) results.Add(item);
        return results;
    }

    public async Task SaveCustomerEnrichmentFactsAsync(
        IEnumerable<CustomerEnrichmentFact> facts,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        foreach (var fact in facts)
        {
            fact.UpdatedAt = DateTimeOffset.Now;
            await using (var existing = db.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT id FROM customer_enrichment_facts
                    WHERE job_id=$job AND field_type=$type AND normalized_value=$normalized
                    """;
                existing.Parameters.AddWithValue("$job", fact.JobId);
                existing.Parameters.AddWithValue("$type", fact.FieldType);
                existing.Parameters.AddWithValue("$normalized", fact.NormalizedValue);
                if (await existing.ExecuteScalarAsync(cancellationToken) is string existingId)
                    fact.Id = existingId;
            }
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO customer_enrichment_facts(
                  id,customer_id,job_id,field_type,field_value,normalized_value,confidence_score,
                  verification_status,source_count,first_discovered_at,last_verified_at,expires_at,
                  created_at,updated_at,data_json)
                VALUES($id,$customer,$job,$type,$value,$normalized,$confidence,$status,$sourceCount,
                  $first,$verified,$expires,$created,$updated,$json)
                ON CONFLICT(id) DO UPDATE SET
                  field_value=excluded.field_value,normalized_value=excluded.normalized_value,
                  confidence_score=excluded.confidence_score,verification_status=excluded.verification_status,
                  source_count=excluded.source_count,last_verified_at=excluded.last_verified_at,
                  expires_at=excluded.expires_at,updated_at=excluded.updated_at,data_json=excluded.data_json
                """;
            command.Parameters.AddWithValue("$id", fact.Id);
            command.Parameters.AddWithValue("$customer", fact.CustomerId);
            command.Parameters.AddWithValue("$job", fact.JobId);
            command.Parameters.AddWithValue("$type", fact.FieldType);
            command.Parameters.AddWithValue("$value", fact.FieldValue);
            command.Parameters.AddWithValue("$normalized", fact.NormalizedValue);
            command.Parameters.AddWithValue("$confidence", fact.ConfidenceScore);
            command.Parameters.AddWithValue("$status", fact.VerificationStatus.ToString());
            command.Parameters.AddWithValue("$sourceCount", fact.SourceCount);
            command.Parameters.AddWithValue("$first", fact.FirstDiscoveredAt.ToString("O"));
            command.Parameters.AddWithValue("$verified", Db(fact.LastVerifiedAt));
            command.Parameters.AddWithValue("$expires", Db(fact.ExpiresAt));
            command.Parameters.AddWithValue("$created", fact.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", fact.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$json", Json.Serialize(fact));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using (var removeLinks = db.CreateCommand())
            {
                removeLinks.Transaction = transaction;
                removeLinks.CommandText = "DELETE FROM customer_enrichment_fact_sources WHERE fact_id=$fact";
                removeLinks.Parameters.AddWithValue("$fact", fact.Id);
                await removeLinks.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var sourceId in fact.SourceIds
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await using var link = db.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = """
                    INSERT INTO customer_enrichment_fact_sources(fact_id,source_id,job_id,customer_id,created_at)
                    VALUES($fact,$source,$job,$customer,$created)
                    """;
                link.Parameters.AddWithValue("$fact", fact.Id);
                link.Parameters.AddWithValue("$source", sourceId);
                link.Parameters.AddWithValue("$job", fact.JobId);
                link.Parameters.AddWithValue("$customer", fact.CustomerId);
                link.Parameters.AddWithValue("$created", fact.UpdatedAt.ToString("O"));
                await link.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerEnrichmentFact>> GetCustomerEnrichmentFactsAsync(
        string customerId,
        bool latestPerValue = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_facts WHERE customer_id=$customer ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$customer", customerId);
        var results = new List<CustomerEnrichmentFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentFact>(reader.GetString(0)) is { } item) results.Add(item);
        if (!latestPerValue) return results;

        var now = DateTimeOffset.Now;
        return results
            .GroupBy(item => $"{item.FieldType}|{item.NormalizedValue}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => GetCustomerEnrichmentFactSelectionRank(item, now))
                .ThenByDescending(item => item.UpdatedAt)
                .First())
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
    }

    private static int GetCustomerEnrichmentFactSelectionRank(
        CustomerEnrichmentFact fact,
        DateTimeOffset now)
    {
        var current = fact.ExpiresAt is null || fact.ExpiresAt > now;
        return fact.VerificationStatus switch
        {
            CustomerEnrichmentVerificationStatus.HumanConfirmed when current => 600,
            CustomerEnrichmentVerificationStatus.Verified when current => 500,
            CustomerEnrichmentVerificationStatus.LikelyMatch => 400,
            CustomerEnrichmentVerificationStatus.PossibleMatch => 300,
            CustomerEnrichmentVerificationStatus.Conflicting => 200,
            CustomerEnrichmentVerificationStatus.HumanConfirmed => 120,
            CustomerEnrichmentVerificationStatus.Verified => 100,
            CustomerEnrichmentVerificationStatus.Outdated => 50,
            CustomerEnrichmentVerificationStatus.Rejected => 0,
            _ => 0
        };
    }

    public async Task<CustomerEnrichmentFact?> GetCustomerEnrichmentFactAsync(
        string factId,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_facts WHERE id=$id";
        command.Parameters.AddWithValue("$id", factId);
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? Json.Deserialize<CustomerEnrichmentFact>(json)
            : null;
    }

    public async Task SaveCustomerEnrichmentReviewAsync(
        CustomerEnrichmentReview review,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_enrichment_reviews(id,customer_id,job_id,fact_id,action,actor,created_at,data_json)
            VALUES($id,$customer,$job,$fact,$action,$actor,$created,$json)
            """;
        command.Parameters.AddWithValue("$id", review.Id);
        command.Parameters.AddWithValue("$customer", review.CustomerId);
        command.Parameters.AddWithValue("$job", review.JobId);
        command.Parameters.AddWithValue("$fact", review.FactId);
        command.Parameters.AddWithValue("$action", review.Action.ToString());
        command.Parameters.AddWithValue("$actor", review.Actor);
        command.Parameters.AddWithValue("$created", review.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(review));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ApplyCustomerEnrichmentReviewAsync(
        CustomerEnrichmentFact fact,
        CustomerEnrichmentReview review,
        CancellationToken cancellationToken = default)
    {
        var expectedUpdatedAt = fact.UpdatedAt;
        fact.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        await using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE customer_enrichment_facts SET
                  field_value=$value,normalized_value=$normalized,confidence_score=$confidence,
                  verification_status=$status,source_count=$sourceCount,last_verified_at=$verified,
                  expires_at=$expires,updated_at=$updated,data_json=$json
                WHERE id=$id AND customer_id=$customer AND job_id=$job AND updated_at=$expectedUpdated
                """;
            update.Parameters.AddWithValue("$value", fact.FieldValue);
            update.Parameters.AddWithValue("$normalized", fact.NormalizedValue);
            update.Parameters.AddWithValue("$confidence", fact.ConfidenceScore);
            update.Parameters.AddWithValue("$status", fact.VerificationStatus.ToString());
            update.Parameters.AddWithValue("$sourceCount", fact.SourceCount);
            update.Parameters.AddWithValue("$verified", Db(fact.LastVerifiedAt));
            update.Parameters.AddWithValue("$expires", Db(fact.ExpiresAt));
            update.Parameters.AddWithValue("$updated", fact.UpdatedAt.ToString("O"));
            update.Parameters.AddWithValue("$json", Json.Serialize(fact));
            update.Parameters.AddWithValue("$id", fact.Id);
            update.Parameters.AddWithValue("$customer", fact.CustomerId);
            update.Parameters.AddWithValue("$job", fact.JobId);
            update.Parameters.AddWithValue("$expectedUpdated", expectedUpdatedAt.ToString("O"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("待审核的客户调查事实已被其他操作更新，请刷新后重试。");
        }
        await using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO customer_enrichment_reviews(id,customer_id,job_id,fact_id,action,actor,created_at,data_json)
                VALUES($id,$customer,$job,$fact,$action,$actor,$created,$json)
                """;
            insert.Parameters.AddWithValue("$id", review.Id);
            insert.Parameters.AddWithValue("$customer", review.CustomerId);
            insert.Parameters.AddWithValue("$job", review.JobId);
            insert.Parameters.AddWithValue("$fact", review.FactId);
            insert.Parameters.AddWithValue("$action", review.Action.ToString());
            insert.Parameters.AddWithValue("$actor", review.Actor);
            insert.Parameters.AddWithValue("$created", review.CreatedAt.ToString("O"));
            insert.Parameters.AddWithValue("$json", Json.Serialize(review));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearLinks = db.CreateCommand())
        {
            clearLinks.Transaction = transaction;
            clearLinks.CommandText = "DELETE FROM customer_enrichment_fact_sources WHERE fact_id=$fact";
            clearLinks.Parameters.AddWithValue("$fact", fact.Id);
            await clearLinks.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var sourceId in fact.SourceIds
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var link = db.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = """
                INSERT INTO customer_enrichment_fact_sources(fact_id,source_id,job_id,customer_id,created_at)
                VALUES($fact,$source,$job,$customer,$created)
                """;
            link.Parameters.AddWithValue("$fact", fact.Id);
            link.Parameters.AddWithValue("$source", sourceId);
            link.Parameters.AddWithValue("$job", fact.JobId);
            link.Parameters.AddWithValue("$customer", fact.CustomerId);
            link.Parameters.AddWithValue("$created", fact.UpdatedAt.ToString("O"));
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        // A review can revoke or materially change a fact that has already been
        // materialized into Customer Brain. Remove that materialization in the same
        // transaction so a failed/cancelled refresh cannot leave stale facts readable.
        await using (var invalidateProfile = db.CreateCommand())
        {
            invalidateProfile.Transaction = transaction;
            invalidateProfile.CommandText = "DELETE FROM customer_intelligence_profiles WHERE customer_id=$customer";
            invalidateProfile.Parameters.AddWithValue("$customer", fact.CustomerId);
            await invalidateProfile.ExecuteNonQueryAsync(cancellationToken);
        }

        var recommendations = new List<AiRecommendationRecord>();
        await using (var readRecommendations = db.CreateCommand())
        {
            readRecommendations.Transaction = transaction;
            readRecommendations.CommandText = """
                SELECT data_json FROM ai_recommendation_history
                WHERE customer_id=$customer AND status IN ('Proposed','Accepted','InProgress')
                """;
            readRecommendations.Parameters.AddWithValue("$customer", fact.CustomerId);
            await using var recommendationReader = await readRecommendations.ExecuteReaderAsync(cancellationToken);
            while (await recommendationReader.ReadAsync(cancellationToken))
                if (Json.Deserialize<AiRecommendationRecord>(recommendationReader.GetString(0)) is { } item)
                    recommendations.Add(item);
        }
        var followUpTasks = new List<FollowUpTask>();
        await using (var readTasks = db.CreateCommand())
        {
            readTasks.Transaction = transaction;
            readTasks.CommandText = "SELECT data_json FROM follow_up_tasks WHERE customer_id=$customer";
            readTasks.Parameters.AddWithValue("$customer", fact.CustomerId);
            await using var taskReader = await readTasks.ExecuteReaderAsync(cancellationToken);
            while (await taskReader.ReadAsync(cancellationToken))
                if (Json.Deserialize<FollowUpTask>(taskReader.GetString(0)) is { } item)
                    followUpTasks.Add(item);
        }
        var salesActions = new List<SalesActionRecord>();
        await using (var readActions = db.CreateCommand())
        {
            readActions.Transaction = transaction;
            readActions.CommandText = "SELECT data_json FROM sales_action_logs WHERE customer_id=$customer";
            readActions.Parameters.AddWithValue("$customer", fact.CustomerId);
            await using var actionReader = await readActions.ExecuteReaderAsync(cancellationToken);
            while (await actionReader.ReadAsync(cancellationToken))
                if (Json.Deserialize<SalesActionRecord>(actionReader.GetString(0)) is { } item)
                    salesActions.Add(item);
        }
        foreach (var recommendation in recommendations)
        {
            var task = followUpTasks.FirstOrDefault(item =>
                item.RecommendationId.Equals(recommendation.Id, StringComparison.OrdinalIgnoreCase));
            var action = salesActions.FirstOrDefault(item =>
                item.RecommendationId.Equals(recommendation.Id, StringComparison.OrdinalIgnoreCase));
            if (recommendation.Status == AiRecommendationStatus.InProgress
                || task?.Status == FollowUpTaskStatus.InProgress
                || action?.Status == SalesActionStatus.InProgress
                || action?.ExecutedAt is not null)
                continue;

            recommendation.Status = AiRecommendationStatus.Superseded;
            recommendation.UpdatedAt = fact.UpdatedAt;
            await using var invalidateRecommendation = db.CreateCommand();
            invalidateRecommendation.Transaction = transaction;
            invalidateRecommendation.CommandText = """
                UPDATE ai_recommendation_history
                SET status=$status,updated_at=$updated,data_json=$json
                WHERE id=$id
                """;
            invalidateRecommendation.Parameters.AddWithValue("$status", recommendation.Status.ToString());
            invalidateRecommendation.Parameters.AddWithValue("$updated", recommendation.UpdatedAt.ToString("O"));
            invalidateRecommendation.Parameters.AddWithValue("$json", Json.Serialize(recommendation));
            invalidateRecommendation.Parameters.AddWithValue("$id", recommendation.Id);
            await invalidateRecommendation.ExecuteNonQueryAsync(cancellationToken);

            if (task is { Status: FollowUpTaskStatus.Proposed or FollowUpTaskStatus.Open })
            {
                task.Status = FollowUpTaskStatus.Dismissed;
                task.Outcome = "客户资料已变化，旧 AI 建议已失效。";
                task.CompletedAt = fact.UpdatedAt;
                task.UpdatedAt = fact.UpdatedAt;
                await using var dismissTask = db.CreateCommand();
                dismissTask.Transaction = transaction;
                dismissTask.CommandText = "UPDATE follow_up_tasks SET status=$status,updated_at=$updated,data_json=$json WHERE id=$id";
                dismissTask.Parameters.AddWithValue("$status", task.Status.ToString());
                dismissTask.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O"));
                dismissTask.Parameters.AddWithValue("$json", Json.Serialize(task));
                dismissTask.Parameters.AddWithValue("$id", task.Id);
                await dismissTask.ExecuteNonQueryAsync(cancellationToken);
            }
            if (action is { Status: SalesActionStatus.Planned or SalesActionStatus.Approved })
            {
                action.Status = SalesActionStatus.Cancelled;
                action.Outcome = "客户资料已变化，旧 AI 建议已失效。";
                action.CompletedAt = fact.UpdatedAt;
                action.UpdatedAt = fact.UpdatedAt;
                await using var cancelAction = db.CreateCommand();
                cancelAction.Transaction = transaction;
                cancelAction.CommandText = "UPDATE sales_action_logs SET status=$status,updated_at=$updated,data_json=$json WHERE id=$id";
                cancelAction.Parameters.AddWithValue("$status", action.Status.ToString());
                cancelAction.Parameters.AddWithValue("$updated", action.UpdatedAt.ToString("O"));
                cancelAction.Parameters.AddWithValue("$json", Json.Serialize(action));
                cancelAction.Parameters.AddWithValue("$id", action.Id);
                await cancelAction.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveCustomerEnrichmentUsageAsync(
        CustomerEnrichmentProviderUsage usage,
        CancellationToken cancellationToken = default)
    {
        var localUsageTime = TimeZoneInfo.ConvertTime(usage.CreatedAt, _timeProvider.LocalTimeZone);
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_enrichment_provider_usage(
              id,provider,job_id,request_day,request_month,requests,estimated_cost_usd,
              succeeded,error_code,created_at,data_json)
            VALUES($id,$provider,$job,$day,$month,$requests,$cost,$succeeded,$error,$created,$json)
            ON CONFLICT(id) DO UPDATE SET
              provider=excluded.provider,job_id=excluded.job_id,request_day=excluded.request_day,
              request_month=excluded.request_month,requests=excluded.requests,
              estimated_cost_usd=excluded.estimated_cost_usd,succeeded=excluded.succeeded,
              error_code=excluded.error_code,created_at=excluded.created_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", usage.Id);
        command.Parameters.AddWithValue("$provider", usage.Provider);
        command.Parameters.AddWithValue("$job", string.IsNullOrWhiteSpace(usage.JobId) || usage.JobId == "settings-test"
            ? DBNull.Value
            : usage.JobId);
        command.Parameters.AddWithValue("$day", localUsageTime.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$month", localUsageTime.ToString("yyyy-MM"));
        command.Parameters.AddWithValue("$requests", usage.Requests);
        command.Parameters.AddWithValue("$cost", usage.EstimatedCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$succeeded", usage.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", usage.ErrorCode);
        command.Parameters.AddWithValue("$created", usage.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(usage));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerEnrichmentProviderUsage>> GetCustomerEnrichmentUsageForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_provider_usage WHERE job_id=$job ORDER BY created_at";
        command.Parameters.AddWithValue("$job", jobId);
        var items = new List<CustomerEnrichmentProviderUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentProviderUsage>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<CustomerEnrichmentUsageSummary> GetCustomerEnrichmentUsageSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetLocalNow();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM customer_enrichment_provider_usage WHERE request_month=$month ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$month", now.ToString("yyyy-MM"));
        var items = new List<CustomerEnrichmentProviderUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<CustomerEnrichmentProviderUsage>(reader.GetString(0)) is { } item) items.Add(item);
        return new CustomerEnrichmentUsageSummary
        {
            TodayRequests = items
                .Where(item => TimeZoneInfo.ConvertTime(item.CreatedAt, _timeProvider.LocalTimeZone).Date == now.Date)
                .Sum(item => item.Requests),
            MonthRequests = items.Sum(item => item.Requests),
            MonthEstimatedCostUsd = items.Sum(item => item.EstimatedCostUsd),
            LastError = items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ErrorMessage))?.ErrorMessage ?? "",
            ProviderRequests = items.GroupBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Requests), StringComparer.OrdinalIgnoreCase)
        };
    }

    public async Task<CustomerEnrichmentSnapshot> GetCustomerEnrichmentSnapshotAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var jobs = await GetCustomerEnrichmentJobsAsync(customerId, cancellationToken);
        var latest = jobs.FirstOrDefault();
        return new CustomerEnrichmentSnapshot
        {
            LatestJob = latest,
            Jobs = jobs.ToList(),
            Facts = (await GetCustomerEnrichmentFactsAsync(customerId, cancellationToken: cancellationToken)).ToList(),
            Sources = latest is null
                ? []
                : (await GetCustomerEnrichmentSourcesAsync(customerId, latest.Id, cancellationToken)).ToList(),
            Usage = await GetCustomerEnrichmentUsageSummaryAsync(cancellationToken)
        };
    }

    private static object Db(DateTimeOffset? value) =>
        (object?)value?.ToString("O") ?? DBNull.Value;
}

public sealed record CustomerEnrichmentQueueSummary(
    string CustomerId,
    CustomerEnrichmentJob? LatestJob,
    int FactCount,
    CustomerEnrichmentJob? LatestHistoricalJob)
{
    public bool HasHistoricalJob => LatestHistoricalJob is not null;
    public bool NeedsRefresh => LatestJob is null && HasHistoricalJob;
}
