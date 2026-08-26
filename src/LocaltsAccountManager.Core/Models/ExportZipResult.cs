namespace LocaltsAccountManager.Core.Models;

public sealed class ExportZipResult
{
    public required string Path { get; init; }
    public int ExportedCount { get; init; }
    public int SkippedCount { get; init; }
}
