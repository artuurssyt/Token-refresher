namespace LocaltsAccountManager.Core.Configuration;

/// <summary>
/// Authentication profile populated only after Phase 0 verification.
/// Empty values mean authentication is BLOCKED.
/// </summary>
public sealed class AuthenticationProfile
{
    public bool IsVerified { get; set; }
    public string? VerificationNotes { get; set; }

    public MicrosoftAuthSettings Microsoft { get; set; } = new();
    public MinecraftAuthSettings Minecraft { get; set; } = new();
}

public sealed class MicrosoftAuthSettings
{
    /// <summary>
    /// OAuth client ID. Localts RefreshTokenApp embeds 00000000402b5328.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Token endpoint. Localts uses https://login.live.com/oauth20_token.srf
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Redirect URI posted with the refresh grant. Localts uses oauth20_desktop.srf.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// OAuth scope string (single space-delimited value as Localts posts it).
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>Legacy Azure AD authority — unused by Localts live.com refresh path.</summary>
    public string? Authority { get; set; }

    /// <summary>Legacy MSAL scopes array — unused by Localts live.com refresh path.</summary>
    public string[] Scopes { get; set; } = Array.Empty<string>();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(TokenEndpoint) &&
        !string.IsNullOrWhiteSpace(Scope);
}

public sealed class MinecraftAuthSettings
{
    public bool Enabled { get; set; }
    public string? XboxUserAuthenticateUrl { get; set; }
    public string? XboxXstsAuthorizeUrl { get; set; }
    public string? MinecraftLoginWithXboxUrl { get; set; }
    public string? MinecraftProfileUrl { get; set; }
    public string? MinecraftEntitlementsUrl { get; set; }
    public string? RelyingParty { get; set; }
    public string? XboxRelyingParty { get; set; }

    /// <summary>
    /// Xbox RpsTicket prefix. Localts RefreshTokenApp uses "t=" (not "d=").
    /// </summary>
    public string RpsTicketPrefix { get; set; } = "t=";

    public bool RequireEntitlementCheck { get; set; } = true;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(XboxUserAuthenticateUrl) &&
        !string.IsNullOrWhiteSpace(XboxXstsAuthorizeUrl) &&
        !string.IsNullOrWhiteSpace(MinecraftLoginWithXboxUrl) &&
        !string.IsNullOrWhiteSpace(MinecraftProfileUrl);
}

public sealed class AppSettings
{
    /// <summary>
    /// Parallel account workers. Keep at 1 — Minecraft login_with_xbox rate-limits hard under concurrency.
    /// </summary>
    public int DefaultConcurrency { get; set; } = 1;

    /// <summary>Ceiling for adaptive concurrency after successes. Cap at 1 to avoid MC 429 storms.</summary>
    public int MaxAdaptiveConcurrency { get; set; } = 1;

    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Extra attempts allowed when the only failure is HTTP 429 (tokens are usually still valid).</summary>
    public int MaxRateLimitRetryAttempts { get; set; } = 25;

    public int BaseBackoffMilliseconds { get; set; } = 5000;
    public int MaxBackoffMilliseconds { get; set; } = 180000;

    /// <summary>When Minecraft/Microsoft return 429 without Retry-After, pause the whole batch this long.</summary>
    public int DefaultRateLimitCooldownMilliseconds { get; set; } = 60000;

    /// <summary>Minimum gap between login_with_xbox calls (serialized).</summary>
    public int MinecraftAuthMinIntervalMilliseconds { get; set; } = 4000;

    public bool ExportUniqueUsernamesOnly { get; set; }
    public bool PersistAccountsBetweenLaunches { get; set; } = true;
    public bool AutoResumeInterruptedBatches { get; set; }
    public ExportLocationMode ExportLocation { get; set; } = ExportLocationMode.SameAsImportFile;

    /// <summary>
    /// Default folder for batch and library exports (ZIP, usernames, errors, etc.).
    /// Empty means resolve next to the .exe (exports\) at runtime.
    /// </summary>
    public string DefaultExportDirectory { get; set; } = string.Empty;

    public ProxySettings Proxy { get; set; } = new();
    public bool EnablePublicProfileLookup { get; set; } = true;
    public int ProfileLookupRateLimitPerMinute { get; set; } = 300;

    /// <summary>When enabled, the pool auto-refreshes accounts before tokens expire.</summary>
    public bool PoolAutoManageEnabled { get; set; } = true;

    /// <summary>How often the pool checks for accounts needing refresh.</summary>
    public int PoolAutoRefreshIntervalMinutes { get; set; } = 30;

    /// <summary>Refresh pool accounts whose MC token expires within this many hours.</summary>
    public int PoolRefreshBeforeExpiryHours { get; set; } = 4;

    /// <summary>DPAPI reference for the Localts API key (X-API-Key).</summary>
    public string? LocaltsApiKeyReference { get; set; }

    /// <summary>Delay between Localts order-detail requests while importing.</summary>
    public int LocaltsImportRequestDelayMilliseconds { get; set; } = 350;

    /// <summary>Wait time between retries when Localts returns HTTP 429.</summary>
    public int LocaltsRateLimitRetrySeconds { get; set; } = 30;
}

public enum ExportLocationMode
{
    SameAsImportFile,
    UserSelected,
    ApplicationData
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
