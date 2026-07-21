using Microsoft.Data.Sqlite;

namespace WAFlow.Core.Infrastructure;

public sealed record DatabaseRecoveryNotice(
    string BackupDirectory,
    IReadOnlyDictionary<string, long> PreservedRows)
{
    public long LeadCount => PreservedRows.GetValueOrDefault("leads");
    public long ConversationCount => PreservedRows.GetValueOrDefault("whatsapp_conversations");
    public long MessageCount => PreservedRows.GetValueOrDefault("whatsapp_messages");
}

public sealed class DatabaseRecoveryException(string message, string backupDirectory, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string BackupDirectory { get; } = backupDirectory;
}

internal static class DatabaseStartupGuard
{
    private const int BackupRetentionCount = 10;

    public static async Task<DatabaseRecoveryNotice?> PrepareAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0) return null;

        var integrity = await CheckIntegrityAsync(databasePath, cancellationToken);
        if (integrity.IsHealthy)
        {
            try { await CreateHealthyBackupAsync(databasePath, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { WriteBackupWarning(databasePath, error); }
            return null;
        }

        return await RecoverAsync(databasePath, integrity.Detail, cancellationToken);
    }

    private static async Task<DatabaseRecoveryNotice> RecoverAsync(string databasePath, string integrityDetail, CancellationToken cancellationToken)
    {
        var databaseDirectory = Path.GetDirectoryName(databasePath)!;
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupDirectory = Path.Combine(databaseDirectory, "recovery-backups", stamp);
        Directory.CreateDirectory(backupDirectory);

        CopyIfPresent(databasePath, Path.Combine(backupDirectory, Path.GetFileName(databasePath) + ".corrupt"));
        CopyIfPresent(databasePath + "-wal", Path.Combine(backupDirectory, Path.GetFileName(databasePath) + "-wal.corrupt"));
        CopyIfPresent(databasePath + "-shm", Path.Combine(backupDirectory, Path.GetFileName(databasePath) + "-shm.corrupt"));
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "integrity-error.txt"), integrityDetail, cancellationToken);

        var recoveredPath = Path.Combine(databaseDirectory, $".{Path.GetFileName(databasePath)}.recovered-{Guid.NewGuid():N}.tmp");
        try
        {
            var originalCounts = await ReadTableCountsAsync(databasePath, cancellationToken);
            try
            {
                await VacuumIntoAsync(databasePath, recoveredPath, cancellationToken);
            }
            catch (Exception vacuumError) when (vacuumError is not OperationCanceledException)
            {
                TryDelete(recoveredPath);
                await File.WriteAllTextAsync(Path.Combine(backupDirectory, "vacuum-error.txt"), vacuumError.ToString(), cancellationToken);
                await LogicalRecoverAsync(databasePath, recoveredPath, cancellationToken);
            }
            await RebuildIndexesAsync(recoveredPath, cancellationToken);

            var recoveredIntegrity = await CheckIntegrityAsync(recoveredPath, cancellationToken);
            if (!recoveredIntegrity.IsHealthy)
                throw new InvalidDataException($"恢复副本仍未通过完整性检查：{recoveredIntegrity.Detail}");

            var foreignKeyErrors = await ReadForeignKeyErrorsAsync(recoveredPath, cancellationToken);
            if (foreignKeyErrors.Count > 0)
                throw new InvalidDataException($"恢复副本存在 {foreignKeyErrors.Count} 条外键错误。");

            var recoveredCounts = await ReadTableCountsAsync(recoveredPath, cancellationToken);
            var mismatches = originalCounts
                .Where(pair => recoveredCounts.GetValueOrDefault(pair.Key, -1) != pair.Value)
                .Select(pair => $"{pair.Key}: {pair.Value} -> {recoveredCounts.GetValueOrDefault(pair.Key, -1)}")
                .ToArray();
            if (mismatches.Length > 0)
                throw new InvalidDataException("恢复前后表记录数不一致：" + string.Join("；", mismatches));

            CopyIfPresent(recoveredPath, Path.Combine(backupDirectory, Path.GetFileName(databasePath) + ".recovered"));
            ReplaceDatabaseFiles(databasePath, recoveredPath, backupDirectory);
            return new DatabaseRecoveryNotice(backupDirectory, recoveredCounts);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            TryDelete(recoveredPath);
            throw new DatabaseRecoveryException(
                $"检测到本地数据库损坏，程序已保留原始数据，但自动恢复未能安全完成。备份位置：{backupDirectory}",
                backupDirectory,
                error);
        }
    }

    private static async Task<(bool IsHealthy, string Detail)> CheckIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var results = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken) && results.Count < 20)
                results.Add(reader.GetString(0));
            var healthy = results.Count == 1 && string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase);
            return (healthy, string.Join(Environment.NewLine, results));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (false, error.Message);
        }
    }

    private static async Task<Dictionary<string, long>> ReadTableCountsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        var names = new List<string>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        }

        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(name)} NOT INDEXED";
            counts[name] = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }
        return counts;
    }

    private static async Task VacuumIntoAsync(string sourcePath, string recoveredPath, CancellationToken cancellationToken)
    {
        TryDelete(recoveredPath);
        await using var connection = new SqliteConnection(ConnectionString(sourcePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $output";
        command.Parameters.AddWithValue("$output", recoveredPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RebuildIndexesAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "REINDEX";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LogicalRecoverAsync(string sourcePath, string recoveredPath, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(ConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
        await source.OpenAsync(cancellationToken);
        var tables = await ReadSchemaObjectsAsync(source, "table", excludeInternal:true, cancellationToken);
        var indexes = await ReadSchemaObjectsAsync(source, "index", excludeInternal:true, cancellationToken);
        var views = await ReadSchemaObjectsAsync(source, "view", excludeInternal:false, cancellationToken);
        var triggers = await ReadSchemaObjectsAsync(source, "trigger", excludeInternal:false, cancellationToken);

        await using var destination = new SqliteConnection(ConnectionString(recoveredPath, SqliteOpenMode.ReadWriteCreate));
        await destination.OpenAsync(cancellationToken);
        await using (var pragmas = destination.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA journal_mode=DELETE;";
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = await destination.BeginTransactionAsync(cancellationToken);
        foreach (var table in tables) await ExecuteSchemaAsync(destination, transaction as SqliteTransaction, table.Sql, cancellationToken);

        foreach (var table in tables)
        {
            await using var select = source.CreateCommand();
            select.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)} NOT INDEXED";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var parameterNames = Enumerable.Range(0, columns.Length).Select(index => $"$p{index}").ToArray();
            await using var insert = destination.CreateCommand();
            insert.Transaction = transaction as SqliteTransaction;
            insert.CommandText = $"INSERT INTO {QuoteIdentifier(table.Name)} ({string.Join(",", columns.Select(QuoteIdentifier))}) VALUES ({string.Join(",", parameterNames)})";
            foreach (var parameterName in parameterNames) insert.Parameters.Add(new SqliteParameter(parameterName, null));
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var index = 0; index < columns.Length; index++) insert.Parameters[index].Value = reader.GetValue(index);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var item in indexes.Concat(views).Concat(triggers))
            await ExecuteSchemaAsync(destination, transaction as SqliteTransaction, item.Sql, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<List<(string Name, string Sql)>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        string type,
        bool excludeInternal,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type=$type AND sql IS NOT NULL" +
                              (excludeInternal ? " AND name NOT LIKE 'sqlite_%'" : "") + " ORDER BY name";
        command.Parameters.AddWithValue("$type", type);
        var results = new List<(string Name, string Sql)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    private static async Task ExecuteSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> ReadForeignKeyErrorsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken) && results.Count < 100)
            results.Add(string.Join("/", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        return results;
    }

    private static async Task CreateHealthyBackupAsync(string databasePath, CancellationToken cancellationToken)
    {
        var backupRoot = Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");
        Directory.CreateDirectory(backupRoot);
        var backupPath = Path.Combine(backupRoot, $"{Path.GetFileNameWithoutExtension(databasePath)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.db");
        try
        {
            await using var source = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadOnly));
            await source.OpenAsync(cancellationToken);
            await using var destination = new SqliteConnection(ConnectionString(backupPath, SqliteOpenMode.ReadWriteCreate));
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);

            var integrity = await CheckIntegrityAsync(backupPath, cancellationToken);
            if (!integrity.IsHealthy) throw new InvalidDataException("启动备份未通过完整性检查。");

            foreach (var stale in new DirectoryInfo(backupRoot)
                         .GetFiles($"{Path.GetFileNameWithoutExtension(databasePath)}-*.db")
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(BackupRetentionCount))
                stale.Delete();
        }
        catch
        {
            TryDelete(backupPath);
            throw;
        }
    }

    private static void ReplaceDatabaseFiles(string databasePath, string recoveredPath, string backupDirectory)
    {
        SqliteConnection.ClearAllPools();
        var archivedOriginal = Path.Combine(backupDirectory, Path.GetFileName(databasePath) + ".replaced-original");
        var archivedWal = Path.Combine(backupDirectory, Path.GetFileName(databasePath) + "-wal.replaced-original");
        var archivedShm = Path.Combine(backupDirectory, Path.GetFileName(databasePath) + "-shm.replaced-original");
        File.Move(databasePath, archivedOriginal, true);
        MoveIfPresent(databasePath + "-wal", archivedWal);
        MoveIfPresent(databasePath + "-shm", archivedShm);
        try
        {
            File.Move(recoveredPath, databasePath, true);
        }
        catch
        {
            if (!File.Exists(databasePath) && File.Exists(archivedOriginal)) File.Copy(archivedOriginal, databasePath, true);
            CopyIfPresent(archivedWal, databasePath + "-wal");
            CopyIfPresent(archivedShm, databasePath + "-shm");
            throw;
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        ForeignKeys = false,
        Pooling = false
    }.ToString();

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, true);
    }

    private static void MoveIfPresent(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination, true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void WriteBackupWarning(string databasePath, Exception error)
    {
        try
        {
            var logDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "database-backup-warnings.log"),
                $"[{DateTimeOffset.Now:O}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
