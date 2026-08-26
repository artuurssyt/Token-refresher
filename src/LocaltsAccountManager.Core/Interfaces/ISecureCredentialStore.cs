namespace LocaltsAccountManager.Core.Interfaces;

public interface ISecureCredentialStore
{
    Task<string> StoreAsync(string credential, CancellationToken cancellationToken = default);
    Task<string?> RetrieveAsync(string credentialReference, CancellationToken cancellationToken = default);
    Task<string> ReplaceAsync(string oldReference, string newCredential, CancellationToken cancellationToken = default);
    Task DeleteAsync(string credentialReference, CancellationToken cancellationToken = default);
}
