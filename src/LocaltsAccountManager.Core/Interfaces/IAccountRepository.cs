namespace LocaltsAccountManager.Core.Interfaces;

public interface IAccountRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(Models.AccountRecord record, CancellationToken cancellationToken = default);
    Task<Models.AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetByBatchIdAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetIncompleteByBatchIdAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetRetryEligibleAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetAllSucceededAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetAllWithStoredAccessTokensAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetAllActiveWithStoredAccessTokensAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetPoolAccountsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.AccountRecord>> GetPoolActiveWithStoredAccessTokensAsync(CancellationToken cancellationToken = default);
    Task<Models.AccountRecord?> GetPoolAccountByFingerprintAsync(string tokenFingerprint, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
