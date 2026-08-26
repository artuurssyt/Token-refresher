using Microsoft.Data.Sqlite;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Persistence;

public sealed class SqliteBatchRepository : IBatchRepository
{
    private readonly string _connectionString;

    public SqliteBatchRepository()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocaltsAccountManager",
            "accounts.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM batches WHERE Id = $Id LIMIT 1;";
        command.Parameters.AddWithValue("$Id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBatch(reader) : null;
    }

    public async Task<IReadOnlyList<BatchRecord>> GetRecentAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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

        return await GetLatestIncompleteAsync(cancellationToken).ConfigureAwait(false);
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

    private static BatchRecord ReadBatch(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        SourceFilePath = reader.GetString(2),
        OutputDirectory = reader.GetString(3),
        Status = (BatchStatus)reader.GetInt32(4),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
        StartedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
        CompletedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        CancelledAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        TotalRecords = reader.GetInt32(9),
        Succeeded = reader.GetInt32(10),
        Failed = reader.GetInt32(11),
        Pending = reader.GetInt32(12),
        Cancelled = reader.GetInt32(13),
        DuplicatesFlagged = reader.GetInt32(14),
        ConcurrencyLimit = reader.GetInt32(15),
        EffectiveConcurrency = reader.GetInt32(16),
        GlobalRateLimitUntil = reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17))
    };
}
