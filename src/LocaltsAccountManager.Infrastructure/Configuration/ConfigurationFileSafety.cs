using System.Text;

namespace LocaltsAccountManager.Infrastructure.Configuration;

/// <summary>
/// Crash-safe read/write helpers for the small JSON files under
/// <c>%LOCALAPPDATA%\LocaltsAccountManager\</c>. These are loaded on every settings read, so a
/// half-written file is enough to break startup, import and export until it is deleted by hand.
/// </summary>
internal static class ConfigurationFileSafety
{
    /// <summary>
    /// Writes via a temporary file and an atomic move, so readers only ever see the previous
    /// complete file or the new complete file — never a truncated one.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Moves an unreadable file aside so the app can rebuild defaults while keeping the original
    /// bytes available for inspection. Best-effort: failure here must not block recovery.
    /// </summary>
    public static void QuarantineCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
