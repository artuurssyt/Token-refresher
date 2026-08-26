namespace LocaltsAccountManager.Core.Models;

public sealed class BatchRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SourceFilePath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public Enums.BatchStatus Status { get; set; } = Enums.BatchStatus.Created;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public int TotalRecords { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int Cancelled { get; set; }
    public int DuplicatesFlagged { get; set; }
    public int ConcurrencyLimit { get; set; } = 1;
    public int EffectiveConcurrency { get; set; } = 1;
    public DateTimeOffset? GlobalRateLimitUntil { get; set; }
}
