namespace LocaltsAccountManager.Core.Models;

public sealed class LocaltsImportSummary
{
    public BatchRecord Batch { get; init; } = null!;
    public string LocaltsUsername { get; init; } = string.Empty;
    public int OrdersScanned { get; init; }
    public int OrdersPackaged { get; init; }
    public int OrdersPending { get; init; }
    public int ItemsImported { get; init; }
    public int ItemsUpdated { get; init; }
    public int ItemsSkipped { get; init; }
    public int ItemsMalformed { get; init; }
}
