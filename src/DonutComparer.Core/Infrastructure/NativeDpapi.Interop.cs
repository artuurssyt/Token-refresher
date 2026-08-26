using System.Runtime.InteropServices;

namespace DonutComparer.Core.Infrastructure;

internal static partial class NativeDpapi
{
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    private sealed class BlobOwner : IDisposable
    {
        public Blob Value;
        private BlobOwner(byte[] bytes)
        {
            Value = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, Value.Data, bytes.Length);
        }
        public static BlobOwner Create(byte[] bytes) => new(bytes);
        public void Dispose()
        {
            if (Value.Data == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Value.Data);
            Value.Data = IntPtr.Zero;
        }
    }
}
