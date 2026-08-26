using System.Net;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public sealed class DonutClient
{
    private readonly ResilientHttpClient _http;
    private readonly RequestRateLimiter _limiter;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _headers;

    public DonutClient(ResilientHttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
        _limiter = new RequestRateLimiter(settings.DonutRequestsPerMinute, TimeSpan.FromMinutes(1));
        _headers = new() { ["Authorization"] = "Bearer " + settings.DonutApiKey.Trim() };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.DonutApiKey);

    public async Task<DonutStats?> GetStatsAsync(string username, CancellationToken token)
    {
        if (!IsConfigured) return null;
        try
        {
            var age = TimeSpan.FromMinutes(_settings.PlayerCacheMinutes);
            using var statsDocument = await _http.GetJsonAsync(
                $"https://api.donutsmp.net/v1/stats/{Uri.EscapeDataString(username)}",
                _headers, _limiter, age, token);
            var root = statsDocument.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            var stats = new DonutStats
            {
                Money = Stat(result, "money"),
                Shards = Stat(result, "shards"),
                PlaytimeSeconds = Playtime(result, "playtime"),
                Kills = (long)Stat(result, "kills"),
                Deaths = (long)Stat(result, "deaths"),
                MobsKilled = (long)Stat(result, "mobs_killed"),
                BrokenBlocks = (long)Stat(result, "broken_blocks"),
                PlacedBlocks = (long)Stat(result, "placed_blocks"),
                MoneyMadeFromSell = Stat(result, "money_made_from_sell"),
                MoneySpentOnShop = Stat(result, "money_spent_on_shop")
            };
            try
            {
                using var lookupDocument = await _http.GetJsonAsync(
                    $"https://api.donutsmp.net/v1/lookup/{Uri.EscapeDataString(username)}",
                    _headers, _limiter, age, token);
                if (lookupDocument.RootElement.TryGetProperty("result", out var lookup))
                {
                    stats.Rank = JsonValue.String(lookup, "rank", string.Empty);
                    stats.Location = JsonValue.String(lookup, "location", string.Empty);
                }
            }
            catch (ApiException) { }
            return stats;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.InternalServerError)
        {
            return null;
        }
    }

    // The official API types every /v1/stats field as a string and can format them the way /stats
    // renders them in game ("3.2M", "$ 230M"), so reuse the GUI-grade parsers instead of TryParse.
    private static decimal Stat(System.Text.Json.JsonElement result, string property)
    {
        var raw = JsonValue.String(result, property, string.Empty);
        return raw.Length == 0 ? 0 : DonutGuiStatsParser.ParseCompactDecimal(raw);
    }

    private static long Playtime(System.Text.Json.JsonElement result, string property)
    {
        var raw = JsonValue.String(result, property, string.Empty);
        if (raw.Length == 0) return 0;
        var fromUnits = DonutGuiStatsParser.ParsePlaytimeSeconds(raw);
        if (fromUnits > 0) return fromUnits;
        var plain = (long)DonutGuiStatsParser.ParseCompactDecimal(raw);
        // A bare count larger than ten years of seconds is being reported in milliseconds.
        return plain > 315_360_000 ? plain / 1000 : plain;
    }

    public async Task<IReadOnlyList<DonutAuctionListing>> GetAuctionListingsAsync(CancellationToken token)
    {
        var listings = new List<DonutAuctionListing>();
        if (!IsConfigured || _settings.MaxDonutAuctionPages == 0) return listings;
        var age = TimeSpan.FromMinutes(_settings.MarketCacheMinutes);
        for (var page = 1; page <= _settings.MaxDonutAuctionPages; page++)
        {
            using var document = await _http.GetJsonAsync($"https://api.donutsmp.net/v1/auction/list/{page}",
                _headers, _limiter, age, token);
            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != System.Text.Json.JsonValueKind.Array) break;
            var nonNull = 0;
            foreach (var entry in result.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                nonNull++;
                var seller = entry.TryGetProperty("seller", out var sellerElement) ? sellerElement : default;
                var item = entry.TryGetProperty("item", out var itemElement) ? itemElement : default;
                listings.Add(new DonutAuctionListing
                {
                    SellerName = JsonValue.String(seller, "name", string.Empty),
                    SellerUuid = JsonValue.String(seller, "uuid", string.Empty).Replace("-", string.Empty),
                    ItemId = JsonValue.String(item, "id", string.Empty),
                    DisplayName = JsonValue.String(item, "display_name", string.Empty),
                    Count = (int)Math.Max(1, JsonValue.Long(item, "count", 1)),
                    Price = JsonValue.Decimal(entry, "price")
                });
            }
            if (nonNull < 44) break;
        }
        return listings;
    }
}
