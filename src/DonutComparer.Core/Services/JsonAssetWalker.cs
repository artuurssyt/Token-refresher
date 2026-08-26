using System.Text.Json;
using DonutComparer.Core.Infrastructure;

namespace DonutComparer.Core.Services;

internal sealed record CategorizedItems(string Category, IReadOnlyList<ParsedItem> Items);

internal static class JsonAssetWalker
{
    public static IReadOnlyList<CategorizedItems> Extract(JsonElement element, string rootPath = "")
    {
        var result = new List<CategorizedItems>();
        Walk(element, rootPath, result);
        return result;
    }

    private static void Walk(JsonElement element, string path, List<CategorizedItems> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            {
                var items = HypixelItemParser.ParseInventory(data.GetString());
                if (items.Count > 0) result.Add(new CategorizedItems(Category(path), items));
            }
            foreach (var property in element.EnumerateObject())
            {
                var childPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                if (property.Name.Equals("pets", StringComparison.OrdinalIgnoreCase))
                    ExtractPets(property.Value, result);
                else if (property.Name.Contains("sacks_counts", StringComparison.OrdinalIgnoreCase))
                    ExtractSacks(property.Value, result);
                else
                    Walk(property.Value, childPath, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Walk(child, path, result);
        }
    }

    private static void ExtractPets(JsonElement pets, List<CategorizedItems> result)
    {
        if (pets.ValueKind != JsonValueKind.Array) return;
        var items = new List<ParsedItem>();
        foreach (var pet in pets.EnumerateArray())
        {
            if (pet.ValueKind != JsonValueKind.Object) continue;
            var type = JsonValue.String(pet, "type", string.Empty);
            var tier = JsonValue.String(pet, "tier", string.Empty);
            if (type.Length > 0 && tier.Length > 0)
                items.Add(new ParsedItem(HypixelItemParser.PetKey(type, tier), $"{tier} {type} pet", 1));
            var heldItem = JsonValue.String(pet, "heldItem", string.Empty);
            if (heldItem.Length > 0) items.Add(new ParsedItem(heldItem, heldItem, 1));
        }
        if (items.Count > 0) result.Add(new CategorizedItems("Other assets", items));
    }

    private static void ExtractSacks(JsonElement sacks, List<CategorizedItems> result)
    {
        if (sacks.ValueKind != JsonValueKind.Object) return;
        var items = new List<ParsedItem>();
        foreach (var item in sacks.EnumerateObject())
        {
            if (!item.Value.TryGetInt32(out var count) || count <= 0) continue;
            items.Add(new ParsedItem(item.Name, item.Name, count));
        }
        if (items.Count > 0) result.Add(new CategorizedItems("Storage", items));
    }

    private static string Category(string path)
    {
        if (path.Contains("armor", StringComparison.OrdinalIgnoreCase)
            || path.Contains("equipment", StringComparison.OrdinalIgnoreCase)) return "Armor & equipment";
        if (path.Contains("ender", StringComparison.OrdinalIgnoreCase)
            || path.Contains("backpack", StringComparison.OrdinalIgnoreCase)
            || path.Contains("storage", StringComparison.OrdinalIgnoreCase)
            || path.Contains("vault", StringComparison.OrdinalIgnoreCase)
            || path.Contains("wardrobe", StringComparison.OrdinalIgnoreCase)
            || path.Contains("bag", StringComparison.OrdinalIgnoreCase)) return "Storage";
        if (path.Contains("inv_contents", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("inventory", StringComparison.OrdinalIgnoreCase)) return "Inventory";
        return "Other assets";
    }
}
