using System.Text.RegularExpressions;

namespace DonutHypixelPlayerComparer.Services;

public sealed record ParsedUsernames(
    IReadOnlyList<string> Usernames,
    IReadOnlyList<string> Duplicates,
    IReadOnlyList<string> Invalid);

public static partial class UsernameParser
{
    public static ParsedUsernames Parse(string text)
    {
        var names = new List<string>();
        var duplicates = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = text.Replace('\r', '\n').Split(new[] { '\n', ',', ';', '\t' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in tokens)
        {
            var token = raw.Trim().Trim('"', '\'');
            if (token.Equals("username", StringComparison.OrdinalIgnoreCase)
                || token.Equals("ign", StringComparison.OrdinalIgnoreCase)
                || token.Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
            if (!UsernamePattern().IsMatch(token) && !UuidPattern().IsMatch(token))
            {
                invalid.Add(token);
                continue;
            }
            if (!seen.Add(token)) duplicates.Add(token);
            else names.Add(token);
        }
        return new ParsedUsernames(names, duplicates, invalid);
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();
}
