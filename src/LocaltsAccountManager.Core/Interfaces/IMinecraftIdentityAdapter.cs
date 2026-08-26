namespace LocaltsAccountManager.Core.Interfaces;

public interface IMinecraftIdentityAdapter
{
    bool IsConfigured { get; }
    Task<Models.AuthenticationResult> RetrieveIdentityAsync(string microsoftAccessToken, CancellationToken cancellationToken = default);
}
