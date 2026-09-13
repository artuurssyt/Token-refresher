using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaltsAccountManager.Infrastructure.Import;

/// <summary>
/// Pulls Microsoft MSA refresh tokens (M.C… / MsaArtifacts) out of mixed dump lines
/// into clean <c>username:refreshToken</c> lines for import.
/// </summary>
public static partial class RefreshTokenLineExtractor
{
    private static readonly Regex RefreshTokenField = RefreshTokenFieldPattern();
    private static readonly Regex McTokenField = McTokenFieldPattern();
    private static readonly Regex EmailAtStart = EmailAtStartPattern();

    /// <summary>Dump field labels that must never be mistaken for an account name.</summary>
    private static readonly HashSet<string> FieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "REFRESHTOKEN",
        "MCTOKEN",
        "TOKEN",
        "ACCESSTOKEN",
        "MSATOKEN",
        "PASS",
        "PASSWORD",
        "USER",
        "USERNAME",
        "EMAIL"
    };

    public sealed record ExtractedLine(string Username, string RefreshToken, int SourceLine);

    public sealed record ExtractResult(
        IReadOnlyList<ExtractedLine> Lines,
        int ScannedLines,
        int MissingRefresh,
        int Malformed);

    public static ExtractResult ExtractFromText(string text)
    {
        using var reader = new StringReader(text);
        return ExtractFromReader(reader);
    }

    public static ExtractResult ExtractFromStream(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ExtractFromReader(reader);
    }

    public static ExtractResult ExtractFromReader(TextReader reader)
    {
        var lines = new List<ExtractedLine>();
        var scanned = 0;
        var missing = 0;
        var malformed = 0;
        var lineNumber = 0;

        while (CappedLineReader.ReadLine(reader, CappedLineReader.MaxLineLength, out _) is { } raw)
        {
            lineNumber++;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0
                || trimmed.StartsWith("===", StringComparison.Ordinal)
                || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            if (trimmed.Length > CappedLineReader.MaxLineLength)
            {
                malformed++;
                continue;
            }

            if (!TryExtract(trimmed, out var username, out var refresh, out var reason))
            {
                if (reason == "missing")
                {
                    missing++;
                }
                else
                {
                    malformed++;
                }

                continue;
            }

            lines.Add(new ExtractedLine(username, refresh, lineNumber));
        }

        return new ExtractResult(lines, scanned, missing, malformed);
    }

    public static IEnumerable<string> ToUsernameTokenLines(IEnumerable<ExtractedLine> lines) =>
        lines.Select(line => $"{SanitizeUsername(line.Username)}:{line.RefreshToken}");

    /// <summary>
    /// Strips characters that would break the <c>username:token</c> round-trip when the emitted
    /// file is re-imported (the parser splits on the first colon).
    /// </summary>
    private static string SanitizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "unknown";
        }

        var cleaned = username.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    /// <summary>
    /// Returns true when <paramref name="line"/> contains a usable MSA refresh token.
    /// Sets username (email / label / JWT name) and the refresh payload.
    /// </summary>
    public static bool TryExtract(string line, out string username, out string refreshToken, out string failReason)
    {
        username = string.Empty;
        refreshToken = string.Empty;
        failReason = "malformed";

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            failReason = "missing";
            return false;
        }

        // Dump format: email:pass | … | MCTOKEN: … | REFRESHTOKEN: M.C…
        var match = RefreshTokenField.Match(trimmed);
        if (match.Success)
        {
            var refresh = match.Groups["token"].Value.Trim().TrimEnd('|').Trim();
            if (string.IsNullOrWhiteSpace(refresh))
            {
                failReason = "missing";
                return false;
            }

            if (!LooksLikeMsaRefresh(refresh))
            {
                failReason = "malformed";
                return false;
            }

            username = ResolveDumpUsername(trimmed) ?? "unknown";
            refreshToken = refresh;
            failReason = string.Empty;
            return true;
        }

        // Clean username:M.C… (or token-only M.C…)
        var colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            var left = trimmed[..colon].Trim();
            var right = trimmed[(colon + 1)..].Trim();
            if (LooksLikeMsaRefresh(right))
            {
                username = string.IsNullOrWhiteSpace(left) ? "unknown" : left;
                refreshToken = right;
                failReason = string.Empty;
                return true;
            }

            // email:password with no REFRESHTOKEN field
            failReason = "missing";
            return false;
        }

        if (LooksLikeMsaRefresh(trimmed))
        {
            username = "unknown";
            refreshToken = trimmed;
            failReason = string.Empty;
            return true;
        }

        failReason = "missing";
        return false;
    }

    private static string? ResolveDumpUsername(string line)
    {
        var email = EmailAtStart.Match(line);
        if (email.Success)
        {
            return email.Groups["email"].Value;
        }

        var mc = McTokenField.Match(line);
        if (mc.Success)
        {
            var jwtName = TryReadJwtProfileName(mc.Groups["token"].Value.Trim());
            if (!string.IsNullOrWhiteSpace(jwtName))
            {
                return jwtName;
            }
        }

        // Fall back to the label in the first pipe-delimited segment, but never promote a dump
        // field name (e.g. a bare "REFRESHTOKEN: M.C…" line) into an account name.
        var firstSegment = line.Split('|', 2)[0];
        var firstColon = firstSegment.IndexOf(':');
        if (firstColon > 0)
        {
            var left = firstSegment[..firstColon].Trim();
            if (left.Length > 0
                && !left.Contains(' ', StringComparison.Ordinal)
                && !FieldLabels.Contains(left))
            {
                return left;
            }
        }

        return null;
    }

    private static string? TryReadJwtProfileName(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pfd", out var pfd) && pfd.ValueKind == JsonValueKind.Array && pfd.GetArrayLength() > 0)
            {
                var first = pfd[0];
                if (first.TryGetProperty("name", out var name))
                {
                    return name.GetString();
                }
            }
        }
        catch
        {
            // ignore JWT parse failures
        }

        return null;
    }

    public static bool LooksLikeMsaRefresh(string value) =>
        value.StartsWith("M.C", StringComparison.OrdinalIgnoreCase)
        || value.Contains("MsaArtifacts", StringComparison.OrdinalIgnoreCase);

    // Stop at the next pipe so trailing dump fields are not swallowed into the token.
    [GeneratedRegex(@"REFRESHTOKEN\s*:\s*(?<token>[^|]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefreshTokenFieldPattern();

    [GeneratedRegex(@"MCTOKEN\s*:\s*(?<token>[^\|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex McTokenFieldPattern();

    [GeneratedRegex(@"^(?<email>[^\s:]+@[^\s:]+)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailAtStartPattern();
}
