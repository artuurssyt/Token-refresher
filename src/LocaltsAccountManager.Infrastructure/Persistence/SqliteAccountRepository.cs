using Microsoft.Data.Sqlite;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Persistence;

public sealed class SqliteAccountRepository : IAccountRepository
{
    private readonly string _connectionString;

    public SqliteAccountRepository()
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
            CREATE TABLE IF NOT EXISTS accounts (
                Id TEXT PRIMARY KEY,
                BatchId TEXT NOT NULL,
                SourceFile TEXT NOT NULL,
                SourceLine INTEGER NOT NULL,
                OriginalLine TEXT NOT NULL,
                OriginalInputForm INTEGER NOT NULL,
                ProvidedUsername TEXT NULL,
                AuthenticatedMinecraftUsername TEXT NULL,
                AuthenticatedMinecraftUuid TEXT NULL,
                ProvidedUsernameMismatch INTEGER NULL,
                CredentialReference TEXT NULL,
                TokenFingerprint TEXT NOT NULL,
                ParseStatus INTEGER NOT NULL,
                ProcessingState INTEGER NOT NULL,
                FailureStage INTEGER NOT NULL,
                ErrorCategory INTEGER NOT NULL,
                OAuthErrorCode TEXT NULL,
                ServiceErrorDetail TEXT NULL,
                IsErrorCauseConfirmed INTEGER NOT NULL,
                RetryCount INTEGER NOT NULL,
                LastAttemptAt TEXT NULL,
                LastSuccessAt TEXT NULL,
                BackoffUntil TEXT NULL,
                DuplicateOfRecordId TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_accounts_batch ON accounts(BatchId);
            CREATE INDEX IF NOT EXISTS idx_accounts_fingerprint ON accounts(TokenFingerprint);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await MigrateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS idx_accounts_uuid ON accounts(AuthenticatedMinecraftUuid);";
        await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(accounts);";
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (!existing.Contains("AuthenticatedMinecraftUuid"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN AuthenticatedMinecraftUuid TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("ProvidedUsernameMismatch"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN ProvidedUsernameMismatch INTEGER NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("MicrosoftAccessTokenExpiresAt"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN MicrosoftAccessTokenExpiresAt TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("MinecraftAccessTokenExpiresAt"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN MinecraftAccessTokenExpiresAt TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("RefreshCredentialUpdatedAt"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN RefreshCredentialUpdatedAt TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("MinecraftAccessTokenReference"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN MinecraftAccessTokenReference TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("InPool"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN InPool INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var backfill = connection.CreateCommand();
            backfill.CommandText = "UPDATE accounts SET InPool = 1 WHERE CredentialReference IS NOT NULL;";
            await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("PoolJoinedAt"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE accounts ADD COLUMN PoolJoinedAt TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var backfill = connection.CreateCommand();
            backfill.CommandText = """
                UPDATE accounts
                SET PoolJoinedAt = COALESCE(LastSuccessAt, CreatedAt)
                WHERE InPool = 1 AND PoolJoinedAt IS NULL;
                """;
            await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var poolIndex = connection.CreateCommand();
        poolIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_accounts_pool ON accounts(InPool);";
        await poolIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(AccountRecord record, CancellationToken cancellationToken = default)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (
                Id, BatchId, SourceFile, SourceLine, OriginalLine, OriginalInputForm,
                ProvidedUsername, AuthenticatedMinecraftUsername, AuthenticatedMinecraftUuid, ProvidedUsernameMismatch,
                CredentialReference, TokenFingerprint,
                ParseStatus, ProcessingState, FailureStage, ErrorCategory, OAuthErrorCode, ServiceErrorDetail,
                IsErrorCauseConfirmed, RetryCount, LastAttemptAt, LastSuccessAt, BackoffUntil,
                DuplicateOfRecordId, CreatedAt, UpdatedAt,
                MicrosoftAccessTokenExpiresAt, MinecraftAccessTokenExpiresAt, RefreshCredentialUpdatedAt,
                MinecraftAccessTokenReference, InPool, PoolJoinedAt
            ) VALUES (
                $Id, $BatchId, $SourceFile, $SourceLine, $OriginalLine, $OriginalInputForm,
                $ProvidedUsername, $AuthenticatedMinecraftUsername, $AuthenticatedMinecraftUuid, $ProvidedUsernameMismatch,
                $CredentialReference, $TokenFingerprint,
                $ParseStatus, $ProcessingState, $FailureStage, $ErrorCategory, $OAuthErrorCode, $ServiceErrorDetail,
                $IsErrorCauseConfirmed, $RetryCount, $LastAttemptAt, $LastSuccessAt, $BackoffUntil,
                $DuplicateOfRecordId, $CreatedAt, $UpdatedAt,
                $MicrosoftAccessTokenExpiresAt, $MinecraftAccessTokenExpiresAt, $RefreshCredentialUpdatedAt,
                $MinecraftAccessTokenReference, $InPool, $PoolJoinedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                ProvidedUsername = excluded.ProvidedUsername,
                AuthenticatedMinecraftUsername = excluded.AuthenticatedMinecraftUsername,
                AuthenticatedMinecraftUuid = excluded.AuthenticatedMinecraftUuid,
                ProvidedUsernameMismatch = excluded.ProvidedUsernameMismatch,
                CredentialReference = excluded.CredentialReference,
                TokenFingerprint = excluded.TokenFingerprint,
                ParseStatus = excluded.ParseStatus,
                ProcessingState = excluded.ProcessingState,
                FailureStage = excluded.FailureStage,
                ErrorCategory = excluded.ErrorCategory,
                OAuthErrorCode = excluded.OAuthErrorCode,
                ServiceErrorDetail = excluded.ServiceErrorDetail,
                IsErrorCauseConfirmed = excluded.IsErrorCauseConfirmed,
                RetryCount = excluded.RetryCount,
                LastAttemptAt = excluded.LastAttemptAt,
                LastSuccessAt = excluded.LastSuccessAt,
                BackoffUntil = excluded.BackoffUntil,
                DuplicateOfRecordId = excluded.DuplicateOfRecordId,
                UpdatedAt = excluded.UpdatedAt,
                MicrosoftAccessTokenExpiresAt = excluded.MicrosoftAccessTokenExpiresAt,
                MinecraftAccessTokenExpiresAt = excluded.MinecraftAccessTokenExpiresAt,
                RefreshCredentialUpdatedAt = excluded.RefreshCredentialUpdatedAt,
                MinecraftAccessTokenReference = excluded.MinecraftAccessTokenReference,
                InPool = excluded.InPool,
                PoolJoinedAt = excluded.PoolJoinedAt;
            """;
        BindRecord(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM accounts WHERE Id = $Id LIMIT 1;";
        command.Parameters.AddWithValue("$Id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<AccountRecord>> GetByBatchIdAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM accounts WHERE BatchId = $BatchId ORDER BY SourceLine;";
        command.Parameters.AddWithValue("$BatchId", batchId.ToString());
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountRecord>> GetIncompleteByBatchIdAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM accounts
            WHERE BatchId = $BatchId
              AND ProcessingState NOT IN ($Succeeded, $Failed, $Cancelled, $Reauth, $Malformed)
            ORDER BY SourceLine;
            """;
        command.Parameters.AddWithValue("$BatchId", batchId.ToString());
        command.Parameters.AddWithValue("$Succeeded", (int)ProcessingState.Succeeded);
        command.Parameters.AddWithValue("$Failed", (int)ProcessingState.Failed);
        command.Parameters.AddWithValue("$Cancelled", (int)ProcessingState.Cancelled);
        command.Parameters.AddWithValue("$Reauth", (int)ProcessingState.ReauthenticationRequired);
        command.Parameters.AddWithValue("$Malformed", (int)ProcessingState.Malformed);
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountRecord>> GetRetryEligibleAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var retryable = new[]
        {
            ErrorCategory.AuthenticationTimeout,
            ErrorCategory.NetworkUnavailable,
            ErrorCategory.DnsFailure,
            ErrorCategory.TlsFailure,
            ErrorCategory.ProxyFailure,
            ErrorCategory.RateLimited,
            ErrorCategory.MicrosoftServiceError,
            ErrorCategory.MinecraftServiceError,
            ErrorCategory.VendorUnavailable,
            ErrorCategory.VendorRateLimited
        };

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM accounts
            WHERE BatchId = $BatchId
              AND ProcessingState IN ($Failed, $Backoff)
              AND ErrorCategory IN ($E0,$E1,$E2,$E3,$E4,$E5,$E6,$E7,$E8,$E9)
            ORDER BY SourceLine;
            """;
        command.Parameters.AddWithValue("$BatchId", batchId.ToString());
        command.Parameters.AddWithValue("$Failed", (int)ProcessingState.Failed);
        command.Parameters.AddWithValue("$Backoff", (int)ProcessingState.Backoff);
        for (var i = 0; i < retryable.Length; i++)
        {
            command.Parameters.AddWithValue("$E" + i, (int)retryable[i]);
        }

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountRecord>> GetAllSucceededAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM accounts
            WHERE ProcessingState = $Succeeded
            ORDER BY LastSuccessAt DESC, UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$Succeeded", (int)ProcessingState.Succeeded);
        var all = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<AccountRecord>();
        foreach (var record in all)
        {
            var key = !string.IsNullOrWhiteSpace(record.AuthenticatedMinecraftUuid)
                ? record.AuthenticatedMinecraftUuid
                : record.AuthenticatedMinecraftUsername ?? record.Id.ToString();
            if (seen.Add(key))
            {
                deduped.Add(record);
            }
        }

        deduped.Sort((a, b) =>
        {
            var aExpiry = a.MinecraftAccessTokenExpiresAt ?? DateTimeOffset.MaxValue;
            var bExpiry = b.MinecraftAccessTokenExpiresAt ?? DateTimeOffset.MaxValue;
            return aExpiry.CompareTo(bExpiry);
        });

        return deduped;
    }

    public async Task<IReadOnlyList<AccountRecord>> GetAllWithStoredAccessTokensAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllSucceededAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(a => !string.IsNullOrWhiteSpace(a.MinecraftAccessTokenReference)).ToList();
    }

    public async Task<IReadOnlyList<AccountRecord>> GetPoolActiveWithStoredAccessTokensAsync(CancellationToken cancellationToken = default)
    {
        var pool = await GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return pool
            .Where(a => !string.IsNullOrWhiteSpace(a.MinecraftAccessTokenReference))
            .Where(a => !a.MinecraftAccessTokenExpiresAt.HasValue || a.MinecraftAccessTokenExpiresAt.Value > now)
            .ToList();
    }

    public async Task<IReadOnlyList<AccountRecord>> GetPoolAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM accounts
            WHERE InPool = 1
            ORDER BY PoolJoinedAt DESC, UpdatedAt DESC;
            """;
        var all = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
        return DedupePoolAccounts(all);
    }

    public async Task<AccountRecord?> GetPoolAccountByFingerprintAsync(string tokenFingerprint, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM accounts
            WHERE InPool = 1 AND TokenFingerprint = $Fingerprint
            ORDER BY PoolJoinedAt DESC, UpdatedAt DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Fingerprint", tokenFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE Id = $Id;";
        command.Parameters.AddWithValue("$Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<AccountRecord> DedupePoolAccounts(IReadOnlyList<AccountRecord> all)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<AccountRecord>();
        foreach (var record in all)
        {
            var key = !string.IsNullOrWhiteSpace(record.TokenFingerprint)
                ? record.TokenFingerprint
                : record.Id.ToString();
            if (seen.Add(key))
            {
                deduped.Add(record);
            }
        }

        deduped.Sort((a, b) =>
        {
            var aJoined = a.PoolJoinedAt ?? a.CreatedAt;
            var bJoined = b.PoolJoinedAt ?? b.CreatedAt;
            return bJoined.CompareTo(aJoined);
        });

        return deduped;
    }

    public async Task<IReadOnlyList<AccountRecord>> GetAllActiveWithStoredAccessTokensAsync(CancellationToken cancellationToken = default)
    {
        var withTokens = await GetAllWithStoredAccessTokensAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return withTokens
            .Where(a => !a.MinecraftAccessTokenExpiresAt.HasValue || a.MinecraftAccessTokenExpiresAt.Value > now)
            .ToList();
    }

    private static void BindRecord(SqliteCommand command, AccountRecord record)
    {
        command.Parameters.AddWithValue("$Id", record.Id.ToString());
        command.Parameters.AddWithValue("$BatchId", record.BatchId.ToString());
        command.Parameters.AddWithValue("$SourceFile", record.SourceFile);
        command.Parameters.AddWithValue("$SourceLine", record.SourceLine);
        command.Parameters.AddWithValue("$OriginalLine", record.OriginalLine);
        command.Parameters.AddWithValue("$OriginalInputForm", (int)record.OriginalInputForm);
        command.Parameters.AddWithValue("$ProvidedUsername", (object?)record.ProvidedUsername ?? DBNull.Value);
        command.Parameters.AddWithValue("$AuthenticatedMinecraftUsername", (object?)record.AuthenticatedMinecraftUsername ?? DBNull.Value);
        command.Parameters.AddWithValue("$AuthenticatedMinecraftUuid", (object?)record.AuthenticatedMinecraftUuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$ProvidedUsernameMismatch", record.ProvidedUsernameMismatch.HasValue
            ? record.ProvidedUsernameMismatch.Value ? 1 : 0
            : DBNull.Value);
        command.Parameters.AddWithValue("$CredentialReference", (object?)record.CredentialReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$TokenFingerprint", record.TokenFingerprint);
        command.Parameters.AddWithValue("$ParseStatus", (int)record.ParseStatus);
        command.Parameters.AddWithValue("$ProcessingState", (int)record.ProcessingState);
        command.Parameters.AddWithValue("$FailureStage", (int)record.FailureStage);
        command.Parameters.AddWithValue("$ErrorCategory", (int)record.ErrorCategory);
        command.Parameters.AddWithValue("$OAuthErrorCode", (object?)record.OAuthErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ServiceErrorDetail", (object?)record.ServiceErrorDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$IsErrorCauseConfirmed", record.IsErrorCauseConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$RetryCount", record.RetryCount);
        command.Parameters.AddWithValue("$LastAttemptAt", record.LastAttemptAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSuccessAt", record.LastSuccessAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$BackoffUntil", record.BackoffUntil?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$DuplicateOfRecordId", record.DuplicateOfRecordId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$CreatedAt", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", record.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$MicrosoftAccessTokenExpiresAt", record.MicrosoftAccessTokenExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$MinecraftAccessTokenExpiresAt", record.MinecraftAccessTokenExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$RefreshCredentialUpdatedAt", record.RefreshCredentialUpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$MinecraftAccessTokenReference", (object?)record.MinecraftAccessTokenReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$InPool", record.InPool ? 1 : 0);
        command.Parameters.AddWithValue("$PoolJoinedAt", record.PoolJoinedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static AccountRecord ReadRecord(SqliteDataReader reader)
    {
        bool? mismatch = reader.IsDBNull(reader.GetOrdinal("ProvidedUsernameMismatch"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("ProvidedUsernameMismatch")) == 1;

        return new AccountRecord
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            BatchId = Guid.Parse(reader.GetString(reader.GetOrdinal("BatchId"))),
            SourceFile = reader.GetString(reader.GetOrdinal("SourceFile")),
            SourceLine = reader.GetInt32(reader.GetOrdinal("SourceLine")),
            OriginalLine = reader.GetString(reader.GetOrdinal("OriginalLine")),
            OriginalInputForm = (OriginalInputForm)reader.GetInt32(reader.GetOrdinal("OriginalInputForm")),
            ProvidedUsername = reader.IsDBNull(reader.GetOrdinal("ProvidedUsername")) ? null : reader.GetString(reader.GetOrdinal("ProvidedUsername")),
            AuthenticatedMinecraftUsername = reader.IsDBNull(reader.GetOrdinal("AuthenticatedMinecraftUsername")) ? null : reader.GetString(reader.GetOrdinal("AuthenticatedMinecraftUsername")),
            AuthenticatedMinecraftUuid = reader.IsDBNull(reader.GetOrdinal("AuthenticatedMinecraftUuid")) ? null : reader.GetString(reader.GetOrdinal("AuthenticatedMinecraftUuid")),
            ProvidedUsernameMismatch = mismatch,
            CredentialReference = reader.IsDBNull(reader.GetOrdinal("CredentialReference")) ? null : reader.GetString(reader.GetOrdinal("CredentialReference")),
            TokenFingerprint = reader.GetString(reader.GetOrdinal("TokenFingerprint")),
            ParseStatus = (ParseStatus)reader.GetInt32(reader.GetOrdinal("ParseStatus")),
            ProcessingState = (ProcessingState)reader.GetInt32(reader.GetOrdinal("ProcessingState")),
            FailureStage = (FailureStage)reader.GetInt32(reader.GetOrdinal("FailureStage")),
            ErrorCategory = (ErrorCategory)reader.GetInt32(reader.GetOrdinal("ErrorCategory")),
            OAuthErrorCode = reader.IsDBNull(reader.GetOrdinal("OAuthErrorCode")) ? null : reader.GetString(reader.GetOrdinal("OAuthErrorCode")),
            ServiceErrorDetail = reader.IsDBNull(reader.GetOrdinal("ServiceErrorDetail")) ? null : reader.GetString(reader.GetOrdinal("ServiceErrorDetail")),
            IsErrorCauseConfirmed = reader.GetInt32(reader.GetOrdinal("IsErrorCauseConfirmed")) == 1,
            RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
            LastAttemptAt = reader.IsDBNull(reader.GetOrdinal("LastAttemptAt")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("LastAttemptAt"))),
            LastSuccessAt = reader.IsDBNull(reader.GetOrdinal("LastSuccessAt")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("LastSuccessAt"))),
            BackoffUntil = reader.IsDBNull(reader.GetOrdinal("BackoffUntil")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("BackoffUntil"))),
            DuplicateOfRecordId = reader.IsDBNull(reader.GetOrdinal("DuplicateOfRecordId")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("DuplicateOfRecordId"))),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
            MicrosoftAccessTokenExpiresAt = ReadOptionalDate(reader, "MicrosoftAccessTokenExpiresAt"),
            MinecraftAccessTokenExpiresAt = ReadOptionalDate(reader, "MinecraftAccessTokenExpiresAt"),
            RefreshCredentialUpdatedAt = ReadOptionalDate(reader, "RefreshCredentialUpdatedAt"),
            MinecraftAccessTokenReference = ReadOptionalString(reader, "MinecraftAccessTokenReference"),
            InPool = ReadOptionalBool(reader, "InPool") ?? false,
            PoolJoinedAt = ReadOptionalDate(reader, "PoolJoinedAt")
        };
    }

    private static bool? ReadOptionalBool(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) == 1;
    }

    private static string? ReadOptionalString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadOptionalDate(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private static async Task<IReadOnlyList<AccountRecord>> ReadAllAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<AccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadRecord(reader));
        }

        return list;
    }
}
