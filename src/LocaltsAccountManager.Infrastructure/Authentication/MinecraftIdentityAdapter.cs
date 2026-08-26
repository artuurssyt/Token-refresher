using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Diagnostics;
using LocaltsAccountManager.Infrastructure.Processing;

namespace LocaltsAccountManager.Infrastructure.Authentication;

public sealed class MinecraftIdentityAdapter : IMinecraftIdentityAdapter
{
    private readonly IAuthenticationProfileStore _profileStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ThrottleCoordinator _throttleCoordinator;
    private readonly SecretSafeLogger _logger;

    public MinecraftIdentityAdapter(
        IAuthenticationProfileStore profileStore,
        IHttpClientFactory httpClientFactory,
        ThrottleCoordinator throttleCoordinator,
        SecretSafeLogger logger)
    {
        _profileStore = profileStore;
        _httpClientFactory = httpClientFactory;
        _throttleCoordinator = throttleCoordinator;
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            var profile = _profileStore.Load();
            return profile.IsVerified && profile.Minecraft.IsConfigured;
        }
    }

    public async Task<AuthenticationResult> RetrieveIdentityAsync(string microsoftAccessToken, CancellationToken cancellationToken = default)
    {
        var profile = _profileStore.Load();
        if (!profile.IsVerified || !profile.Minecraft.IsConfigured)
        {
            return Blocked(
                "Minecraft authentication chain is not verified. Populate Minecraft endpoints in authentication_profile.json after Phase 0.");
        }

        var settings = profile.Minecraft;
        var http = _httpClientFactory.CreateClient("Minecraft");

        try
        {
            var xbox = await AuthenticateXboxUserAsync(http, settings, microsoftAccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (!xbox.Result.Success)
            {
                return xbox.Result;
            }

            var xsts = await AuthorizeXstsAsync(http, settings, xbox.Token!, cancellationToken)
                .ConfigureAwait(false);
            if (!xsts.Result.Success)
            {
                return xsts.Result;
            }

            // Localts uses XSTS DisplayClaims uhs when present; fall back to XBL uhs.
            var userHash = xsts.UserHash ?? xbox.UserHash;
            if (string.IsNullOrWhiteSpace(userHash) || string.IsNullOrWhiteSpace(xsts.Token))
            {
                return new AuthenticationResult
                {
                    Success = false,
                    FailureStage = FailureStage.Minecraft,
                    ErrorCategory = ErrorCategory.MinecraftServiceError,
                    ServiceErrorDetail = "XSTS response did not include user hash and token.",
                    IsRetryable = false
                };
            }

            await using var loginSlot = await _throttleCoordinator
                .AcquireMinecraftAuthSlotAsync(cancellationToken)
                .ConfigureAwait(false);
            var mcLogin = await LoginMinecraftAsync(http, settings, userHash!, xsts.Token!, cancellationToken)
                .ConfigureAwait(false);
            if (!mcLogin.Success)
            {
                return mcLogin;
            }

            if (settings.RequireEntitlementCheck && !string.IsNullOrWhiteSpace(settings.MinecraftEntitlementsUrl))
            {
                var entitlements = await CheckEntitlementsAsync(http, settings, mcLogin.MinecraftAccessToken!, cancellationToken)
                    .ConfigureAwait(false);
                if (!entitlements.Success)
                {
                    return entitlements;
                }
            }

            var profileResult = await GetProfileAsync(http, settings, mcLogin.MinecraftAccessToken!, cancellationToken)
                .ConfigureAwait(false);
            if (!profileResult.Success)
            {
                return profileResult;
            }

            return new AuthenticationResult
            {
                Success = true,
                AuthenticatedMinecraftUsername = profileResult.AuthenticatedMinecraftUsername,
                AuthenticatedMinecraftUuid = profileResult.AuthenticatedMinecraftUuid,
                MinecraftAccessToken = mcLogin.MinecraftAccessToken,
                MinecraftAccessTokenExpiresAt = mcLogin.MinecraftAccessTokenExpiresAt
            };
        }
        catch (HttpRequestException ex)
        {
            return Transient(FailureStage.Minecraft, ErrorCategory.NetworkUnavailable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Transient(FailureStage.Minecraft, ErrorCategory.AuthenticationTimeout, "Minecraft identity request timed out.");
        }
    }

    private sealed record XboxStep(AuthenticationResult Result, string? Token = null, string? UserHash = null);

    private async Task<XboxStep> AuthenticateXboxUserAsync(
        HttpClient http,
        MinecraftAuthSettings settings,
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"{settings.RpsTicketPrefix}{microsoftAccessToken}"
            },
            RelyingParty = settings.RelyingParty ?? "http://auth.xboxlive.com",
            TokenType = "JWT"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.XboxUserAuthenticateUrl);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogOperation("Minecraft", "XboxUserAuthenticate", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

        if ((int)response.StatusCode == 429)
        {
            return new XboxStep(RateLimited(response));
        }

        if ((int)response.StatusCode >= 500)
        {
            return new XboxStep(ServiceError(response, FailureStage.Minecraft));
        }

        if (!response.IsSuccessStatusCode)
        {
            return new XboxStep(new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftServiceError,
                ServiceErrorDetail = $"Xbox user authenticate failed with status {(int)response.StatusCode}.",
                IsRetryable = false
            });
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("Token", out var tokenElement))
        {
            return new XboxStep(new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftServiceError,
                ServiceErrorDetail = "Xbox user authenticate response did not contain a token.",
                IsRetryable = false
            });
        }

        var token = tokenElement.GetString();
        var userHash = TryReadUserHash(doc.RootElement);
        return new XboxStep(new AuthenticationResult { Success = true }, token, userHash);
    }

    private async Task<XboxStep> AuthorizeXstsAsync(
        HttpClient http,
        MinecraftAuthSettings settings,
        string xboxUserToken,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxUserToken }
            },
            RelyingParty = settings.XboxRelyingParty ?? "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.XboxXstsAuthorizeUrl);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogOperation("Minecraft", "XboxXstsAuthorize", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

        if ((int)response.StatusCode == 429)
        {
            return new XboxStep(RateLimited(response));
        }

        if ((int)response.StatusCode >= 500)
        {
            return new XboxStep(ServiceError(response, FailureStage.Minecraft));
        }

        if (!response.IsSuccessStatusCode)
        {
            return new XboxStep(new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftServiceError,
                ServiceErrorDetail = $"XSTS authorize failed with status {(int)response.StatusCode}.",
                IsRetryable = false
            });
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("Token", out var tokenElement))
        {
            return new XboxStep(new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftServiceError,
                ServiceErrorDetail = "XSTS authorize response did not contain a token.",
                IsRetryable = false
            });
        }

        return new XboxStep(new AuthenticationResult { Success = true }, tokenElement.GetString(), TryReadUserHash(doc.RootElement));
    }

    private async Task<AuthenticationResult> LoginMinecraftAsync(
        HttpClient http,
        MinecraftAuthSettings settings,
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        // Matches Localts MinecraftTokenRequest: "XBL3.0 x={uhs};{xsts}"
        var payload = JsonSerializer.Serialize(new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.MinecraftLoginWithXboxUrl);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogOperation("Minecraft", "LoginWithXbox", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

        if ((int)response.StatusCode == 429)
        {
            return RateLimited(response);
        }

        if ((int)response.StatusCode >= 500)
        {
            return ServiceError(response, FailureStage.Minecraft);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftProfileUnavailable,
                ServiceErrorDetail = $"Minecraft login_with_xbox failed with status {(int)response.StatusCode}.",
                IsRetryable = false
            };
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftServiceError,
                ServiceErrorDetail = "Minecraft login response did not contain access_token.",
                IsRetryable = false
            };
        }

        DateTimeOffset? mcExpiresAt = null;
        if (doc.RootElement.TryGetProperty("expires_in", out var expiresElement))
        {
            var seconds = expiresElement.ValueKind == JsonValueKind.Number
                ? expiresElement.GetInt64()
                : long.TryParse(expiresElement.GetString(), out var parsed) ? parsed : 0;
            if (seconds > 0)
            {
                mcExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            }
        }

        return new AuthenticationResult
        {
            Success = true,
            MinecraftAccessToken = accessToken,
            MinecraftAccessTokenExpiresAt = mcExpiresAt
        };
    }

    private async Task<AuthenticationResult> CheckEntitlementsAsync(
        HttpClient http,
        MinecraftAuthSettings settings,
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.MinecraftEntitlementsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogOperation("Minecraft", "Entitlements", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

        if ((int)response.StatusCode == 429)
        {
            return RateLimited(response);
        }

        if ((int)response.StatusCode >= 500)
        {
            return ServiceError(response, FailureStage.Minecraft);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftProfileUnavailable,
                ServiceErrorDetail = $"Minecraft entitlements check failed with status {(int)response.StatusCode}.",
                IsRetryable = false,
                IsErrorCauseConfirmed = true
            };
        }

        return new AuthenticationResult { Success = true };
    }

    private static string? TryReadUserHash(JsonElement root)
    {
        if (!root.TryGetProperty("DisplayClaims", out var claims) ||
            !claims.TryGetProperty("xui", out var xui) ||
            xui.ValueKind != JsonValueKind.Array ||
            xui.GetArrayLength() == 0)
        {
            return null;
        }

        var first = xui[0];
        if (first.TryGetProperty("uhs", out var uhs))
        {
            return uhs.GetString();
        }

        return null;
    }

    private async Task<AuthenticationResult> GetProfileAsync(
        HttpClient http,
        MinecraftAuthSettings settings,
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.MinecraftProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogOperation("Minecraft", "Profile", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

        if ((int)response.StatusCode == 429)
        {
            return RateLimited(response);
        }

        if ((int)response.StatusCode >= 500)
        {
            return ServiceError(response, FailureStage.Minecraft);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftProfileUnavailable,
                ServiceErrorDetail = $"Minecraft profile request failed with status {(int)response.StatusCode}.",
                IsRetryable = false
            };
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var username = doc.RootElement.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(username))
        {
            return new AuthenticationResult
            {
                Success = false,
                FailureStage = FailureStage.Minecraft,
                ErrorCategory = ErrorCategory.MinecraftProfileUnavailable,
                ServiceErrorDetail = "Minecraft profile response did not contain name.",
                IsRetryable = false
            };
        }

        string? uuid = null;
        if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            uuid = idElement.GetString()?.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        }

        return new AuthenticationResult
        {
            Success = true,
            AuthenticatedMinecraftUsername = username,
            AuthenticatedMinecraftUuid = uuid
        };
    }

    private static AuthenticationResult Blocked(string detail) => new()
    {
        Success = false,
        FailureStage = FailureStage.Minecraft,
        ErrorCategory = ErrorCategory.ConfigurationRequired,
        ServiceErrorDetail = detail,
        IsRetryable = false
    };

    private static AuthenticationResult Transient(FailureStage stage, ErrorCategory category, string detail) => new()
    {
        Success = false,
        FailureStage = stage,
        ErrorCategory = category,
        ServiceErrorDetail = detail,
        IsRetryable = true
    };

    private static AuthenticationResult RateLimited(HttpResponseMessage response) => new()
    {
        Success = false,
        FailureStage = FailureStage.Minecraft,
        ErrorCategory = ErrorCategory.RateLimited,
        ServiceErrorDetail = "Minecraft service returned HTTP 429.",
        RetryAfter = response.Headers.RetryAfter?.Delta,
        IsRetryable = true
    };

    private static AuthenticationResult ServiceError(HttpResponseMessage response, FailureStage stage) => new()
    {
        Success = false,
        FailureStage = stage,
        ErrorCategory = ErrorCategory.MinecraftServiceError,
        ServiceErrorDetail = $"Service returned HTTP {(int)response.StatusCode}.",
        IsRetryable = true
    };
}
