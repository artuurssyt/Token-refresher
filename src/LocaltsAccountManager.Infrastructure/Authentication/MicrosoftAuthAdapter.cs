using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using AuthResult = LocaltsAccountManager.Core.Models.AuthenticationResult;
using LocaltsAccountManager.Infrastructure.Diagnostics;

namespace LocaltsAccountManager.Infrastructure.Authentication;

/// <summary>
/// Microsoft refresh flow matching Localts RefreshTokenApp
/// (login.live.com oauth20_token.srf form POST — not Azure AD MSAL).
/// </summary>
public sealed class MicrosoftAuthAdapter : IMicrosoftAuthAdapter
{
    private readonly IAuthenticationProfileStore _profileStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SecretSafeLogger _logger;

    public MicrosoftAuthAdapter(
        IAuthenticationProfileStore profileStore,
        IHttpClientFactory httpClientFactory,
        SecretSafeLogger logger)
    {
        _profileStore = profileStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            var profile = _profileStore.Load();
            return profile.IsVerified && profile.Microsoft.IsConfigured;
        }
    }

    public async Task<AuthResult> AuthenticateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var profile = _profileStore.Load();
        if (!profile.IsVerified || !profile.Microsoft.IsConfigured)
        {
            return Blocked(
                "Authentication profile is not verified. Complete Phase 0 and populate authentication_profile.json.");
        }

        var ms = profile.Microsoft;
        var http = _httpClientFactory.CreateClient("Microsoft");

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ms.ClientId!,
                ["redirect_uri"] = ms.RedirectUri ?? "https://login.live.com/oauth20_desktop.srf",
                ["grant_type"] = "refresh_token",
                ["scope"] = ms.Scope!,
                ["refresh_token"] = refreshToken
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, ms.TokenEndpoint);
            request.Content = content;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogOperation("Microsoft", "LiveComRefreshToken", (int)response.StatusCode, response.Headers.RetryAfter?.Delta, null);

            if ((int)response.StatusCode == 429)
            {
                return new AuthResult
                {
                    Success = false,
                    FailureStage = FailureStage.Authentication,
                    ErrorCategory = ErrorCategory.RateLimited,
                    ServiceErrorDetail = "Microsoft token endpoint returned HTTP 429.",
                    RetryAfter = response.Headers.RetryAfter?.Delta,
                    IsRetryable = true
                };
            }

            if ((int)response.StatusCode >= 500)
            {
                return new AuthResult
                {
                    Success = false,
                    FailureStage = FailureStage.Authentication,
                    ErrorCategory = ErrorCategory.MicrosoftServiceError,
                    ServiceErrorDetail = $"Microsoft token endpoint returned HTTP {(int)response.StatusCode}.",
                    IsRetryable = true
                };
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                var error = errorElement.GetString() ?? "error";
                var description = root.TryGetProperty("error_description", out var desc)
                    ? desc.GetString()
                    : null;

                var category = error switch
                {
                    "invalid_grant" => ErrorCategory.CredentialRejected,
                    "interaction_required" => ErrorCategory.UserInteractionRequired,
                    _ => ErrorCategory.CredentialRejected
                };

                return new AuthResult
                {
                    Success = false,
                    FailureStage = FailureStage.Authentication,
                    ErrorCategory = category,
                    OAuthErrorCode = error,
                    ServiceErrorDetail = description ?? error,
                    IsErrorCauseConfirmed = category is ErrorCategory.CredentialRejected or ErrorCategory.UserInteractionRequired,
                    IsRetryable = false
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AuthResult
                {
                    Success = false,
                    FailureStage = FailureStage.Authentication,
                    ErrorCategory = ErrorCategory.MicrosoftServiceError,
                    ServiceErrorDetail = $"Microsoft token endpoint returned HTTP {(int)response.StatusCode}.",
                    IsRetryable = (int)response.StatusCode >= 500
                };
            }

            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new AuthResult
                {
                    Success = false,
                    FailureStage = FailureStage.Authentication,
                    ErrorCategory = ErrorCategory.CredentialRejected,
                    ServiceErrorDetail = "Token response did not contain access_token.",
                    IsRetryable = false
                };
            }

            var replacement = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            DateTimeOffset? accessExpiresAt = null;
            if (root.TryGetProperty("expires_in", out var expiresElement))
            {
                var seconds = expiresElement.ValueKind == JsonValueKind.Number
                    ? expiresElement.GetInt64()
                    : long.TryParse(expiresElement.GetString(), out var parsed) ? parsed : 0;
                if (seconds > 0)
                {
                    accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                }
            }

            return new AuthResult
            {
                Success = true,
                MicrosoftAccessToken = accessToken,
                ReplacementRefreshToken = string.IsNullOrWhiteSpace(replacement) ? null : replacement,
                MicrosoftAccessTokenExpiresAt = accessExpiresAt
            };
        }
        catch (HttpRequestException ex)
        {
            return new AuthResult
            {
                Success = false,
                FailureStage = FailureStage.Authentication,
                ErrorCategory = ErrorCategory.NetworkUnavailable,
                ServiceErrorDetail = ex.Message,
                IsRetryable = true
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AuthResult
            {
                Success = false,
                FailureStage = FailureStage.Authentication,
                ErrorCategory = ErrorCategory.AuthenticationTimeout,
                IsRetryable = true
            };
        }
        catch (JsonException ex)
        {
            return new AuthResult
            {
                Success = false,
                FailureStage = FailureStage.Authentication,
                ErrorCategory = ErrorCategory.MicrosoftServiceError,
                ServiceErrorDetail = "Invalid JSON from Microsoft token endpoint: " + ex.Message,
                IsRetryable = true
            };
        }
    }

    private static AuthResult Blocked(string detail) => new()
    {
        Success = false,
        FailureStage = FailureStage.Authentication,
        ErrorCategory = ErrorCategory.ConfigurationRequired,
        ServiceErrorDetail = detail,
        IsRetryable = false
    };
}
