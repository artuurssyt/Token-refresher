namespace LocaltsAccountManager.Core.Interfaces;

public interface IBatchRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(Models.BatchRecord batch, CancellationToken cancellationToken = default);
    Task<Models.BatchRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.BatchRecord>> GetRecentAsync(int count = 20, CancellationToken cancellationToken = default);
    Task<Models.BatchRecord?> GetLatestIncompleteAsync(CancellationToken cancellationToken = default);
    Task<Models.BatchRecord?> GetLatestForStartupAsync(CancellationToken cancellationToken = default);
}
