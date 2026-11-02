using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace WEBit.Platform.Windows;

/// <summary>Listening IPv4 TCP endpoints with owning process (GetExtendedTcpTable).</summary>
public static class TcpListenerTable
{
    public sealed record Listener(string Address, int Port, int ProcessId, string? ProcessName);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    public static IReadOnlyList<Listener> GetIPv4Listeners()
    {
        var result = new List<Listener>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidListener, 0);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                var rc = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidListener, 0);
                if (rc == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (rc != 0)
                {
                    return result;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var names = new Dictionary<int, string?>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + (i * rowSize));
                    var address = new IPAddress(row.LocalAddr).ToString();
                    var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    var pid = (int)row.OwningPid;
                    if (!names.TryGetValue(pid, out var name))
                    {
                        name = ProcessName(pid);
                        names[pid] = name;
                    }

                    result.Add(new Listener(address, port, pid, name));
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    private static string? ProcessName(int pid)
    {
        if (pid == 0)
        {
            return "Idle";
        }

        if (pid == 4)
        {
            return "System (http.sys)";
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
