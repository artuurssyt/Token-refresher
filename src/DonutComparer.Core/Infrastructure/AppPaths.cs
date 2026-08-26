namespace DonutComparer.Core.Infrastructure;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DonutHypixelPlayerComparer");

    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string SecretsFile { get; } = Path.Combine(DataDirectory, "secrets.dat");
    public static string BridgeLogFile { get; } = Path.Combine(DataDirectory, "bridge-log.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
