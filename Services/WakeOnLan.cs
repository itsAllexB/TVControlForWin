// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TVControlForWin.Services;

/// <summary>
/// Wake-on-LAN: resolves a TV's MAC address while it's reachable (ARP), and later wakes
/// it by broadcasting the magic packet. webOS TVs answer WoL when "Quick Start+" /
/// "Turn on via Wi-Fi" is enabled in the TV's settings.
/// </summary>
public static class WakeOnLan
{
    /// <summary>
    /// Looks up the MAC address for a host on the local subnet via ARP. Returns it as
    /// "AA:BB:CC:DD:EE:FF", or null if the host can't be resolved right now. Blocking
    /// (up to a few seconds) — call from a background thread.
    /// </summary>
    public static string? TryResolveMac(string host)
    {
        try
        {
            var ip = Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) return null;

            var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            var mac = new byte[6];
            var len = (uint)mac.Length;
            if (SendARP(dest, 0, mac, ref len) != 0 || len != 6) return null;
            if (mac.All(b => b == 0)) return null;

            return string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the WoL magic packet for <paramref name="mac"/> ("AA:BB:CC:DD:EE:FF"), broadcast
    /// on every up network interface plus the global broadcast, a few times for reliability.
    /// Never throws.
    /// </summary>
    public static async Task SendAsync(string mac, CancellationToken ct = default)
    {
        var packets = MagicPackets(mac);
        if (packets.Count == 0) return;

        foreach (var bindAddress in BroadcastSources())
        {
            try
            {
                using var client = bindAddress is null
                    ? new UdpClient()
                    : new UdpClient(new IPEndPoint(bindAddress, 0));
                client.EnableBroadcast = true;
                var target = new IPEndPoint(IPAddress.Broadcast, 9);
                foreach (var packet in packets)
                    for (var i = 0; i < 3; i++)
                        await client.SendAsync(packet, target, ct).ConfigureAwait(false);
            }
            catch { /* one interface failing shouldn't stop the others */ }
        }

        Log.Write($"WoL sent to {mac}");
    }

    /// <summary>
    /// One magic packet (6× 0xFF, then the MAC 16 times) per address in <paramref name="mac"/>.
    /// The Mac version of this app stores every address a TV has (Wi-Fi and wired) as a
    /// comma-separated list in the same field, and a backup or devices.json can come from there;
    /// each address gets its packet, and the TV answers on whichever it's connected by.
    /// Addresses that can't be read are skipped.
    /// </summary>
    internal static List<byte[]> MagicPackets(string mac)
    {
        var packets = new List<byte[]>();
        foreach (var single in mac.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseMac(single, out var bytes)) continue;
            var packet = new byte[6 + 16 * 6];
            for (var i = 0; i < 6; i++) packet[i] = 0xFF;
            for (var i = 0; i < 16; i++) Buffer.BlockCopy(bytes, 0, packet, 6 + i * 6, 6);
            packets.Add(packet);
        }
        return packets;
    }

    private static bool TryParseMac(string mac, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var parts = mac.Split(':', '-');
        if (parts.Length != 6) return false;
        var result = new byte[6];
        for (var i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out result[i]))
                return false;
        bytes = result;
        return true;
    }

    // Every up IPv4 interface address (so the broadcast leaves on each subnet), plus
    // null for a default-route socket as a catch-all.
    private static IEnumerable<IPAddress?> BroadcastSources()
    {
        var list = new List<IPAddress?>();
        try
        {
            list.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (IPAddress?)a.Address)
                .Distinct());
        }
        catch { }
        list.Add(null);
        return list;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destIP, uint srcIP, byte[] macAddr, ref uint macAddrLen);
}
