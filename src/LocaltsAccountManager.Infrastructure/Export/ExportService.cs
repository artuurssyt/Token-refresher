using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Export;

public sealed class ExportService : IExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IAccountRepository _accountRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly IAppSettingsStore _settingsStore;

    public ExportService(
        IAccountRepository accountRepository,
        IBatchRepository batchRepository,
        ISecureCredentialStore credentialStore,
        IAppSettingsStore settingsStore)
    {
        _accountRepository = accountRepository;
        _batchRepository = batchRepository;
        _credentialStore = credentialStore;
        _settingsStore = settingsStore;
    }

    public async Task<string> ExportSuccessfulUsernamesAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var settings = _settingsStore.Load();
        var usernames = accounts
            .Where(a => a.ProcessingState == ProcessingState.Succeeded && !string.IsNullOrWhiteSpace(a.AuthenticatedMinecraftUsername))
            .Select(a => a.AuthenticatedMinecraftUsername!)
            .ToList();

        if (settings.ExportUniqueUsernamesOnly)
        {
            usernames = usernames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var path = Path.Combine(ResolveOutputDirectory(batch, outputDirectory), "successful_usernames.txt");
        await File.WriteAllLinesAsync(path, usernames, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportSuccessfulMinecraftAccessTokensAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var succeeded = accounts.Where(a => a.ProcessingState == ProcessingState.Succeeded).ToList();
        var outDir = ResolveOutputDirectory(batch, outputDirectory);

        var txtLines = new List<string>
        {
            "# SENSITIVE: Minecraft access tokens (JWT). ~24h lifetime. Do not share or upload."
        };
        var jsonEntries = new List<object>();

        foreach (var account in succeeded)
        {
            var token = await ReadMinecraftAccessTokenAsync(account, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var username = account.AuthenticatedMinecraftUsername ?? account.ProvidedUsername ?? "Unknown";
            txtLines.Add($"{username}:{token}");
            jsonEntries.Add(new
            {
                username,
                uuid = account.AuthenticatedMinecraftUuid,
                accessToken = token,
                expiresAt = account.MinecraftAccessTokenExpiresAt?.ToString("O"),
                sourceLine = account.SourceLine
            });
        }

        var txtPath = Path.Combine(outDir, "successful_minecraft_access_tokens.txt");
        await File.WriteAllLinesAsync(txtPath, txtLines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var jsonPath = Path.Combine(outDir, "successful_minecraft_access_tokens.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(jsonEntries, JsonOptions), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        return txtPath;
    }

    public async Task<string> ExportSuccessfulRefreshTokensAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var succeeded = accounts.Where(a => a.ProcessingState == ProcessingState.Succeeded).ToList();
        var lines = new List<string>
        {
            "# SENSITIVE: Microsoft refresh tokens. Keep private — each success may have rotated the refresh."
        };

        foreach (var account in succeeded)
        {
            if (string.IsNullOrWhiteSpace(account.CredentialReference))
            {
                continue;
            }

            var refresh = await _credentialStore.RetrieveAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refresh))
            {
                continue;
            }

            var username = account.AuthenticatedMinecraftUsername ?? account.ProvidedUsername ?? "Unknown";
            lines.Add($"{username}:{refresh}");
        }

        var path = Path.Combine(ResolveOutputDirectory(batch, outputDirectory), "successful_refresh_tokens.txt");
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<ExportZipResult> ExportAccessTokensZipAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var withTokens = accounts.Where(a =>
            a.ProcessingState == ProcessingState.Succeeded &&
            !string.IsNullOrWhiteSpace(a.MinecraftAccessTokenReference)).ToList();
        var outDir = ResolveOutputDirectory(batch, outputDirectory);
        return await WriteAccessTokensZipAsync(withTokens, outDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportZipResult> ExportLibraryAccessTokensZipAsync(string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var active = await _accountRepository.GetAllActiveWithStoredAccessTokensAsync(cancellationToken).ConfigureAwait(false);
        var outDir = ResolveLibraryOutputDirectory(_settingsStore, outputDirectory);
        return await WriteAccessTokensZipAsync(active, outDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportZipResult> ExportPoolAccessTokensZipAsync(string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var pool = await _accountRepository.GetPoolActiveWithStoredAccessTokensAsync(cancellationToken).ConfigureAwait(false);
        var outDir = ResolveLibraryOutputDirectory(_settingsStore, outputDirectory);
        return await WriteAccessTokensZipAsync(pool, outDir, "pool_access_tokens", cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportPoolRefreshTokensAsync(string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var pool = await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "# SENSITIVE: Microsoft refresh tokens from Pool. Keep private — each refresh may rotate the token."
        };

        var written = 0;
        foreach (var account in pool)
        {
            if (string.IsNullOrWhiteSpace(account.CredentialReference))
            {
                continue;
            }

            var refresh = await _credentialStore.RetrieveAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refresh))
            {
                continue;
            }

            var username = account.AuthenticatedMinecraftUsername ?? account.ProvidedUsername ?? "Unknown";
            lines.Add($"{username}:{refresh}");
            written++;
        }

        if (written == 0)
        {
            throw new InvalidOperationException("No pool accounts with stored refresh tokens to export.");
        }

        var outDir = ResolveLibraryOutputDirectory(_settingsStore, outputDirectory);
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"pool_refresh_tokens_{written}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private async Task<ExportZipResult> WriteAccessTokensZipAsync(
        IReadOnlyList<AccountRecord> accounts,
        string outDir,
        CancellationToken cancellationToken) =>
        await WriteAccessTokensZipAsync(accounts, outDir, "access_tokens", cancellationToken).ConfigureAwait(false);

    private async Task<ExportZipResult> WriteAccessTokensZipAsync(
        IReadOnlyList<AccountRecord> accounts,
        string outDir,
        string zipPrefix,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "LocaltsAccountManager", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempDir);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        var skipped = 0;

        try
        {
            foreach (var account in accounts)
            {
                var token = await ReadMinecraftAccessTokenAsync(account, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    skipped++;
                    continue;
                }

                var baseName = account.AuthenticatedMinecraftUsername ?? account.ProvidedUsername ?? $"line{account.SourceLine}";
                var fileName = UniqueTxtFileName(baseName, usedNames);
                await File.WriteAllTextAsync(Path.Combine(tempDir, fileName), token, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                written++;
            }

            if (written == 0)
            {
                throw new InvalidOperationException(BuildMissingTokenMessage(accounts.Count, skipped));
            }

            var zipPath = Path.Combine(outDir, $"{zipPrefix}_{written}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            return new ExportZipResult
            {
                Path = zipPath,
                ExportedCount = written,
                SkippedCount = skipped
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string BuildMissingTokenMessage(int candidateCount, int missingTokenCount)
    {
        if (candidateCount == 0)
        {
            return "No tokens ready yet. Switch to the Batch tab and start processing — then come back here anytime to export what's ready.";
        }

        return $"{candidateCount} account(s) listed but none have stored tokens yet. Keep the batch running, then export again as accounts finish.";
    }

    private static string ResolveLibraryOutputDirectory(IAppSettingsStore settingsStore, string? outputDirectory) =>
        ExportDirectoryResolver.Resolve(settingsStore, outputDirectory);

    private string ResolveOutputDirectory(BatchRecord batch, string? outputDirectory) =>
        ExportDirectoryResolver.Resolve(_settingsStore, outputDirectory, batch.OutputDirectory);

    private static string UniqueTxtFileName(string baseName, ISet<string> used)
    {
        var sanitized = SanitizeFileName(baseName);
        var candidate = sanitized;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{sanitized}_{suffix}";
            suffix++;
        }

        return candidate + ".txt";
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "account";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "account" : sanitized;
    }

    public async Task<string> ExportErrorsAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.AppendLine($"Batch started: {batch.StartedAt:O}");
        builder.AppendLine($"Total records: {batch.TotalRecords}");
        builder.AppendLine();

        foreach (var account in accounts.Where(a => a.ProcessingState is ProcessingState.Failed or ProcessingState.ReauthenticationRequired or ProcessingState.Malformed))
        {
            builder.AppendLine(new string('-', 32));
            builder.AppendLine($"Input line: {account.SourceLine}");
            builder.AppendLine($"Provided username: {account.ProvidedUsername ?? "Unknown"}");
            builder.AppendLine($"Status: Failed");
            builder.AppendLine($"Stage: {account.FailureStage}");
            builder.AppendLine($"Reason: {account.ErrorCategory}");
            if (!string.IsNullOrWhiteSpace(account.OAuthErrorCode))
            {
                builder.AppendLine($"OAuth error: {account.OAuthErrorCode}");
            }

            if (!string.IsNullOrWhiteSpace(account.ServiceErrorDetail))
            {
                builder.AppendLine($"Detail: {account.ServiceErrorDetail}");
            }

            if (account.ProvidedUsernameMismatch == true)
            {
                builder.AppendLine("Provided username mismatch: true");
            }

            builder.AppendLine($"Cause confirmed: {account.IsErrorCauseConfirmed}");
            builder.AppendLine();
        }

        var path = Path.Combine(ResolveOutputDirectory(batch, outputDirectory), "errors.txt");
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportDetailedResultsAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var builder = new StringBuilder();

        foreach (var account in accounts.Where(a => a.ProcessingState == ProcessingState.Succeeded))
        {
            builder.AppendLine($"Input line: {account.SourceLine}");
            builder.AppendLine($"Provided username: {account.ProvidedUsername ?? "Unknown"}");
            builder.AppendLine($"Authenticated username: {account.AuthenticatedMinecraftUsername ?? "Unknown"}");
            builder.AppendLine($"Authenticated UUID: {account.AuthenticatedMinecraftUuid ?? "Unknown"}");
            if (account.MinecraftAccessTokenExpiresAt.HasValue)
            {
                builder.AppendLine($"Minecraft access expires at (UTC): {account.MinecraftAccessTokenExpiresAt:O}");
                builder.AppendLine($"Minecraft access remaining: {Core.TokenExpiryFormatter.FormatRemaining(account.MinecraftAccessTokenExpiresAt)}");
            }
            if (account.MicrosoftAccessTokenExpiresAt.HasValue)
            {
                builder.AppendLine($"MSA access expires at (UTC): {account.MicrosoftAccessTokenExpiresAt:O}");
            }
            if (account.RefreshCredentialUpdatedAt.HasValue)
            {
                builder.AppendLine($"Refresh credential updated at (UTC): {account.RefreshCredentialUpdatedAt:O}");
            }
            builder.AppendLine($"Minecraft access token stored: {!string.IsNullOrWhiteSpace(account.MinecraftAccessTokenReference)}");
            if (account.ProvidedUsernameMismatch == true)
            {
                builder.AppendLine("Provided username mismatch: true");
            }
            builder.AppendLine("Status: Success");
            builder.AppendLine();
        }

        var path = Path.Combine(ResolveOutputDirectory(batch, outputDirectory), "results.txt");
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportFailedOriginalRecordsAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var lines = accounts
            .Where(a => a.ProcessingState is ProcessingState.Failed or ProcessingState.ReauthenticationRequired or ProcessingState.Malformed)
            .Select(a => a.OriginalLine)
            .ToList();

        var path = Path.Combine(ResolveOutputDirectory(batch, outputDirectory), "failed_records.txt");
        var header = "# SENSITIVE: This file contains original credential lines. Do not attach to bug reports or upload anywhere.";
        await File.WriteAllLinesAsync(path, new[] { header }.Concat(lines), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private async Task<string?> ReadMinecraftAccessTokenAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.MinecraftAccessTokenReference))
        {
            return null;
        }

        return await _credentialStore.RetrieveAsync(account.MinecraftAccessTokenReference, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BatchRecord> RequireBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await _batchRepository.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch == null)
        {
            throw new InvalidOperationException($"Batch {batchId} was not found.");
        }

        return batch;
    }
}
