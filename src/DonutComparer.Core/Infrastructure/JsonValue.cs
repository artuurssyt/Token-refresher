using System.Globalization;
using System.Text.Json;

namespace DonutComparer.Core.Infrastructure;

public static class JsonValue
{
    public static bool TryPath(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }
        return true;
    }

    public static string String(JsonElement element, string property, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? AsString(value, fallback) : fallback;

    public static string AsString(JsonElement value, string fallback = "") => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? fallback,
        JsonValueKind.Number => value.GetRawText(),
        _ => fallback
    };

    public static decimal Decimal(JsonElement element, string property, decimal fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? Decimal(value, fallback) : fallback;

    public static decimal Decimal(JsonElement value, decimal fallback = 0)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
        return fallback;
    }

    public static long Long(JsonElement element, string property, long fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            ? number : fallback;
    }
}
