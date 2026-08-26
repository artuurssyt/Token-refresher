using System.Globalization;
using System.Text.RegularExpressions;
using DonutComparer.Core.Models;

namespace DonutComparer.Core.Services;

public sealed class DonutBridgeStatsPayload
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
    public Dictionary<string, List<string>>? RawGuiText { get; set; }
}

public static partial class DonutGuiStatsParser
{
    /// <summary>
    /// Slot is only a hint. DonutSMP can move items around, so a label match anywhere in the GUI
    /// beats a positional guess, and the unlabelled slot value is used only as a last resort.
    /// </summary>
    private sealed record StatSlot(
        int Slot,
        string[] Keywords,
        string[] Exclude,
        bool IsDuration,
        Func<DonutStats, bool> HasValue,
        Action<DonutStats, string> Apply);

    private static readonly StatSlot[] SlotMap =
    [
        new(10, ["money", "balance", "coins", "purse", "$"], ["sell", "sold", "shop", "spent", "made"], false,
            s => s.Money != 0, (s, v) => s.Money = ParseCompactDecimal(v)),
        new(11, ["shard"], [], false,
            s => s.Shards != 0, (s, v) => s.Shards = ParseCompactDecimal(v)),
        new(12, ["kill"], ["mob", "zombie", "monster"], false,
            s => s.Kills != 0, (s, v) => s.Kills = ParseCompactLong(v)),
        new(13, ["death", "died"], [], false,
            s => s.Deaths != 0, (s, v) => s.Deaths = ParseCompactLong(v)),
        new(14, ["playtime", "play time", "time played", "time online", "played"], [], true,
            s => s.PlaytimeSeconds != 0, (s, v) => s.PlaytimeSeconds = ParsePlaytimeSeconds(v)),
        new(15, ["placed", "place"], ["broken", "break", "mined", "destroy"], false,
            s => s.PlacedBlocks != 0, (s, v) => s.PlacedBlocks = ParseCompactLong(v)),
        new(16, ["broken", "break", "mined", "mine", "destroyed"], ["placed", "place"], false,
            s => s.BrokenBlocks != 0, (s, v) => s.BrokenBlocks = ParseCompactLong(v)),
        new(19, ["mob", "monster", "zombie"], [], false,
            s => s.MobsKilled != 0, (s, v) => s.MobsKilled = ParseCompactLong(v))
    ];

    public static DonutStats FromPayload(DonutBridgeStatsPayload payload)
    {
        var stats = new DonutStats
        {
            Money = payload.Money,
            Shards = payload.Shards,
            PlaytimeSeconds = payload.PlaytimeSeconds,
            Kills = payload.Kills,
            Deaths = payload.Deaths,
            MobsKilled = payload.MobsKilled,
            BrokenBlocks = payload.BrokenBlocks,
            PlacedBlocks = payload.PlacedBlocks,
            MoneyMadeFromSell = payload.MoneyMadeFromSell,
            MoneySpentOnShop = payload.MoneySpentOnShop,
            Rank = payload.Rank ?? string.Empty,
            Location = payload.Location ?? string.Empty
        };
        if (payload.RawGuiText is { Count: > 0 })
            MergeFromSlotLore(stats, payload.RawGuiText);
        return stats;
    }

    public static DonutStats ParseFromSlotLore(IReadOnlyDictionary<int, IReadOnlyList<string>> slotLore)
    {
        var stats = new DonutStats();
        MergeFromSlotLore(stats, slotLore.ToDictionary(
            pair => pair.Key.ToString(CultureInfo.InvariantCulture),
            pair => pair.Value.ToList()));
        return stats;
    }

    /// <summary>Fills only the fields the client left empty; values it already parsed are never overwritten.</summary>
    public static void MergeFromSlotLore(DonutStats stats, IReadOnlyDictionary<string, List<string>> slotLore)
    {
        foreach (var entry in SlotMap)
        {
            if (entry.HasValue(stats)) continue;
            // Labels only. Reading a bare number off a guessed slot invents values from unrelated
            // items, and a wrong balance is worse than a missing one when pricing an account.
            var value = ReadLabelled(SlotLines(slotLore, entry.Slot), entry)
                        ?? ScanEverySlot(slotLore, entry);
            if (value is not null) entry.Apply(stats, value);
        }
        MergeShopTotals(stats, slotLore);
    }

