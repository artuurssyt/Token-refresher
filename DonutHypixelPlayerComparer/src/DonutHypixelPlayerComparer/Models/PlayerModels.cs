using System.Globalization;
using System.Text.Json.Serialization;

namespace DonutHypixelPlayerComparer.Models;

public sealed class DonutStats
{
    public decimal Money { get; set; }
    public decimal Shards { get; set; }
    public long PlaytimeSeconds { get; set; }
    public long Kills { get; set; }
    public long Deaths { get; set; }
    public long MobsKilled { get; set; }
    public long BrokenBlocks { get; set; }
    public long PlacedBlocks { get; set; }
    public decimal MoneyMadeFromSell { get; set; }
    public decimal MoneySpentOnShop { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

public sealed class ValuationBreakdown
{
    public decimal LiquidCoins { get; set; }
    public decimal InventoryValue { get; set; }
    public decimal StorageValue { get; set; }
    public decimal ArmorEquipmentValue { get; set; }
    public decimal OtherAssetsValue { get; set; }
    public decimal Total => LiquidCoins + InventoryValue + StorageValue + ArmorEquipmentValue + OtherAssetsValue;
    public int ValuedItemStacks { get; set; }
    public int UnvaluedItemStacks { get; set; }
    public string Methodology { get; set; } = string.Empty;
}

public sealed class AssetLine
{
    public string Category { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalValue => UnitPrice * Count;
    public string PriceSource { get; set; } = string.Empty;
}

public sealed class PlayerScanResult
{
    public string Username { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Error { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public DonutStats? Donut { get; set; }
    /// <summary>"API", "Bridge", or "" — which route actually produced the Donut numbers.</summary>
    public string DonutSource { get; set; } = string.Empty;
    public decimal DonutAuctionListingsValue { get; set; }
    public ValuationBreakdown DonutValuation { get; set; } = new();
    public string SkyBlockProfile { get; set; } = string.Empty;
    public string SkyBlockProfiles { get; set; } = string.Empty;
    public double SkyBlockLevel { get; set; }
    public ValuationBreakdown SkyBlockValuation { get; set; } = new();
    public List<AssetLine> Assets { get; set; } = new();
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;

    public decimal CombinedNetWorth => DonutValuation.Total + SkyBlockValuation.Total;

    [JsonIgnore] public decimal DonutMoney => Donut?.Money ?? 0;
    [JsonIgnore] public decimal DonutShards => Donut?.Shards ?? 0;
    [JsonIgnore] public long DonutKills => Donut?.Kills ?? 0;
    [JsonIgnore] public long DonutDeaths => Donut?.Deaths ?? 0;
    [JsonIgnore] public long DonutMobsKilled => Donut?.MobsKilled ?? 0;
    [JsonIgnore] public long DonutBrokenBlocks => Donut?.BrokenBlocks ?? 0;
    [JsonIgnore] public long DonutPlacedBlocks => Donut?.PlacedBlocks ?? 0;
    [JsonIgnore] public decimal DonutMoneyMadeFromSell => Donut?.MoneyMadeFromSell ?? 0;
    [JsonIgnore] public decimal DonutMoneySpentOnShop => Donut?.MoneySpentOnShop ?? 0;
    [JsonIgnore] public string DonutRank => Donut?.Rank ?? string.Empty;
    [JsonIgnore] public string DonutLocation => Donut?.Location ?? string.Empty;
    [JsonIgnore] public string DonutPlaytime => Donut is null ? string.Empty : FormatDuration(Donut.PlaytimeSeconds);
    [JsonIgnore] public decimal SkyBlockLiquid => SkyBlockValuation.LiquidCoins;
    [JsonIgnore] public decimal SkyBlockInventory => SkyBlockValuation.InventoryValue;
    [JsonIgnore] public decimal SkyBlockStorage => SkyBlockValuation.StorageValue;
    [JsonIgnore] public decimal SkyBlockOther => SkyBlockValuation.ArmorEquipmentValue + SkyBlockValuation.OtherAssetsValue;
    [JsonIgnore] public decimal SkyBlockNetWorth => SkyBlockValuation.Total;
    [JsonIgnore] public decimal DonutNetWorth => DonutValuation.Total;

    private static string FormatDuration(long seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        // Invariant so the exported text matches regardless of the operator's regional settings.
        return duration.TotalDays >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}d {1}h {2}m",
                (int)duration.TotalDays, duration.Hours, duration.Minutes)
            : string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", duration.Hours, duration.Minutes);
    }
}

public sealed record MinecraftIdentity(string Name, string Uuid);

public sealed class PriceEntry
{
    public decimal UnitPrice { get; init; }
    public string Source { get; init; } = string.Empty;
}

public sealed class MarketSnapshot
{
    public Dictionary<string, PriceEntry> Prices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int AuctionPagesLoaded { get; set; }
    public int BazaarProductsLoaded { get; set; }

    public PriceEntry? Find(string itemId) => Prices.GetValueOrDefault(itemId);
}

public sealed class DonutAuctionListing
{
    public string SellerName { get; init; } = string.Empty;
    public string SellerUuid { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal Price { get; init; }
}
