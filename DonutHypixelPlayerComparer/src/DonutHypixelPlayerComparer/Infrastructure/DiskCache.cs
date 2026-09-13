using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DonutHypixelPlayerComparer.Infrastructure;

public sealed class DiskCache
{
    public async Task<string?> TryGetAsync(string key, TimeSpan maximumAge, CancellationToken token)
    {
        if (maximumAge <= TimeSpan.Zero) return null;
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json);
            return entry is not null && DateTimeOffset.UtcNow - entry.StoredUtc <= maximumAge
                ? entry.Body
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Best effort by design. The response this caches has already been fetched successfully, so a
    /// full disk, a locked file or a read-only profile must degrade to "no caching" rather than
    /// failing the API call that just worked.
    /// </summary>
    public async Task SetAsync(string key, string body, CancellationToken token)
    {
        // Each writer uses its own temporary file, so two clients caching the same URL — or two
        // copies of the app — cannot collide on one fixed ".tmp" name.
        var path = GetPath(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(new CacheEntry(DateTimeOffset.UtcNow, body));
            await File.WriteAllTextAsync(temporary, json, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (IOException) { Cleanup(temporary); }
        catch (UnauthorizedAccessException) { Cleanup(temporary); }
        catch (NotSupportedException) { Cleanup(temporary); }
        catch (OperationCanceledException) { Cleanup(temporary); throw; }
    }

    private static void Cleanup(string temporary)
    {
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.CacheDirectory, hash + ".json");
    }

    private sealed record CacheEntry(DateTimeOffset StoredUtc, string Body);
}
