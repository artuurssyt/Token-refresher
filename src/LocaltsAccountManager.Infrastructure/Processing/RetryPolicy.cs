using LocaltsAccountManager.Core.Configuration;

using LocaltsAccountManager.Core.Enums;

using LocaltsAccountManager.Core.Interfaces;

using LocaltsAccountManager.Core.Models;



namespace LocaltsAccountManager.Infrastructure.Processing;



public sealed class RetryPolicy

{

    private readonly IAppSettingsStore _settingsStore;



    public RetryPolicy(IAppSettingsStore settingsStore)

    {

        _settingsStore = settingsStore;

    }



    public TimeSpan ComputeBackoff(AccountRecord record, TimeSpan? retryAfter)

    {

        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)

        {

            return retryAfter.Value;

        }



        var settings = _settingsStore.Load();

        var attempt = Math.Max(record.RetryCount, 1);

        var milliseconds = Math.Min(

            settings.BaseBackoffMilliseconds * (int)Math.Pow(2, Math.Min(attempt - 1, 6)),

            settings.MaxBackoffMilliseconds);

        return TimeSpan.FromMilliseconds(milliseconds);

    }



    public bool CanRetry(AccountRecord record, ErrorCategory category)

    {

        var settings = _settingsStore.Load();

        var maxAttempts = category == ErrorCategory.RateLimited || category == ErrorCategory.VendorRateLimited

            ? settings.MaxRateLimitRetryAttempts

            : settings.MaxRetryAttempts;



        if (record.RetryCount >= maxAttempts)

        {

            return false;

        }



        return category is

            ErrorCategory.AuthenticationTimeout or

            ErrorCategory.NetworkUnavailable or

            ErrorCategory.DnsFailure or

            ErrorCategory.TlsFailure or

            ErrorCategory.ProxyFailure or

            ErrorCategory.RateLimited or

            ErrorCategory.MicrosoftServiceError or

            ErrorCategory.MinecraftServiceError or

            ErrorCategory.VendorUnavailable or

            ErrorCategory.VendorRateLimited;

    }

}



public sealed class ThrottleCoordinator : IAsyncDisposable

{

    private readonly IAppSettingsStore _settingsStore;

    private readonly object _sync = new();

    private readonly SemaphoreSlim _minecraftGate = new(1, 1);

    private int _effectiveConcurrency = 1;

    private DateTimeOffset? _globalRateLimitUntil;

    private DateTimeOffset _nextMinecraftSlotUtc = DateTimeOffset.MinValue;



    public ThrottleCoordinator(IAppSettingsStore settingsStore)

    {

        _settingsStore = settingsStore;

    }



    public int EffectiveConcurrency

    {

        get

        {

            lock (_sync)

            {

                return _effectiveConcurrency;

            }

        }

    }



    public DateTimeOffset? GlobalRateLimitUntil

    {

        get

        {

            lock (_sync)

            {

                return _globalRateLimitUntil;

            }

        }

    }



    public void Initialize(int concurrencyLimit)

    {

        var settings = _settingsStore.Load();

        lock (_sync)

        {

            var capped = Math.Min(Math.Max(1, concurrencyLimit), Math.Max(1, settings.MaxAdaptiveConcurrency));

            _effectiveConcurrency = capped;

            _globalRateLimitUntil = null;

            _nextMinecraftSlotUtc = DateTimeOffset.MinValue;

        }

    }



    public void RegisterRateLimit(TimeSpan? retryAfter)

    {

        var settings = _settingsStore.Load();

        var cooldown = retryAfter is { } ra && ra > TimeSpan.Zero

            ? ra

            : TimeSpan.FromMilliseconds(Math.Max(5_000, settings.DefaultRateLimitCooldownMilliseconds));



        lock (_sync)

        {

            _effectiveConcurrency = 1;

            var until = DateTimeOffset.UtcNow.Add(cooldown);

            if (!_globalRateLimitUntil.HasValue || until > _globalRateLimitUntil.Value)

            {

                _globalRateLimitUntil = until;

            }

        }

    }



    public void RegisterSuccessWindow()

    {

        var settings = _settingsStore.Load();

        lock (_sync)

        {

            var max = Math.Max(1, settings.MaxAdaptiveConcurrency);

            _effectiveConcurrency = Math.Min(_effectiveConcurrency + 1, max);

            if (_globalRateLimitUntil.HasValue && DateTimeOffset.UtcNow >= _globalRateLimitUntil.Value)

            {

                _globalRateLimitUntil = null;

            }

        }

    }



    public async Task WaitForGlobalLimitAsync(CancellationToken cancellationToken)

    {

        while (true)

        {

            DateTimeOffset? until;

            lock (_sync)

            {

                until = _globalRateLimitUntil;

            }



            if (!until.HasValue || DateTimeOffset.UtcNow >= until.Value)

            {

                return;

            }



            var delay = until.Value - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero)

            {

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            }

        }

    }



    /// <summary>

    /// Serializes Minecraft login_with_xbox and enforces a minimum gap between calls.

    /// Hold the returned lease until the HTTP call finishes.

    /// </summary>

    public async Task<IAsyncDisposable> AcquireMinecraftAuthSlotAsync(CancellationToken cancellationToken)

    {

        await WaitForGlobalLimitAsync(cancellationToken).ConfigureAwait(false);

        await _minecraftGate.WaitAsync(cancellationToken).ConfigureAwait(false);



        try

        {

            await WaitForGlobalLimitAsync(cancellationToken).ConfigureAwait(false);



            var settings = _settingsStore.Load();

            var minInterval = TimeSpan.FromMilliseconds(Math.Max(0, settings.MinecraftAuthMinIntervalMilliseconds));



            DateTimeOffset nextSlot;

            lock (_sync)

            {

                nextSlot = _nextMinecraftSlotUtc;

            }



            var wait = nextSlot - DateTimeOffset.UtcNow;

            if (wait > TimeSpan.Zero)

            {

                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            }



            lock (_sync)

            {

                _nextMinecraftSlotUtc = DateTimeOffset.UtcNow.Add(minInterval);

            }



            return new MinecraftSlotLease(_minecraftGate);

        }

        catch

        {

            _minecraftGate.Release();

            throw;

        }

    }



    public ValueTask DisposeAsync()

    {

        _minecraftGate.Dispose();

        return ValueTask.CompletedTask;

    }



    private sealed class MinecraftSlotLease : IAsyncDisposable

    {

        private SemaphoreSlim? _gate;



        public MinecraftSlotLease(SemaphoreSlim gate) => _gate = gate;



        public ValueTask DisposeAsync()

        {

            var gate = Interlocked.Exchange(ref _gate, null);

            gate?.Release();

            return ValueTask.CompletedTask;

        }

    }

}


