using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Minecraft;

namespace LocaltsAccountManager.Core.Tests;

public class MinecraftProfileEnrichmentTests
{
    [Fact]
    public void ApplyUsernameComparison_FlagsMismatchWhenNamesDiffer()
    {
        var account = new AccountRecord
        {
            ProvidedUsername = "VendorLabel",
            AuthenticatedMinecraftUsername = "RealPlayer"
        };

        MinecraftProfileEnrichment.ApplyUsernameComparison(account);

        Assert.True(account.ProvidedUsernameMismatch);
        Assert.Contains("VendorLabel", MinecraftProfileEnrichment.BuildMismatchNote(account));
    }

    [Fact]
    public void ApplyUsernameComparison_AllowsCaseOnlyDifference()
    {
        var account = new AccountRecord
        {
            ProvidedUsername = "RealPlayer",
            AuthenticatedMinecraftUsername = "realplayer"
        };

        MinecraftProfileEnrichment.ApplyUsernameComparison(account);

        Assert.False(account.ProvidedUsernameMismatch);
        Assert.Null(MinecraftProfileEnrichment.BuildMismatchNote(account));
    }

    [Fact]
    public void ApplyIdentity_UpdatesCanonicalNameAndUuid()
    {
        var account = new AccountRecord();
        var identity = new MinecraftProfileIdentity("CanonicalName", "abc123def4567890abc123def4567890");

        MinecraftProfileEnrichment.ApplyIdentity(account, identity);

        Assert.Equal("CanonicalName", account.AuthenticatedMinecraftUsername);
        Assert.Equal("abc123def4567890abc123def4567890", account.AuthenticatedMinecraftUuid);
    }
}
