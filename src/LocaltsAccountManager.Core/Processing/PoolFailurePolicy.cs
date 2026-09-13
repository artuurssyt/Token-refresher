using LocaltsAccountManager.Core.Enums;

namespace LocaltsAccountManager.Core.Processing;

public static class PoolFailurePolicy
{
    /// <summary>
    /// Categories that mean the stored refresh token itself is unusable, so the pool entry is
    /// worth discarding. Everything else — network, DNS, TLS, proxy, rate limits, service errors,
    /// configuration problems, cancellation — is transient or environmental.
    /// </summary>
    /// <remarks>
    /// Removing a pool account also deletes its DPAPI refresh-token blob, which cannot be undone.
    /// This list previously kept only rate-limited accounts, which meant a brief network outage or
    /// an unverified authentication profile wiped the whole pool and every token in it.
    /// </remarks>
    private static readonly ErrorCategory[] UnusableCredentialCategories =
    [
        ErrorCategory.MalformedInput,
        ErrorCategory.MissingCredential,
        ErrorCategory.UnsupportedCredentialFormat,
        ErrorCategory.CredentialRejected
    ];

    public static bool ShouldKeepInPool(ErrorCategory category) =>
        Array.IndexOf(UnusableCredentialCategories, category) < 0;

    public static bool ShouldRemoveFromPool(ErrorCategory category) => !ShouldKeepInPool(category);
}
