using System.Net;
using System.Text.Json;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Infrastructure;

public sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public ApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

public sealed class ResilientHttpClient : IDisposable
{
    /// <summary>A server asking for a multi-minute pause would look like a hang, so honour it only this far.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Guards against an endpoint streaming an unbounded body into memory.</summary>
    private const long MaxResponseBytes = 64L * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly DiskCache _cache = new();
    private readonly int _retryCount;

    public ResilientHttpClient(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        if (Uri.TryCreate(settings.HttpProxyUrl, UriKind.Absolute, out var proxyUri))
        {
            var proxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(settings.HttpProxyUsername))
                proxy.Credentials = new NetworkCredential(settings.HttpProxyUsername, settings.HttpProxyPassword);
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DonutHypixelPlayerComparer/1.0");
        _retryCount = settings.RetryCount;
    }

    public async Task<JsonDocument> GetJsonAsync(string url,
        IReadOnlyDictionary<string, string>? headers, RequestRateLimiter limiter,
        TimeSpan cacheAge, CancellationToken token)
    {
        var fingerprint = headers is null ? string.Empty :
            string.Join('|', headers.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
        var headerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fingerprint)));
        var cacheKey = url + "|" + headerHash;
        var cached = await _cache.TryGetAsync(cacheKey, cacheAge, token).ConfigureAwait(false);
        if (cached is not null)
        {
            // A corrupted cache entry must not be fatal; fall through and re-fetch.
            try { return JsonDocument.Parse(cached); }
            catch (JsonException) { }
        }

        Exception? lastError = null;
        HttpStatusCode? lastStatus = null;
        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await limiter.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers is not null)
                    foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var body = await ReadBodyAsync(response, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var document = Parse(body, response.StatusCode);
                    // The response already succeeded, so a cache write failure must not fail the call.
                    await _cache.SetAsync(cacheKey, body, token).ConfigureAwait(false);
                    return document;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new ApiException("No matching player or resource was found.", response.StatusCode);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ApiException("The API key is missing, invalid, or not permitted.", response.StatusCode);
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                    throw new ApiException(ExtractMessage(body) ?? $"API request failed ({(int)response.StatusCode}).",
                        response.StatusCode);
                lastStatus = response.StatusCode;
                lastError = new ApiException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "The API rate limit was reached." : "The API is temporarily unavailable.", response.StatusCode);
                if (attempt < _retryCount)
                    await Task.Delay(RetryAfter(response) ?? Backoff(attempt), token).ConfigureAwait(false);
            }
            // Only server-side and transport faults are worth repeating; a 400-class answer will not
            // change on its own and retrying it just burns the rate-limit budget.
            catch (ApiException ex) when (ex.StatusCode is null or HttpStatusCode.TooManyRequests
                                          || (int)ex.StatusCode >= 500)
            {
                lastError = ex;
                lastStatus = ex.StatusCode ?? lastStatus;
                if (attempt < _retryCount) await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                if (attempt < _retryCount) await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
            {
                lastError = ex;
                if (attempt < _retryCount) await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
        }
        // Keeping the status lets callers tell a rate limit apart from an outage.
        throw new ApiException("The API request failed after retries.", lastStatus, lastError);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new ApiException("The API response was too large to process.", response.StatusCode);
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Retry-After arrives either as a delay or as an HTTP date, and either may be far longer than a
    /// desktop scan should sit still for, so both forms are read and then clamped.
    /// </summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response) => Clamp(response.Headers.RetryAfter);

    internal static TimeSpan? Clamp(System.Net.Http.Headers.RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        var delay = header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null) return null;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    private static JsonDocument Parse(string body, HttpStatusCode status)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException ex) { throw new ApiException("The server returned invalid JSON.", status, ex); }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(
        Math.Min(15_000, 500 * Math.Pow(2, attempt) + Random.Shared.Next(50, 300)));

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var name in new[] { "cause", "message", "reason" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public void Dispose() => _client.Dispose();
}
