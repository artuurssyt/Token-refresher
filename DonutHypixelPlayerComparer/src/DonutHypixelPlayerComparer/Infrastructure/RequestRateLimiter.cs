namespace DonutHypixelPlayerComparer.Infrastructure;

public sealed class RequestRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _period;
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public RequestRateLimiter(int limit, TimeSpan period)
    {
        _limit = Math.Max(1, limit);
        _period = period;
    }

    public async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            TimeSpan delay;
            await _mutex.WaitAsync(token);
            try
            {
                var now = DateTimeOffset.UtcNow;
                while (_timestamps.Count > 0 && now - _timestamps.Peek() >= _period)
                    _timestamps.Dequeue();
                if (_timestamps.Count < _limit)
                {
                    _timestamps.Enqueue(now);
                    return;
                }
                delay = _period - (now - _timestamps.Peek()) + TimeSpan.FromMilliseconds(25);
            }
            finally { _mutex.Release(); }
            await Task.Delay(delay, token);
        }
    }
}
