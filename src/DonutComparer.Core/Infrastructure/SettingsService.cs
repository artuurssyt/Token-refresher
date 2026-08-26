using System.Text.Json;
using DonutComparer.Core.Models;

namespace DonutComparer.Core.Infrastructure;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly DpapiSecretStore _secrets = new();

    public AppSettings Load()
    {
        AppPaths.EnsureCreated();
        AppSettings settings;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new()
                : new();
        }
        catch { settings = new(); }

        var secrets = _secrets.Load();
        settings.HypixelApiKey = secrets.GetValueOrDefault("HypixelApiKey", string.Empty);
        settings.DonutApiKey = secrets.GetValueOrDefault("DonutApiKey", string.Empty);
        settings.HttpProxyPassword = secrets.GetValueOrDefault("HttpProxyPassword", string.Empty);
        Normalize(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        AppPaths.EnsureCreated();
        var temporary = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, AppPaths.SettingsFile, true);
        _secrets.Save(new Dictionary<string, string>
        {
            ["HypixelApiKey"] = settings.HypixelApiKey.Trim(),
            ["DonutApiKey"] = settings.DonutApiKey.Trim(),
            ["HttpProxyPassword"] = settings.HttpProxyPassword
        });
    }

    private static void Normalize(AppSettings settings)
    {
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
