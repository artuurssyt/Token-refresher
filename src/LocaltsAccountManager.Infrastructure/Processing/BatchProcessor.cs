using System.Collections.Concurrent;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Diagnostics;
using LocaltsAccountManager.Infrastructure.Minecraft;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, CachedMicrosoftAccess> _microsoftAccessCache = new();

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
        IServiceProvider serviceProvider)
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
        _serviceProvider = serviceProvider;
    }

    public event EventHandler<BatchProgressEventArgs>? ProgressChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        EnsurePoolNotRunning();
        if (IsRunning)
        {
            throw new InvalidOperationException("A batch is already running.");
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunBatchAsync(batchId, _runCts.Token);
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

    public async Task RetryFailedAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        EnsurePoolNotRunning();
        if (IsRunning)
        {
            throw new InvalidOperationException("A batch is still running. Wait for it to finish or click Cancel first.");
        }

        var retryable = await _accountRepository.GetRetryEligibleAsync(batchId, cancellationToken).ConfigureAwait(false);
        foreach (var account in retryable)
        {
            account.ProcessingState = ProcessingState.Queued;
            account.BackoffUntil = null;
            account.RetryCount = 0;
            account.ErrorCategory = ErrorCategory.None;
            account.FailureStage = FailureStage.None;
            account.OAuthErrorCode = null;
            account.ServiceErrorDetail = null;
            _microsoftAccessCache.TryRemove(account.Id, out _);
            await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
        }

        await StartAsync(batchId, cancellationToken).ConfigureAwait(false);
    }

    public Task RefreshAccountAsync(Guid batchId, Guid accountId, CancellationToken cancellationToken = default)
    {
        EnsurePoolNotRunning();
        if (IsRunning)
        {
            throw new InvalidOperationException("A batch is still running. Wait for it to finish or click Cancel first.");
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RefreshSingleAccountAsync(batchId, accountId, _runCts.Token);
        return _runTask;
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

            var queue = new Queue<AccountRecord>();
            await ProcessAccountAsync(batch, account, queue, cancellationToken).ConfigureAwait(false);

            batch = await RecalculateBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
            batch.Status = BatchStatus.Completed;
            batch.CompletedAt = DateTimeOffset.UtcNow;
            await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
            RaiseProgress(batch, account);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
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
            foreach (var account in accounts.Where(NeedsAccessTokenRefresh))
            {
                account.ProcessingState = ProcessingState.Queued;
                account.BackoffUntil = null;
                _microsoftAccessCache.TryRemove(account.Id, out _);
                await _accountRepository.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            }

            var processable = await _accountRepository.GetIncompleteByBatchIdAsync(batchId, cancellationToken).ConfigureAwait(false);
            var queue = new Queue<AccountRecord>(processable.Where(a => a.ProcessingState is ProcessingState.Queued or ProcessingState.Backoff));
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

                    if (account.DuplicateOfRecordId.HasValue)
                    {
                        account.ProcessingState = ProcessingState.Failed;
                        account.FailureStage = FailureStage.Parse;
                        account.ErrorCategory = ErrorCategory.MalformedInput;
                        account.ServiceErrorDetail = $"Possible duplicate of record {account.DuplicateOfRecordId.Value}.";
                        account.IsErrorCauseConfirmed = true;
                        await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    workers.Add(ProcessAccountAsync(batch, account, queue, cancellationToken));
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

            batch = await RecalculateBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
            batch.Status = cancellationToken.IsCancellationRequested ? BatchStatus.Cancelled : BatchStatus.Completed;
            batch.CompletedAt = DateTimeOffset.UtcNow;
            if (cancellationToken.IsCancellationRequested)
            {
                batch.CancelledAt = DateTimeOffset.UtcNow;
            }

            await _batchRepository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
            RaiseProgress(batch);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task ProcessAccountAsync(
        BatchRecord batch,
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
            RaiseProgress(batch, account);

            if (account.ParseStatus != ParseStatus.Parsed || string.IsNullOrWhiteSpace(account.CredentialReference))
            {
                await FailAsync(batch, account, FailureStage.Parse, ErrorCategory.MalformedInput, "Record was not parsed successfully.", true, false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var credential = await _credentialStore.RetrieveAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
            {
                await FailAsync(batch, account, FailureStage.Authentication, ErrorCategory.MissingCredential, "Credential could not be retrieved from secure storage.", true, false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!_microsoftAuthAdapter.IsConfigured || !_minecraftIdentityAdapter.IsConfigured)
            {
                await FailAsync(batch, account, FailureStage.Authentication, ErrorCategory.ConfigurationRequired,
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
                    await FailAsync(batch, account, FailureStage.Authentication, ErrorCategory.CredentialRejected,
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
            account.ProcessingState = ProcessingState.Cancelled;
            account.ErrorCategory = ErrorCategory.Cancelled;
            await PersistAccountAndUpdateBatchAsync(batch, account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"Unexpected error for record {account.Id}: {ex.GetType().Name}");
            await FailAsync(batch, account, FailureStage.Authentication, ErrorCategory.Unknown, ex.Message, false, true, cancellationToken)
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
        Queue<AccountRecord> queue,
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
        FailureStage stage,
        ErrorCategory category,
        string detail,
        bool confirmed,
        bool retryable,
        CancellationToken cancellationToken)
    {
        account.ProcessingState = retryable ? ProcessingState.Backoff : ProcessingState.Failed;
        account.FailureStage = stage;
        account.ErrorCategory = category;
        account.ServiceErrorDetail = detail;
        account.IsErrorCauseConfirmed = confirmed;
        if (retryable && _retryPolicy.CanRetry(account, category))
        {
            account.BackoffUntil = DateTimeOffset.UtcNow.Add(_retryPolicy.ComputeBackoff(account, null));
        }

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

    private void EnsurePoolNotRunning()
    {
        var pool = _serviceProvider.GetService<IPoolProcessor>();
        if (pool?.IsRunning == true)
        {
            throw new InvalidOperationException("Pool refresh is running. Wait for it to finish before starting batch processing.");
        }
    }
}
