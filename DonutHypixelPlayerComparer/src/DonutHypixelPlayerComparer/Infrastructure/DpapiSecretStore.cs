using System.Text.Json;

namespace DonutHypixelPlayerComparer.Infrastructure;

public sealed class DpapiSecretStore
{
    /// <summary>
    /// Set when a secrets file existed but could not be read — copied from another machine or
    /// Windows account, or truncated by a crash. Without this the keys just silently disappear and
    /// the user is left guessing why the app stopped authenticating.
    /// </summary>
    public string? LastLoadError { get; private set; }

    public Dictionary<string, string> Load()
    {
        LastLoadError = null;
        if (!File.Exists(AppPaths.SecretsFile)) return new(StringComparer.Ordinal);
        try
        {
            var clearBytes = NativeDpapi.Unprotect(File.ReadAllBytes(AppPaths.SecretsFile));
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(clearBytes);
            if (loaded is null)
            {
                LastLoadError = Quarantine("the stored secrets were empty");
                return new(StringComparer.Ordinal);
            }
            // A null value in the file would otherwise reach Trim() on the way back out.
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in loaded) result[pair.Key] = pair.Value ?? string.Empty;
            return result;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                       or System.ComponentModel.Win32Exception
                                       or JsonException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            LastLoadError = Quarantine(ex.Message);
            return new(StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, string> secrets)
    {
        AppPaths.EnsureCreated();
        var output = NativeDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(secrets));
        var temporary = AppPaths.SecretsFile + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, output);
            File.Move(temporary, AppPaths.SecretsFile, true);
            LastLoadError = null;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Moves unreadable secrets aside rather than leaving them to be silently overwritten, so the
    /// original bytes are still recoverable on the machine that can actually decrypt them.
    /// </summary>
    private static string Quarantine(string reason)
    {
        var message = "Saved API keys could not be decrypted (" + reason
                      + "). They were encrypted for a different Windows user or machine, or the file was damaged. "
                      + "Re-enter the keys in Settings.";
        try
        {
            var aside = AppPaths.SecretsFile + ".unreadable";
            File.Move(AppPaths.SecretsFile, aside, true);
            message += " The previous file was kept as " + Path.GetFileName(aside) + ".";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return message;
    }
}
