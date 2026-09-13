using System.Collections.Concurrent;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Diagnostics;
using LocaltsAccountManager.Infrastructure.Minecraft;

namespace LocaltsAccountManager.Infrastructure.Processing;

public sealed class BatchProcessor : IBatchProcessor
{
    private readonly IAccountRepository _accountRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly IMicrosoftAuthAdapter _microsoftAuthAdapter;
    private readonly IMinecraftIdentityAdapter _minecraftIdentityAdapter;
    private readonly IMinecraftProfileLookupService _profileLookupService;
    private readonly RetryPolicy _retryPolicy;
    private readonly ThrottleCoordinator _throttleCoordinator;
    private readonly SecretSafeLogger _logger;
    private readonly ProcessingArbiter _arbiter;
    private readonly ConcurrentDictionary<Guid, CachedMicrosoftAccess> _microsoftAccessCache = new();

    /// <summary>Longest the coordinator loop idles before re-checking backoff and rate-limit state.</summary>
    private static readonly TimeSpan MaxIdlePoll = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    private readonly record struct CachedMicrosoftAccess(string AccessToken, DateTimeOffset ExpiresAt);

    public BatchProcessor(
        IAccountRepository accountRepository,
        IBatchRepository batchRepository,
        ISecureCredentialStore credentialStore,
        IMicrosoftAuthAdapter microsoftAuthAdapter,
        IMinecraftIdentityAdapter minecraftIdentityAdapter,
        IMinecraftProfileLookupService profileLookupService,
        RetryPolicy retryPolicy,
        ThrottleCoordinator throttleCoordinator,
        SecretSafeLogger logger,
        ProcessingArbiter arbiter)
    {
        _accountRepository = accountRepository;
        _batchRepository = batchRepository;
        _credentialStore = credentialStore;
        _microsoftAuthAdapter = microsoftAuthAdapter;
        _minecraftIdentityAdapter = minecraftIdentityAdapter;
        _profileLookupService = profileLookupService;
        _retryPolicy = retryPolicy;
        _throttleCoordinator = throttleCoordinator;
        _logger = logger;
        _arbiter = arbiter;
    }

    public event EventHandler<BatchProgressEventArgs>? ProgressChanged;

    public bool IsRunning => _arbiter.IsHeldBy(ProcessingArbiter.BatchOwner);

