namespace LocaltsAccountManager.Infrastructure.Processing;

/// <summary>
/// Single exclusive run slot shared by <see cref="BatchProcessor"/> and <see cref="PoolProcessor"/>.
/// Both mutate the same account rows, the same SQLite file and the same
/// <see cref="ThrottleCoordinator"/>, so only one of them may run at a time.
/// Claiming is atomic, which the previous "check IsRunning, then assign _runTask" pattern was not:
/// two rapid clicks (or a timer tick racing a manual start) could both pass the check and leave the
/// first run orphaned and uncancellable.
/// </summary>
public sealed class ProcessingArbiter
{
    public const string BatchOwner = "batch";
    public const string PoolOwner = "pool";

    private readonly object _sync = new();
    private string? _owner;

    /// <summary>Owner that currently holds the run slot, or <see langword="null"/> when idle.</summary>
    public string? CurrentOwner
    {
        get
        {
            lock (_sync)
            {
                return _owner;
            }
        }
    }

    public bool IsHeldBy(string owner)
    {
        lock (_sync)
        {
            return string.Equals(_owner, owner, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Atomically claims the run slot. Returns <see langword="false"/> when another owner (or a
    /// previous run by the same owner) still holds it.
    /// </summary>
    public bool TryAcquire(string owner)
    {
        lock (_sync)
        {
            if (_owner != null)
            {
                return false;
            }

            _owner = owner;
            return true;
        }
    }

    /// <summary>Releases the slot only if <paramref name="owner"/> still holds it.</summary>
    public void Release(string owner)
    {
        lock (_sync)
        {
            if (string.Equals(_owner, owner, StringComparison.Ordinal))
            {
                _owner = null;
            }
        }
    }
}
