namespace LocaltsAccountManager.Core.Models;

public sealed class AccountRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public int SourceLine { get; set; }
    public string OriginalLine { get; set; } = string.Empty;
    public Enums.OriginalInputForm OriginalInputForm { get; set; } = Enums.OriginalInputForm.Unknown;
    public string? ProvidedUsername { get; set; }
    public string? AuthenticatedMinecraftUsername { get; set; }
    public string? AuthenticatedMinecraftUuid { get; set; }
    public bool? ProvidedUsernameMismatch { get; set; }

    /// <summary>DPAPI reference for the last successful Minecraft (JWT) access token.</summary>
    public string? MinecraftAccessTokenReference { get; set; }

    /// <summary>When the last MSA access token from a successful refresh is expected to expire.</summary>
    public DateTimeOffset? MicrosoftAccessTokenExpiresAt { get; set; }

    /// <summary>When the last Minecraft (JWT) access token from a successful auth is expected to expire.</summary>
    public DateTimeOffset? MinecraftAccessTokenExpiresAt { get; set; }

    /// <summary>When the stored refresh credential was last replaced after a successful refresh.</summary>
    public DateTimeOffset? RefreshCredentialUpdatedAt { get; set; }

    public string? CredentialReference { get; set; }
    public string TokenFingerprint { get; set; } = string.Empty;
    public Enums.ParseStatus ParseStatus { get; set; } = Enums.ParseStatus.Pending;
    public Enums.ProcessingState ProcessingState { get; set; } = Enums.ProcessingState.Pending;
    public Enums.FailureStage FailureStage { get; set; } = Enums.FailureStage.None;
    public Enums.ErrorCategory ErrorCategory { get; set; } = Enums.ErrorCategory.None;
    public string? OAuthErrorCode { get; set; }
    public string? ServiceErrorDetail { get; set; }
    public bool IsErrorCauseConfirmed { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? BackoffUntil { get; set; }
    public Guid? DuplicateOfRecordId { get; set; }

    /// <summary>When true, this account is managed by the Pool tab (auto-refresh, bulk export).</summary>
    public bool InPool { get; set; }

    /// <summary>When the account was added to the pool.</summary>
    public DateTimeOffset? PoolJoinedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
