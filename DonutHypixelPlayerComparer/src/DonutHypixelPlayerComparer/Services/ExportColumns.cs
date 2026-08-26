using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public static partial class ExportService
{
    private static readonly (string Header, Func<PlayerScanResult, object?> Value)[] Columns =
    {
        ("Username", row => row.Username),
        ("UUID", row => row.Uuid),
        ("Status", row => row.Status),
        ("Donut Money", row => row.DonutMoney),
        ("Donut Shards", row => row.DonutShards),
        ("Donut Playtime", row => row.DonutPlaytime),
        ("Donut Kills", row => row.DonutKills),
        ("Donut Deaths", row => row.DonutDeaths),
        ("Donut Mobs Killed", row => row.DonutMobsKilled),
        ("Donut Blocks Broken", row => row.DonutBrokenBlocks),
        ("Donut Blocks Placed", row => row.DonutPlacedBlocks),
        ("Donut Money Made From Sell", row => row.DonutMoneyMadeFromSell),
        ("Donut Money Spent On Shop", row => row.DonutMoneySpentOnShop),
        ("Donut Rank", row => row.DonutRank),
        ("Donut Location", row => row.DonutLocation),
        ("Donut Auction Value", row => row.DonutAuctionListingsValue),
        ("Donut Net Worth", row => row.DonutNetWorth),
        ("SkyBlock Profiles", row => row.SkyBlockProfiles),
        ("Selected Profile", row => row.SkyBlockProfile),
        ("SkyBlock Level", row => row.SkyBlockLevel),
        ("SkyBlock Liquid", row => row.SkyBlockLiquid),
        ("Inventory Value", row => row.SkyBlockInventory),
        ("Storage Value", row => row.SkyBlockStorage),
        ("Other SkyBlock Assets", row => row.SkyBlockOther),
        ("SkyBlock Net Worth", row => row.SkyBlockNetWorth),
        ("Numeric Combined", row => row.CombinedNetWorth),
        ("Error", row => row.Error),
        ("Scanned At", row => row.ScannedAt.ToString("O"))
    };
}
