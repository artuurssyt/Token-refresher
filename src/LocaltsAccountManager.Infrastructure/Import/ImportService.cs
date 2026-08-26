using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Export;

namespace LocaltsAccountManager.Infrastructure.Import;

public enum ImportLineOutcome
{
    Imported,
    UpdatedPoolCredential,
    DuplicateInBatch,
    Malformed,
    SkippedBlank
}

public sealed class ImportService : IImportService
{
    private readonly ITxtCredentialParser _parser;
    private readonly IAccountRepository _accountRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILocaltsService _localtsService;

    public ImportService(
        ITxtCredentialParser parser,
        IAccountRepository accountRepository,
        IBatchRepository batchRepository,
        ISecureCredentialStore credentialStore,
        IAppSettingsStore settingsStore,
        ILocaltsService localtsService)
    {
        _parser = parser;
        _accountRepository = accountRepository;
        _batchRepository = batchRepository;
        _credentialStore = credentialStore;
        _settingsStore = settingsStore;
        _localtsService = localtsService;
    }

    public Task<BatchRecord> ImportFileAsync(
        string filePath,
        ImportDestination destination = ImportDestination.Pool,
        CancellationToken cancellationToken = default) =>
        ImportFileInternalAsync(filePath, destination, cancellationToken);

    public async Task<LocaltsImportSummary> ImportFromLocaltsAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var delay = TimeSpan.FromMilliseconds(Math.Max(100, settings.LocaltsImportRequestDelayMilliseconds));
        var me = await _localtsService.ValidateApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var allOrders = await _localtsService.FetchAllOrdersAsync(cancellationToken).ConfigureAwait(false);

        var batch = CreateLocaltsImportBatch(me.Username);
        var fingerprintMap = new Dictionary<string, Guid>();
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var malformed = 0;
        var packaged = 0;
        var pending = 0;
        var lineNumber = 0;

