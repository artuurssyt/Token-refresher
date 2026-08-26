namespace LocaltsAccountManager.Core.Models;

public sealed class AuthenticationResult
{
    public bool Success { get; init; }
    public string? MicrosoftAccessToken { get; init; }
    public string? AuthenticatedMinecraftUsername { get; init; }
    public string? AuthenticatedMinecraftUuid { get; init; }
    public string? MinecraftAccessToken { get; init; }
    public string? ReplacementRefreshToken { get; init; }
    public DateTimeOffset? MicrosoftAccessTokenExpiresAt { get; init; }
    public DateTimeOffset? MinecraftAccessTokenExpiresAt { get; init; }
    public Enums.ErrorCategory ErrorCategory { get; init; } = Enums.ErrorCategory.None;
    public Enums.FailureStage FailureStage { get; init; } = Enums.FailureStage.None;
    public bool IsErrorCauseConfirmed { get; init; }
    public string? OAuthErrorCode { get; init; }
    public string? ServiceErrorDetail { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public bool IsRetryable { get; init; }
}
