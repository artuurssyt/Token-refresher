using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LocaltsAccountManager.Core.Interfaces;

namespace LocaltsAccountManager.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ISecureCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocaltsAccountManager.v1");

    public Task<string> StoreAsync(string credential, CancellationToken cancellationToken = default)
    {
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
        var path = GetBlobPath(credentialReference);
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
    }

    public async Task<string> ReplaceAsync(string oldReference, string newCredential, CancellationToken cancellationToken = default)
    {
        var newReference = await StoreAsync(newCredential, cancellationToken).ConfigureAwait(false);
        await DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
        return newReference;
    }

    public Task DeleteAsync(string credentialReference, CancellationToken cancellationToken = default)
    {
        var path = GetBlobPath(credentialReference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string GetRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocaltsAccountManager",
            "credentials");

    private static string GetBlobPath(string reference) =>
        Path.Combine(GetRoot(), $"{reference}.dpapi");

    private static void WriteBlob(string reference, byte[] protectedBytes)
    {
        var root = GetRoot();
        Directory.CreateDirectory(root);
        File.WriteAllBytes(GetBlobPath(reference), protectedBytes);
    }
}
