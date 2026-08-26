namespace LocaltsAccountManager.Infrastructure.Paths;

/// <summary>
/// Portable paths based on where the .exe lives (any drive), with LocalAppData as fallback.
/// </summary>
public static class ApplicationPaths
{
    public static string InstallDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return dir;
                }
            }

            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static string DefaultExportDirectory => Path.Combine(InstallDirectory, "exports");

    public static string LocalAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocaltsAccountManager");

    public static string LocalAppDataExports => Path.Combine(LocalAppDataRoot, "exports");

    /// <summary>
    /// True for the old hardcoded F:\ default, empty values, or a root that Windows cannot use.
    /// </summary>
    public static bool NeedsPortableExportMigration(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return true;
        }

        var trimmed = configuredPath.Trim();
        if (trimmed.Equals(@"F:\Token refresher\exports", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(@"F:/Token refresher/exports", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(@"F:\Token refresher\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(@"F:/Token refresher/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(trimmed));
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            return !drive.IsReady;
        }
        catch
        {
            return true;
        }
    }

    public static string EnsureWritableDirectory(params string[] candidates)
    {
        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                lastError = ex;
            }
        }

        throw new IOException(
            "Could not create an export folder next to the app or under Local AppData.",
            lastError);
    }
}
