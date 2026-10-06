using L = Diga.Core.Localization.AppText;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Diga.Core.Dlna;

public sealed class DlnaDiscoveryService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly IDlnaSsdpTransport _transport;

    /// <param name="client">Optional test client. Production clients must disable redirects, proxies and credentials.</param>
    public DlnaDiscoveryService(HttpClient? client = null, IDlnaSsdpTransport? transport = null)
    {
        _http = client ?? EndpointPolicy.CreateClient(); _ownsClient = client is null;
        _transport = transport ?? new Ipv4SsdpTransport();
    }

    public async Task<IReadOnlyList<DlnaDevice>> DiscoverAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interval = duration ?? TimeSpan.FromSeconds(3);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromSeconds(20))
            throw new ArgumentOutOfRangeException(nameof(duration), L.T("Core.Dlna.Discovery.DurationInvalid"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(interval + TimeSpan.FromSeconds(20));
        var responses = await _transport.SearchAsync(interval, budget.Token).ConfigureAwait(false);
        var locations = responses.Take(256).Select(r => TryParseSearchResponse(r, out var uri) ? uri : null)
            .OfType<Uri>().DistinctBy(uri => uri.AbsoluteUri).Take(64).ToArray();
        var found = new List<DlnaDevice>();
        try
        {
            await Parallel.ForEachAsync(locations, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (uri, ct) =>
            {
                try
                {
                    var device = await GetDeviceAsync(uri, ct).ConfigureAwait(false);
                    lock (found) if (!found.Any(d => d.Id == device.Id && d.DescriptionUri.Host == device.DescriptionUri.Host)) found.Add(device);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or XmlException or TimeoutException or OperationCanceledException)
                {
                    // One unrelated or unavailable UPnP responder must not hide the others.
                    ct.ThrowIfCancellationRequested();
                }
            }).ConfigureAwait(false);
        }
        // The discovery budget elapsed, not the caller's token: keep the devices already found.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return found.OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DlnaDevice> GetDeviceAsync(Uri descriptionUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); EndpointPolicy.Validate(descriptionUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, descriptionUri);
        var document = DlnaXml.Parse(await DlnaXml.ReadAsync(_http, request, cancellationToken).ConfigureAwait(false));
        var root = document.Root ?? throw new InvalidDataException(L.T("Core.Dlna.Discovery.DescriptionEmpty"));
        var urlBase = DlnaXml.Child(root, "URLBase");
        var baseUri = string.IsNullOrWhiteSpace(urlBase) ? descriptionUri : EndpointPolicy.Resolve(descriptionUri, descriptionUri, urlBase);
        var service = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "service" && IsContentDirectory(DlnaXml.Child(e, "serviceType")));
        if (service is null) throw new InvalidDataException(L.T("Core.Dlna.Discovery.ServiceMissing"));
        var device = service.Ancestors().FirstOrDefault(e => e.Name.LocalName == "device")
            ?? throw new InvalidDataException(L.T("Core.Dlna.Discovery.DeviceDescriptionMissing"));
        var control = DlnaXml.Child(service, "controlURL");
        if (string.IsNullOrWhiteSpace(control)) throw new InvalidDataException(L.T("Core.Dlna.Discovery.ControlUrlMissing"));
        return new(DlnaXml.Field(device, "UDN") is { Length: > 0 } id ? id : descriptionUri.AbsoluteUri,
            DlnaXml.Field(device, "friendlyName", 1024) is { Length: > 0 } name ? name : descriptionUri.Host,
            DlnaXml.Field(device, "manufacturer", 1024), DlnaXml.Field(device, "modelName", 1024), descriptionUri,
            EndpointPolicy.Resolve(descriptionUri, baseUri, control), DlnaXml.Child(service, "serviceType"));
    }

    internal static bool IsContentDirectory(string type) => type.StartsWith("urn:schemas-upnp-org:service:ContentDirectory:", StringComparison.Ordinal)
        && int.TryParse(type[(type.LastIndexOf(':') + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var version) && version is >= 1 and <= 99;

    /// <summary>SSDP locations must name the replying IPv4 address; a responder that advertises a hostname is left out of the list.</summary>
    public static bool TryParseSearchResponse(DlnaSsdpResponse response, out Uri? location)
    {
        location = null;
        if (response.Headers.Length > 16 * 1024 || response.RemoteAddress.AddressFamily != AddressFamily.InterNetwork) return false;
        var lines = response.Headers.Split('\n');
        if (lines.Length == 0 || !lines[0].TrimEnd('\r').Equals("HTTP/1.1 200 OK", StringComparison.OrdinalIgnoreCase)) return false;
        string? value = null;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon <= 0) return false;
            if (!line[..colon].Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not null) return false; // Ambiguous duplicate headers are not accepted.
            value = line[(colon + 1)..].Trim();
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !EndpointPolicy.IsHttp(uri) || uri.AbsoluteUri.Length > 8192) return false;
        if (!IPAddress.TryParse(uri.Host, out var address) || !address.Equals(response.RemoteAddress)) return false;
        location = uri; return true;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }

    private sealed class Ipv4SsdpTransport : IDlnaSsdpTransport
    {
        public async Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Distinct().Take(16).ToArray();
            var replies = new List<DlnaSsdpResponse>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(duration);
            await Task.WhenAll(addresses.Select(async address =>
            {
                try
                {
                    using var socket = new UdpClient(new IPEndPoint(address, 0));
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    int mx = Math.Clamp((int)Math.Ceiling(duration.TotalSeconds), 1, 5);
                    foreach (var target in new[] { "urn:schemas-upnp-org:device:MediaServer:1", "urn:schemas-upnp-org:service:ContentDirectory:1" })
                    {
                        var data = Encoding.ASCII.GetBytes(FormattableString.Invariant($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: {mx}\r\nST: {target}\r\n\r\n"));
                        await socket.SendAsync(data, endpoint, deadline.Token).ConfigureAwait(false);
                    }
                    while (!deadline.IsCancellationRequested)
                    {
                        var reply = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                        if (reply.Buffer.Length > 16 * 1024) continue;
                        lock (replies)
                        {
                            if (replies.Count >= 256) break;
                            replies.Add(new(Encoding.ASCII.GetString(reply.Buffer), reply.RemoteEndPoint.Address));
                        }
                    }
                }
                catch (SocketException) { /* Other interfaces remain available; a recorder reachable only through this one is not found. */ }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            })).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return replies;
        }
    }
}
