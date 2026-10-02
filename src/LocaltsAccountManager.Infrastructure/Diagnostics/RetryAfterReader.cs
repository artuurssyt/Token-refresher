namespace LocaltsAccountManager.Infrastructure.Diagnostics;

/// <summary>
/// Reads <c>Retry-After</c> from a response. Callers previously used only
/// <see cref="System.Net.Http.Headers.RetryConditionHeaderValue.Delta"/>, which is null whenever
/// the server sends the HTTP-date form ("Retry-After: Wed, 13 Sep 2026 18:00:00 GMT"). Microsoft
/// and Mojang both use that form, so those waits were being dropped and the request retried
/// immediately into another 429.
/// </summary>
internal static class RetryAfterReader
{
    /// <summary>Upper bound so an absurd header cannot stall a batch for hours.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(30);

    public static TimeSpan? Read(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return Clamp(delta);
        }

        if (header.Date is { } date)
        {
            return Clamp(date - DateTimeOffset.UtcNow);
        }

        return null;
    }

    private static TimeSpan? Clamp(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return null;
        }

        return value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}
