namespace LocaltsAccountManager.Core.Interfaces;

public interface IMicrosoftAuthAdapter
{
    bool IsConfigured { get; }
    Task<Models.AuthenticationResult> AuthenticateAsync(string refreshToken, CancellationToken cancellationToken = default);
}
