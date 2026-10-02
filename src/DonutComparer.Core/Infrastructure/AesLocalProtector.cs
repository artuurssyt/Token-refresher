using System.Security.Cryptography;

namespace DonutComparer.Core.Infrastructure;

/// <summary>
/// AES-GCM file protector used when DPAPI (crypt32) is not available.
/// </summary>
internal static class AesLocalProtector
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static byte[] Protect(byte[] plain)
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

    public static byte[] Unprotect(byte[] blob)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported or truncated secret blob.");
        }

        var nonce = blob.AsSpan(1, NonceSize);
        var tag = blob.AsSpan(1 + NonceSize, TagSize);
        var cipher = blob.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(GetOrCreateKey(), TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] GetOrCreateKey()
    {
        AppPaths.EnsureCreated();
        var keyPath = Path.Combine(AppPaths.DataDirectory, "secrets.key");
        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == KeySize)
            {
                RestrictUserOnlyFile(keyPath);
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var tempPath = keyPath + ".tmp";
        File.WriteAllBytes(tempPath, key);
        RestrictUserOnlyFile(tempPath);
        File.Move(tempPath, keyPath, overwrite: true);
        RestrictUserOnlyFile(keyPath);
        return key;
    }

    private static void RestrictUserOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
