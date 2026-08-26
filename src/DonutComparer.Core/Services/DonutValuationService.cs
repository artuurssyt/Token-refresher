using DonutComparer.Core.Models;

namespace DonutComparer.Core.Services;

public static class DonutValuationService
{
    public static (ValuationBreakdown Valuation, List<AssetLine> Assets) Calculate(
        DonutStats? stats, string username, string uuid, IReadOnlyList<DonutAuctionListing> allListings,
        AppSettings settings)
    {
        var valuation = new ValuationBreakdown();
        var assets = new List<AssetLine>();
        if (stats is null) return (valuation, assets);
        valuation.LiquidCoins = stats.Money;
        var listings = allListings.Where(listing =>
            listing.SellerName.Equals(username, StringComparison.OrdinalIgnoreCase)
            || listing.SellerUuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)).ToList();
        var listingValue = listings.Sum(listing => listing.Price);
        var shardValue = stats.Shards * settings.DonutShardUnitValue;
        valuation.OtherAssetsValue = listingValue + shardValue;
        valuation.ValuedItemStacks = listings.Count;
        foreach (var listing in listings)
        {
            assets.Add(new AssetLine
            {
                Category = "DonutSMP auction listing",
                ItemId = listing.ItemId,
                DisplayName = listing.DisplayName,
                Count = listing.Count,
                UnitPrice = listing.Count > 0 ? listing.Price / listing.Count : listing.Price,
                PriceSource = "Current DonutSMP asking price"
            });
        }
        if (stats.Shards > 0)
        {
            assets.Add(new AssetLine
            {
                Category = "DonutSMP shards",
                ItemId = "SHARD",
                DisplayName = "Shards (configured conversion)",
                Count = (int)Math.Min(int.MaxValue, stats.Shards),
                UnitPrice = settings.DonutShardUnitValue,
                PriceSource = settings.DonutShardUnitValue > 0 ? "User-configured shard value" : "Not monetized"
            });
        }
        valuation.Methodology = settings.DonutBridgeEnabled
            ? "In-game /stats GUI via Minecraft client bridge: money plus configured shard value. Auction listings are unavailable without the DonutSMP API."
            : "Official money balance plus this player's visible current auction asking prices and the configured shard value. " +
              "DonutSMP's official API does not expose player inventory, Ender Chest, base contents, or other held assets, " +
              "so those assets are excluded rather than guessed.";
        return (valuation, assets);
    }
}
