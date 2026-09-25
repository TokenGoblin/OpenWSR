using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenWSR.App;

/// <summary>Where the LAN dashboard can be reached from, and what counts as a usable port.</summary>
public static class DashboardAddresses
{
    public const int DefaultPort = 8765;

    /// <summary>
    /// Ports below this need elevation on some configurations and collide with real services
    /// on most, and nobody choosing a dashboard port means one of them.
    /// </summary>
    public const int MinPort = 1024;

    /// <summary>A port typed into Settings, or null when it is not one the server can use.</summary>
    public static int? ParsePort(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= MinPort and <= 65535
            ? port
            : null;

    /// <summary>The address to open on this PC itself — the tray's "open in browser".</summary>
    public static string Local(int port) => $"http://localhost:{port}/";

    /// <summary>
    /// The addresses another device would type: the machine's name first, since it survives
    /// the router handing out a new address, then each LAN IPv4 address. Link-local and
    /// tunnel adapters are left out — nothing on the network can reach this PC through them,
    /// and listing one invites copying the address that does not work.
    /// </summary>
    public static IReadOnlyList<string> For(int port)
    {
        var urls = new List<string>();
        try
        {
            urls.Add($"http://{Dns.GetHostName().ToLowerInvariant()}:{port}/");
        }
        catch (SocketException)
        {
            // No name is still a working dashboard; the addresses below are enough.
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IsLinkLocal(address)) continue;
                    urls.Add($"http://{address}:{port}/");
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Serilog.Log.Warning(ex, "Could not list network adapters for the dashboard addresses");
        }

        return urls.Distinct().ToList();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
