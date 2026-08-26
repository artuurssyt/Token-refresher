namespace LocaltsAccountManager.Core.Interfaces;

public interface IBatchProcessor
{
    event EventHandler<BatchProgressEventArgs>? ProgressChanged;
    bool IsRunning { get; }
    Task StartAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task CancelAsync();
    Task RetryFailedAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task RefreshAccountAsync(Guid batchId, Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class BatchProgressEventArgs : EventArgs
{
    public Guid BatchId { get; init; }
    public Models.BatchRecord Batch { get; init; } = null!;
    public Models.AccountRecord? LastUpdatedAccount { get; init; }
}
