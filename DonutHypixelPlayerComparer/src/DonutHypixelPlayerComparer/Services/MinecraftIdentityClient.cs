using System.Net;
using System.Runtime.ExceptionServices;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public sealed class MinecraftIdentityClient
{
    // Mojang's profile endpoints are far stricter than the game APIs and answer 429 well below the
    // rate the rest of a scan runs at, so identity lookups get their own conservative budget.
    private readonly ResilientHttpClient _http;
    private readonly RequestRateLimiter _limiter = new(120, TimeSpan.FromMinutes(1));

    public MinecraftIdentityClient(ResilientHttpClient http) => _http = http;

    public async Task<MinecraftIdentity?> ResolveAsync(string input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        var compact = input.Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length == 32 && compact.All(Uri.IsHexDigit))
        {
            var uuid = compact.ToLowerInvariant();
            try
            {
                using var document = await _http.GetJsonAsync(
                    $"https://sessionserver.mojang.com/session/minecraft/profile/{compact}",
                    null, _limiter, TimeSpan.FromDays(1), token).ConfigureAwait(false);
                return new MinecraftIdentity(JsonValue.String(document.RootElement, "name", input), uuid);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
            catch (ApiException)
            {
                // The UUID was supplied by the caller, so an unreachable session server only costs the
                // display name. Reporting "does not exist" here would be a lie.
                return new MinecraftIdentity(input, uuid);
            }
        }

        ApiException? lastError = null;
        foreach (var url in new[]
        {
            $"https://api.minecraftservices.com/minecraft/profile/lookup/name/{Uri.EscapeDataString(input)}",
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(input)}"
        })
        {
            try
            {
                using var document = await _http.GetJsonAsync(url, null, _limiter, TimeSpan.FromDays(1), token)
                    .ConfigureAwait(false);
                var root = document.RootElement;
                var id = JsonValue.String(root, "id", string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);
                if (id.Length == 32)
                    return new MinecraftIdentity(JsonValue.String(root, "name", input), id.ToLowerInvariant());
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
            catch (ApiException ex) { lastError = ex; }
        }
        // Rethrowing the instance directly would discard where it came from.
        if (lastError is not null) ExceptionDispatchInfo.Capture(lastError).Throw();
        return null;
    }
}
