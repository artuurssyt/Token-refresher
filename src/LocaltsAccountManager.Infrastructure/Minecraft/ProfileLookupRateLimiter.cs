namespace LocaltsAccountManager.Infrastructure.Minecraft;

internal sealed class ProfileLookupRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly object _sync = new();

    public ProfileLookupRateLimiter(int maxRequests, TimeSpan window)
    {
        _maxRequests = Math.Max(1, maxRequests);
        _window = window;
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_sync)
            {
                Trim();
                if (_timestamps.Count < _maxRequests)
                {
                    _timestamps.Enqueue(DateTimeOffset.UtcNow);
                    return;
                }

                var oldest = _timestamps.Peek();
                delay = oldest + _window - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    _timestamps.Dequeue();
                    continue;
                }
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
        {
            _timestamps.Dequeue();
        }
    }
}
