namespace LocaltsAccountManager.Core.Models;

public sealed class CredentialStructureAnalysis
{
    public int LineNumber { get; init; }
    public Enums.OriginalInputForm InputForm { get; init; }
    public string? ProvidedUsername { get; init; }
    public int CredentialLength { get; init; }
    public string CredentialPrefix { get; init; } = string.Empty;
    public string CredentialSuffix { get; init; } = string.Empty;
    public string TokenFingerprint { get; init; } = string.Empty;
    public bool ContainsMsaArtifactsMarker { get; init; }
    public bool StartsWithMcPrefix { get; init; }
    public string ObservedStructureNotes { get; init; } = string.Empty;
}
