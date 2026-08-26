using System.Net;
using System.Text.Json;
using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Diagnostics;

namespace LocaltsAccountManager.Infrastructure.Minecraft;

/// <summary>
/// Resolves Minecraft usernames and UUIDs via public Mojang/Minecraft profile APIs.
/// Ported from DonutHypixelPlayerComparer MinecraftIdentityClient.
/// </summary>
public sealed class MinecraftProfileLookupService : IMinecraftProfileLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppSettingsStore _settingsStore;
    private readonly SecretSafeLogger _logger;
    private ProfileLookupRateLimiter? _limiter;

    public MinecraftProfileLookupService(
        IHttpClientFactory httpClientFactory,
        IAppSettingsStore settingsStore,
        SecretSafeLogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<MinecraftProfileIdentity?> ResolveAsync(string usernameOrUuid, CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.EnablePublicProfileLookup)
        {
            return null;
        }

        _limiter ??= new ProfileLookupRateLimiter(
            settings.ProfileLookupRateLimitPerMinute,
            TimeSpan.FromMinutes(1));

        var input = usernameOrUuid.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var compact = input.Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length == 32 && compact.All(Uri.IsHexDigit))
        {
            return await ResolveByUuidAsync(compact, input, cancellationToken).ConfigureAwait(false);
        }

        return await ResolveByNameAsync(input, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MinecraftProfileIdentity?> ResolveByUuidAsync(
        string compactUuid,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        var url = $"https://sessionserver.mojang.com/session/minecraft/profile/{compactUuid}";
        try
        {
            using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var name = GetJsonString(root, "name") ?? fallbackName;
            var id = GetJsonString(root, "id")?.Replace("-", string.Empty, StringComparison.Ordinal) ?? compactUuid;
            if (id.Length != 32)
            {
                return null;
            }

            return new MinecraftProfileIdentity(name, id.ToLowerInvariant());
        }
        catch (MinecraftApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<MinecraftProfileIdentity?> ResolveByNameAsync(string name, CancellationToken cancellationToken)
    {
        MinecraftApiException? lastError = null;
        foreach (var url in new[]
        {
            $"https://api.minecraftservices.com/minecraft/profile/lookup/name/{Uri.EscapeDataString(name)}",
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}"
        })
        {
            try
            {
                using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var id = GetJsonString(root, "id")?.Replace("-", string.Empty, StringComparison.Ordinal) ?? string.Empty;
                if (id.Length != 32)
                {
                    continue;
                }

                var resolvedName = GetJsonString(root, "name") ?? name;
                return new MinecraftProfileIdentity(resolvedName, id.ToLowerInvariant());
            }
            catch (MinecraftApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
            catch (MinecraftApiException ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        var http = _httpClientFactory.CreateClient("MinecraftProfile");
        Exception? lastError = null;

        for (var attempt = 0; attempt <= settings.MaxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await (_limiter ?? new ProfileLookupRateLimiter(300, TimeSpan.FromMinutes(1)))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogOperation("MinecraftProfile", "Lookup", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return JsonDocument.Parse(body);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new MinecraftApiException("Minecraft profile was not found.", HttpStatusCode.NotFound);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    lastError = new MinecraftApiException(
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "Minecraft profile lookup rate limit reached."
                            : "Minecraft profile lookup service is temporarily unavailable.",
                        response.StatusCode);
                    if (attempt < settings.MaxRetryAttempts)
                    {
                        var delay = response.Headers.RetryAfter?.Delta
                                    ?? TimeSpan.FromMilliseconds(settings.BaseBackoffMilliseconds * Math.Pow(2, attempt));
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw lastError;
                }

                throw new MinecraftApiException(
                    $"Minecraft profile lookup failed with HTTP {(int)response.StatusCode}.",
                    response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                if (attempt < settings.MaxRetryAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(settings.BaseBackoffMilliseconds * Math.Pow(2, attempt)), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new MinecraftApiException("Minecraft profile lookup timed out.");
                if (attempt < settings.MaxRetryAttempts)
                {
                    continue;
                }
            }
        }

        throw new MinecraftApiException("Minecraft profile lookup failed after retries.", null, lastError);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
