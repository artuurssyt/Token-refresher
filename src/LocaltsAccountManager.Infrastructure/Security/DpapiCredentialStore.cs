using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Infrastructure.Paths;

namespace LocaltsAccountManager.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ISecureCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocaltsAccountManager.v1");

    public Task<string> StoreAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("Refusing to store an empty credential.", nameof(credential));
        }

        var reference = Guid.NewGuid().ToString("N");
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(credential),
            Entropy,
            DataProtectionScope.CurrentUser);
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
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException)
        {
            // Blob is truncated, or was protected by a different Windows user or machine.
            // Callers treat null as "credential unavailable" and keep the record.
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
    }

    public async Task<string> ReplaceAsync(string oldReference, string newCredential, CancellationToken cancellationToken = default)
    {
        // Store first so a failure here leaves the original reference intact and usable.
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
            // Blob is locked; it will be overwritten or cleaned up on a later pass.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    private static string GetRoot() => ApplicationPaths.CredentialsDirectory;

    /// <summary>
    /// Maps a reference to its blob path, rejecting anything that is not one of our own
    /// 32-character hex identifiers. References come back out of the database, so an edited or
    /// corrupted value such as <c>..\..\something</c> must not be able to escape the folder.
    /// </summary>
    private static bool TryGetBlobPath(string? reference, out string path)
    {
        path = string.Empty;
        if (!IsValidReference(reference))
        {
            return false;
        }

        path = Path.Combine(GetRoot(), $"{reference}.dpapi");
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
        var root = GetRoot();
        Directory.CreateDirectory(root);

        // Write to a temporary file and move into place so a crash mid-write cannot leave a
        // half-written blob that later fails to decrypt.
        var finalPath = Path.Combine(root, $"{reference}.dpapi");
        var tempPath = finalPath + ".tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
