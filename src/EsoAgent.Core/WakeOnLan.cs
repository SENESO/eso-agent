using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EsoAgent.Core;

/// <summary>
/// Sends Wake-on-LAN magic packets (v1.2): the global broadcast plus every
/// up interface's subnet-directed broadcast, on UDP ports 9 and 7 (both are
/// in common use). Sending is best-effort per interface — the result lists
/// what was actually attempted. A machine can only be woken by a sender on
/// its LAN (or a router that forwards broadcasts) — the fleet server relays
/// the request to online fleet devices for exactly that reason.
/// </summary>
public static class WakeSender
{
    public static async Task<List<string>> SendAsync(string macText, CancellationToken ct = default)
    {
        var packet = MagicPacket.Build(macText); // throws ArgumentException on a bad MAC
        var sent = new List<string>();
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask == null) continue;
                    var ip = addr.Address.GetAddressBytes();
                    var mask = addr.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    targets.Add(new IPAddress(broadcast));
                }
            }
        }
        catch { /* interface enumeration is best-effort; the global broadcast still goes out */ }

        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var target in targets)
        {
            foreach (var port in new[] { 9, 7 })
            {
                try
                {
                    await udp.SendAsync(packet, new IPEndPoint(target, port), ct);
                    sent.Add($"{target}:{port}");
                }
                catch { /* one dead route must not stop the others */ }
            }
        }
        return sent;
    }

    /// <summary>This machine's wakeable MACs: up, non-loopback, physical interfaces.</summary>
    public static List<string> LocalMacs()
    {
        var macs = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var mac = MagicPacket.Normalize(nic.GetPhysicalAddress().ToString());
                if (mac.Length > 0 && !macs.Contains(mac)) macs.Add(mac);
            }
        }
        catch { }
        return macs;
    }
}