        foreach (var order in allOrders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            LocaltsOrderDetail detail;
            try
            {
                detail = await _localtsService.FetchOrderAsync(order.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }

            if (!string.Equals(detail.Status, "PACKAGED", StringComparison.OrdinalIgnoreCase))
            {
                if (detail.Status is "PENDING" or "PACKAGING")
                {
                    pending++;
                }
                else
                {
                    skipped++;
                }

                continue;
            }

            packaged++;
            foreach (var item in detail.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Content))
                {
                    skipped++;
                    continue;
                }

                foreach (var line in SplitItemContent(item.Content))
                {
                    lineNumber++;
                    var outcome = await ImportParsedLineAsync(
                        batch,
                        fingerprintMap,
                        lineNumber,
                        line,
                        $"localts://order/{detail.OrderId}/item/{item.Id}",
                        ImportDestination.Pool,
                        cancellationToken).ConfigureAwait(false);

                    switch (outcome)
                    {
                        case ImportLineOutcome.Imported:
                            imported++;
                            break;
                        case ImportLineOutcome.UpdatedPoolCredential:
                            updated++;
                            break;
                        case ImportLineOutcome.Malformed:
                            malformed++;
                            break;
                        default:
                            skipped++;
                            break;
                    }
                }
            }
        }

        await FinalizeBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        return new LocaltsImportSummary
        {
            Batch = batch,
            LocaltsUsername = me.Username,
            OrdersScanned = allOrders.Count,
            OrdersPackaged = packaged,
            OrdersPending = pending,
            ItemsImported = imported,
            ItemsUpdated = updated,
            ItemsSkipped = skipped,
            ItemsMalformed = malformed
        };
    }

    private async Task<BatchRecord> ImportFileInternalAsync(
        string filePath,
        ImportDestination destination,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Import file was not found.", filePath);
        }

        var settings = _settingsStore.Load();
        var batch = new BatchRecord
        {
            Name = Path.GetFileName(filePath),
            SourceFilePath = filePath,
            OutputDirectory = ExportDirectoryResolver.Resolve(_settingsStore),
            ConcurrencyLimit = settings.DefaultConcurrency,
            EffectiveConcurrency = settings.DefaultConcurrency
        };

        await using var stream = File.OpenRead(filePath);
        var parsedLines = _parser.ParseFile(filePath, stream);
        var fingerprintMap = new Dictionary<string, Guid>();

        foreach (var parsed in parsedLines)
        {
            if (parsed.ParseStatus == ParseStatus.SkippedBlank)
            {
                continue;
            }

            await ImportParsedLineAsync(
                batch,
                fingerprintMap,
                parsed.LineNumber,
                parsed.OriginalLine,
                filePath,
                destination,
                cancellationToken,
                parsed).ConfigureAwait(false);
        }

        await FinalizeBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        return batch;
    }

    internal BatchRecord CreateLocaltsImportBatch(string username) =>
        new()
        {
            Name = $"Localts import {username} {DateTime.Now:yyyy-MM-dd HH:mm}",
            SourceFilePath = "localts://orders",
            OutputDirectory = ExportDirectoryResolver.Resolve(_settingsStore),
            ConcurrencyLimit = _settingsStore.Load().DefaultConcurrency,
            EffectiveConcurrency = _settingsStore.Load().DefaultConcurrency
        };

    internal async Task FinalizeBatchAsync(BatchRecord batch, CancellationToken cancellationToken) =>
        await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);

    internal async Task<ImportLineOutcome> ImportParsedLineAsync(
        BatchRecord batch,
        Dictionary<string, Guid> fingerprintMap,
        int lineNumber,
        string originalLine,
        string sourceFile,
        ImportDestination destination,
        CancellationToken cancellationToken,
        ParsedCredentialLine? preParsed = null)
    {
        var parsed = preParsed ?? _parser.ParseLine(lineNumber, originalLine);
        if (parsed.ParseStatus == ParseStatus.SkippedBlank)
        {
            return ImportLineOutcome.SkippedBlank;
        }

        var addToPool = destination == ImportDestination.Pool;
        var isDuplicateInBatch = false;
        Guid? duplicateId = null;
        if (parsed.ParseStatus == ParseStatus.Parsed &&
            fingerprintMap.TryGetValue(parsed.TokenFingerprint, out var existingBatchId))
        {
            isDuplicateInBatch = true;
            duplicateId = existingBatchId;
        }

        var existingPoolAccount = addToPool && parsed.ParseStatus == ParseStatus.Parsed
            ? await _accountRepository.GetPoolAccountByFingerprintAsync(parsed.TokenFingerprint, cancellationToken)
                .ConfigureAwait(false)
            : null;

        var record = new AccountRecord
        {
            BatchId = batch.Id,
            SourceFile = sourceFile,
            SourceLine = lineNumber,
            OriginalLine = parsed.OriginalLine,
            OriginalInputForm = parsed.InputForm,
            ProvidedUsername = parsed.ProvidedUsername,
            TokenFingerprint = parsed.TokenFingerprint,
            ParseStatus = parsed.ParseStatus,
            ProcessingState = parsed.ParseStatus == ParseStatus.Parsed ? ProcessingState.Queued : ProcessingState.Malformed,
            FailureStage = parsed.ParseStatus == ParseStatus.Malformed ? FailureStage.Parse : FailureStage.None,
            ErrorCategory = parsed.ParseStatus == ParseStatus.Malformed ? ErrorCategory.MalformedInput : ErrorCategory.None,
            ServiceErrorDetail = parsed.ParseError,
            IsErrorCauseConfirmed = parsed.ParseStatus == ParseStatus.Malformed,
            DuplicateOfRecordId = duplicateId,
            InPool = false
        };

        ImportLineOutcome outcome;
        if (parsed.ParseStatus == ParseStatus.Parsed && !string.IsNullOrWhiteSpace(parsed.CredentialPayload))
        {
            if (addToPool && existingPoolAccount != null)
            {
                record.InPool = false;
                record.DuplicateOfRecordId = existingPoolAccount.Id;
                record.ProcessingState = ProcessingState.Failed;
                record.FailureStage = FailureStage.Parse;
                record.ErrorCategory = ErrorCategory.MalformedInput;
                record.ServiceErrorDetail =
                    $"Already in pool as {existingPoolAccount.AuthenticatedMinecraftUsername ?? existingPoolAccount.ProvidedUsername ?? "account"}. Pool credential updated.";
                record.IsErrorCauseConfirmed = true;
                batch.DuplicatesFlagged++;
                outcome = ImportLineOutcome.UpdatedPoolCredential;

                existingPoolAccount.CredentialReference = await _credentialStore
                    .ReplaceAsync(existingPoolAccount.CredentialReference!, parsed.CredentialPayload, cancellationToken)
                    .ConfigureAwait(false);
                existingPoolAccount.ProcessingState = ProcessingState.Queued;
                existingPoolAccount.BackoffUntil = null;
                existingPoolAccount.RetryCount = 0;
                existingPoolAccount.ErrorCategory = ErrorCategory.None;
                existingPoolAccount.FailureStage = FailureStage.None;
                existingPoolAccount.PoolJoinedAt ??= DateTimeOffset.UtcNow;
                await _accountRepository.UpsertAsync(existingPoolAccount, cancellationToken).ConfigureAwait(false);
            }
            else if (addToPool && !isDuplicateInBatch)
            {
                record.CredentialReference = await _credentialStore.StoreAsync(parsed.CredentialPayload, cancellationToken)
                    .ConfigureAwait(false);
                record.InPool = true;
                record.PoolJoinedAt = DateTimeOffset.UtcNow;
                fingerprintMap[parsed.TokenFingerprint] = record.Id;
                outcome = ImportLineOutcome.Imported;
            }
            else if (!addToPool && !isDuplicateInBatch)
            {
                record.CredentialReference = await _credentialStore.StoreAsync(parsed.CredentialPayload, cancellationToken)
                    .ConfigureAwait(false);
                record.InPool = false;
                outcome = ImportLineOutcome.Imported;
                fingerprintMap[parsed.TokenFingerprint] = record.Id;
            }
            else
            {
                record.CredentialReference = await _credentialStore.StoreAsync(parsed.CredentialPayload, cancellationToken)
                    .ConfigureAwait(false);
                batch.DuplicatesFlagged++;
                outcome = ImportLineOutcome.DuplicateInBatch;
            }
        }
        else if (parsed.ParseStatus == ParseStatus.Malformed)
        {
            record.ProcessingState = ProcessingState.Failed;
            outcome = ImportLineOutcome.Malformed;
        }
        else
        {
            outcome = ImportLineOutcome.SkippedBlank;
        }

        await _accountRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        batch.TotalRecords++;
        if (record.ProcessingState is ProcessingState.Queued or ProcessingState.Pending)
        {
            batch.Pending++;
        }
        else if (record.ProcessingState == ProcessingState.Failed)
        {
            batch.Failed++;
        }

        return outcome;
    }

    private static IEnumerable<string> SplitItemContent(string content)
    {
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? [content.Trim()] : lines;
    }
}
