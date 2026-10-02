using System.Text.Json;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Infrastructure;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly DpapiSecretStore _secrets = new();

    /// <summary>
    /// Why the last Load fell back to defaults, if it did. Silently resetting every setting — or
    /// every API key — is indistinguishable from the app forgetting them for no reason.
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        AppPaths.EnsureCreated();
        AppSettings settings;
        string? warning = null;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            settings = new();
            warning = "settings.json could not be read (" + ex.Message + "), so defaults are in use.";
            Quarantine();
        }

        var secrets = _secrets.Load();
        settings.HypixelApiKey = secrets.GetValueOrDefault("HypixelApiKey", string.Empty);
        settings.DonutApiKey = secrets.GetValueOrDefault("DonutApiKey", string.Empty);
        settings.HttpProxyPassword = secrets.GetValueOrDefault("HttpProxyPassword", string.Empty);
        if (_secrets.LastLoadError is { } secretError)
            warning = warning is null ? secretError : warning + " " + secretError;
        LastLoadWarning = warning;
        Normalize(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        AppPaths.EnsureCreated();
        var temporary = AppPaths.SettingsFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, AppPaths.SettingsFile, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            throw;
        }
        _secrets.Save(new Dictionary<string, string>
        {
            ["HypixelApiKey"] = settings.HypixelApiKey.Trim(),
            ["DonutApiKey"] = settings.DonutApiKey.Trim(),
            ["HttpProxyPassword"] = settings.HttpProxyPassword
        });
        LastLoadWarning = null;
    }

    private static void Quarantine()
    {
        try { File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + ".unreadable", true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Normalize(AppSettings settings)
    {
        // A JSON null deserializes over the property initializer, so every string is re-grounded
        // here rather than left to blow up at the first Trim().
        settings.HypixelApiKey ??= string.Empty;
        settings.DonutApiKey ??= string.Empty;
        settings.HttpProxyUrl ??= string.Empty;
        settings.HttpProxyUsername ??= string.Empty;
        settings.HttpProxyPassword ??= string.Empty;
        settings.Concurrency = Math.Clamp(settings.Concurrency, 1, 16);
        settings.HypixelRequestsPerMinute = Math.Clamp(settings.HypixelRequestsPerMinute, 1, 300);
        settings.DonutRequestsPerMinute = Math.Clamp(settings.DonutRequestsPerMinute, 1, 250);
        settings.RequestTimeoutSeconds = Math.Clamp(settings.RequestTimeoutSeconds, 5, 180);
        settings.RetryCount = Math.Clamp(settings.RetryCount, 0, 8);
        settings.PlayerCacheMinutes = Math.Clamp(settings.PlayerCacheMinutes, 0, 1440);
        settings.MarketCacheMinutes = Math.Clamp(settings.MarketCacheMinutes, 1, 1440);
        settings.MaxHypixelAuctionPages = Math.Clamp(settings.MaxHypixelAuctionPages, 0, 200);
        settings.MaxDonutAuctionPages = Math.Clamp(settings.MaxDonutAuctionPages, 0, 200);
        settings.DonutShardUnitValue = Math.Max(0, settings.DonutShardUnitValue);
        settings.DonutBridgePort = Math.Clamp(settings.DonutBridgePort, 1024, 65535);
        settings.DonutBridgeJobTimeoutSeconds = Math.Clamp(settings.DonutBridgeJobTimeoutSeconds, 15, 300);
        settings.DonutBridgeCommandDelayMs = Math.Clamp(settings.DonutBridgeCommandDelayMs, 250, 10_000);
        if (string.IsNullOrWhiteSpace(settings.DonutBridgeCommandTemplate)
            || settings.DonutBridgeCommandTemplate.Trim().Equals("/stats {username}", StringComparison.OrdinalIgnoreCase)
            || settings.DonutBridgeCommandTemplate.Trim().Equals("/playerstats {username}", StringComparison.OrdinalIgnoreCase))
        {
            settings.DonutBridgeCommandTemplate = "/bal {username}";
        }
    }
}
