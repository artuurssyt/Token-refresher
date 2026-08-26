using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Minecraft;

public static class MinecraftProfileEnrichment
{
    public static void ApplyIdentity(AccountRecord account, MinecraftProfileIdentity identity)
    {
        account.AuthenticatedMinecraftUsername = identity.Name;
        account.AuthenticatedMinecraftUuid = identity.Uuid;
    }

    public static void ApplyUsernameComparison(AccountRecord account)
    {
        if (string.IsNullOrWhiteSpace(account.ProvidedUsername) ||
            string.IsNullOrWhiteSpace(account.AuthenticatedMinecraftUsername))
        {
            account.ProvidedUsernameMismatch = null;
            return;
        }

        account.ProvidedUsernameMismatch = !string.Equals(
            account.ProvidedUsername,
            account.AuthenticatedMinecraftUsername,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string? BuildMismatchNote(AccountRecord account)
    {
        if (account.ProvidedUsernameMismatch != true)
        {
            return null;
        }

        return $"Provided username '{account.ProvidedUsername}' differs from authenticated username '{account.AuthenticatedMinecraftUsername}'.";
    }
}
