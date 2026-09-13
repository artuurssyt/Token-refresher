using System.Collections.Concurrent;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public sealed record ScanProgress(
    int Completed,
    int Total,
    string Message,
    PlayerScanResult? Result = null,
    DonutBridgeStatus? BridgeStatus = null);

public sealed class PlayerScanService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ResilientHttpClient _http;
    private readonly MinecraftIdentityClient _identity;
    private readonly HypixelClient _hypixel;
    private readonly DonutClient _donut;
    private DonutBridgeHost? _bridge;
    // The bridge drives one Minecraft client, so it stays serial even when the rest of the scan is parallel.
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);
    private int _consecutiveApiFailures;
    private int _apiAbandoned;

    public PlayerScanService(AppSettings settings)
    {
        _settings = settings;
        _http = new ResilientHttpClient(settings);
        _identity = new MinecraftIdentityClient(_http);
        _hypixel = new HypixelClient(_http, settings);
        _donut = new DonutClient(_http, settings);
    }

    public DonutBridgeStatus? BridgeStatus => _bridge?.Status;

    /// <summary>
    /// The API is tried first when a key exists and bridge-only mode is off. The bridge stays armed
    /// so a flaky or empty API response falls through to the in-game lookup instead of losing the player.
    /// </summary>
    private bool ApiFirst => _donut.IsConfigured && !_settings.DonutBridgeOnly && Volatile.Read(ref _apiAbandoned) == 0;

    public async Task<IReadOnlyList<PlayerScanResult>> ScanAsync(IReadOnlyList<string> usernames,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var setupErrors = new List<string>();
        var completed = 0;
        Volatile.Write(ref _apiAbandoned, 0);
        Volatile.Write(ref _consecutiveApiFailures, 0);
        if (_settings.DonutBridgeEnabled && ApiFirst)
            progress?.Report(new ScanProgress(0, usernames.Count,
                "Donut stats: trying the official API first, falling back to the in-game bridge whenever it fails."));
        else if (_settings.DonutBridgeEnabled && _settings.DonutBridgeOnly)
            progress?.Report(new ScanProgress(0, usernames.Count,
                "Donut stats: bridge-only mode — using in-game /bal lookups (Donut API skipped)."));
        if (_settings.DonutBridgeEnabled)
        {
            try
            {
                _bridge = new DonutBridgeHost(_settings);
                _bridge.Diagnostic += message => progress?.Report(new ScanProgress(
                    Volatile.Read(ref completed), usernames.Count, message, BridgeStatus: _bridge?.Status));
                _bridge.Start();
                progress?.Report(new ScanProgress(0, usernames.Count,
                    $"Donut bridge listening on 127.0.0.1:{_settings.DonutBridgePort}. Enable PlayerCheckerBridge on DonutSMP. "
                    + $"Traffic log: {AppPaths.BridgeLogFile}",
                    BridgeStatus: _bridge.Status));
            }
            catch (Exception ex)
            {
                setupErrors.Add("Donut bridge unavailable: " + ex.Message);
            }
        }

        var market = new MarketSnapshot();
        IReadOnlyList<DonutAuctionListing> donutListings = Array.Empty<DonutAuctionListing>();
        if (_hypixel.IsConfigured)
        {
            try
            {
                var marketProgress = new Progress<string>(message => progress?.Report(
                    new ScanProgress(0, usernames.Count, message, BridgeStatus: _bridge?.Status)));
                market = await new MarketPriceService(_hypixel, _settings).LoadAsync(marketProgress, token).ConfigureAwait(false);
            }
            // Market prices are an enrichment, never a reason to abandon the whole scan, so anything
            // short of the user cancelling is downgraded to a warning.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                setupErrors.Add("SkyBlock market prices unavailable: " + ex.Message);
            }
        }
        if (_donut.IsConfigured)
        {
            try { donutListings = await _donut.GetAuctionListingsAsync(token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                setupErrors.Add("DonutSMP auction listings unavailable: " + ex.Message);
            }
        }

        foreach (var error in setupErrors)
            progress?.Report(new ScanProgress(0, usernames.Count, error, BridgeStatus: _bridge?.Status));
        var results = new ConcurrentBag<PlayerScanResult>();
        // Only bridge calls have to be serial, and _bridgeGate already enforces that, so an API-first
        // scan keeps its parallelism instead of being slowed to one player at a time.
        var concurrency = _bridge is not null && (!_donut.IsConfigured || _settings.DonutBridgeOnly) ? 1 : _settings.Concurrency;
        // Settings normally arrive normalized, but a hand-edited file must not throw here.
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 64));
        try
        {
            var tasks = usernames.Select(async input =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var result = await ScanOneAsync(input, market, donutListings, token).ConfigureAwait(false);
                    results.Add(result);
                    var count = Interlocked.Increment(ref completed);
                    var message = string.IsNullOrWhiteSpace(result.Error)
                        ? $"{result.Username}: {result.Status}"
                        : $"{result.Username}: {result.Status} — {result.Error}";
                    progress?.Report(new ScanProgress(count, usernames.Count, message, result, _bridge?.Status));
                }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _bridge?.Dispose();
            _bridge = null;
        }
        return results.OrderBy(result => result.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<PlayerScanResult> ScanOneAsync(string input, MarketSnapshot market,
        IReadOnlyList<DonutAuctionListing> donutListings, CancellationToken token)
    {
        var result = new PlayerScanResult { Username = input, Status = "Resolving" };
        try
        {
            var identity = await _identity.ResolveAsync(input, token).ConfigureAwait(false);
            if (identity is null)
            {
                result.Status = "Not found";
                result.Error = "Minecraft username or UUID does not exist.";
                return result;
            }
            result.Username = identity.Name;
            result.Uuid = identity.Uuid;
            var errors = new List<string>();

            await FetchDonutAsync(result, identity, donutListings, errors, token).ConfigureAwait(false);

            if (_hypixel.IsConfigured)
            {
                try
                {
                    using var profiles = await _hypixel.GetProfilesAsync(identity.Uuid, token).ConfigureAwait(false);
                    var data = await new SkyBlockValuationService(_hypixel, _settings)
                        .CalculateAsync(profiles.RootElement, identity.Uuid, market, token)
                        .ConfigureAwait(false);
                    result.SkyBlockProfile = data.SelectedProfile;
                    result.SkyBlockProfiles = data.AllProfiles;
                    result.SkyBlockLevel = data.Level;
                    result.SkyBlockValuation = data.Valuation;
                    result.Assets.AddRange(data.Assets);
                    if (data.SelectedProfile.Length > 0 && !data.InventoryApiEnabled)
                        errors.Add("Hypixel: SkyBlock inventory API is off for this profile, so only coins could be valued.");
                }
                catch (ApiException ex) { errors.Add("Hypixel: " + ex.Message); }
            }

            if (!_settings.DonutBridgeEnabled && !_donut.IsConfigured && !_hypixel.IsConfigured)
                errors.Add("Add a Hypixel API key and/or enable the Donut bridge in Settings.");
            else if (!_hypixel.IsConfigured && result.Donut is null && !_settings.DonutBridgeEnabled)
                errors.Add("Configure a Hypixel API key in Settings for SkyBlock lookups.");
            // Drop API noise when bridge/API already filled Donut — Donut-only scans are Complete.
            if (result.Donut is not null)
            {
                errors.RemoveAll(e => e.StartsWith("DonutSMP API:", StringComparison.Ordinal)
                                      || e.StartsWith("Configure a Hypixel API key", StringComparison.Ordinal)
                                      || e.StartsWith("Hypixel:", StringComparison.Ordinal));
            }
            else if (_settings.DonutBridgeOnly)
            {
                errors.RemoveAll(e => e.StartsWith("DonutSMP API:", StringComparison.Ordinal));
            }
            result.Error = string.Join(" | ", errors);
            result.Status = errors.Count == 0 ? "Complete" :
                result.Donut is not null || result.SkyBlockProfiles.Length > 0 ? "Partial" : "Unavailable";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Status = "Error";
            result.Error = ex.Message;
        }
        result.ScannedAt = DateTimeOffset.Now;
        return result;
    }

    /// <summary>
    /// Tries the official API first, then the in-game bridge. Either route alone is enough, and a
    /// player is only reported as missing when both are exhausted.
    /// </summary>
    private async Task FetchDonutAsync(PlayerScanResult result, MinecraftIdentity identity,
        IReadOnlyList<DonutAuctionListing> donutListings, List<string> errors, CancellationToken token)
    {
        var listings = donutListings;
        if (ApiFirst)
        {
            try
            {
                result.Donut = await _donut.GetStatsAsync(identity.Name, token).ConfigureAwait(false);
                if (result.Donut is not null)
                {
                    Volatile.Write(ref _consecutiveApiFailures, 0);
                    result.DonutSource = "API";
                }
                else if (!BridgeReady)
                    errors.Add("DonutSMP API: no profile returned for this player.");
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
            {
                NoteApiFailure(errors, ex.Message);
            }
        }

        if (result.Donut is null && BridgeReady)
        {
            await _bridgeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                result.Donut = await _bridge!.WaitForStatsAsync(identity.Name, token).ConfigureAwait(false);
                // The bridge reads one player's GUI and never sees the auction house.
                listings = Array.Empty<DonutAuctionListing>();
                if (result.Donut is not null) result.DonutSource = "Bridge";
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ApiException)
            {
                errors.Add("DonutSMP bridge: " + ex.Message);
            }
            finally { _bridgeGate.Release(); }
        }

        if (result.Donut is null) return;
        var donutValue = DonutValuationService.Calculate(result.Donut, identity.Name,
            identity.Uuid, listings, _settings, result.DonutSource);
        result.DonutValuation = donutValue.Valuation;
        result.DonutAuctionListingsValue = donutValue.Valuation.OtherAssetsValue
            - result.Donut.Shards * _settings.DonutShardUnitValue;
        result.Assets.AddRange(donutValue.Assets);
    }

    private bool BridgeReady => _settings.DonutBridgeEnabled && _bridge is not null;

    /// <summary>Stop hammering an API that is clearly down and let the bridge take the rest of the scan.</summary>
    private void NoteApiFailure(List<string> errors, string message)
    {
        errors.Add("DonutSMP API: " + message);
        if (Interlocked.Increment(ref _consecutiveApiFailures) < 3) return;
        if (BridgeReady && Interlocked.Exchange(ref _apiAbandoned, 1) == 0)
            errors.Add("DonutSMP API failed three times in a row; using the in-game bridge for the rest of this scan.");
    }

    public void Dispose()
    {
        _bridge?.Dispose();
        _bridgeGate.Dispose();
        _http.Dispose();
    }
}
