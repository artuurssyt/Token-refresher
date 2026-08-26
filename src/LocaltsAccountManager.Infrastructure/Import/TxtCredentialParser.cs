using System.Text;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Import;

public sealed class TxtCredentialParser : ITxtCredentialParser
{
    private const int MaxLineLength = 65536;
    private readonly ICredentialFingerprinter _fingerprinter;

    public TxtCredentialParser(ICredentialFingerprinter fingerprinter)
    {
        _fingerprinter = fingerprinter;
    }

    public IReadOnlyList<ParsedCredentialLine> ParseFile(string filePath, Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var results = new List<ParsedCredentialLine>();
        var lineNumber = 0;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine() ?? string.Empty;
            lineNumber++;
            results.Add(ParseLine(lineNumber, line));
        }

        return results;
    }

    public ParsedCredentialLine ParseLine(int lineNumber, string line)
    {
        if (line.Length > MaxLineLength)
        {
            return new ParsedCredentialLine
            {
                LineNumber = lineNumber,
                OriginalLine = line[..Math.Min(line.Length, 120)] + "...",
                ParseStatus = ParseStatus.Malformed,
                ParseError = "Line exceeds maximum supported length.",
                InputForm = OriginalInputForm.Unknown
            };
        }

        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new ParsedCredentialLine
            {
                LineNumber = lineNumber,
                OriginalLine = line,
                ParseStatus = ParseStatus.SkippedBlank,
                InputForm = OriginalInputForm.Unknown
            };
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex >= 0)
        {
            var username = trimmed[..colonIndex].Trim();
            var token = trimmed[(colonIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(username))
            {
                return Malformed(lineNumber, line, OriginalInputForm.UsernameToken, "Empty username before delimiter.");
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return Malformed(lineNumber, line, OriginalInputForm.UsernameToken, "Missing credential after delimiter.");
            }

            return Parsed(lineNumber, line, OriginalInputForm.UsernameToken, username, token);
        }

        return Parsed(lineNumber, line, OriginalInputForm.TokenOnly, null, trimmed);
    }

    public CredentialStructureAnalysis AnalyzeStructure(ParsedCredentialLine parsed)
    {
        var payload = parsed.CredentialPayload ?? string.Empty;
        var prefixLength = Math.Min(8, payload.Length);
        var suffixLength = Math.Min(12, payload.Length);
        var notes = new List<string>();

        if (parsed.InputForm == OriginalInputForm.UsernameToken)
        {
            notes.Add("Input form: username:token (split on first colon only).");
        }
        else if (parsed.InputForm == OriginalInputForm.TokenOnly)
        {
            notes.Add("Input form: token-only line.");
        }

        if (payload.StartsWith("M.C", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Observation: credential prefix resembles reported M.C... pattern (unverified lead only).");
        }

        if (payload.Contains("MsaArtifacts", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Observation: credential contains MsaArtifacts marker (unverified lead only).");
        }

        return new CredentialStructureAnalysis
        {
            LineNumber = parsed.LineNumber,
            InputForm = parsed.InputForm,
            ProvidedUsername = parsed.ProvidedUsername,
            CredentialLength = payload.Length,
            CredentialPrefix = prefixLength == 0 ? string.Empty : payload[..prefixLength],
            CredentialSuffix = suffixLength == 0 ? string.Empty : payload[^suffixLength..],
            TokenFingerprint = parsed.TokenFingerprint,
            ContainsMsaArtifactsMarker = payload.Contains("MsaArtifacts", StringComparison.OrdinalIgnoreCase),
            StartsWithMcPrefix = payload.StartsWith("M.C", StringComparison.OrdinalIgnoreCase),
            ObservedStructureNotes = string.Join(' ', notes)
        };
    }

    private ParsedCredentialLine Parsed(int lineNumber, string originalLine, OriginalInputForm form, string? username, string token)
    {
        return new ParsedCredentialLine
        {
            LineNumber = lineNumber,
            OriginalLine = originalLine,
            InputForm = form,
            ProvidedUsername = username,
            CredentialPayload = token,
            ParseStatus = ParseStatus.Parsed,
            TokenFingerprint = _fingerprinter.ComputeFingerprint(token)
        };
    }

    private static ParsedCredentialLine Malformed(int lineNumber, string originalLine, OriginalInputForm form, string error) =>
        new()
        {
            LineNumber = lineNumber,
            OriginalLine = originalLine,
            InputForm = form,
            ParseStatus = ParseStatus.Malformed,
            ParseError = error
        };
}