    private static IReadOnlyList<string>? SlotLines(IReadOnlyDictionary<string, List<string>> slotLore, int slot) =>
        slotLore.TryGetValue(slot.ToString(CultureInfo.InvariantCulture), out var lines) ? lines : null;

    private static string? ScanEverySlot(IReadOnlyDictionary<string, List<string>> slotLore, StatSlot entry)
    {
        foreach (var lines in slotLore.Values)
        {
            var value = ReadLabelled(lines, entry);
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>Finds the labelled line, taking the value from that line or the line directly below it.</summary>
    private static string? ReadLabelled(IReadOnlyList<string>? lines, StatSlot entry)
    {
        if (lines is null) return null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = Clean(lines[i]);
            if (line.Length == 0) continue;
            var lower = line.ToLowerInvariant();
            if (!entry.Keywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal))) continue;
            if (entry.Exclude.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal))) continue;
            var value = ValueFrom(line, entry.IsDuration);
            if (value is not null) return value;
            if (i + 1 < lines.Count)
            {
                value = ValueFrom(Clean(lines[i + 1]), entry.IsDuration);
                if (value is not null) return value;
            }
        }
        return null;
    }

    private static string? ValueFrom(string line, bool isDuration)
    {
        if (line.Length == 0) return null;
        if (isDuration) return ParsePlaytimeSeconds(line) > 0 ? line : null;
        var number = ExtractLastNumber(line);
        return number.Length > 0 ? number : null;
    }

    private static void MergeShopTotals(DonutStats stats, IReadOnlyDictionary<string, List<string>> slotLore)
    {
        foreach (var lines in slotLore.Values)
        {
            if (lines is null) continue;
            foreach (var raw in lines)
            {
                var line = Clean(raw);
                if (line.Length == 0) continue;
                var lower = line.ToLowerInvariant();
                if (stats.MoneyMadeFromSell == 0
                    && (lower.Contains("sell", StringComparison.Ordinal) || lower.Contains("sold", StringComparison.Ordinal)))
                    stats.MoneyMadeFromSell = ParseCompactDecimal(line);
                else if (stats.MoneySpentOnShop == 0
                         && (lower.Contains("shop", StringComparison.Ordinal) || lower.Contains("spent", StringComparison.Ordinal)))
                    stats.MoneySpentOnShop = ParseCompactDecimal(line);
            }
        }
    }

    public static bool HasMinimumStats(DonutStats stats) =>
        HasMinimumStats(stats, new DonutBridgeStatsPayload());

    /// <summary>
    /// Checked against the merged stats, never against the presence of raw text alone — except
    /// /bal chat completions, where money may be zero but chat evidence proves a real reply.
    /// </summary>
    public static bool HasMinimumStats(DonutStats stats, DonutBridgeStatsPayload payload)
    {
        if (stats.Money > 0 || stats.Shards > 0 || stats.Kills > 0 || stats.Deaths > 0 || stats.PlaytimeSeconds > 0
            || stats.MobsKilled > 0 || stats.BrokenBlocks > 0 || stats.PlacedBlocks > 0
            || payload.Money > 0 || payload.Shards > 0 || payload.Kills > 0 || payload.Deaths > 0
            || payload.PlaytimeSeconds > 0)
        {
            return true;
        }

        // Explicit $0 from /bal is still a successful lookup when chat evidence is present.
        if (payload.RawGuiText is { Count: > 0 } &&
            payload.RawGuiText.TryGetValue("chat", out var chatLines) &&
            chatLines is { Count: > 0 } &&
            chatLines.Any(line => !string.IsNullOrWhiteSpace(line)))
        {
            return true;
        }

        return false;
    }

    /// <summary>Parses /bal chat replies such as "ardatzpr has $ 1.2K".</summary>
    public static bool TryParseBalanceFromChat(IEnumerable<string>? lines, string username, out decimal money)
    {
        money = 0;
        if (lines is null) return false;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = Clean(raw);
            if (line.Length == 0) continue;
            var lower = line.ToLowerInvariant();
            var looksLikeBalance = lower.Contains("bal", StringComparison.Ordinal)
                                   || lower.Contains("money", StringComparison.Ordinal)
                                   || lower.Contains("coin", StringComparison.Ordinal)
                                   || lower.Contains('$')
                                   || lower.Contains("purse", StringComparison.Ordinal);
            if (!looksLikeBalance && !line.Contains('$')) continue;
            if (!string.IsNullOrWhiteSpace(username))
            {
                var userLower = username.ToLowerInvariant();
                if (!lower.Contains(userLower, StringComparison.Ordinal)
                    && !lower.Contains("bal", StringComparison.Ordinal)
                    && !line.Contains('$'))
                {
                    continue;
                }
            }
            var parsed = ParseCompactDecimal(line);
            if (parsed > 0 || line.Contains('$'))
            {
                money = parsed;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// When the Minecraft client posts /fail but captured chat or GUI text, recover a /bal balance
    /// instead of treating the player as unavailable.
    /// </summary>
    public static DonutStats? TrySalvageFromBridgePayload(string? error,
        IReadOnlyDictionary<string, List<string>>? rawGuiText, string username)
    {
        if (rawGuiText?.TryGetValue("chat", out var chatLines) == true
            && TryParseBalanceFromChat(chatLines, username, out var chatMoney))
        {
            return new DonutStats { Money = chatMoney };
        }

        var lastChat = ExtractLastChatFromError(error);
        if (lastChat is not null && TryParseBalanceFromChat([lastChat], username, out chatMoney))
            return new DonutStats { Money = chatMoney };

        if (rawGuiText is not { Count: > 0 }) return null;
        var payload = new DonutBridgeStatsPayload
        {
            RawGuiText = rawGuiText.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
        };
        var stats = FromPayload(payload);
        return HasMinimumStats(stats, payload) ? stats : null;
    }

    private static string? ExtractLastChatFromError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var match = LastChatPattern().Match(error);
        if (!match.Success) return null;
        var text = match.Groups["chat"].Value.Trim();
        return text.Length == 0 ? null : text;
    }

    public static string ExtractLastNumber(string text)
    {
        text = Clean(text).Replace(",", string.Empty);
        Match? last = null;
        foreach (Match match in CompactNumberPattern().Matches(text))
        {
            if (match.Success) last = match;
        }
        return last?.Value.Trim() ?? string.Empty;
    }

    public static decimal ParseCompactDecimal(string text)
    {
        var matchText = ExtractLastNumber(text);
        if (matchText.Length == 0) return 0;
        var match = CompactNumberPattern().Match(matchText);
        if (!match.Success) return 0;
        if (!decimal.TryParse(match.Groups["num"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            return 0;
        try
        {
            return match.Groups["suffix"].Value.ToUpperInvariant() switch
            {
                "K" => number * 1_000m,
                "M" => number * 1_000_000m,
                "B" => number * 1_000_000_000m,
                "T" => number * 1_000_000_000_000m,
                _ => number
            };
        }
        catch (OverflowException) { return 0; }
    }

    public static long ParseCompactLong(string text)
    {
        var value = ParseCompactDecimal(text);
        if (value >= long.MaxValue) return long.MaxValue;
        if (value <= long.MinValue) return long.MinValue;
        return (long)value;
    }

    public static long ParsePlaytimeSeconds(string text)
    {
        text = Clean(text).ToLowerInvariant();
        long total = 0;
        foreach (Match match in DurationPartPattern().Matches(text))
        {
            if (!long.TryParse(match.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;
            total += match.Groups["unit"].Value[0] switch
            {
                'd' => value * 86_400,
                'h' => value * 3_600,
                'm' => value * 60,
                's' => value,
                _ => 0
            };
        }
        return total;
    }

    private static string Clean(string? text) =>
        text is null ? string.Empty : FormattingPattern().Replace(text, string.Empty).Trim();

    [GeneratedRegex(@"lastChat='(?<chat>(?:\\'|[^'])*?)'", RegexOptions.CultureInvariant)]
    private static partial Regex LastChatPattern();

    [GeneratedRegex(@"(?<num>\d+(?:\.\d+)?)\s*(?<suffix>[kmbt])(?![\p{L}\d])|(?<num>\d+(?:\.\d+)?)(?![\p{L}\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompactNumberPattern();

    [GeneratedRegex(@"(?<num>\d+)\s*(?<unit>days?|hours?|minutes?|seconds?|[dhms])(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPartPattern();

    [GeneratedRegex("§.", RegexOptions.CultureInvariant)]
    private static partial Regex FormattingPattern();
}
