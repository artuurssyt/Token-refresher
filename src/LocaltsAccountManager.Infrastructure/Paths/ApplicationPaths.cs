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

    public static string LocalAppDataRoot => Path.Combine(LocalAppDataBase, "LocaltsAccountManager");

    public static string LocalAppDataExports => Path.Combine(LocalAppDataRoot, "exports");

    /// <summary>Database file shared by the account and batch repositories.</summary>
    public static string DatabaseFile => Path.Combine(LocalAppDataRoot, "accounts.db");

    /// <summary>Folder holding encrypted credential blobs (DPAPI on Windows, AES files on Linux).</summary>
    public static string CredentialsDirectory => Path.Combine(LocalAppDataRoot, "credentials");

    /// <summary>
    /// Local AppData on Windows, or XDG data home on Linux (`~/.local/share`). Falls back to the
    /// install directory when no profile folder exists so paths never become CWD-relative.
    /// </summary>
    private static string LocalAppDataBase
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (!string.IsNullOrWhiteSpace(xdg))
                {
                    return xdg;
                }

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    return Path.Combine(home, ".local", "share");
                }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData) ? InstallDirectory : localAppData;
        }
    }

    /// <summary>
    /// Creates the app data and credentials folders. On Unix, restricts them to the current user
    /// (0700) so token blobs are not world-readable.
    /// </summary>
    public static void EnsureDataLayout()
    {
        Directory.CreateDirectory(LocalAppDataRoot);
        Directory.CreateDirectory(CredentialsDirectory);
        RestrictUserOnlyDirectory(LocalAppDataRoot);
        RestrictUserOnlyDirectory(CredentialsDirectory);
    }

    internal static void RestrictUserOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    internal static void RestrictUserOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

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
