using System.Text.Json;
using System.Text.RegularExpressions;

namespace DonutComparer.Core.Services;

public sealed record ParsedItem(string ItemId, string DisplayName, int Count);

public static class HypixelItemParser
{
    public static IReadOnlyList<ParsedItem> ParseInventory(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return Array.Empty<ParsedItem>();
        try
        {
            var root = NbtReader.ReadRoot(Convert.FromBase64String(base64));
            if (!root.TryGetValue("i", out var value) || value is not List<object?> list)
                return Array.Empty<ParsedItem>();
            var result = new List<ParsedItem>();
            foreach (var entry in list)
            {
                if (entry is not Dictionary<string, object?> item) continue;
                var count = Math.Max(0, ConvertNumber(item.GetValueOrDefault("Count")));
                if (count == 0) continue;
                var tag = item.GetValueOrDefault("tag") as Dictionary<string, object?>;
                var extra = tag?.GetValueOrDefault("ExtraAttributes") as Dictionary<string, object?>;
                var id = extra?.GetValueOrDefault("id") as string ?? string.Empty;
                if (id.Equals("PET", StringComparison.OrdinalIgnoreCase))
                    id = PetKey(extra) ?? id;
                var display = tag?.GetValueOrDefault("display") as Dictionary<string, object?>;
                var name = CleanName(display?.GetValueOrDefault("Name") as string ?? id);
                result.Add(new ParsedItem(id, name, count));
            }
            return result;
        }
        catch (FormatException) { return Array.Empty<ParsedItem>(); }
        catch (InvalidDataException) { return Array.Empty<ParsedItem>(); }
        catch (EndOfStreamException) { return Array.Empty<ParsedItem>(); }
    }

    public static string PetKey(string type, string tier) => $"PET:{type}:{tier}".ToUpperInvariant();

    private static string? PetKey(Dictionary<string, object?>? extra)
    {
        if (extra?.GetValueOrDefault("petInfo") is not string petInfo) return null;
        try
        {
            using var document = JsonDocument.Parse(petInfo);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var tier = root.TryGetProperty("tier", out var tierElement) ? tierElement.GetString() : null;
            return string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(tier)
                ? null : PetKey(type, tier);
        }
        catch (JsonException) { return null; }
    }

    private static int ConvertNumber(object? value) => value switch
    {
        sbyte number => number & 0xff,
        byte number => number,
        short number => number,
        int number => number,
        long number when number <= int.MaxValue => (int)number,
        _ => 0
    };

    private static string CleanName(string name)
    {
        var clean = Regex.Replace(name, "§.", string.Empty);
        if (!clean.StartsWith('{')) return clean;
        try
        {
            using var document = JsonDocument.Parse(clean);
            return document.RootElement.TryGetProperty("text", out var text)
                ? text.GetString() ?? clean : clean;
        }
        catch (JsonException) { return clean; }
    }
}
