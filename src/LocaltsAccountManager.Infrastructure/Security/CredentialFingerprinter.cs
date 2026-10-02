using System.Security.Cryptography;
using System.Text;
using LocaltsAccountManager.Core.Interfaces;

namespace LocaltsAccountManager.Infrastructure.Security;

public sealed class CredentialFingerprinter : ICredentialFingerprinter
{
    public string ComputeFingerprint(string credential)
    {
        // No credential means no identity to compare: returning an empty fingerprint keeps
        // duplicate detection from treating every blank record as a duplicate of the first.
        if (string.IsNullOrEmpty(credential))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string FormatFingerprintPrefix(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return "unknown";
        }

        var prefixLength = Math.Min(8, fingerprint.Length);
        return $"sha256:{fingerprint[..prefixLength]}...";
    }
}
