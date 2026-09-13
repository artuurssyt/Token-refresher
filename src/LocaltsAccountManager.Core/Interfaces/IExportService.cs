namespace LocaltsAccountManager.Core.Interfaces;

public interface IExportService
{
    Task<string> ExportSuccessfulUsernamesAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportLibraryUsernamesAsync(string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportPoolUsernamesAsync(string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportErrorsAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportDetailedResultsAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportFailedOriginalRecordsAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportSuccessfulMinecraftAccessTokensAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportSuccessfulRefreshTokensAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<Models.ExportZipResult> ExportAccessTokensZipAsync(Guid batchId, string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<Models.ExportZipResult> ExportLibraryAccessTokensZipAsync(string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<Models.ExportZipResult> ExportPoolAccessTokensZipAsync(string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<string> ExportPoolRefreshTokensAsync(string? outputDirectory = null, CancellationToken cancellationToken = default);
}
