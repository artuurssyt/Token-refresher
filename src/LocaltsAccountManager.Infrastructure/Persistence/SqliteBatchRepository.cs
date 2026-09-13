using Microsoft.Data.Sqlite;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Paths;

namespace LocaltsAccountManager.Infrastructure.Persistence;

public sealed class SqliteBatchRepository : IBatchRepository
{
    private readonly string _connectionString;

    public SqliteBatchRepository()
    {
        var dbPath = ApplicationPaths.DatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = SqliteSupport.BuildConnectionString(dbPath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSupport.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        await SqliteSupport.EnableWriteAheadLoggingAsync(connection, cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS batches (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                SourceFilePath TEXT NOT NULL,
                OutputDirectory TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT NULL,
                CompletedAt TEXT NULL,
                CancelledAt TEXT NULL,
                TotalRecords INTEGER NOT NULL,
                Succeeded INTEGER NOT NULL,
                Failed INTEGER NOT NULL,
                Pending INTEGER NOT NULL,
                Cancelled INTEGER NOT NULL,
                DuplicatesFlagged INTEGER NOT NULL,
                ConcurrencyLimit INTEGER NOT NULL,
                EffectiveConcurrency INTEGER NOT NULL,
                GlobalRateLimitUntil TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(BatchRecord batch, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSupport.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO batches (
                Id, Name, SourceFilePath, OutputDirectory, Status, CreatedAt, StartedAt, CompletedAt, CancelledAt,
                TotalRecords, Succeeded, Failed, Pending, Cancelled, DuplicatesFlagged,
                ConcurrencyLimit, EffectiveConcurrency, GlobalRateLimitUntil
            ) VALUES (
                $Id, $Name, $SourceFilePath, $OutputDirectory, $Status, $CreatedAt, $StartedAt, $CompletedAt, $CancelledAt,
                $TotalRecords, $Succeeded, $Failed, $Pending, $Cancelled, $DuplicatesFlagged,
                $ConcurrencyLimit, $EffectiveConcurrency, $GlobalRateLimitUntil
            )
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                SourceFilePath = excluded.SourceFilePath,
                OutputDirectory = excluded.OutputDirectory,
                Status = excluded.Status,
                StartedAt = excluded.StartedAt,
                CompletedAt = excluded.CompletedAt,
                CancelledAt = excluded.CancelledAt,
                TotalRecords = excluded.TotalRecords,
                Succeeded = excluded.Succeeded,
                Failed = excluded.Failed,
                Pending = excluded.Pending,
                Cancelled = excluded.Cancelled,
                DuplicatesFlagged = excluded.DuplicatesFlagged,
                ConcurrencyLimit = excluded.ConcurrencyLimit,
                EffectiveConcurrency = excluded.EffectiveConcurrency,
                GlobalRateLimitUntil = excluded.GlobalRateLimitUntil;
            """;
        BindBatch(command, batch);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSupport.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM batches WHERE Id = $Id LIMIT 1;";
        command.Parameters.AddWithValue("$Id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBatch(reader) : null;
    }

    public async Task<IReadOnlyList<BatchRecord>> GetRecentAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSupport.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM batches ORDER BY CreatedAt DESC LIMIT $Count;";
        command.Parameters.AddWithValue("$Count", count);
        var list = new List<BatchRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadBatch(reader));
        }

        return list;
    }

    public async Task<BatchRecord?> GetLatestIncompleteAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSupport.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM batches
            WHERE Status IN ($Running, $Paused, $Created)
            ORDER BY CreatedAt DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Running", (int)BatchStatus.Running);
        command.Parameters.AddWithValue("$Paused", (int)BatchStatus.PausedRateLimited);
        command.Parameters.AddWithValue("$Created", (int)BatchStatus.Created);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBatch(reader) : null;
    }

    public async Task<BatchRecord?> GetLatestForStartupAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteSupport.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);

        var active = connection.CreateCommand();
        active.CommandText = """
            SELECT * FROM batches
            WHERE Status IN ($Running, $Paused)
            ORDER BY CreatedAt DESC LIMIT 1;
            """;
        active.Parameters.AddWithValue("$Running", (int)BatchStatus.Running);
        active.Parameters.AddWithValue("$Paused", (int)BatchStatus.PausedRateLimited);
        await using (var reader = await active.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReadBatch(reader);
            }
        }

        var processed = connection.CreateCommand();
        processed.CommandText = """
            SELECT * FROM batches
            WHERE (Succeeded + Failed) > 0
            ORDER BY CreatedAt DESC LIMIT 1;
            """;
        await using (var reader = await processed.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReadBatch(reader);
            }
        }

        // Reuse the open connection instead of calling GetLatestIncompleteAsync, which would open
        // a second one while this is still held.
        var incomplete = connection.CreateCommand();
        incomplete.CommandText = """
            SELECT * FROM batches
            WHERE Status IN ($Running, $Paused, $Created)
            ORDER BY CreatedAt DESC LIMIT 1;
            """;
        incomplete.Parameters.AddWithValue("$Running", (int)BatchStatus.Running);
        incomplete.Parameters.AddWithValue("$Paused", (int)BatchStatus.PausedRateLimited);
        incomplete.Parameters.AddWithValue("$Created", (int)BatchStatus.Created);
        await using (var reader = await incomplete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBatch(reader) : null;
        }
    }

    private static void BindBatch(SqliteCommand command, BatchRecord batch)
    {
        command.Parameters.AddWithValue("$Id", batch.Id.ToString());
        command.Parameters.AddWithValue("$Name", batch.Name);
        command.Parameters.AddWithValue("$SourceFilePath", batch.SourceFilePath);
        command.Parameters.AddWithValue("$OutputDirectory", batch.OutputDirectory);
        command.Parameters.AddWithValue("$Status", (int)batch.Status);
        command.Parameters.AddWithValue("$CreatedAt", batch.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$StartedAt", batch.StartedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$CompletedAt", batch.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$CancelledAt", batch.CancelledAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$TotalRecords", batch.TotalRecords);
        command.Parameters.AddWithValue("$Succeeded", batch.Succeeded);
        command.Parameters.AddWithValue("$Failed", batch.Failed);
        command.Parameters.AddWithValue("$Pending", batch.Pending);
        command.Parameters.AddWithValue("$Cancelled", batch.Cancelled);
        command.Parameters.AddWithValue("$DuplicatesFlagged", batch.DuplicatesFlagged);
        command.Parameters.AddWithValue("$ConcurrencyLimit", batch.ConcurrencyLimit);
        command.Parameters.AddWithValue("$EffectiveConcurrency", batch.EffectiveConcurrency);
        command.Parameters.AddWithValue("$GlobalRateLimitUntil", batch.GlobalRateLimitUntil?.ToString("O") ?? (object)DBNull.Value);
    }

    // Resolved by name rather than ordinal: the queries use SELECT *, so any future column
    // added ahead of an existing one would have silently shifted every value across.
    private static BatchRecord ReadBatch(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        SourceFilePath = reader.GetString(reader.GetOrdinal("SourceFilePath")),
        OutputDirectory = reader.GetString(reader.GetOrdinal("OutputDirectory")),
        Status = (BatchStatus)reader.GetInt32(reader.GetOrdinal("Status")),
        CreatedAt = SqliteSupport.ParseTimestamp(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        StartedAt = ReadOptionalDate(reader, "StartedAt"),
        CompletedAt = ReadOptionalDate(reader, "CompletedAt"),
        CancelledAt = ReadOptionalDate(reader, "CancelledAt"),
        TotalRecords = reader.GetInt32(reader.GetOrdinal("TotalRecords")),
        Succeeded = reader.GetInt32(reader.GetOrdinal("Succeeded")),
        Failed = reader.GetInt32(reader.GetOrdinal("Failed")),
        Pending = reader.GetInt32(reader.GetOrdinal("Pending")),
        Cancelled = reader.GetInt32(reader.GetOrdinal("Cancelled")),
        DuplicatesFlagged = reader.GetInt32(reader.GetOrdinal("DuplicatesFlagged")),
        ConcurrencyLimit = reader.GetInt32(reader.GetOrdinal("ConcurrencyLimit")),
        EffectiveConcurrency = reader.GetInt32(reader.GetOrdinal("EffectiveConcurrency")),
        GlobalRateLimitUntil = ReadOptionalDate(reader, "GlobalRateLimitUntil")
    };

    private static DateTimeOffset? ReadOptionalDate(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : SqliteSupport.TryParseTimestamp(reader.GetString(ordinal));
    }
}
