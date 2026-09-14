using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Vaguul.CodexAccountSwitcher.Services;

public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

public sealed class DpapiProtector : ISecretProtector
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Vaguul/CodexAccountSwitcher/v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> source, bool protect)
    {
        var input = CreateBlob(source);
        var entropy = CreateBlob(Entropy);
        DataBlob output = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the account data.");
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            ClearAndFree(ref input);
            ClearAndFree(ref entropy);
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob CreateBlob(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return default;
        }

        var copy = data.ToArray();
        try
        {
            var blob = new DataBlob { Length = copy.Length, Data = Marshal.AllocHGlobal(copy.Length) };
            Marshal.Copy(copy, 0, blob.Data, copy.Length);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static void ClearAndFree(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
