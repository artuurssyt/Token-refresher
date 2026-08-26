namespace LocaltsAccountManager.Core.Enums;

public enum ImportDestination
{
    /// <summary>Account is added to the auto-managed pool.</summary>
    Pool,

    /// <summary>Account is imported for the active Accounts tab only — not added to the pool.</summary>
    ActiveOnly
}
