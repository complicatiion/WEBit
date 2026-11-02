using System.Net;
using System.Net.Sockets;

namespace WEBit.Core.Net;

/// <summary>Tests whether an endpoint can be bound right now.</summary>
public static class PortProbe
{
    public static bool CanBind(string? address, int port, out string? error)
    {
        error = null;
        if (!Endpoint.TryParse(address, out var ip))
        {
            error = "invalid address";
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(ip, port);
            if (OperatingSystem.IsWindows())
            {
                listener.Server.ExclusiveAddressUse = true;
            }
            listener.Start();
            return true;
        }
        catch (SocketException ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch
            {
                // Ignore.
            }
        }
    }
}
