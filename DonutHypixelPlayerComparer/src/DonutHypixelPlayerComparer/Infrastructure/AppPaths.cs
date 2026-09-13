namespace DonutHypixelPlayerComparer.Infrastructure;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(ResolveLocalAppData(), "DonutHypixelPlayerComparer");

    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string SecretsFile { get; } = Path.Combine(DataDirectory, "secrets.dat");
    public static string BridgeLogFile { get; } = Path.Combine(DataDirectory, "bridge-log.txt");

    /// <summary>
    /// GetFolderPath returns an empty string when the shell folder cannot be resolved, which would
    /// silently turn every app path into a relative one rooted at the current directory.
    /// </summary>
    private static string ResolveLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) return local;
        var profile = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(profile)) return profile;
        return Path.Combine(Path.GetTempPath(), "DonutHypixelPlayerComparerData");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    /// <summary>
    /// The response cache is never pruned while the app runs, so old entries are swept once at
    /// startup instead of growing without limit. Failures here are irrelevant to the app working.
    /// </summary>
    public static void PruneCache(TimeSpan maximumAge)
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;
            var cutoff = DateTime.UtcNow - maximumAge;
            foreach (var file in Directory.EnumerateFiles(CacheDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
