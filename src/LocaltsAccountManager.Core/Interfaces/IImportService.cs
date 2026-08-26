using LocaltsAccountManager.Core.Enums;

namespace LocaltsAccountManager.Core.Interfaces;

public interface IImportService
{
    Task<Models.BatchRecord> ImportFileAsync(
        string filePath,
        ImportDestination destination = ImportDestination.Pool,
        CancellationToken cancellationToken = default);

    Task<Models.LocaltsImportSummary> ImportFromLocaltsAsync(CancellationToken cancellationToken = default);
}
