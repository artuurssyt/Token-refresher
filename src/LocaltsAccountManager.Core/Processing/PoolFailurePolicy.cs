using LocaltsAccountManager.Core.Enums;

namespace LocaltsAccountManager.Core.Processing;

public static class PoolFailurePolicy
{
    public static bool ShouldKeepInPool(ErrorCategory category) =>
        category is ErrorCategory.RateLimited or ErrorCategory.VendorRateLimited;

    public static bool ShouldRemoveFromPool(ErrorCategory category) => !ShouldKeepInPool(category);
}
