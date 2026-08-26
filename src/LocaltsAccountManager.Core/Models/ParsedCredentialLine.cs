namespace LocaltsAccountManager.Core.Models;

public sealed class ParsedCredentialLine
{
    public int LineNumber { get; set; }
    public string OriginalLine { get; set; } = string.Empty;
    public Enums.OriginalInputForm InputForm { get; set; }
    public string? ProvidedUsername { get; set; }
    public string? CredentialPayload { get; set; }
    public Enums.ParseStatus ParseStatus { get; set; }
    public string? ParseError { get; set; }
    public string TokenFingerprint { get; set; } = string.Empty;
}
