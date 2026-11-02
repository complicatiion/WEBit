using System.Runtime.InteropServices;

namespace WEBit.Platform.Windows;

/// <summary>
/// Cheap check whether a named pipe server exists. NamedPipeClientStream.ConnectAsync spins
/// on the CPU for the whole timeout while the server is missing, which makes the UI sluggish.
/// </summary>
public static class PipeProbe
{
    private const int ErrorFileNotFound = 2;

    public static bool Exists(string pipeName)
    {
        try
        {
            if (WaitNamedPipe($@"\\.\pipe\{pipeName}", 1))
            {
                return true;
            }

            // ERROR_SEM_TIMEOUT etc.: server exists but all instances are busy.
            return Marshal.GetLastPInvokeError() != ErrorFileNotFound;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, int timeout);
}
