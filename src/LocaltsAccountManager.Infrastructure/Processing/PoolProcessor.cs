using System.Collections.Concurrent;
using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Core.Processing;
using LocaltsAccountManager.Infrastructure.Diagnostics;
using LocaltsAccountManager.Infrastructure.Minecraft;

namespace LocaltsAccountManager.Infrastructure.Processing;

public sealed class PoolProcessor : IPoolProcessor
{
    private readonly IAccountRepository _accountRepository;
    private readonly IBatchProcessor _batchProcessor;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly IMicrosoftAuthAdapter _microsoftAuthAdapter;
    private readonly IMinecraftIdentityAdapter _minecraftIdentityAdapter;
    private readonly IMinecraftProfileLookupService _profileLookupService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly RetryPolicy _retryPolicy;
    private readonly ThrottleCoordinator _throttleCoordinator;
    private readonly SecretSafeLogger _logger;
    private readonly ProcessingArbiter _arbiter;
    private readonly ConcurrentDictionary<Guid, CachedMicrosoftAccess> _microsoftAccessCache = new();

    /// <summary>Longest the coordinator loop idles before re-checking backoff and rate-limit state.</summary>
    private static readonly TimeSpan MaxIdlePoll = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _removedCount;

    private readonly record struct CachedMicrosoftAccess(string AccessToken, DateTimeOffset ExpiresAt);

    public PoolProcessor(
        IAccountRepository accountRepository,
        IBatchProcessor batchProcessor,
        ISecureCredentialStore credentialStore,
        IMicrosoftAuthAdapter microsoftAuthAdapter,
        IMinecraftIdentityAdapter minecraftIdentityAdapter,
        IMinecraftProfileLookupService profileLookupService,
        IAppSettingsStore settingsStore,
        RetryPolicy retryPolicy,
        ThrottleCoordinator throttleCoordinator,
        SecretSafeLogger logger,
        ProcessingArbiter arbiter)
    {
        _accountRepository = accountRepository;
        _batchProcessor = batchProcessor;
        _credentialStore = credentialStore;
        _microsoftAuthAdapter = microsoftAuthAdapter;
        _minecraftIdentityAdapter = minecraftIdentityAdapter;
        _profileLookupService = profileLookupService;
        _settingsStore = settingsStore;
        _retryPolicy = retryPolicy;
        _throttleCoordinator = throttleCoordinator;
        _logger = logger;
        _arbiter = arbiter;
    }

    public event EventHandler<PoolProgressEventArgs>? ProgressChanged;

    public bool IsRunning => _arbiter.IsHeldBy(ProcessingArbiter.PoolOwner);

    public Task RefreshPoolAsync(CancellationToken cancellationToken = default)
    {
        if (!_arbiter.TryAcquire(ProcessingArbiter.PoolOwner))
        {
            throw new InvalidOperationException(
                _arbiter.IsHeldBy(ProcessingArbiter.BatchOwner)
                    ? "A batch is running. Wait for it to finish before refreshing the pool."
                    : "Pool refresh is already running.");
        }

        return StartClaimedRun(refreshAll: true, cancellationToken);
    }

    public Task AutoManageAsync(CancellationToken cancellationToken = default)
    {
        // Background timer path: silently skip when a batch or another pool run owns the slot.
        if (!_arbiter.TryAcquire(ProcessingArbiter.PoolOwner))
        {
            return Task.CompletedTask;
        }

        return StartClaimedRun(refreshAll: false, cancellationToken);
    }

