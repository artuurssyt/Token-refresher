using System.Text.Json;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public sealed record SkyBlockScanData(
    string SelectedProfile,
    string AllProfiles,
    double Level,
    ValuationBreakdown Valuation,
    IReadOnlyList<AssetLine> Assets,
    bool InventoryApiEnabled = true);

public sealed class SkyBlockValuationService
{
    private readonly HypixelClient _client;
    private readonly AppSettings _settings;

    public SkyBlockValuationService(HypixelClient client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<SkyBlockScanData> CalculateAsync(JsonElement root, string uuid,
        MarketSnapshot market, CancellationToken token)
    {
        var breakdown = new ValuationBreakdown();
        var assets = new List<AssetLine>();
        if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            return new SkyBlockScanData(string.Empty, string.Empty, 0, breakdown, assets);

        var available = profiles.EnumerateArray().ToList();
        var names = available.Select(profile => JsonValue.String(profile, "cute_name", string.Empty))
            .Where(name => name.Length > 0).ToArray();
        var selected = available.FirstOrDefault(IsSelected);
        if (selected.ValueKind == JsonValueKind.Undefined && available.Count > 0) selected = available[0];
        if (selected.ValueKind == JsonValueKind.Undefined)
            return new SkyBlockScanData(string.Empty, string.Join(", ", names), 0, breakdown, assets);

        var member = FindMember(selected, uuid);
        // Current API nests the purse under currencies.coin_purse; the rest are pre-2024 layouts.
        var purse = FirstDecimal(member,
            new[] { "currencies", "coin_purse" }, new[] { "profile", "currencies", "coin_purse" },
            new[] { "currencies", "coin" }, new[] { "profile", "currencies", "coin" },
            new[] { "coin_purse" }, new[] { "profile", "coin_purse" });
        var bank = selected.TryGetProperty("banking", out var banking)
            ? JsonValue.Decimal(banking, "balance") : 0;
        breakdown.LiquidCoins = purse + bank;
        var levelExperience = FirstDecimal(member,
            new[] { "leveling", "experience" }, new[] { "profile", "leveling", "experience" });

        var groups = member.ValueKind == JsonValueKind.Undefined
            ? Array.Empty<CategorizedItems>()
            : JsonAssetWalker.Extract(member).ToArray();
        foreach (var group in groups) AddItems(group.Category, group.Items, market, breakdown, assets);
        var inventoryApiEnabled = member.ValueKind == JsonValueKind.Object
                                  && member.TryGetProperty("inventory", out var inventoryNode)
                                  && inventoryNode.ValueKind == JsonValueKind.Object;

        var profileId = JsonValue.String(selected, "profile_id", string.Empty);
        if (_settings.IncludeMuseum && profileId.Length > 0)
        {
            try
            {
                using var museum = await _client.GetMuseumAsync(profileId, token);
                var museumMember = museum.RootElement.TryGetProperty("members", out var museumMembers)
                    ? FindByUuid(museumMembers, uuid) : default;
                if (museumMember.ValueKind != JsonValueKind.Undefined)
                    foreach (var group in JsonAssetWalker.Extract(museumMember, "museum"))
                        AddItems("Other assets", group.Items, market, breakdown, assets);
            }
            catch (ApiException) { }
        }

        if (_settings.IncludePlayerAuctions)
        {
            try
            {
                using var auctions = await _client.GetPlayerAuctionsAsync(uuid, token);
                if (auctions.RootElement.TryGetProperty("auctions", out var auctionArray))
                {
                    foreach (var auction in auctionArray.EnumerateArray())
                    {
                        var itemBytes = JsonValue.String(auction, "item_bytes", string.Empty);
                        AddItems("Other assets", HypixelItemParser.ParseInventory(itemBytes),
                            market, breakdown, assets);
                    }
                }
            }
            catch (ApiException) { }
        }

        breakdown.Methodology =
            "Selected profile only. Liquid coins include accessible purse and bank. Items use configured Bazaar price, " +
            "then lowest active BIN, then official NPC sell price. Unknown or API-hidden assets are valued at zero; " +
            "item upgrades may make the estimate conservative."
            + (inventoryApiEnabled
                ? string.Empty
                : " This profile has the SkyBlock inventory API disabled, so no item values could be read.");
        var consolidated = assets.GroupBy(item => new { item.Category, item.ItemId, item.UnitPrice, item.PriceSource })
            .Select(group => new AssetLine
            {
                Category = group.Key.Category,
                ItemId = group.Key.ItemId,
                DisplayName = group.First().DisplayName,
                Count = group.Sum(item => item.Count),
                UnitPrice = group.Key.UnitPrice,
                PriceSource = group.Key.PriceSource
            })
            .OrderByDescending(item => item.TotalValue).ToList();
        return new SkyBlockScanData(JsonValue.String(selected, "cute_name", string.Empty), string.Join(", ", names),
            (double)(levelExperience / 100m), breakdown, consolidated, inventoryApiEnabled);
    }

    private static void AddItems(string category, IReadOnlyList<ParsedItem> items, MarketSnapshot market,
        ValuationBreakdown breakdown, List<AssetLine> assets)
    {
        foreach (var item in items)
        {
            var price = market.Find(item.ItemId);
            var value = (price?.UnitPrice ?? 0) * item.Count;
            if (price is null) breakdown.UnvaluedItemStacks++;
            else breakdown.ValuedItemStacks++;
            switch (category)
            {
                case "Inventory": breakdown.InventoryValue += value; break;
                case "Storage": breakdown.StorageValue += value; break;
                case "Armor & equipment": breakdown.ArmorEquipmentValue += value; break;
                default: breakdown.OtherAssetsValue += value; break;
            }
            assets.Add(new AssetLine
            {
                Category = category,
                ItemId = item.ItemId,
                DisplayName = item.DisplayName,
                Count = item.Count,
                UnitPrice = price?.UnitPrice ?? 0,
                PriceSource = price?.Source ?? "No public market match"
            });
        }
    }

    private static bool IsSelected(JsonElement profile) =>
        profile.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True;

    private static JsonElement FindMember(JsonElement profile, string uuid) =>
        profile.TryGetProperty("members", out var members) ? FindByUuid(members, uuid) : default;

    private static JsonElement FindByUuid(JsonElement members, string uuid)
    {
        if (members.ValueKind != JsonValueKind.Object) return default;
        var normalized = uuid.Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var member in members.EnumerateObject())
            if (member.Name.Replace("-", string.Empty, StringComparison.Ordinal)
                .Equals(normalized, StringComparison.OrdinalIgnoreCase)) return member.Value;
        return default;
    }

    private static decimal FirstDecimal(JsonElement root, params string[][] paths)
    {
        foreach (var path in paths)
            if (JsonValue.TryPath(root, out var value, path)) return JsonValue.Decimal(value);
        return 0;
    }
}
