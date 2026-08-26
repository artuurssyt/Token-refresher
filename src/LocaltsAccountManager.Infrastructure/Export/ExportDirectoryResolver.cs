using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Infrastructure.Paths;

namespace LocaltsAccountManager.Infrastructure.Export;

internal static class ExportDirectoryResolver
{
    public static string Resolve(IAppSettingsStore settingsStore, string? overridePath = null, string? batchOutputDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ApplicationPaths.EnsureWritableDirectory(overridePath, ApplicationPaths.DefaultExportDirectory, ApplicationPaths.LocalAppDataExports);
        }

        var settings = settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.DefaultExportDirectory)
            && !ApplicationPaths.NeedsPortableExportMigration(settings.DefaultExportDirectory))
        {
            return ApplicationPaths.EnsureWritableDirectory(
                settings.DefaultExportDirectory,
                ApplicationPaths.DefaultExportDirectory,
                ApplicationPaths.LocalAppDataExports);
        }

        if (!string.IsNullOrWhiteSpace(batchOutputDirectory))
        {
            return ApplicationPaths.EnsureWritableDirectory(
                batchOutputDirectory,
                ApplicationPaths.DefaultExportDirectory,
                ApplicationPaths.LocalAppDataExports);
        }

        return ApplicationPaths.EnsureWritableDirectory(
            ApplicationPaths.DefaultExportDirectory,
            ApplicationPaths.LocalAppDataExports);
    }
}
