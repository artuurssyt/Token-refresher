using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using System.Text.Json;

namespace DonutComparer.Core.Services;

public sealed class HypixelClient
{
    private readonly ResilientHttpClient _http;
    private readonly RequestRateLimiter _limiter;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _headers;

    public HypixelClient(ResilientHttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
        _limiter = new RequestRateLimiter(settings.HypixelRequestsPerMinute, TimeSpan.FromMinutes(1));
        _headers = new() { ["API-Key"] = settings.HypixelApiKey.Trim() };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.HypixelApiKey);
    private TimeSpan PlayerAge => TimeSpan.FromMinutes(_settings.PlayerCacheMinutes);
    private TimeSpan MarketAge => TimeSpan.FromMinutes(_settings.MarketCacheMinutes);

    public Task<JsonDocument> GetProfilesAsync(string uuid, CancellationToken token) =>
        _http.GetJsonAsync($"https://api.hypixel.net/v2/skyblock/profiles?uuid={uuid}",
            _headers, _limiter, PlayerAge, token);

    public Task<JsonDocument> GetMuseumAsync(string profileId, CancellationToken token) =>
        _http.GetJsonAsync($"https://api.hypixel.net/v2/skyblock/museum?profile={profileId}",
            _headers, _limiter, PlayerAge, token);

    public Task<JsonDocument> GetPlayerAuctionsAsync(string uuid, CancellationToken token) =>
        _http.GetJsonAsync($"https://api.hypixel.net/v2/skyblock/auction?player={uuid}",
            _headers, _limiter, PlayerAge, token);

    public Task<JsonDocument> GetBazaarAsync(CancellationToken token) =>
        _http.GetJsonAsync("https://api.hypixel.net/v2/skyblock/bazaar", null, _limiter, MarketAge, token);

    public Task<JsonDocument> GetItemsAsync(CancellationToken token) =>
        _http.GetJsonAsync("https://api.hypixel.net/v2/resources/skyblock/items", null, _limiter, MarketAge, token);

    public Task<JsonDocument> GetAuctionsPageAsync(int page, CancellationToken token) =>
        _http.GetJsonAsync($"https://api.hypixel.net/v2/skyblock/auctions?page={page}",
            null, _limiter, MarketAge, token);
}
