using System.Text.Json.Serialization;

namespace DonutComparer.Core.Models;

public enum BazaarPriceMode
{
    ConservativeSell,
    ReplacementBuy
}

public sealed class AppSettings
{
    [JsonIgnore] public string HypixelApiKey { get; set; } = string.Empty;
    [JsonIgnore] public string DonutApiKey { get; set; } = string.Empty;
    public int Concurrency { get; set; } = 4;
    public int HypixelRequestsPerMinute { get; set; } = 100;
    public int DonutRequestsPerMinute { get; set; } = 220;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int RetryCount { get; set; } = 4;
    public int PlayerCacheMinutes { get; set; } = 15;
    public int MarketCacheMinutes { get; set; } = 20;
    public BazaarPriceMode BazaarPriceMode { get; set; } = BazaarPriceMode.ConservativeSell;
    public int MaxHypixelAuctionPages { get; set; } = 0;
    public int MaxDonutAuctionPages { get; set; } = 25;
    public decimal DonutShardUnitValue { get; set; }
    public bool IncludeMuseum { get; set; } = true;
    public bool IncludePlayerAuctions { get; set; } = true;
    public string HttpProxyUrl { get; set; } = string.Empty;
    public string HttpProxyUsername { get; set; } = string.Empty;
    [JsonIgnore] public string HttpProxyPassword { get; set; } = string.Empty;
    public bool DonutBridgeEnabled { get; set; } = true;
    /// <summary>When true, Donut stats come only from the in-game bridge — the Donut API is skipped.</summary>
    public bool DonutBridgeOnly { get; set; } = true;
    public int DonutBridgePort { get; set; } = 47891;
    public int DonutBridgeJobTimeoutSeconds { get; set; } = 60;
    public string DonutBridgeCommandTemplate { get; set; } = "/bal {username}";
    public int DonutBridgeCommandDelayMs { get; set; } = 1500;
}
