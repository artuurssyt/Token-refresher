using System.Security.Cryptography;
using System.Text;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Infrastructure.Paths;

namespace LocaltsAccountManager.Infrastructure.Security;

/// <summary>
/// AES-GCM credential blobs on disk. Used on non-Windows hosts where DPAPI is unavailable.
/// A random 256-bit key is stored next to the blobs with user-only Unix permissions (0600).
/// </summary>
public sealed class AesFileCredentialStore : ISecureCredentialStore
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string KeyFileName = "store.key";

    private readonly object _keyLock = new();
    private byte[]? _key;

    public Task<string> StoreAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("Refusing to store an empty credential.", nameof(credential));
        }

        var reference = Guid.NewGuid().ToString("N");
        var plain = Encoding.UTF8.GetBytes(credential);
        var protectedBytes = Protect(plain);
        CryptographicOperations.ZeroMemory(plain);
        WriteBlob(reference, protectedBytes);
        return Task.FromResult(reference);
    }

    public Task<string?> RetrieveAsync(string credentialReference, CancellationToken cancellationToken = default)
    {
        if (!TryGetBlobPath(credentialReference, out var path) || !File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plain = Unprotect(protectedBytes);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (IOException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (FormatException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public async Task<string> ReplaceAsync(string oldReference, string newCredential, CancellationToken cancellationToken = default)
    {
        var newReference = await StoreAsync(newCredential, cancellationToken).ConfigureAwait(false);
        await DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
        return newReference;
    }

    public Task DeleteAsync(string credentialReference, CancellationToken cancellationToken = default)
    {
        if (!TryGetBlobPath(credentialReference, out var path))
        {
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    private byte[] Protect(byte[] plain)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(GetOrCreateKey(), TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[1 + NonceSize + TagSize + cipher.Length];
        output[0] = FormatVersion;
        Buffer.BlockCopy(nonce, 0, output, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, output, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, 1 + NonceSize + TagSize, cipher.Length);
        return output;
    }

    private byte[] Unprotect(byte[] blob)
    {
        if (blob.Length < 1 + NonceSize + TagSize)
        {
            throw new FormatException("Credential blob is truncated.");
        }

        if (blob[0] != FormatVersion)
        {
            throw new FormatException("Unsupported credential blob version.");
        }

        var nonce = blob.AsSpan(1, NonceSize);
        var tag = blob.AsSpan(1 + NonceSize, TagSize);
        var cipher = blob.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(GetOrCreateKey(), TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private byte[] GetOrCreateKey()
    {
        if (_key != null)
        {
            return _key;
        }

        lock (_keyLock)
        {
            if (_key != null)
            {
                return _key;
            }

            ApplicationPaths.EnsureDataLayout();
            var keyPath = Path.Combine(ApplicationPaths.CredentialsDirectory, KeyFileName);
            if (File.Exists(keyPath))
            {
                var existing = File.ReadAllBytes(keyPath);
                if (existing.Length == KeySize)
                {
                    _key = existing;
                    ApplicationPaths.RestrictUserOnlyFile(keyPath);
                    return _key;
                }
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            var tempPath = keyPath + ".tmp";
            File.WriteAllBytes(tempPath, key);
            ApplicationPaths.RestrictUserOnlyFile(tempPath);
            File.Move(tempPath, keyPath, overwrite: true);
            ApplicationPaths.RestrictUserOnlyFile(keyPath);
            _key = key;
            return _key;
        }
    }

    private static bool TryGetBlobPath(string? reference, out string path)
    {
        path = string.Empty;
        if (!IsValidReference(reference))
        {
            return false;
        }

        path = Path.Combine(ApplicationPaths.CredentialsDirectory, $"{reference}.aes");
        return true;
    }

    private static bool IsValidReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length != 32)
        {
            return false;
        }

        foreach (var character in reference)
        {
            var isHex = character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteBlob(string reference, byte[] protectedBytes)
    {
        ApplicationPaths.EnsureDataLayout();
        var finalPath = Path.Combine(ApplicationPaths.CredentialsDirectory, $"{reference}.aes");
        var tempPath = finalPath + ".tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        ApplicationPaths.RestrictUserOnlyFile(tempPath);
        File.Move(tempPath, finalPath, overwrite: true);
        ApplicationPaths.RestrictUserOnlyFile(finalPath);
    }
}
