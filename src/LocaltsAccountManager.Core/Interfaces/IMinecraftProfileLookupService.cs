namespace LocaltsAccountManager.Core.Interfaces;

public interface IMinecraftProfileLookupService
{
    Task<Models.MinecraftProfileIdentity?> ResolveAsync(string usernameOrUuid, CancellationToken cancellationToken = default);
}
