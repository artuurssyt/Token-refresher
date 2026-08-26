using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DonutComparer.Core.Infrastructure;

public sealed class DiskCache
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<string?> TryGetAsync(string key, TimeSpan maximumAge, CancellationToken token)
    {
        if (maximumAge <= TimeSpan.Zero) return null;
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, token);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json);
            return entry is not null && DateTimeOffset.UtcNow - entry.StoredUtc <= maximumAge
                ? entry.Body
                : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task SetAsync(string key, string body, CancellationToken token)
    {
        AppPaths.EnsureCreated();
        var path = GetPath(key);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var temporary = path + ".tmp";
            var json = JsonSerializer.Serialize(new CacheEntry(DateTimeOffset.UtcNow, body));
            await File.WriteAllTextAsync(temporary, json, token);
            File.Move(temporary, path, true);
        }
        finally { gate.Release(); }
    }

    private static string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.CacheDirectory, hash + ".json");
    }

    private sealed record CacheEntry(DateTimeOffset StoredUtc, string Body);
}
