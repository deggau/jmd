using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace JMD.App;

internal static class WorkflowVariableProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static string Protect(string value)
    {
        var inputBytes = Encoding.UTF8.GetBytes(value);
        var input = ToBlob(inputBytes);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try { return Convert.ToBase64String(ToBytes(output)); }
            finally { FreeProtectedBlob(output); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            FreeBlob(input);
        }
    }

    public static string Unprotect(string protectedValue)
    {
        var inputBytes = Convert.FromBase64String(protectedValue);
        var input = ToBlob(inputBytes);
        IntPtr description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var outputBytes = ToBytes(output);
                try { return Encoding.UTF8.GetString(outputBytes); }
                finally { CryptographicOperations.ZeroMemory(outputBytes); }
            }
            finally { FreeProtectedBlob(output); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            FreeBlob(input);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static byte[] ToBytes(DataBlob blob)
    {
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        var zeros = new byte[blob.Size];
        Marshal.Copy(zeros, 0, blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
    }

    private static void FreeProtectedBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        var zeros = new byte[blob.Size];
        Marshal.Copy(zeros, 0, blob.Data, blob.Size);
        LocalFree(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
