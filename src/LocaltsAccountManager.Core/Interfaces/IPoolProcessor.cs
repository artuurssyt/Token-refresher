namespace LocaltsAccountManager.Core.Interfaces;

public interface IPoolProcessor
{
    event EventHandler<PoolProgressEventArgs>? ProgressChanged;
    bool IsRunning { get; }
    Task RefreshPoolAsync(CancellationToken cancellationToken = default);
    Task AutoManageAsync(CancellationToken cancellationToken = default);
    Task CancelAsync();
}

public sealed class PoolProgressEventArgs : EventArgs
{
    public int Total { get; init; }
    public int Pending { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Removed { get; init; }
    public Models.AccountRecord? LastUpdatedAccount { get; init; }
    public bool AccountRemoved { get; init; }
}
