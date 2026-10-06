using L = Diga.Core.Localization.AppText;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Xml;

namespace Diga.Core.Dlna;

public sealed class DlnaDiscoveryService : IDisposable
{
    /// <summary>
    /// How many different description addresses are read from one responder address. A recorder announces one or two and a PC that
    /// runs several media servers one for each; a device that names more only keeps the search busy.
    /// </summary>
    public const int MaximumLocationsPerResponder = 8;
    private const int MaximumLocations = 64;
    private const int MaximumReplies = DlnaSsdpTransport.MaximumReplies;
    // The most descriptions there can be, 64, read sixteen at a time with the usual three seconds for each, take twelve seconds.
    // The search has twenty for them, so an address that was selected is asked however many before it never answer.
    private const int DescriptionsAtATime = 16;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly IDlnaSsdpTransport _transport;
    private readonly TimeSpan _descriptionLimit;

    /// <param name="client">Optional test client. Production clients must disable redirects, proxies and credentials.</param>
    /// <param name="descriptionLimit">
    /// The longest the search waits for one description; three seconds when not given. A device on the same network answers in a
    /// fraction of a second, and one that accepts the connection and then says nothing must not use up the time of the others.
    /// </param>
    public DlnaDiscoveryService(HttpClient? client = null, IDlnaSsdpTransport? transport = null, TimeSpan? descriptionLimit = null)
    {
        _descriptionLimit = descriptionLimit ?? TimeSpan.FromSeconds(3);
        if (_descriptionLimit <= TimeSpan.Zero || _descriptionLimit > DlnaXml.ExchangeLimit) throw new ArgumentOutOfRangeException(nameof(descriptionLimit));
        _http = client ?? EndpointPolicy.CreateClient(); _ownsClient = client is null;
        _transport = transport ?? new DlnaSsdpTransport();
    }

    /// <summary>
    /// Searches every network this PC is on for media servers. On a PC that is connected to no network it fails with a message
    /// that says so, an <see cref="IOException"/>, and does not return an empty list as if no recorder had answered.
    /// </summary>
    public async Task<IReadOnlyList<DlnaDevice>> DiscoverAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
        (await DiscoverWithRespondersAsync(duration, cancellationToken).ConfigureAwait(false)).Devices;

    /// <summary>The same search as <see cref="DiscoverAsync"/>, which also says how many different addresses answered it.</summary>
    public Task<DlnaDiscoveryResult> DiscoverWithRespondersAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
        SearchAsync(duration, (interval, token) => _transport.SearchAsync(interval, token), cancellationToken);

