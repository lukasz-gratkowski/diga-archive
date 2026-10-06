using L = Diga.Core.Localization.AppText;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Diga.Core.Dlna;

/// <summary>An IPv4 address of this PC together with the mask of the network it is on.</summary>
public sealed record DlnaLocalInterface(IPAddress Address, IPAddress Mask);

/// <summary>
/// The search for media servers over IPv4 SSDP: an M-SEARCH datagram to the multicast group of every network this PC is on, or
/// to one address. The parameters are for tests, which name a loopback address and a port of their own.
/// </summary>
/// <param name="interfaces">Says which addresses of this PC to search from; <see cref="LocalInterfaces"/> when not given.</param>
/// <param name="group">Where a search goes, and the port a single address is asked on; 239.255.255.250:1900 when not given.</param>
public sealed class DlnaSsdpTransport(Func<IReadOnlyList<DlnaLocalInterface>>? interfaces = null, IPEndPoint? group = null) : IDlnaSsdpTransport
{
    internal const int MaximumReplies = 256;
    private static readonly string[] Targets = ["urn:schemas-upnp-org:device:MediaServer:1", "urn:schemas-upnp-org:service:ContentDirectory:1"];

    private readonly Func<IReadOnlyList<DlnaLocalInterface>> _interfaces = interfaces ?? LocalInterfaces;
    private readonly IPEndPoint _group = group ?? new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

    /// <summary>The IPv4 addresses of the networks this PC is connected to, each with its mask; none when it is connected to no network.</summary>
    public static IReadOnlyList<DlnaLocalInterface> LocalInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => new DlnaLocalInterface(a.Address, a.IPv4Mask)).DistinctBy(a => a.Address).Take(16).ToArray();
        }
        // Windows could not list its network adapters: there is nothing to search from.
        catch (NetworkInformationException) { return []; }
    }

    public async Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The mask stays with its address: a reply is accepted only from the subnet of the interface whose socket received it.
        var interfaces = _interfaces();
        // Said in words: a search that silently finds nothing sends the user to check a recorder that is not the problem.
        if (interfaces.Count == 0) throw new IOException(L.T("Core.Dlna.Discovery.NoNetwork"));
        var replies = new List<DlnaSsdpResponse>();
        var named = new Dictionary<IPAddress, HashSet<string>>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        await Task.WhenAll(interfaces.Select(async local =>
        {
            try
            {
                using var socket = new UdpClient(new IPEndPoint(local.Address, 0));
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                foreach (var request in Requests(Math.Clamp((int)Math.Ceiling(duration.TotalSeconds), 1, 5)))
                    await socket.SendAsync(request, _group, deadline.Token).ConfigureAwait(false);
                while (!deadline.IsCancellationRequested)
                {
                    var reply = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                    if (reply.Buffer.Length > 16 * 1024) continue;
                    var remote = reply.RemoteEndPoint.Address;
                    // Left out here already, so that replies with a made-up source address do not take the places of real ones.
                    if (!DlnaDiscoveryService.IsFromLocalSubnet(remote, local.Address, local.Mask)) continue;
                    lock (replies)
                    {
                        if (replies.Count >= MaximumReplies) break;
                        Keep(replies, named, new(Encoding.ASCII.GetString(reply.Buffer), remote, local.Address, local.Mask));
                    }
                }
            }
            catch (SocketException) { /* Other interfaces remain available; a recorder reachable only through this one is not found. */ }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return replies;
    }

    /// <summary>
    /// The same search sent to one address. A router that does not pass multicast between Wi-Fi and cable still delivers this.
    /// Only what that address itself answers is returned; the caller decides which addresses may be asked.
    /// </summary>
    public async Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(IPAddress address, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException(L.T("Core.Dlna.Discovery.AddressInvalid"));
        if (!IPAddress.IsLoopback(address) && _interfaces().Count == 0) throw new IOException(L.T("Core.Dlna.Discovery.NoNetwork"));
        var replies = new List<DlnaSsdpResponse>();
        var named = new Dictionary<IPAddress, HashSet<string>>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var elapsed = Stopwatch.StartNew();
        try
        {
            // Windows chooses the interface that leads to the address.
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var target = new IPEndPoint(address, _group.Port);
            // One second to answer: nothing else was asked, so the device has no reason to spread its replies.
            foreach (var request in Requests(1)) await socket.SendAsync(request, target, deadline.Token).ConfigureAwait(false);
            var answered = false;
            while (!deadline.IsCancellationRequested && replies.Count < DlnaDiscoveryService.MaximumLocationsPerResponder)
            {
                var reply = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (reply.Buffer.Length > 16 * 1024 || !reply.RemoteEndPoint.Address.Equals(address)) continue;
                Keep(replies, named, new(Encoding.ASCII.GetString(reply.Buffer), address));
                if (answered || replies.Count == 0) continue;
                // The device has answered. Its reply to the second search follows within the second it was given, if at all. The
                // shorter wait is set once: replies that go on arriving must not put the end off again and again.
                answered = true;
                if (duration - elapsed.Elapsed > TimeSpan.FromSeconds(1.2)) deadline.CancelAfter(TimeSpan.FromSeconds(1.2));
            }
        }
        // Nothing listens for searches at that address (Windows reports the refusal on the next receive), or no route leads there.
        catch (SocketException) { }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return replies;
    }

    /// <summary>
    /// Keeps a reply when it names a description address the search would read and its responder has not named that address
    /// before, for as many addresses of one responder as the search reads (<see cref="DlnaDiscoveryService.MaximumLocationsPerResponder"/>).
    /// What is counted is what a responder says, not how often: a device answers each of the two searches, many of them two or
    /// three times and on every interface that reaches it, and none of that may use up its places. Nor can one address fill the
    /// list before a recorder, which may wait MX seconds, has answered. As in <see cref="DlnaDiscoveryService.SelectLocations"/>,
    /// this holds for a device that answers from its own address; replies made up in the name of another address count as that
    /// address's own.
    /// </summary>
    private static void Keep(List<DlnaSsdpResponse> replies, Dictionary<IPAddress, HashSet<string>> named, DlnaSsdpResponse reply)
    {
        if (!DlnaDiscoveryService.TryParseSearchResponse(reply, out var location)) return;
        if (!named.TryGetValue(reply.RemoteAddress, out var locations)) named.Add(reply.RemoteAddress, locations = new(StringComparer.Ordinal));
        if (locations.Count < DlnaDiscoveryService.MaximumLocationsPerResponder && locations.Add(location!.AbsoluteUri)) replies.Add(reply);
    }

    // Asked twice, for media servers and for their content directory, because some devices answer only one of the two. The same
    // text goes to one address as to the group, with the group's HOST line and with MX: a device made before searches of a
    // single address were specified takes it for the search it knows.
    private static IEnumerable<byte[]> Requests(int mx) => Targets.Select(target =>
        Encoding.ASCII.GetBytes(FormattableString.Invariant($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: {mx}\r\nST: {target}\r\n\r\n")));
}
