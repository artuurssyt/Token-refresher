namespace LocaltsAccountManager.Core;

public static class TokenExpiryFormatter
{
    public static string FormatRemaining(DateTimeOffset? expiresAt, DateTimeOffset? now = null)
    {
        if (!expiresAt.HasValue)
        {
            return "—";
        }

        var current = now ?? DateTimeOffset.UtcNow;
        var remaining = expiresAt.Value - current;
        if (remaining <= TimeSpan.Zero)
        {
            return "Expired";
        }

        if (remaining.TotalHours >= 24)
        {
            var days = (int)remaining.TotalDays;
            var hours = remaining.Hours;
            return $"{days}d {hours}h left";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m left";
        }

        return $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s left";
    }

    public static string FormatStatus(DateTimeOffset? expiresAt, DateTimeOffset? now = null)
    {
        if (!expiresAt.HasValue)
        {
            return "Unknown";
        }

        var current = now ?? DateTimeOffset.UtcNow;
        var remaining = expiresAt.Value - current;
        if (remaining <= TimeSpan.Zero)
        {
            return "Expired";
        }

        if (remaining.TotalMinutes <= 15)
        {
            return "Expiring soon";
        }

        return "Valid";
    }
}
