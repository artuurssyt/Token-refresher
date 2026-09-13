using System.Text;
using System.Text.Json;
using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Infrastructure.Paths;

namespace LocaltsAccountManager.Infrastructure.Configuration;

public sealed class AuthenticationProfileStore : IAuthenticationProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ProfilePath => Path.Combine(GetAppRoot(), "authentication_profile.json");

    public AuthenticationProfile Load()
    {
        if (!File.Exists(ProfilePath))
        {
            return CreateDefaultTemplate();
        }

        // Load is called during DI setup, so a damaged file used to throw before the main window
        // existed and the app died with no message. Fall back to the template instead.
        try
        {
            var json = File.ReadAllText(ProfilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AuthenticationProfile>(json, JsonOptions) ?? CreateDefaultTemplate();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ConfigurationFileSafety.QuarantineCorruptFile(ProfilePath);
            return CreateDefaultTemplate();
        }
    }

    public void Save(AuthenticationProfile profile)
    {
        Directory.CreateDirectory(GetAppRoot());
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        ConfigurationFileSafety.WriteAllTextAtomic(ProfilePath, json);
    }

    public static AuthenticationProfile CreateDefaultTemplate() => new()
    {
        IsVerified = true,
        VerificationNotes =
            "Verified from Localts RefreshTokenApp.jar (org.localts.request.RefreshTokenRequest / RefreshTokenAuthentication). " +
            "Uses login.live.com public Xbox client 00000000402b5328 — not Azure AD MSAL.",
        Microsoft = new MicrosoftAuthSettings
        {
            ClientId = "00000000402b5328",
            TokenEndpoint = "https://login.live.com/oauth20_token.srf",
            RedirectUri = "https://login.live.com/oauth20_desktop.srf",
            Scope = "service::user.auth.xboxlive.com::MBI_SSL"
        },
        Minecraft = new MinecraftAuthSettings
        {
            Enabled = true,
            XboxUserAuthenticateUrl = "https://user.auth.xboxlive.com/user/authenticate",
            XboxXstsAuthorizeUrl = "https://xsts.auth.xboxlive.com/xsts/authorize",
            MinecraftLoginWithXboxUrl = "https://api.minecraftservices.com/authentication/login_with_xbox",
            MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile",
            MinecraftEntitlementsUrl = "https://api.minecraftservices.com/entitlements/license?requestId=auth",
            RelyingParty = "http://auth.xboxlive.com",
            XboxRelyingParty = "rp://api.minecraftservices.com/",
            RpsTicketPrefix = "t=",
            RequireEntitlementCheck = true
        }
    };

    public static void EnsureTemplateExists()
    {
        var store = new AuthenticationProfileStore();

        // Runs while the DI container is being built, before any window exists, so an unwritable
        // profile folder must not take the process down. Load() already falls back to the
        // in-memory template when the file cannot be read.
        try
        {
            if (!File.Exists(store.ProfilePath))
            {
                store.Save(CreateDefaultTemplate());
                return;
            }

            // Upgrade previously blocked / empty profiles to Localts-extracted defaults.
            var existing = store.Load();
            if (!existing.IsVerified || !existing.Microsoft.IsConfigured || !existing.Minecraft.IsConfigured)
            {
                store.Save(CreateDefaultTemplate());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string GetAppRoot() => ApplicationPaths.LocalAppDataRoot;
}

public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();

    /// <summary>
    /// Serialized snapshot of the last known settings. <see cref="Load"/> is called on every retry,
    /// backoff and throttle decision, so reading and migrating the file each time meant constant
    /// disk I/O (and a rewrite whenever a migration fired) from every worker thread at once.
    /// </summary>
    private string? _cachedJson;

    public string SettingsPath => Path.Combine(GetAppRoot(), "appsettings.json");

    public AppSettings Load()
    {
        lock (_sync)
        {
            if (_cachedJson is { } cached)
            {
                // Deserialize per call so callers still get their own instance to mutate, as they
                // did when every Load hit the disk.
                return JsonSerializer.Deserialize<AppSettings>(cached, JsonOptions) ?? new AppSettings();
            }

            var settings = LoadFromDisk();
            _cachedJson = JsonSerializer.Serialize(settings, JsonOptions);
            return settings;
        }
    }

    private AppSettings LoadFromDisk()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings
            {
                DefaultExportDirectory = ApplicationPaths.DefaultExportDirectory
            };
            TrySave(defaults);
            return defaults;
        }

        AppSettings settings;
        var dirty = false;
        try
        {
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A truncated appsettings.json (e.g. power loss during a save) used to throw from every
            // Load() call, which meant startup, import and export all failed with no way back.
            ConfigurationFileSafety.QuarantineCorruptFile(SettingsPath);
            settings = new AppSettings { DefaultExportDirectory = ApplicationPaths.DefaultExportDirectory };
            dirty = true;
        }

        // Older installs defaulted to concurrency 3 and hammered login_with_xbox into 429s.
        // Clamp to at least 1: a damaged file with MaxAdaptiveConcurrency <= 0 would otherwise
        // drag DefaultConcurrency down with it and create batches that never dispatch a worker.
        var maxConcurrency = Math.Max(1, settings.MaxAdaptiveConcurrency);
        if (settings.MaxAdaptiveConcurrency != maxConcurrency)
        {
            settings.MaxAdaptiveConcurrency = maxConcurrency;
            dirty = true;
        }

        var defaultConcurrency = Math.Clamp(settings.DefaultConcurrency, 1, maxConcurrency);
        if (settings.DefaultConcurrency != defaultConcurrency)
        {
            settings.DefaultConcurrency = defaultConcurrency;
            dirty = true;
        }

        if (ApplicationPaths.NeedsPortableExportMigration(settings.DefaultExportDirectory)
            || !PathsEqual(settings.DefaultExportDirectory, ApplicationPaths.DefaultExportDirectory))
        {
            // Keep exports beside the current .exe so the app is portable across drives.
            settings.DefaultExportDirectory = ApplicationPaths.DefaultExportDirectory;
            dirty = true;
        }

        // Older builds defaulted this on and refreshed the pool on every launch.
        // Force opt-in once so opening the app only loads data.
        if (settings.PoolAutoManageEnabled && !settings.PoolAutoManageOptInAcknowledged)
        {
            settings.PoolAutoManageEnabled = false;
            settings.PoolAutoManageOptInAcknowledged = true;
            dirty = true;
        }

        if (dirty)
        {
            TrySave(settings);
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(GetAppRoot());
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            ConfigurationFileSafety.WriteAllTextAtomic(SettingsPath, json);
            _cachedJson = json;
        }
    }

    /// <summary>
    /// Persists migrated defaults without letting a read-only or full disk turn a settings read
    /// into a failure; the in-memory values stay usable either way.
    /// </summary>
    private void TrySave(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(GetAppRoot());
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            ConfigurationFileSafety.WriteAllTextAtomic(SettingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string GetAppRoot() => ApplicationPaths.LocalAppDataRoot;

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
