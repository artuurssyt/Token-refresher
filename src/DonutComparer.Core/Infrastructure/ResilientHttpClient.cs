using System.Net;
using System.Text.Json;
using DonutComparer.Core.Models;

namespace DonutComparer.Core.Infrastructure;

public sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public ApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

public sealed class ResilientHttpClient : IDisposable
{
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
        var cached = await _cache.TryGetAsync(cacheKey, cacheAge, token);
        if (cached is not null) return JsonDocument.Parse(cached);

        Exception? lastError = null;
        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await limiter.WaitAsync(token);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers is not null)
                    foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (response.IsSuccessStatusCode)
                {
                    var document = Parse(body, response.StatusCode);
                    await _cache.SetAsync(cacheKey, body, token);
                    return document;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new ApiException("No matching player or resource was found.", response.StatusCode);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ApiException("The API key is missing, invalid, or not permitted.", response.StatusCode);
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                    throw new ApiException(ExtractMessage(body) ?? $"API request failed ({(int)response.StatusCode}).",
                        response.StatusCode);
                lastError = new ApiException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "The API rate limit was reached." : "The API is temporarily unavailable.", response.StatusCode);
                if (attempt < _retryCount)
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? Backoff(attempt), token);
            }
            catch (ApiException ex) when (ex.StatusCode is not HttpStatusCode.NotFound
                                          and not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
            {
                lastError = ex;
                if (attempt < _retryCount) await Task.Delay(Backoff(attempt), token);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                if (attempt < _retryCount) await Task.Delay(Backoff(attempt), token);
            }
            catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
            {
                lastError = ex;
                if (attempt < _retryCount) await Task.Delay(Backoff(attempt), token);
            }
        }
        throw new ApiException("The API request failed after retries.", null, lastError);
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
