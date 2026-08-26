namespace LocaltsAccountManager.Core.Enums;

public enum ProcessingState
{
    Pending,
    Parsing,
    Malformed,
    Queued,
    Authenticating,
    Backoff,
    Succeeded,
    Failed,
    ReauthenticationRequired,
    Cancelled
}
