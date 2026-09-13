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

        var json = File.ReadAllText(ProfilePath, Encoding.UTF8);
        return JsonSerializer.Deserialize<AuthenticationProfile>(json, JsonOptions) ?? CreateDefaultTemplate();
    }

    public void Save(AuthenticationProfile profile)
    {
        Directory.CreateDirectory(GetAppRoot());
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(ProfilePath, json, Encoding.UTF8);
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

    private static string GetAppRoot() => ApplicationPaths.LocalAppDataRoot;
}

public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string SettingsPath => Path.Combine(GetAppRoot(), "appsettings.json");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings
            {
                DefaultExportDirectory = ApplicationPaths.DefaultExportDirectory
            };
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        var dirty = false;

        // Older installs defaulted to concurrency 3 and hammered login_with_xbox into 429s.
        if (settings.DefaultConcurrency > settings.MaxAdaptiveConcurrency)
        {
            settings.DefaultConcurrency = settings.MaxAdaptiveConcurrency;
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
            Save(settings);
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(GetAppRoot());
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json, Encoding.UTF8);
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
