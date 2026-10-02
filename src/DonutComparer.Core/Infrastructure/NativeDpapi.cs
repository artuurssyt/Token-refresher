using System.Runtime.InteropServices;

namespace DonutComparer.Core.Infrastructure;

internal static partial class NativeDpapi
{
    public static byte[] Protect(byte[] bytes) =>
        OperatingSystem.IsWindows() ? Transform(bytes, true) : AesLocalProtector.Protect(bytes);

    public static byte[] Unprotect(byte[] bytes) =>
        OperatingSystem.IsWindows() ? Transform(bytes, false) : AesLocalProtector.Unprotect(bytes);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        using var input = BlobOwner.Create(bytes);
        Blob output;
        var success = protect
            ? CryptProtectData(ref input.Value, "Player comparer secrets", IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, 1, out output)
            : CryptUnprotectData(ref input.Value, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, 1, out output);
        if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