    /// <summary>
    /// Asks one address whether a media server is there: the same search, sent to that address alone (port 1900). It is for a
    /// home network that does not pass a search from Wi-Fi to cable or back, where <see cref="DiscoverAsync"/> finds nothing
    /// although the recorder is on. Only an IPv4 address that <see cref="IsLocalAddress"/> accepts is asked; any other is
    /// refused with an <see cref="ArgumentException"/> whose message says what to enter. An address where nothing answers gives
    /// an empty list.
    /// </summary>
    public async Task<IReadOnlyList<DlnaDevice>> DiscoverAtAsync(IPAddress address, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || !EndpointPolicy.IsLocalAddress(address)) throw new ArgumentException(L.T("Core.Dlna.Discovery.AddressInvalid"));
        var result = await SearchAsync(duration, async (interval, token) =>
            // The address that was asked is the only one whose answer counts, whatever else the transport hands over.
            (await _transport.SearchAsync(address, interval, token).ConfigureAwait(false)).Where(response => response.RemoteAddress.Equals(address)).ToArray(), cancellationToken).ConfigureAwait(false);
        return result.Devices;
    }

    /// <summary>The same for an address as a person types it: four numbers with dots between them, such as 192.168.1.40.</summary>
    public async Task<IReadOnlyList<DlnaDevice>> DiscoverAtAsync(string address, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        if (!TryParseAddress(address, out var parsed)) throw new ArgumentException(L.T("Core.Dlna.Discovery.AddressInvalid"));
        return await DiscoverAtAsync(parsed, duration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an address written as four numbers and tells whether <see cref="DiscoverAtAsync(IPAddress, TimeSpan?, CancellationToken)"/>
    /// would ask it. Shorter forms that Windows also takes for addresses, such as "192.168.1" or "3232235816", are not accepted:
    /// nobody who types them means what they stand for.
    /// </summary>
    public static bool TryParseAddress(string? text, [NotNullWhen(true)] out IPAddress? address)
    {
        var trimmed = text?.Trim() ?? "";
        address = IPAddress.TryParse(trimmed, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() == trimmed && EndpointPolicy.IsLocalAddress(parsed) ? parsed : null;
        return address is not null;
    }

    /// <summary>
    /// The rule for every address this library contacts, for the catalogue and for recordings alike: this computer itself, the
    /// private IPv4 ranges (10.x.x.x, 172.16-31.x.x, 192.168.x.x), link-local addresses and unique local IPv6 addresses.
    /// </summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return EndpointPolicy.IsLocalAddress(address);
    }

    private async Task<DlnaDiscoveryResult> SearchAsync(TimeSpan? duration, Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DlnaSsdpResponse>>> search, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interval = duration ?? TimeSpan.FromSeconds(3);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromSeconds(20))
            throw new ArgumentOutOfRangeException(nameof(duration), L.T("Core.Dlna.Discovery.DurationInvalid"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(interval + TimeSpan.FromSeconds(20));
        var responses = await search(interval, budget.Token).ConfigureAwait(false);
        var locations = SelectLocations(responses, out var responders);
        var found = new List<DlnaDevice>();
        try
        {
            // The locations are taken in their order, so every responder's first one is asked before any responder's second.
            await Parallel.ForEachAsync(locations, new ParallelOptions { MaxDegreeOfParallelism = DescriptionsAtATime, CancellationToken = budget.Token }, async (uri, ct) =>
            {
                try
                {
                    var device = await GetDeviceAsync(uri, _descriptionLimit, ct).ConfigureAwait(false);
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
        return new(found.OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Address, StringComparer.Ordinal).ToArray(), responders);
    }

    /// <summary>
    /// The description addresses a search will read, in the order it reads them: the first of every responder address, then the
    /// second of every one that named a second, and so on, at most <see cref="MaximumLocationsPerResponder"/> different ones from
    /// one responder and 64 in all. A device that answers at once with many addresses, while a recorder waits the seconds the
    /// search allows it, then takes neither the places nor the time of the others. Its further addresses are not thrown away:
    /// they are read after everyone's first.
    /// <para>
    /// This protects against a device that answers from its own address, and against no other. Whoever sends a UDP reply chooses
    /// the address it seems to come from. A device on the home network that makes up replies before the recorder has answered
    /// still keeps the recorder out of the list: with <see cref="MaximumLocationsPerResponder"/> addresses in the recorder's
    /// name, or with 64 in the names of other addresses of the subnet. Counting replies cannot tell those from real ones. What
    /// uses the list must therefore not take a device for the recorder because it is the only one listed.
    /// </para>
    /// </summary>
    /// <param name="responderCount">The number of different addresses that sent an acceptable reply.</param>
    public static IReadOnlyList<Uri> SelectLocations(IEnumerable<DlnaSsdpResponse> responses, out int responderCount)
    {
        ArgumentNullException.ThrowIfNull(responses);
        var byResponder = new Dictionary<IPAddress, List<Uri>>();
        var responders = new List<List<Uri>>();
        foreach (var response in responses.Take(MaximumReplies))
        {
            if (!TryParseSearchResponse(response, out var uri)) continue;
            if (!byResponder.TryGetValue(response.RemoteAddress, out var locations))
            {
                byResponder.Add(response.RemoteAddress, locations = []);
                responders.Add(locations);
            }
            if (locations.Count < MaximumLocationsPerResponder && !locations.Any(known => known.AbsoluteUri == uri!.AbsoluteUri)) locations.Add(uri!);
        }
        responderCount = responders.Count;
        return Enumerable.Range(0, MaximumLocationsPerResponder)
            .SelectMany(round => responders.Where(locations => locations.Count > round).Select(locations => locations[round]))
            .Take(MaximumLocations).ToArray();
    }

    public Task<DlnaDevice> GetDeviceAsync(Uri descriptionUri, CancellationToken cancellationToken = default) => GetDeviceAsync(descriptionUri, DlnaXml.ExchangeLimit, cancellationToken);

    private async Task<DlnaDevice> GetDeviceAsync(Uri descriptionUri, TimeSpan limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); EndpointPolicy.Validate(descriptionUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, descriptionUri);
        var document = DlnaXml.Parse(await DlnaXml.ReadAsync(_http, request, cancellationToken, limit).ConfigureAwait(false));
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

    /// <summary>
    /// SSDP locations must name the replying IPv4 address; a responder that advertises a hostname is left out of the list.
    /// When the reply says which interface received it, it must also come from that interface's subnet (<see cref="IsFromLocalSubnet"/>).
    /// </summary>
    public static bool TryParseSearchResponse(DlnaSsdpResponse response, out Uri? location)
    {
        location = null;
        if (response.Headers.Length > 16 * 1024 || response.RemoteAddress.AddressFamily != AddressFamily.InterNetwork) return false;
        if (response.LocalAddress is not null && response.LocalMask is not null && !IsFromLocalSubnet(response.RemoteAddress, response.LocalAddress, response.LocalMask)) return false;
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

    /// <summary>
    /// True when <paramref name="remote"/> lies in the subnet of the interface with the address <paramref name="local"/> and the
    /// mask <paramref name="mask"/>, or is a link-local address (169.254.x.x), which no router passes on and which is therefore on
    /// the link the reply arrived by. The sender of a UDP reply chooses its source address: without this rule a device on the home
    /// network could name a host of another network of this PC, such as a VPN, and the description would be requested from it.
    /// </summary>
    public static bool IsFromLocalSubnet(IPAddress remote, IPAddress local, IPAddress mask)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork || local.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork) return false;
        Span<byte> bytes = stackalloc byte[12];
        if (!remote.TryWriteBytes(bytes[..4], out _) || !local.TryWriteBytes(bytes[4..8], out _) || !mask.TryWriteBytes(bytes[8..], out _)) return false;
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        // A mask of 0.0.0.0 names every address there is, not a network. Then only the interface's own address passes.
        var whole = (bytes[8] | bytes[9] | bytes[10] | bytes[11]) == 0;
        for (var index = 0; index < 4; index++)
        {
            var bits = whole ? (byte)0xFF : bytes[8 + index];
            if ((bytes[index] & bits) != (bytes[4 + index] & bits)) return false;
        }
        return true;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
