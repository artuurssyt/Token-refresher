using System.Text.Json;

namespace DonutComparer.Core.Infrastructure;

public sealed class DpapiSecretStore
{
    public Dictionary<string, string> Load()
    {
        if (!File.Exists(AppPaths.SecretsFile)) return new(StringComparer.Ordinal);
        try
        {
            var clearBytes = NativeDpapi.Unprotect(File.ReadAllBytes(AppPaths.SecretsFile));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(clearBytes)
                ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, string> secrets)
    {
        AppPaths.EnsureCreated();
        var output = NativeDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(secrets));
        var temporary = AppPaths.SecretsFile + ".tmp";
        File.WriteAllBytes(temporary, output);
        File.Move(temporary, AppPaths.SecretsFile, true);
    }
}