    private Task StartClaimedRun(bool refreshAll, CancellationToken cancellationToken)
    {
        try
        {
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunPoolAsync(refreshAll, _runCts.Token);
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
        _arbiter.Release(ProcessingArbiter.PoolOwner);
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

    private async Task RunPoolAsync(bool refreshAll, CancellationToken cancellationToken)
    {
        try
        {
            _removedCount = 0;
            var settings = _settingsStore.Load();
            _throttleCoordinator.Initialize(settings.DefaultConcurrency);

            var poolAccounts = await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false);
            var toProcess = refreshAll
                ? poolAccounts.ToList()
                : poolAccounts.Where(a => NeedsAutoRefresh(a, settings)).ToList();

            if (toProcess.Count == 0)
            {
                RaiseProgress(poolAccounts, null);
                return;
            }

            foreach (var account in toProcess)
            {
                PrepareAccountForRefresh(account);
                await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            }

            // Workers re-enqueue their own account on retryable failures, so the queue is written
            // from several threads at once and must be concurrent.
            var queue = new ConcurrentQueue<AccountRecord>(toProcess);
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

                        workers.Add(ProcessAccountAsync(account, queue, cancellationToken));
                        dispatched++;
                    }

                    if (workers.Count > 0)
                    {
                        await Task.WhenAny(workers).ConfigureAwait(false);
                    }
                    else if (!queue.IsEmpty && dispatched == 0)
                    {
                        await Task.Delay(ComputeIdleDelay(queue), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Fall through and report the final pool state below.
            }
            finally
            {
                // In-flight workers hold the linked token; let them observe cancellation and finish
                // before ReleaseRunSlot disposes the source underneath them.
                await DrainWorkersAsync(workers).ConfigureAwait(false);
            }

            var remaining = await _accountRepository.GetPoolAccountsAsync(CancellationToken.None).ConfigureAwait(false);
            RaiseProgress(remaining, null);
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
                _logger.LogInfo($"Pool worker faulted: {error.GetBaseException().GetType().Name}");
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
            _logger.LogInfo($"Pool worker faulted while draining: {ex.GetType().Name}");
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

    private static bool NeedsAutoRefresh(AccountRecord account, AppSettings settings)
    {
        if (account.ProcessingState is ProcessingState.Authenticating)
        {
            return false;
        }

        if (account.ProcessingState == ProcessingState.Backoff &&
            account.BackoffUntil.HasValue &&
            account.BackoffUntil.Value > DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(account.MinecraftAccessTokenReference))
        {
            return account.ParseStatus == ParseStatus.Parsed &&
                   !string.IsNullOrWhiteSpace(account.CredentialReference);
        }

        if (!account.MinecraftAccessTokenExpiresAt.HasValue)
        {
            return true;
        }

        var threshold = DateTimeOffset.UtcNow.AddHours(Math.Max(1, settings.PoolRefreshBeforeExpiryHours));
        return account.MinecraftAccessTokenExpiresAt.Value <= threshold;
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

    private async Task ProcessAccountAsync(
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
            RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account);

            if (account.ParseStatus != ParseStatus.Parsed || string.IsNullOrWhiteSpace(account.CredentialReference))
            {
                await RemoveFromPoolAsync(account, cancellationToken).ConfigureAwait(false);
                return;
            }

            var credential = await _credentialStore.RetrieveAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
            {
                // Keep the row: the blob may be unreadable for a fixable reason (different Windows
                // user or machine), and deleting it would destroy the only copy of the token.
                await MarkPoolFailureAsync(
                    account,
                    ProcessingState.ReauthenticationRequired,
                    ErrorCategory.MissingCredential,
                    FailureStage.Authentication,
                    "Stored credential could not be read from secure storage (DPAPI).",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_microsoftAuthAdapter.IsConfigured || !_minecraftIdentityAdapter.IsConfigured)
            {
                // A missing or unverified authentication_profile.json is a configuration problem,
                // never a reason to delete pool accounts and their refresh tokens.
                await MarkPoolFailureAsync(
                    account,
                    ProcessingState.Failed,
                    ErrorCategory.ConfigurationRequired,
                    FailureStage.Authentication,
                    "Authentication profile is not verified. Configure authentication_profile.json.",
                    cancellationToken).ConfigureAwait(false);
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
                    await HandleAuthFailureAsync(account, msResult, queue, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(msResult.MicrosoftAccessToken))
                {
                    await MarkPoolFailureAsync(
                        account,
                        ProcessingState.Failed,
                        ErrorCategory.MicrosoftServiceError,
                        FailureStage.Authentication,
                        "Microsoft authentication succeeded but returned no access token.",
                        cancellationToken).ConfigureAwait(false);
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
                await HandleAuthFailureAsync(account, mcResult, queue, cancellationToken).ConfigureAwait(false);
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
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Persist with None: the token is already cancelled, so passing it here meant the
            // Cancelled state never reached the database.
            account.ProcessingState = ProcessingState.Cancelled;
            account.ErrorCategory = ErrorCategory.Cancelled;
            await _accountRepository.UpsertAsync(account, CancellationToken.None).ConfigureAwait(false);
            RaiseProgress(await _accountRepository.GetPoolAccountsAsync(CancellationToken.None).ConfigureAwait(false), account);
        }
        catch (Exception ex)
        {
            // An unexpected fault (DB hiccup, DPAPI error, bad JSON) says nothing about the
            // validity of the refresh token, so record it and keep the account.
            _logger.LogInfo($"Unexpected pool error for record {account.Id}: {ex.GetType().Name}");
            await MarkPoolFailureAsync(
                account,
                ProcessingState.Failed,
                ErrorCategory.Unknown,
                FailureStage.Authentication,
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a failure against a pool account without deleting it or its stored credential.
    /// </summary>
    private async Task MarkPoolFailureAsync(
        AccountRecord account,
        ProcessingState state,
        ErrorCategory category,
        FailureStage stage,
        string detail,
        CancellationToken cancellationToken)
    {
        account.ProcessingState = state;
        account.ErrorCategory = category;
        account.FailureStage = stage;
        account.ServiceErrorDetail = detail;
        account.BackoffUntil = null;
        _microsoftAccessCache.TryRemove(account.Id, out _);

        await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
        RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account);
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
            _logger.LogInfo($"Public profile lookup failed for pool record {account.Id}: {ex.Message}");
        }
    }

    private async Task HandleAuthFailureAsync(
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

        if (PoolFailurePolicy.ShouldKeepInPool(result.ErrorCategory))
        {
            if (result.ErrorCategory == ErrorCategory.RateLimited)
            {
                _throttleCoordinator.RegisterRateLimit(result.RetryAfter);
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
                await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
                queue.Enqueue(account);
                RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account);
                return;
            }

            account.ProcessingState = ProcessingState.Failed;
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account);
            return;
        }

        _microsoftAccessCache.TryRemove(account.Id, out _);
        await RemoveFromPoolAsync(account, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveFromPoolAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.CredentialReference))
        {
            await _credentialStore.DeleteAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(account.MinecraftAccessTokenReference))
        {
            await _credentialStore.DeleteAsync(account.MinecraftAccessTokenReference, cancellationToken).ConfigureAwait(false);
        }

        await _accountRepository.DeleteAsync(account.Id, cancellationToken).ConfigureAwait(false);
        _microsoftAccessCache.TryRemove(account.Id, out _);
        _removedCount++;
        RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account, accountRemoved: true);
    }

    private void RaiseProgress(
        IReadOnlyList<AccountRecord> poolAccounts,
        AccountRecord? account,
        bool accountRemoved = false)
    {
        var succeeded = poolAccounts.Count(a => a.ProcessingState == ProcessingState.Succeeded);
        var failed = poolAccounts.Count(a =>
            a.ProcessingState is ProcessingState.Failed or ProcessingState.ReauthenticationRequired or ProcessingState.Malformed);
        var pending = poolAccounts.Count(a =>
            a.ProcessingState is ProcessingState.Queued or ProcessingState.Backoff or ProcessingState.Authenticating or ProcessingState.Pending);

        ProgressChanged?.Invoke(this, new PoolProgressEventArgs
        {
            Total = poolAccounts.Count,
            Pending = pending,
            Succeeded = succeeded,
            Failed = failed,
            Removed = _removedCount,
            LastUpdatedAccount = account,
            AccountRemoved = accountRemoved
        });
    }
}
