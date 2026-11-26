// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using TVControlForWin.Models;

namespace TVControlForWin.Services;

/// <summary>A TV found on the LAN via SSDP, ready to be added by one click.</summary>
public sealed record DiscoveredTv(string Name, string Host, string Model, TvKind Kind);

/// <summary>
/// Finds controllable TVs on the local network via SSDP (UPnP discovery). Two searches go out:
/// LG's "webos-second-screen" service, which only webOS TVs answer, and DIAL, which every
/// Android TV / Google TV answers (as do other cast-capable devices, so each DIAL responder
/// is kept only if its Android TV Remote port is open). Each responder's device-description
/// XML supplies the friendly name and model.
/// </summary>
public static class SsdpDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private const string WebOsTarget = "urn:lge-com:service:webos-second-screen:1";
    private const string DialTarget = "urn:dial-multiscreen-org:service:dial:1";

    private static string MSearch(string target) =>
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        $"ST: {target}\r\n" +
        "\r\n";

    /// <summary>
    /// Scans for TVs for roughly <paramref name="timeout"/> (default 4 s), reporting each unique
    /// TV through <paramref name="found"/> as it answers, and returns the complete list.
    /// Never throws; an interface that can't be bound is simply skipped.
    /// </summary>
    public static async Task<List<DiscoveredTv>> FindTvsAsync(
        IProgress<DiscoveredTv>? found = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var results = new List<DiscoveredTv>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        var sockets = CreateSockets();
        if (sockets.Count == 0) return results;

        using var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scan.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        // Looking a responder up takes network round-trips, so it runs alongside the listening
        // rather than holding up other replies — and is allowed to finish after the window closes.
        var lookups = new List<Task>();
        void Describe(string location, bool isWebOs)
        {
            var lookup = Task.Run(async () =>
            {
                try
                {
                    var tv = isWebOs
                        ? await DescribeWebOsAsync(http, location, ct).ConfigureAwait(false)
                        : await DescribeAndroidTvAsync(http, location, ct).ConfigureAwait(false);
                    if (tv is null) return;
                    lock (gate)
                    {
                        if (!seenHosts.Add(tv.Host)) return;
                        results.Add(tv);
                    }
                    found?.Report(tv);
                }
                catch { /* cancelled mid-lookup */ }
            }, CancellationToken.None);
            lock (gate) lookups.Add(lookup);
        }

        try
        {
            var listeners = sockets
                .Select(s => ListenAsync(s, gate, seenLocations, Describe, scan.Token))
                .ToList();

            // Fire the searches a few times per socket — SSDP is UDP, replies can be lost.
            var searches = new[] { WebOsTarget, DialTarget }
                .Select(st => Encoding.ASCII.GetBytes(MSearch(st)))
                .ToList();
            var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
            for (var burst = 0; burst < 3; burst++)
            {
                foreach (var socket in sockets)
                {
                    foreach (var search in searches)
                    {
                        try { await socket.SendAsync(search, target, scan.Token).ConfigureAwait(false); }
                        catch { /* interface may have gone away mid-scan */ }
                    }
                }
                try { await Task.Delay(150, scan.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            await Task.WhenAll(listeners).ConfigureAwait(false);

            Task[] pending;
            lock (gate) pending = lookups.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }

        ct.ThrowIfCancellationRequested();
        return results;
    }

    // Receives responses on one socket until the scan window closes, handing each new
    // responder to <paramref name="describe"/> (location, isWebOs). Never throws.
    private static async Task ListenAsync(
        UdpClient socket, object gate, HashSet<string> seenLocations,
        Action<string, bool> describe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { return; /* socket error — stop listening on this one */ }

            var response = Encoding.UTF8.GetString(received.Buffer);
            var location = ParseHeader(response, "LOCATION");
            if (location is null || !Uri.IsWellFormedUriString(location, UriKind.Absolute)) continue;

            // LG TVs answer DIAL too; they're picked up by the webOS search instead.
            var isWebOs = string.Equals(ParseHeader(response, "ST"), WebOsTarget, StringComparison.OrdinalIgnoreCase);
            if (!isWebOs && (ParseHeader(response, "SERVER") ?? "").Contains("webOS", StringComparison.OrdinalIgnoreCase))
                continue;

            lock (gate)
            {
                if (!seenLocations.Add(location)) continue;
            }

            describe(location, isWebOs);
        }
    }

    // One socket per IPv4 interface address (so multi-NIC machines search every subnet),
    // plus a wildcard socket as a catch-all.
    private static List<UdpClient> CreateSockets()
    {
        var sockets = new List<UdpClient>();

        void TryAdd(IPAddress bindAddress)
        {
            try
            {
                var client = new UdpClient(new IPEndPoint(bindAddress, 0));
                client.Client.ReceiveBufferSize = 64 * 1024;
                sockets.Add(client);
            }
            catch { /* address not bindable right now */ }
        }

        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .Distinct();
            foreach (var address in addresses) TryAdd(address);
        }
        catch { }

        TryAdd(IPAddress.Any);
        return sockets;
    }

    private static string? ParseHeader(string response, string name)
    {
        foreach (var line in response.Split("\r\n"))
        {
            if (line.Length > name.Length && line[name.Length] == ':' &&
                line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return line[(name.Length + 1)..].Trim();
        }
        return null;
    }

    // Fetches the UPnP device description: friendly name, manufacturer and model. Empty
    // strings when the description is unreachable.
    private static async Task<(string Name, string Manufacturer, string Model)> ReadDescriptionAsync(
        HttpClient http, string location, CancellationToken ct)
    {
        try
        {
            var xml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var device = doc.Descendants(ns + "device").FirstOrDefault();
            return (device?.Element(ns + "friendlyName")?.Value?.Trim() ?? "",
                    device?.Element(ns + "manufacturer")?.Value?.Trim() ?? "",
                    device?.Element(ns + "modelName")?.Value?.Trim() ?? "");
        }
        catch
        {
            return ("", "", "");
        }
    }

    private static async Task<DiscoveredTv?> DescribeWebOsAsync(HttpClient http, string location, CancellationToken ct)
    {
        var host = new Uri(location).Host;
        var (name, _, model) = await ReadDescriptionAsync(http, location, ct).ConfigureAwait(false);

        // friendlyName is usually "[LG] webOS TV OLED55C34LA" — trim the boilerplate.
        name = name.Replace("[LG]", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("webOS TV", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();
        if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(model) ? "LG TV" : model;

        return new DiscoveredTv(name, host, model, TvKind.WebOs);
    }

    // A DIAL responder is only a TV we can control if it also runs the Android TV Remote
    // service; speakers, streaming sticks of other makes and the like don't.
    private static async Task<DiscoveredTv?> DescribeAndroidTvAsync(HttpClient http, string location, CancellationToken ct)
    {
        var host = new Uri(location).Host;
        if (!await TvControllerFactory.IsPortOpenAsync(host, AndroidTvTls.RemotePort, ct).ConfigureAwait(false))
            return null;

        var (name, manufacturer, model) = await ReadDescriptionAsync(http, location, ct).ConfigureAwait(false);

        // e.g. manufacturer "TCL", model "Smart TV Pro" → "TCL Smart TV Pro".
        if (!string.IsNullOrEmpty(manufacturer) && !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            model = $"{manufacturer} {model}".Trim();
        if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(model) ? "Google TV" : model;

        return new DiscoveredTv(name, host, model, TvKind.AndroidTv);
    }
}