    public Task StartAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        AcquireRunSlot();
        return StartClaimedRun(batchId, cancellationToken);
    }

    public async Task CancelAsync()
    {
        // Snapshot both fields: the run's finally can null and dispose them at any moment.
        var cts = _runCts;
        var task = _runTask;

        if (cts != null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The run finished and disposed its token source before we got here.
            }
        }

        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task RetryFailedAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        // Hold the run slot across the prep writes too, otherwise the pool could start
        // mutating the same rows between the requeue pass and the actual run.
        AcquireRunSlot();

        Dictionary<Guid, AccountRecord> byId;
        try
        {
            var recovered = await RecoverPoolLinkedFailuresAsync(batchId, cancellationToken).ConfigureAwait(false);
            var retryable = await _accountRepository.GetRetryEligibleAsync(batchId, cancellationToken).ConfigureAwait(false);
            byId = retryable.ToDictionary(a => a.Id);
            foreach (var account in recovered)
            {
                byId[account.Id] = account;
            }

            if (byId.Count == 0)
            {
                throw new InvalidOperationException(
                    "No retryable failures. Re-import the TXT on Batch (Import TXT), then click Start Processing.");
            }

            await RequeueForRetryAsync(byId.Values, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _arbiter.Release(ProcessingArbiter.BatchOwner);
            throw;
        }

        await StartClaimedRun(batchId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequeueForRetryAsync(IEnumerable<AccountRecord> accounts, CancellationToken cancellationToken)
    {
        foreach (var account in accounts)
        {
            account.ProcessingState = ProcessingState.Queued;
            account.BackoffUntil = null;
            account.RetryCount = 0;
            account.ErrorCategory = ErrorCategory.None;
            account.FailureStage = FailureStage.None;
            account.OAuthErrorCode = null;
            if (account.ServiceErrorDetail?.Contains("Already in pool", StringComparison.OrdinalIgnoreCase) == true
                || account.ServiceErrorDetail?.Contains("Also in pool", StringComparison.OrdinalIgnoreCase) == true)
            {
                account.ServiceErrorDetail = null;
            }

            account.IsErrorCauseConfirmed = false;
            _microsoftAccessCache.TryRemove(account.Id, out _);
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Claims the shared run slot, translating a failed claim into the message the UI expects.
    /// </summary>
    private void AcquireRunSlot()
    {
        if (_arbiter.TryAcquire(ProcessingArbiter.BatchOwner))
        {
            return;
        }

        throw new InvalidOperationException(
            _arbiter.IsHeldBy(ProcessingArbiter.PoolOwner)
                ? "Pool refresh is running. Wait for it to finish before starting batch processing."
                : "A batch is already running. Wait for it to finish or click Cancel first.");
    }

    /// <summary>Starts a run that already owns the slot; the run releases it when it finishes.</summary>
    private Task StartClaimedRun(Guid batchId, CancellationToken cancellationToken)
    {
        try
        {
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunBatchAsync(batchId, _runCts.Token);
            return _runTask;
        }
        catch
        {
            ReleaseRunSlot();
            throw;
        }
    }

    private void ReleaseRunSlot()
    {
        var cts = _runCts;
        _runCts = null;
        cts?.Dispose();
        _arbiter.Release(ProcessingArbiter.BatchOwner);
    }

    /// <summary>
    /// Old imports marked "Already in pool" as Failed without a usable batch credential.
    /// Pull the refresh token from the pool account so Batch Start/Retry can refresh them.
    /// </summary>
    private async Task<List<AccountRecord>> RecoverPoolLinkedFailuresAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
        var recovered = new List<AccountRecord>();
        foreach (var account in accounts)
        {
            var looksLikePoolBlock =
                account.ProcessingState is ProcessingState.Failed or ProcessingState.Malformed
                && (account.ErrorCategory == ErrorCategory.MalformedInput
                    || account.ServiceErrorDetail?.Contains("Already in pool", StringComparison.OrdinalIgnoreCase) == true
                    || account.ServiceErrorDetail?.Contains("Also in pool", StringComparison.OrdinalIgnoreCase) == true
                    || account.DuplicateOfRecordId.HasValue);

            if (!looksLikePoolBlock)
            {
                continue;
            }

            AccountRecord? poolAccount = null;
            if (account.DuplicateOfRecordId.HasValue)
            {
                poolAccount = await _accountRepository.GetByIdAsync(account.DuplicateOfRecordId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (poolAccount is not null && poolAccount.BatchId == account.BatchId)
                {
                    poolAccount = null; // in-batch duplicate, not pool
                }
            }

            if (poolAccount is null && !string.IsNullOrWhiteSpace(account.TokenFingerprint))
            {
                poolAccount = await _accountRepository.GetPoolAccountByFingerprintAsync(account.TokenFingerprint, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (poolAccount is null || string.IsNullOrWhiteSpace(poolAccount.CredentialReference))
            {
                continue;
            }

            account.CredentialReference = poolAccount.CredentialReference;
            account.DuplicateOfRecordId = null;
            account.ParseStatus = ParseStatus.Parsed;
            account.ProvidedUsername ??= poolAccount.ProvidedUsername;
            account.AuthenticatedMinecraftUsername ??= poolAccount.AuthenticatedMinecraftUsername;
            account.AuthenticatedMinecraftUuid ??= poolAccount.AuthenticatedMinecraftUuid;
            recovered.Add(account);
        }

        return recovered;
    }

    public Task RefreshAccountAsync(Guid batchId, Guid accountId, CancellationToken cancellationToken = default)
    {
        AcquireRunSlot();

        try
        {
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RefreshSingleAccountAsync(batchId, accountId, _runCts.Token);
            return _runTask;
        }
        catch
        {
            ReleaseRunSlot();
            throw;
        }
    }

    private async Task RefreshSingleAccountAsync(Guid batchId, Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await _batchRepository.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Batch {batchId} was not found.");
            var account = await _accountRepository.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"Account {accountId} was not found.");
            if (account.BatchId != batchId)
            {
                throw new InvalidOperationException("Account does not belong to the current batch.");
            }

            PrepareAccountForRefresh(account);
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);

            batch.Status = BatchStatus.Running;
            batch.StartedAt ??= DateTimeOffset.UtcNow;
            _throttleCoordinator.Initialize(batch.ConcurrencyLimit);
            await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);

            var queue = new ConcurrentQueue<AccountRecord>();
            await ProcessAccountAsync(batch, account, queue, cancellationToken).ConfigureAwait(false);

            batch = await RecalculateBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
            batch.Status = BatchStatus.Completed;
            batch.CompletedAt = DateTimeOffset.UtcNow;
            await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
            RaiseProgress(batch, account);
        }
        finally
        {
            ReleaseRunSlot();
        }
    }

    private void PrepareAccountForRefresh(AccountRecord account)
    {
        account.ProcessingState = ProcessingState.Queued;
        account.BackoffUntil = null;
        account.RetryCount = 0;
        account.ErrorCategory = ErrorCategory.None;
        account.FailureStage = FailureStage.None;
        account.OAuthErrorCode = null;
        account.ServiceErrorDetail = null;
        account.IsErrorCauseConfirmed = false;
        _microsoftAccessCache.TryRemove(account.Id, out _);
    }

    private async Task RunBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await _batchRepository.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (batch == null)
            {
                throw new InvalidOperationException($"Batch {batchId} was not found.");
            }

            batch.Status = BatchStatus.Running;
            batch.StartedAt ??= DateTimeOffset.UtcNow;
            _throttleCoordinator.Initialize(batch.ConcurrencyLimit);
            await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);

            var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);

            // Fix leftover "Already in pool" Failed rows so Start Processing works without re-import.
            var recovered = await RecoverPoolLinkedFailuresAsync(batchId, cancellationToken).ConfigureAwait(false);
            foreach (var account in recovered)
            {
                account.ProcessingState = ProcessingState.Queued;
                account.BackoffUntil = null;
                account.RetryCount = 0;
                account.ErrorCategory = ErrorCategory.None;
                account.FailureStage = FailureStage.None;
                account.ServiceErrorDetail = null;
                account.IsErrorCauseConfirmed = false;
                _microsoftAccessCache.TryRemove(account.Id, out _);
                await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            }

            accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
            foreach (var account in accounts.Where(NeedsAccessTokenRefresh))
            {
                account.ProcessingState = ProcessingState.Queued;
                account.BackoffUntil = null;
                _microsoftAccessCache.TryRemove(account.Id, out _);
                await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            }

            var processable = await _accountRepository.GetIncompleteByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
            // Workers re-enqueue their own account on retryable failures, so the queue is written
            // from several threads at once and must be concurrent.
            var queue = new ConcurrentQueue<AccountRecord>(
                processable.Where(a => a.ProcessingState is ProcessingState.Queued or ProcessingState.Backoff));
            var workers = new List<Task>();

            try
            {
                while (!queue.IsEmpty || workers.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await _throttleCoordinator.WaitForGlobalLimitAsync(cancellationToken).ConfigureAwait(false);

                    HarvestCompletedWorkers(workers);
                    var dispatched = 0;
                    while (workers.Count < _throttleCoordinator.EffectiveConcurrency && queue.TryDequeue(out var account))
                    {
                        if (account.BackoffUntil.HasValue && account.BackoffUntil.Value > DateTimeOffset.UtcNow)
                        {
                            // Rotate to the tail so ready accounts behind it still get a turn.
                            queue.Enqueue(account);
                            break;
                        }

                        if (account.DuplicateOfRecordId.HasValue)
                        {
                            var original = await _accountRepository.GetByIdAsync(account.DuplicateOfRecordId.Value, cancellationToken)
                                .ConfigureAwait(false);
                            // Only skip true in-batch duplicates. Pool overlaps are allowed to refresh here.
                            if (original is not null && original.BatchId == account.BatchId)
                            {
                                account.ProcessingState = ProcessingState.Failed;
                                account.FailureStage = FailureStage.Parse;
                                account.ErrorCategory = ErrorCategory.MalformedInput;
                                account.ServiceErrorDetail = $"Possible duplicate of record {account.DuplicateOfRecordId.Value}.";
                                account.IsErrorCauseConfirmed = true;
                                await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                        }

                        workers.Add(ProcessAccountAsync(batch, account, queue, cancellationToken));
                        dispatched++;
                    }

                    if (workers.Count > 0)
                    {
                        await Task.WhenAny(workers).ConfigureAwait(false);
                    }
                    else if (!queue.IsEmpty && dispatched == 0)
                    {
                        // Everything left is still in backoff; sleep until the soonest one is due
                        // instead of spinning dequeue/enqueue every 500ms.
                        await Task.Delay(ComputeIdleDelay(queue), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Fall through and finalise the batch as Cancelled below.
            }
            finally
            {
                // In-flight workers hold the linked token; let them observe cancellation and finish
                // before ReleaseRunSlot disposes the source underneath them.
                await DrainWorkersAsync(workers).ConfigureAwait(false);
            }

            // Finalise with None: using the cancelled token here left the batch stuck at Running,
            // which then looked like an interrupted batch on the next launch.
            var cancelled = cancellationToken.IsCancellationRequested;
            batch = await RecalculateBatchAsync(batchId, CancellationToken.None).ConfigureAwait(false);
            batch.Status = cancelled ? BatchStatus.Cancelled : BatchStatus.Completed;
            batch.CompletedAt = DateTimeOffset.UtcNow;
            if (cancelled)
            {
                batch.CancelledAt = DateTimeOffset.UtcNow;
            }

            await _batchRepository.UpsertAsync(batch, CancellationToken.None).ConfigureAwait(false);
            RaiseProgress(batch);
        }
        finally
        {
            ReleaseRunSlot();
        }
    }

    /// <summary>
    /// Drops finished workers from the tracking list, touching <see cref="Task.Exception"/> so a
    /// fault that escaped <see cref="ProcessAccountAsync"/> is observed and logged rather than
    /// silently discarded.
    /// </summary>
    private void HarvestCompletedWorkers(List<Task> workers)
    {
        for (var i = workers.Count - 1; i >= 0; i--)
        {
            var worker = workers[i];
            if (!worker.IsCompleted)
            {
                continue;
            }

            workers.RemoveAt(i);
            if (worker.IsFaulted && worker.Exception is { } error)
            {
                _logger.LogInfo($"Batch worker faulted: {error.GetBaseException().GetType().Name}");
            }
        }
    }

    private async Task DrainWorkersAsync(List<Task> workers)
    {
        if (workers.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"Batch worker faulted while draining: {ex.GetType().Name}");
        }
        finally
        {
            workers.Clear();
        }
    }

    /// <summary>
    /// How long the coordinator may idle when nothing could be dispatched. Returns zero if any
    /// queued account is already due (the head may be in backoff while a later one is ready),
    /// otherwise the wait until the soonest backoff expires, capped so the loop keeps re-checking
    /// the global rate limit and cancellation.
    /// </summary>
    private static TimeSpan ComputeIdleDelay(ConcurrentQueue<AccountRecord> queue)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? soonest = null;

        foreach (var account in queue)
        {
            var until = account.BackoffUntil;
            if (!until.HasValue || until.Value <= now)
            {
                return TimeSpan.Zero;
            }

            var wait = until.Value - now;
            if (soonest is null || wait < soonest.Value)
            {
                soonest = wait;
            }
        }

        if (soonest is not { } delay)
        {
            return TimeSpan.Zero;
        }

        return delay < MaxIdlePoll ? delay : MaxIdlePoll;
    }

    private async Task ProcessAccountAsync(
        BatchRecord batch,
        AccountRecord account,
        ConcurrentQueue<AccountRecord> queue,
        CancellationToken cancellationToken)
    {
        try
        {
            account.ProcessingState = ProcessingState.Authenticating;
            account.LastAttemptAt = DateTimeOffset.UtcNow;
            account.RetryCount++;
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            RaiseProgress(batch, account);

            if (account.ParseStatus != ParseStatus.Parsed || string.IsNullOrWhiteSpace(account.CredentialReference))
            {
                await FailAsync(batch, account, queue, FailureStage.Parse, ErrorCategory.MalformedInput, "Record was not parsed successfully.", true, false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var credential = await _credentialStore.RetrieveAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
            {
                await FailAsync(batch, account, queue, FailureStage.Authentication, ErrorCategory.MissingCredential, "Credential could not be retrieved from secure storage.", true, false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!_microsoftAuthAdapter.IsConfigured || !_minecraftIdentityAdapter.IsConfigured)
            {
                await FailAsync(batch, account, queue, FailureStage.Authentication, ErrorCategory.ConfigurationRequired,
                    "Authentication profile is not verified. Complete Phase 0 and configure authentication_profile.json.", true, false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            string microsoftAccessToken;
            if (_microsoftAccessCache.TryGetValue(account.Id, out var cachedAccess) &&
                cachedAccess.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                microsoftAccessToken = cachedAccess.AccessToken;
                account.MicrosoftAccessTokenExpiresAt = cachedAccess.ExpiresAt;
            }
            else
            {
                var msResult = await _microsoftAuthAdapter.AuthenticateAsync(credential, cancellationToken).ConfigureAwait(false);
                if (!msResult.Success)
                {
                    _microsoftAccessCache.TryRemove(account.Id, out _);
                    await HandleAuthFailureAsync(batch, account, msResult, queue, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(msResult.MicrosoftAccessToken))
                {
                    await FailAsync(batch, account, queue, FailureStage.Authentication, ErrorCategory.CredentialRejected,
                        "Microsoft authentication succeeded but no access token was returned.", false, false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(msResult.ReplacementRefreshToken))
                {
                    account.CredentialReference = await _credentialStore
                        .ReplaceAsync(account.CredentialReference, msResult.ReplacementRefreshToken, cancellationToken)
                        .ConfigureAwait(false);
                    account.RefreshCredentialUpdatedAt = DateTimeOffset.UtcNow;
                }

                microsoftAccessToken = msResult.MicrosoftAccessToken;
                var expiresAt = msResult.MicrosoftAccessTokenExpiresAt ?? DateTimeOffset.UtcNow.AddHours(1);
                account.MicrosoftAccessTokenExpiresAt = expiresAt;
                _microsoftAccessCache[account.Id] = new CachedMicrosoftAccess(microsoftAccessToken, expiresAt);
            }

            var mcResult = await _minecraftIdentityAdapter.RetrieveIdentityAsync(microsoftAccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (!mcResult.Success)
            {
                await HandleAuthFailureAsync(batch, account, mcResult, queue, cancellationToken).ConfigureAwait(false);
                return;
            }

            _microsoftAccessCache.TryRemove(account.Id, out _);

            account.AuthenticatedMinecraftUsername = mcResult.AuthenticatedMinecraftUsername;
            account.AuthenticatedMinecraftUuid = mcResult.AuthenticatedMinecraftUuid;
            account.MinecraftAccessTokenExpiresAt = mcResult.MinecraftAccessTokenExpiresAt;
            if (!string.IsNullOrWhiteSpace(mcResult.MinecraftAccessToken))
            {
                if (!string.IsNullOrWhiteSpace(account.MinecraftAccessTokenReference))
                {
                    await _credentialStore.DeleteAsync(account.MinecraftAccessTokenReference, cancellationToken).ConfigureAwait(false);
                }

                account.MinecraftAccessTokenReference = await _credentialStore
                    .StoreAsync(mcResult.MinecraftAccessToken, cancellationToken)
                    .ConfigureAwait(false);
            }

            await EnrichProfileAsync(account, cancellationToken).ConfigureAwait(false);
            MinecraftProfileEnrichment.ApplyUsernameComparison(account);
            account.ServiceErrorDetail = MinecraftProfileEnrichment.BuildMismatchNote(account);
            account.ProcessingState = ProcessingState.Succeeded;
            account.LastSuccessAt = DateTimeOffset.UtcNow;
            account.ErrorCategory = ErrorCategory.None;
            account.FailureStage = FailureStage.None;
            account.OAuthErrorCode = null;
            account.IsErrorCauseConfirmed = false;
            _throttleCoordinator.RegisterSuccessWindow();
            await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Persist with None: the token is already cancelled, so passing it here meant the
            // Cancelled state never reached the database.
            account.ProcessingState = ProcessingState.Cancelled;
            account.ErrorCategory = ErrorCategory.Cancelled;
            await PersistAccountAndUpdateBatchAsync(batch, account, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"Unexpected error for record {account.Id}: {ex.GetType().Name}");
            await FailAsync(batch, account, queue, FailureStage.Authentication, ErrorCategory.Unknown, ex.Message, false, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task EnrichProfileAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.AuthenticatedMinecraftUsername))
        {
            return;
        }

        try
        {
            var lookupInput = account.AuthenticatedMinecraftUuid ?? account.AuthenticatedMinecraftUsername;
            var identity = await _profileLookupService.ResolveAsync(lookupInput!, cancellationToken).ConfigureAwait(false);
            if (identity is null)
            {
                return;
            }

            MinecraftProfileEnrichment.ApplyIdentity(account, identity);
        }
        catch (MinecraftApiException ex)
        {
            _logger.LogInfo($"Public profile lookup failed for record {account.Id}: {ex.Message}");
        }
    }

    private async Task HandleAuthFailureAsync(
        BatchRecord batch,
        AccountRecord account,
        AuthenticationResult result,
        ConcurrentQueue<AccountRecord> queue,
        CancellationToken cancellationToken)
    {
        account.FailureStage = result.FailureStage;
        account.ErrorCategory = result.ErrorCategory;
        account.OAuthErrorCode = result.OAuthErrorCode;
        account.ServiceErrorDetail = result.ServiceErrorDetail;
        account.IsErrorCauseConfirmed = result.IsErrorCauseConfirmed;

        if (result.ErrorCategory == ErrorCategory.RateLimited)
        {
            _throttleCoordinator.RegisterRateLimit(result.RetryAfter);
            batch.Status = BatchStatus.PausedRateLimited;
            batch.GlobalRateLimitUntil = _throttleCoordinator.GlobalRateLimitUntil;
        }

        if (result.ErrorCategory == ErrorCategory.UserInteractionRequired)
        {
            _microsoftAccessCache.TryRemove(account.Id, out _);
            account.ProcessingState = ProcessingState.ReauthenticationRequired;
            await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.IsRetryable && _retryPolicy.CanRetry(account, result.ErrorCategory))
        {
            var backoff = _retryPolicy.ComputeBackoff(account, result.RetryAfter);
            if (result.ErrorCategory == ErrorCategory.RateLimited &&
                _throttleCoordinator.GlobalRateLimitUntil is { } until)
            {
                var globalWait = until - DateTimeOffset.UtcNow;
                if (globalWait > backoff)
                {
                    backoff = globalWait;
                }
            }

            account.ProcessingState = ProcessingState.Backoff;
            account.BackoffUntil = DateTimeOffset.UtcNow.Add(backoff);
            await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
            queue.Enqueue(account);
            return;
        }

        _microsoftAccessCache.TryRemove(account.Id, out _);

        account.ProcessingState = ProcessingState.Failed;
        await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(
        BatchRecord batch,
        AccountRecord account,
        ConcurrentQueue<AccountRecord> queue,
        FailureStage stage,
        ErrorCategory category,
        string detail,
        bool confirmed,
        bool retryable,
        CancellationToken cancellationToken)
    {
        account.FailureStage = stage;
        account.ErrorCategory = category;
        account.ServiceErrorDetail = detail;
        account.IsErrorCauseConfirmed = confirmed;

        // Only enter Backoff when the policy will actually retry, and re-queue when we do.
        // Previously a retryable=true call with a non-retryable category (e.g. Unknown from the
        // catch-all) parked the record in Backoff with no BackoffUntil and never re-queued it, so
        // it was counted as Pending forever and the batch "completed" with work outstanding.
        if (retryable && _retryPolicy.CanRetry(account, category))
        {
            account.ProcessingState = ProcessingState.Backoff;
            account.BackoffUntil = DateTimeOffset.UtcNow.Add(_retryPolicy.ComputeBackoff(account, null));
            await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
            queue.Enqueue(account);
            return;
        }

        account.ProcessingState = ProcessingState.Failed;
        await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAccountAndUpdateBatchAsync(BatchRecord batch, AccountRecord account, CancellationToken cancellationToken)
    {
        await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
        batch = await RecalculateBatchAsync(batch.Id, cancellationToken).ConfigureAwait(false);
        RaiseProgress(batch, account);
    }

    private async Task<BatchRecord> RecalculateBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await _batchRepository.GetByIdAsync(batchId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Batch disappeared during processing.");
        var accounts = await _accountRepository.GetByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);

        batch.Succeeded = accounts.Count(a => a.ProcessingState == ProcessingState.Succeeded);
        batch.Failed = accounts.Count(a => a.ProcessingState is ProcessingState.Failed or ProcessingState.ReauthenticationRequired or ProcessingState.Malformed);
        batch.Cancelled = accounts.Count(a => a.ProcessingState == ProcessingState.Cancelled);
        batch.Pending = accounts.Count(a => a.ProcessingState is ProcessingState.Queued or ProcessingState.Backoff or ProcessingState.Authenticating or ProcessingState.Pending);
        batch.EffectiveConcurrency = _throttleCoordinator.EffectiveConcurrency;
        batch.GlobalRateLimitUntil = _throttleCoordinator.GlobalRateLimitUntil;
        await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
        return batch;
    }

    private void RaiseProgress(BatchRecord batch, AccountRecord? account = null) =>
        ProgressChanged?.Invoke(this, new BatchProgressEventArgs
        {
            BatchId = batch.Id,
            Batch = batch,
            LastUpdatedAccount = account
        });

    private static bool NeedsAccessTokenRefresh(AccountRecord account) =>
        account.ProcessingState == ProcessingState.Succeeded &&
        string.IsNullOrWhiteSpace(account.MinecraftAccessTokenReference);
}
