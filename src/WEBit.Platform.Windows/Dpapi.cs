using System.Runtime.InteropServices;
using System.Text;

namespace WEBit.Platform.Windows;

/// <summary>
/// Windows DPAPI with machine scope: the secret can only be decrypted on this computer, by the
/// service in either run mode (service account or signed-in user). Elsewhere: plain text (tests).
/// </summary>
public static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectLocalMachine = 0x4;

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "plain:" + secret;
        }

        return "dpapi:" + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(secret), protect: true));
    }

    public static string? Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
            {
                return stored["plain:".Length..];
            }

            if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(stored["dpapi:".Length..]), protect: false));
            }
        }
        catch (Exception)
        {
            // Corrupt or from another computer.
        }

        return null;
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var flags = CryptProtectUiForbidden | CryptProtectLocalMachine;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, ref output);
            if (!ok)
            {
                throw new InvalidOperationException("DPAPI failed: " + Marshal.GetLastWin32Error());
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
