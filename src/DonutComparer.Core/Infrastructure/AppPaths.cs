namespace DonutComparer.Core.Infrastructure;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(ResolveDataHome(), "DonutHypixelPlayerComparer");

    private static string ResolveDataHome()
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
        return string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData;
    }

    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string SecretsFile { get; } = Path.Combine(DataDirectory, "secrets.dat");
    public static string BridgeLogFile { get; } = Path.Combine(DataDirectory, "bridge-log.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        RestrictUserOnlyDirectory(DataDirectory);
        RestrictUserOnlyDirectory(CacheDirectory);
    }

    private static void RestrictUserOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
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
}
