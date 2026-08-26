using System.Net;
using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;

namespace DonutComparer.Core.Services;

public sealed class MinecraftIdentityClient
{
    private readonly ResilientHttpClient _http;
    private readonly RequestRateLimiter _limiter = new(300, TimeSpan.FromMinutes(1));

    public MinecraftIdentityClient(ResilientHttpClient http) => _http = http;

    public async Task<MinecraftIdentity?> ResolveAsync(string input, CancellationToken token)
    {
        var compact = input.Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length == 32 && compact.All(Uri.IsHexDigit))
        {
            try
            {
                using var document = await _http.GetJsonAsync(
                    $"https://sessionserver.mojang.com/session/minecraft/profile/{compact}",
                    null, _limiter, TimeSpan.FromDays(1), token);
                var root = document.RootElement;
                return new MinecraftIdentity(JsonValue.String(root, "name", input), compact.ToLowerInvariant());
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
            catch (ApiException) { return null; }
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
                using var document = await _http.GetJsonAsync(url, null, _limiter, TimeSpan.FromDays(1), token);
                var root = document.RootElement;
                var id = JsonValue.String(root, "id", string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);
                if (id.Length == 32)
                    return new MinecraftIdentity(JsonValue.String(root, "name", input), id.ToLowerInvariant());
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
            catch (ApiException ex) { lastError = ex; }
        }
        if (lastError is not null)
            throw lastError;
        return null;
    }
}
