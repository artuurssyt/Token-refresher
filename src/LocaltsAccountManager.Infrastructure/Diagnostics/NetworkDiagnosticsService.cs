using System.Net;
using LocaltsAccountManager.Core.Interfaces;

namespace LocaltsAccountManager.Infrastructure.Diagnostics;

public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthenticationProfileStore _profileStore;

    public NetworkDiagnosticsService(IHttpClientFactory httpClientFactory, IAuthenticationProfileStore profileStore)
    {
        _httpClientFactory = httpClientFactory;
        _profileStore = profileStore;
    }

    public async Task<NetworkDiagnosticResult> TestMicrosoftAsync(CancellationToken cancellationToken = default)
    {
        var profile = _profileStore.Load();
        if (string.IsNullOrWhiteSpace(profile.Microsoft.Authority))
        {
            return new NetworkDiagnosticResult
            {
                ServiceName = "Microsoft",
                IsReachable = false,
                Summary = "Microsoft authority is not configured in authentication_profile.json.",
                FailureCategory = "ConfigurationRequired"
            };
        }

        return await ProbeAsync("Microsoft", profile.Microsoft.Authority!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NetworkDiagnosticResult> TestMinecraftAsync(CancellationToken cancellationToken = default)
    {
        var profile = _profileStore.Load();
        var url = profile.Minecraft.MinecraftProfileUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new NetworkDiagnosticResult
            {
                ServiceName = "Minecraft",
                IsReachable = false,
                Summary = "Minecraft profile URL is not configured in authentication_profile.json.",
                FailureCategory = "ConfigurationRequired"
            };
        }

        return await ProbeAsync("Minecraft", url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NetworkDiagnosticResult> TestLocaltsAsync(CancellationToken cancellationToken = default)
    {
        return await ProbeAsync("Localts", "https://localts.store/", cancellationToken).ConfigureAwait(false);
    }

    private async Task<NetworkDiagnosticResult> ProbeAsync(string serviceName, string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Diagnostics");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return new NetworkDiagnosticResult
            {
                ServiceName = serviceName,
                IsReachable = true,
                Summary = $"HTTP {(int)response.StatusCode} received from {url}."
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return new NetworkDiagnosticResult
            {
                ServiceName = serviceName,
                IsReachable = true,
                Summary = "HTTP 403 received. Server is reachable but may block automated requests.",
                FailureCategory = "HttpForbidden"
            };
        }
        catch (HttpRequestException ex)
        {
            return new NetworkDiagnosticResult
            {
                ServiceName = serviceName,
                IsReachable = false,
                Summary = ex.Message,
                FailureCategory = "HttpRequestException"
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NetworkDiagnosticResult
            {
                ServiceName = serviceName,
                IsReachable = false,
                Summary = "Request timed out.",
                FailureCategory = "Timeout"
            };
        }
    }
}
