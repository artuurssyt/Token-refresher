using System.Text.Json;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public sealed class MarketPriceService
{
    /// <summary>
    /// "0 = all pages" still has to mean a finite number: totalPages comes straight off the wire, and
    /// a wrong or hostile value would otherwise schedule that many page requests.
    /// </summary>
    private const int AbsoluteMaxAuctionPages = 200;

    private readonly HypixelClient _client;
    private readonly AppSettings _settings;

    public MarketPriceService(HypixelClient client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<MarketSnapshot> LoadAsync(IProgress<string>? progress, CancellationToken token)
    {
        var snapshot = new MarketSnapshot();
        progress?.Report("Loading SkyBlock Bazaar and NPC prices…");
        await LoadBazaarAsync(snapshot, token).ConfigureAwait(false);
        await LoadNpcPricesAsync(snapshot, token).ConfigureAwait(false);

        progress?.Report("Loading SkyBlock active-auction prices…");
        using var firstDocument = await _client.GetAuctionsPageAsync(0, token).ConfigureAwait(false);
        var first = ExtractAuctionPage(firstDocument.RootElement);
        ApplyAuctionCandidates(snapshot, first.Candidates);
        var pageLimit = _settings.MaxHypixelAuctionPages == 0
            ? first.TotalPages
            : Math.Min(first.TotalPages, _settings.MaxHypixelAuctionPages);
        pageLimit = Math.Clamp(pageLimit, 1, AbsoluteMaxAuctionPages);
        snapshot.AuctionPagesLoaded = 1;

        using var gate = new SemaphoreSlim(Math.Min(4, _settings.Concurrency));
        var tasks = Enumerable.Range(1, Math.Max(0, pageLimit - 1)).Select(async page =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var document = await _client.GetAuctionsPageAsync(page, token).ConfigureAwait(false);
                var data = ExtractAuctionPage(document.RootElement);
                progress?.Report($"Loaded SkyBlock market page {page + 1}/{pageLimit}");
                return data.Candidates;
            }
            finally { gate.Release(); }
        }).ToArray();

        foreach (var candidates in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            ApplyAuctionCandidates(snapshot, candidates);
            snapshot.AuctionPagesLoaded++;
        }
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        return snapshot;
    }

    private async Task LoadBazaarAsync(MarketSnapshot snapshot, CancellationToken token)
    {
        using var document = await _client.GetBazaarAsync(token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("products", out var products)) return;
        foreach (var product in products.EnumerateObject())
        {
            if (!product.Value.TryGetProperty("quick_status", out var status)) continue;
            var preferred = _settings.BazaarPriceMode == BazaarPriceMode.ConservativeSell
                ? JsonValue.Decimal(status, "sellPrice") : JsonValue.Decimal(status, "buyPrice");
            var fallback = _settings.BazaarPriceMode == BazaarPriceMode.ConservativeSell
                ? JsonValue.Decimal(status, "buyPrice") : JsonValue.Decimal(status, "sellPrice");
            var price = preferred > 0 ? preferred : fallback;
            if (price <= 0) continue;
            snapshot.Prices[product.Name] = new PriceEntry
            {
                UnitPrice = price,
                Source = _settings.BazaarPriceMode == BazaarPriceMode.ConservativeSell
                    ? "Bazaar instant-sell estimate" : "Bazaar replacement estimate"
            };
            snapshot.BazaarProductsLoaded++;
        }
    }

    private async Task LoadNpcPricesAsync(MarketSnapshot snapshot, CancellationToken token)
    {
        using var document = await _client.GetItemsAsync(token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("items", out var items)) return;
        foreach (var item in items.EnumerateArray())
        {
            var id = JsonValue.String(item, "id", string.Empty);
            var price = JsonValue.Decimal(item, "npc_sell_price");
            if (id.Length == 0 || price <= 0 || snapshot.Prices.ContainsKey(id)) continue;
            snapshot.Prices[id] = new PriceEntry { UnitPrice = price, Source = "NPC sell price" };
        }
    }

    private static AuctionPage ExtractAuctionPage(JsonElement root)
    {
        var candidates = new List<PriceCandidate>();
        var totalPages = root.TryGetProperty("totalPages", out var pages) && pages.TryGetInt32(out var count)
            ? count : 1;
        if (!root.TryGetProperty("auctions", out var auctions)) return new AuctionPage(totalPages, candidates);
        foreach (var auction in auctions.EnumerateArray())
        {
            if (!auction.TryGetProperty("bin", out var bin) || bin.ValueKind != JsonValueKind.True) continue;
            var price = JsonValue.Decimal(auction, "starting_bid");
            var bytes = JsonValue.String(auction, "item_bytes", string.Empty);
            var item = HypixelItemParser.ParseInventory(bytes).FirstOrDefault();
            if (item is null || item.ItemId.Length == 0 || price <= 0) continue;
            candidates.Add(new PriceCandidate(item.ItemId, price / Math.Max(1, item.Count)));
        }
        return new AuctionPage(totalPages, candidates);
    }

    private static void ApplyAuctionCandidates(MarketSnapshot snapshot, IEnumerable<PriceCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (snapshot.Prices.TryGetValue(candidate.ItemId, out var existing)
                && (existing.Source.StartsWith("Bazaar", StringComparison.Ordinal)
                    || existing.Source == "Lowest active BIN" && existing.UnitPrice <= candidate.UnitPrice)) continue;
            snapshot.Prices[candidate.ItemId] = new PriceEntry
                { UnitPrice = candidate.UnitPrice, Source = "Lowest active BIN" };
        }
    }

    private sealed record PriceCandidate(string ItemId, decimal UnitPrice);
    private sealed record AuctionPage(int TotalPages, List<PriceCandidate> Candidates);
}
