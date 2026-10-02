using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Diagnostics;

namespace LocaltsAccountManager.Infrastructure.Localts;

public sealed class LocaltsApiService : ILocaltsService
{
    private readonly LocaltsApiClient _client;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly SecretSafeLogger _logger;

    public LocaltsApiService(
        LocaltsApiClient client,
        IAppSettingsStore settingsStore,
        ISecureCredentialStore credentialStore,
        SecretSafeLogger logger)
    {
        _client = client;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public bool IsApiVerified => true;

    public async Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.LocaltsApiKeyReference))
        {
            return false;
        }

        var key = await _credentialStore.RetrieveAsync(settings.LocaltsApiKeyReference, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(key);
    }

    public async Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        var settings = _settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.LocaltsApiKeyReference))
        {
            await _credentialStore.DeleteAsync(settings.LocaltsApiKeyReference, cancellationToken).ConfigureAwait(false);
        }

        settings.LocaltsApiKeyReference = await _credentialStore.StoreAsync(apiKey.Trim(), cancellationToken).ConfigureAwait(false);
        _settingsStore.Save(settings);
        _client.SetApiKey(apiKey.Trim());
    }

    public async Task ClearApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.LocaltsApiKeyReference))
        {
            await _credentialStore.DeleteAsync(settings.LocaltsApiKeyReference, cancellationToken).ConfigureAwait(false);
            settings.LocaltsApiKeyReference = null;
            _settingsStore.Save(settings);
        }

        _client.ClearApiKey();
    }

    public async Task<LocaltsUserInfo> ValidateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await ConfigureClientAsync(cancellationToken).ConfigureAwait(false);
        return await _client.GetMeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocaltsOrderSummary>> FetchAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        await ConfigureClientAsync(cancellationToken).ConfigureAwait(false);
        var allOrders = new List<LocaltsOrderSummary>();
        var page = 0;
        var totalPages = 1;
        while (page < totalPages)
        {
            var result = await _client.GetOrdersAsync(page, 100, cancellationToken).ConfigureAwait(false);
            allOrders.AddRange(result.Orders);
            totalPages = Math.Max(1, result.TotalPages);
            page++;
        }

        return allOrders;
    }

    public async Task<LocaltsOrderDetail> FetchOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await ConfigureClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _client.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        }
        catch (LocaltsApiException ex)
        {
            _logger.LogInfo($"Localts order fetch failed for {orderId}: {ex.Message}");
            throw;
        }
    }

    public async Task<LocaltsConnectivityResult> CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await HasApiKeyAsync(cancellationToken).ConfigureAwait(false))
            {
                var me = await ValidateApiKeyAsync(cancellationToken).ConfigureAwait(false);
                return new LocaltsConnectivityResult
                {
                    IsReachable = true,
                    DiagnosticSummary = $"Connected as {me.Username} ({me.Balance} credits)."
                };
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("https://localts.store/v1/products", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return new LocaltsConnectivityResult
            {
                IsReachable = response.IsSuccessStatusCode,
                DiagnosticSummary = response.IsSuccessStatusCode
                    ? "Localts public API reachable. Add your API key in the Pool tab to import owned accounts."
                    : $"Localts returned HTTP {(int)response.StatusCode}."
            };
        }
        catch (Exception ex)
        {
            return new LocaltsConnectivityResult
            {
                IsReachable = false,
                DiagnosticSummary = "Unable to reach Localts. Check internet, DNS, firewall, and VPN/proxy. Detail: " + ex.Message,
                FailureCategory = "ConnectivityFailure"
            };
        }
    }

    private async Task ConfigureClientAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.LocaltsApiKeyReference))
        {
            throw new InvalidOperationException("Localts API key is not configured. Add it in the Pool tab first.");
        }

        var apiKey = await _credentialStore.RetrieveAsync(settings.LocaltsApiKeyReference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Stored Localts API key could not be retrieved.");
        }

        _client.SetApiKey(apiKey);
    }
}
