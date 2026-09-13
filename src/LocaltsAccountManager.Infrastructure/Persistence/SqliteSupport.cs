using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LocaltsAccountManager.Infrastructure.Persistence;

/// <summary>
/// Shared SQLite plumbing for the account and batch repositories, which point at the same
/// database file and are written concurrently by batch/pool workers.
/// </summary>
internal static class SqliteSupport
{
    /// <summary>Seconds Microsoft.Data.Sqlite keeps retrying a busy/locked database.</summary>
    private const int BusyTimeoutSeconds = 30;

    public static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // Without a timeout, a write that collides with another worker fails immediately with
            // SQLITE_BUSY and surfaces as a spurious per-account error.
            DefaultTimeout = BusyTimeoutSeconds
        }.ConnectionString;

    /// <summary>
    /// Opens a connection and applies the per-connection pragmas. Disposes the connection if
    /// opening or configuring it fails, so a failed open cannot leak a handle.
    /// </summary>
    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutSeconds * 1000};";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Switches the database to write-ahead logging so readers do not block the writer. The
    /// setting is persisted in the file, so this only needs running at initialisation.
    /// </summary>
    public static async Task EnableWriteAheadLoggingAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a timestamp written with the round-trip ("O") format. Culture-sensitive parsing was
    /// used before, which can fail outright under a non-Gregorian host culture.
    /// </summary>
    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Lenient variant for columns that may hold empty or damaged values.</summary>
    public static DateTimeOffset? TryParseTimestamp(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
