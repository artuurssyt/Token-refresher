using LocaltsAccountManager.Core;

namespace LocaltsAccountManager.Core.Tests;

public class TokenExpiryFormatterTests
{
    [Fact]
    public void FormatRemaining_ShowsExpiredWhenPast()
    {
        var text = TokenExpiryFormatter.FormatRemaining(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow);
        Assert.Equal("Expired", text);
    }

    [Fact]
    public void FormatRemaining_ShowsHoursAndMinutes()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var text = TokenExpiryFormatter.FormatRemaining(now.AddHours(5).AddMinutes(7), now);
        Assert.Equal("5h 07m left", text);
    }

    [Fact]
    public void FormatStatus_ExpiringSoonWithinFifteenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var status = TokenExpiryFormatter.FormatStatus(now.AddMinutes(10), now);
        Assert.Equal("Expiring soon", status);
    }
}
