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
    private readonly ConcurrentDictionary<Guid, CachedMicrosoftAccess> _microsoftAccessCache = new();

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
        SecretSafeLogger logger)
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
    }

    public event EventHandler<PoolProgressEventArgs>? ProgressChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task RefreshPoolAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanStart();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunPoolAsync(refreshAll: true, _runCts.Token);
        return _runTask;
    }

    public Task AutoManageAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning || _batchProcessor.IsRunning)
        {
            return Task.CompletedTask;
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunPoolAsync(refreshAll: false, _runCts.Token);
        return _runTask;
    }

    public async Task CancelAsync()
    {
        if (_runCts == null)
        {
            return;
        }

        await _runCts.CancelAsync().ConfigureAwait(false);
        if (_runTask != null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void EnsureCanStart()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Pool refresh is already running.");
        }

        if (_batchProcessor.IsRunning)
        {
            throw new InvalidOperationException("A batch is running. Wait for it to finish before refreshing the pool.");
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

            var queue = new Queue<AccountRecord>(toProcess);
            var workers = new List<Task>();

            while (queue.Count > 0 || workers.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _throttleCoordinator.WaitForGlobalLimitAsync(cancellationToken).ConfigureAwait(false);

                workers.RemoveAll(w => w.IsCompleted);
                while (queue.Count > 0 && workers.Count < _throttleCoordinator.EffectiveConcurrency)
                {
                    var account = queue.Dequeue();
                    if (account.BackoffUntil.HasValue && account.BackoffUntil.Value > DateTimeOffset.UtcNow)
                    {
                        queue.Enqueue(account);
                        break;
                    }

                    workers.Add(ProcessAccountAsync(account, queue, cancellationToken));
                }

                if (workers.Count == 0 && queue.Count > 0)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                else if (workers.Count > 0)
                {
                    await Task.WhenAny(workers).ConfigureAwait(false);
                }
            }

            var remaining = await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false);
            RaiseProgress(remaining, null);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
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
        Queue<AccountRecord> queue,
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
                await RemoveFromPoolAsync(account, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_microsoftAuthAdapter.IsConfigured || !_minecraftIdentityAdapter.IsConfigured)
            {
                await RemoveFromPoolAsync(account, cancellationToken).ConfigureAwait(false);
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
                    await RemoveFromPoolAsync(account, cancellationToken).ConfigureAwait(false);
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
            account.ProcessingState = ProcessingState.Cancelled;
            account.ErrorCategory = ErrorCategory.Cancelled;
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            RaiseProgress(await _accountRepository.GetPoolAccountsAsync(cancellationToken).ConfigureAwait(false), account);
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"Unexpected pool error for record {account.Id}: {ex.GetType().Name}");
            await RemoveFromPoolAsync(account, cancellationToken).ConfigureAwait(false);
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
            _logger.LogInfo($"Public profile lookup failed for pool record {account.Id}: {ex.Message}");
        }
    }

    private async Task HandleAuthFailureAsync(
        AccountRecord account,
        AuthenticationResult result,
        Queue<AccountRecord> queue,
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
