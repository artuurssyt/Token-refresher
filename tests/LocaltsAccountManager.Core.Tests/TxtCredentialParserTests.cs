using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Infrastructure.Import;
using LocaltsAccountManager.Infrastructure.Security;

namespace LocaltsAccountManager.Core.Tests;

public class TxtCredentialParserTests
{
    private readonly ITxtCredentialParser _parser = new TxtCredentialParser(new CredentialFingerprinter());

    [Fact]
    public void ParseLine_TokenOnly_ParsesSuccessfully()
    {
        var result = _parser.ParseLine(1, "REFRESH_TOKEN_DATA");
        Assert.Equal(ParseStatus.Parsed, result.ParseStatus);
        Assert.Equal(OriginalInputForm.TokenOnly, result.InputForm);
        Assert.Equal("REFRESH_TOKEN_DATA", result.CredentialPayload);
    }

    [Fact]
    public void ParseLine_UsernameToken_SplitsOnFirstColonOnly()
    {
        var result = _parser.ParseLine(2, "Player:part1:part2");
        Assert.Equal(ParseStatus.Parsed, result.ParseStatus);
        Assert.Equal("Player", result.ProvidedUsername);
        Assert.Equal("part1:part2", result.CredentialPayload);
    }

    [Fact]
    public void ParseLine_BlankLine_IsSkipped()
    {
        var result = _parser.ParseLine(3, "   ");
        Assert.Equal(ParseStatus.SkippedBlank, result.ParseStatus);
    }

    [Fact]
    public void ParseLine_EmptyUsername_IsMalformed()
    {
        var result = _parser.ParseLine(4, ":token");
        Assert.Equal(ParseStatus.Malformed, result.ParseStatus);
    }

    [Fact]
    public void ParseLine_EmptyToken_IsMalformed()
    {
        var result = _parser.ParseLine(5, "user:");
        Assert.Equal(ParseStatus.Malformed, result.ParseStatus);
    }

    [Fact]
    public void ParseFile_HandlesUtf8Bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)':', (byte)'b' };
        using var stream = new MemoryStream(bytes);
        var results = _parser.ParseFile("test.txt", stream);
        Assert.Single(results);
        Assert.Equal(ParseStatus.Parsed, results[0].ParseStatus);
        Assert.Equal("a", results[0].ProvidedUsername);
        Assert.Equal("b", results[0].CredentialPayload);
    }

    [Fact]
    public void AnalyzeStructure_DetectsObservedMarkers()
    {
        var parsed = _parser.ParseLine(1, "user:M.C123__MsaArtifacts");
        var analysis = _parser.AnalyzeStructure(parsed);
        Assert.True(analysis.StartsWithMcPrefix);
        Assert.True(analysis.ContainsMsaArtifactsMarker);
    }

    [Fact]
    public void ParseLine_DoesNotMarkGarbageAsSuccessState()
    {
        var result = _parser.ParseLine(6, "::::");
        Assert.Equal(ParseStatus.Malformed, result.ParseStatus);
        Assert.NotEqual(ParseStatus.Parsed, result.ParseStatus);
    }
}
