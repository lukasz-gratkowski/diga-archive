using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Diga.Core.Dlna;
using Diga.Core.Localization;

namespace Diga.Tests;

public sealed class DlnaDiscoveryTests
{
    internal static string Description(string control = "/control", string? urlBase = null) =>
        $"<root xmlns=\"urn:schemas-upnp-org:device-1-0\">{(urlBase is null ? "" : "<URLBase>" + urlBase + "</URLBase>")}<device><UDN>uuid:recorder</UDN><friendlyName>Living room &amp; recorder</friendlyName><manufacturer>Panasonic</manufacturer><modelName>DMR-BS850</modelName><serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><controlURL>{control}</controlURL></service></serviceList></device></root>";

    [Fact]
    public async Task DescriptionPreservesNamesAndResolvesRelativeControlOnSameHostDifferentPort()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml(Description("ctl/browse", "http://192.168.1.8:7000/base/"))));
        using var service = new DlnaDiscoveryService(http);
        var device = await service.GetDeviceAsync(new("http://192.168.1.8:6000/device.xml"));
        Assert.Equal("Living room & recorder", device.FriendlyName);
        Assert.Equal("Panasonic", device.Manufacturer);
        Assert.Equal("DMR-BS850", device.ModelName);
        Assert.Equal(new Uri("http://192.168.1.8:7000/base/ctl/browse"), device.ContentDirectoryControlUri);
    }

    [Theory]
    [InlineData("http://other.example/control", null)]
    [InlineData("/control", "http://other.example/base/")]
    [InlineData("http://user:password@192.168.1.8/control", null)]
    [InlineData("file:///C:/private", null)]
    public async Task RejectsUnsafeAdvertisedEndpoints(string control, string? urlBase)
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml(Description(control, urlBase))));
        using var service = new DlnaDiscoveryService(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDeviceAsync(new("http://192.168.1.8/description.xml")));
    }

    [Theory]
    [InlineData("file:///C:/private")]
    [InlineData("http://user:password@192.168.1.8/description.xml")]
    [InlineData("http://192.168.1.8/description.xml#fragment")]
    public async Task RejectsManualUnsafeUrlBeforeNetwork(string url)
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Network must not run")));
        using var service = new DlnaDiscoveryService(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDeviceAsync(new(url)));
    }

    [Fact]
    public async Task DtdAndExternalEntityAreRejected()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml("<!DOCTYPE root [<!ENTITY e SYSTEM 'file:///C:/private'>]><root>&e;</root>")));
        using var service = new DlnaDiscoveryService(http);
        await Assert.ThrowsAsync<XmlException>(() => service.GetDeviceAsync(new("http://192.168.1.8/device.xml")));
    }

    [Fact]
    public async Task DeepXmlRejectedBeforeMaterialization()
    {
        var body = string.Concat(Enumerable.Repeat("<a>", 70)) + string.Concat(Enumerable.Repeat("</a>", 70));
        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml(body)));
        using var service = new DlnaDiscoveryService(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDeviceAsync(new("http://192.168.1.8/device.xml")));
    }

    [Fact]
    public async Task OversizeChunkedBodyIsBounded()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => new(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableBytes(new byte[4 * 1024 * 1024 + 1])) }));
        using var service = new DlnaDiscoveryService(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDeviceAsync(new("http://192.168.1.8/device.xml")));
    }

    [Fact]
    public async Task DiscoveryDeduplicatesAndIgnoresOtherHostLocationsAndMalformedResponders()
    {
        var responses = new[] { Reply("http://192.168.1.8/desc"), Reply("http://192.168.1.8/desc"),
            Reply("http://evil.example/desc"), Reply("http://192.168.1.8/broken"), new DlnaSsdpResponse("garbage", IPAddress.Loopback) };
        var requests = new List<string>();
        using var http = new HttpClient(new DlnaTestHandler(request =>
        {
            lock (requests) requests.Add(request.RequestUri!.AbsolutePath);
            return DlnaTestHandler.Xml(request.RequestUri!.AbsolutePath == "/broken" ? "bad xml" : Description());
        }));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp(responses));
        Assert.Single(await service.DiscoverAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><UDN>uuid:other</UDN><serviceList><service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType><controlURL>/cm</controlURL></service></serviceList></device></root>")]
    [InlineData("")]
    [InlineData("timeout")]
    public async Task DiscoveryKeepsRecorderWhenAnotherResponderIsUnusable(string otherBody)
    {
        var responses = new[] { Reply("http://192.168.1.8/desc"), Reply("http://192.168.1.8/other") };
        using var http = new HttpClient(new DlnaTestHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/other") return DlnaTestHandler.Xml(Description());
            return otherBody == "timeout" ? throw new TaskCanceledException("timed out", new TimeoutException()) : DlnaTestHandler.Xml(otherBody);
        }));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp(responses));
        var device = Assert.Single(await service.DiscoverAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal("uuid:recorder", device.Id);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.8/desc\r\n\r\n", true)]
    [InlineData("HTTP/1.1 200 OK\r\nlocation: http://192.168.1.8:9999/desc\r\n\r\n", true)]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.9/desc\r\n\r\n", false)]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.8/a\r\nLOCATION: http://192.168.1.8/b\r\n\r\n", false)]
    [InlineData("NOTIFY * HTTP/1.1\r\nLOCATION: http://192.168.1.8/a\r\n\r\n", false)]
    public void SsdpResponseValidation(string response, bool accepted) =>
        Assert.Equal(accepted, DlnaDiscoveryService.TryParseSearchResponse(new(response, IPAddress.Parse("192.168.1.8")), out _));

    [Theory]
    // The interface that received the reply is 192.168.1.5 with the given mask.
    [InlineData("192.168.1.8", "255.255.255.0", true)]
    [InlineData("192.168.1.254", "255.255.255.0", true)]
    [InlineData("192.168.2.8", "255.255.255.0", false)]
    [InlineData("192.168.2.8", "255.255.0.0", true)]
    [InlineData("10.20.30.40", "255.255.255.0", false)]
    [InlineData("172.16.0.9", "255.255.0.0", false)]
    [InlineData("192.168.1.130", "255.255.255.128", false)]
    [InlineData("192.168.1.100", "255.255.255.128", true)]
    [InlineData("192.168.1.5", "255.255.255.255", true)]
    [InlineData("192.168.1.8", "255.255.255.255", false)]
    // An interface that reports no mask at all is not a network on which every address is a neighbour.
    [InlineData("192.168.1.8", "0.0.0.0", false)]
    [InlineData("10.20.30.40", "0.0.0.0", false)]
    [InlineData("192.168.1.5", "0.0.0.0", true)]
    // Link-local addresses are never routed, so such a reply can only come from the link itself.
    [InlineData("169.254.7.9", "255.255.255.0", true)]
    [InlineData("8.8.8.8", "255.255.255.0", false)]
    public void SsdpReplyMustComeFromTheSubnetOfTheInterfaceThatReceivedIt(string remote, string mask, bool accepted)
    {
        var (sender, local, subnet) = (IPAddress.Parse(remote), IPAddress.Parse("192.168.1.5"), IPAddress.Parse(mask));
        Assert.Equal(accepted, DlnaDiscoveryService.IsFromLocalSubnet(sender, local, subnet));
        // The LOCATION names the sender, as it must: the forged source address is all that is wrong with the reply.
        var headers = $"HTTP/1.1 200 OK\r\nLOCATION: http://{remote}:8080/any/path?x=y\r\n\r\n";
        Assert.Equal(accepted, DlnaDiscoveryService.TryParseSearchResponse(new(headers, sender, local, subnet), out var location));
        Assert.Equal(accepted, location is not null);
        // A transport that does not say which interface received the reply is not held to the rule; the built-in one always says.
        Assert.True(DlnaDiscoveryService.TryParseSearchResponse(new(headers, sender), out _));
    }

    [Fact]
    public void SubnetRuleIsForIpv4Only()
    {
        Assert.False(DlnaDiscoveryService.IsFromLocalSubnet(IPAddress.Parse("fe80::1"), IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0")));
        Assert.False(DlnaDiscoveryService.IsFromLocalSubnet(IPAddress.Parse("192.168.1.8"), IPAddress.Parse("fe80::5"), IPAddress.Parse("255.255.255.0")));
    }

    [Fact]
    public void LocationsAreLimitedPerResponderAndTakenInTurn()
    {
        static DlnaSsdpResponse From(string host, string path) => new($"HTTP/1.1 200 OK\r\nLOCATION: http://{host}{path}\r\n\r\n", IPAddress.Parse(host));
        var responses = new List<DlnaSsdpResponse>();
        // One device answers at once with thirty addresses, twice each; the recorder and a NAS answer after it.
        for (var index = 0; index < 30; index++) { responses.Add(From("192.168.1.66", "/fake" + index)); responses.Add(From("192.168.1.66", "/fake" + index)); }
        responses.Add(From("192.168.1.8", "/dms/ddd.xml"));
        responses.Add(From("192.168.1.20", "/nas/a"));
        responses.Add(From("192.168.1.20", "/nas/b"));
        responses.Add(From("192.168.1.20", "/nas/c"));
        responses.Add(new("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.99/other-host\r\n\r\n", IPAddress.Parse("192.168.1.77")));
        var locations = DlnaDiscoveryService.SelectLocations(responses, out var responders);
        Assert.Equal(3, responders);
        // Everyone's first, then everyone's second, and so on: the NAS with its three media servers loses none of them, and the
        // device with thirty addresses gets eight, all of them after the recorder's.
        Assert.Equal(["http://192.168.1.66/fake0", "http://192.168.1.8/dms/ddd.xml", "http://192.168.1.20/nas/a", "http://192.168.1.66/fake1", "http://192.168.1.20/nas/b",
            "http://192.168.1.66/fake2", "http://192.168.1.20/nas/c", "http://192.168.1.66/fake3", "http://192.168.1.66/fake4", "http://192.168.1.66/fake5", "http://192.168.1.66/fake6", "http://192.168.1.66/fake7"],
            locations.Select(uri => uri.AbsoluteUri));
        Assert.Equal(8, DlnaDiscoveryService.MaximumLocationsPerResponder);
    }

    /// <summary>
    /// The sender of a reply chooses the address it seems to come from. A few replies made up in the recorder's name, sent before
    /// the recorder itself answers, used to take both of its places, and its real description was never read.
    /// </summary>
    [Fact]
    public async Task RepliesMadeUpInTheRecordersNameDelayItsDescriptionAndDoNotReplaceIt()
    {
        static DlnaSsdpResponse From(string location) => new($"HTTP/1.1 200 OK\r\nLOCATION: {location}\r\n\r\n", IPAddress.Parse("192.168.1.8"), IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0"));
        var real = From("http://192.168.1.8:60606/Server0/ddd");
        DlnaSsdpResponse[] responses = [From("http://192.168.1.8:1/a"), From("http://192.168.1.8:2/b"), real, real];
        Assert.Equal(["http://192.168.1.8:1/a", "http://192.168.1.8:2/b", "http://192.168.1.8:60606/Server0/ddd"], DlnaDiscoveryService.SelectLocations(responses, out var responders).Select(uri => uri.AbsoluteUri));
        Assert.Equal(1, responders);
        var requests = new List<Uri>();
        using var http = new HttpClient(new DlnaTestHandler(request =>
        {
            lock (requests) requests.Add(request.RequestUri!);
            // Nothing listens on the ports that were made up: the recorder refuses the connection.
            return request.RequestUri!.Port == 60606 ? DlnaTestHandler.Xml(Description()) : throw new HttpRequestException("Connection refused");
        }));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp(responses));
        var result = await service.DiscoverWithRespondersAsync(TimeSpan.FromMilliseconds(100));
        Assert.Equal(new Uri("http://192.168.1.8:60606/Server0/ddd"), Assert.Single(result.Devices).DescriptionUri);
        Assert.Equal(1, result.ResponderCount);
        Assert.Equal(3, requests.Count);

        // The limit of what counting can do, stated so that nobody takes it for more: as many made-up addresses as one responder
        // may have, all ahead of the real one, still keep it out. Replies cannot be told apart by who really sent them.
        var filled = Enumerable.Range(1, DlnaDiscoveryService.MaximumLocationsPerResponder).Select(port => From($"http://192.168.1.8:{port}/x")).Append(real);
        Assert.DoesNotContain(DlnaDiscoveryService.SelectLocations(filled, out _), uri => uri.Port == 60606);
    }

    /// <summary>
    /// Replies in the names of forty addresses of the subnet where nothing answers, all ahead of the recorder's. Read four at a
    /// time with three seconds each, they used up the twenty seconds of the search before the recorder was asked.
    /// </summary>
    [Fact]
    public async Task AddressesThatNeverAnswerDoNotUseUpTheTimeBeforeTheRecorderIsAsked()
    {
        static DlnaSsdpResponse From(string host) => new($"HTTP/1.1 200 OK\r\nLOCATION: http://{host}/desc\r\n\r\n", IPAddress.Parse(host), IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0"));
        var responses = Enumerable.Range(100, 40).Select(host => From("192.168.1." + host)).Prepend(From("192.168.1.66")).Append(From("192.168.1.8")).ToArray();
        var asked = new List<string>();
        var silence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waiting = 0, mostWaiting = 0;
        using var http = new HttpClient(new AsyncHandler(async (request, cancellationToken) =>
        {
            var host = request.RequestUri!.Host;
            lock (asked) asked.Add(host);
            if (host is "192.168.1.66" or "192.168.1.8") return DlnaTestHandler.Xml(Description().Replace("uuid:recorder", "uuid:" + host));
            // A made-up address says nothing. The silence lasts until sixteen requests wait at once, the number the search is
            // meant to make at a time; from then on such a request fails at once. So the test takes no real seconds, and a search
            // that asks fewer at a time waits out its three seconds per address and is found out by the number below.
            lock (asked) { mostWaiting = Math.Max(mostWaiting, ++waiting); if (waiting == 16) silence.TrySetResult(); }
            try { await silence.Task.WaitAsync(cancellationToken); }
            finally { lock (asked) waiting--; }
            throw new HttpRequestException("No route to host");
        }));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp(responses));
        var result = await service.DiscoverWithRespondersAsync(TimeSpan.FromMilliseconds(100));
        Assert.Equal(["192.168.1.66", "192.168.1.8"], result.Devices.Select(device => device.Address).Order(StringComparer.Ordinal));
        Assert.Equal(42, result.ResponderCount);
        Assert.Equal(42, asked.Count);
        // Sixteen at a time: the 64 descriptions a search reads at most, three seconds each, then take 12 of its 20 seconds.
        Assert.Equal(16, mostWaiting);
    }

    [Fact]
    public void ManyRespondersCannotExceedTheOverallNumberOfLocations()
    {
        var responses = Enumerable.Range(1, 250).Select(host => new DlnaSsdpResponse($"HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.{host}/d\r\n\r\n", IPAddress.Parse("10.0.0." + host)));
        var locations = DlnaDiscoveryService.SelectLocations(responses, out var responders);
        Assert.Equal(64, locations.Count);
        Assert.Equal(250, responders);
        Assert.Equal(64, locations.Select(uri => uri.Host).Distinct().Count());
    }

    [Fact]
    public async Task OneResponderCanNeitherCrowdOutNorStallTheRecorder()
    {
        static DlnaSsdpResponse From(string host, string path) => new($"HTTP/1.1 200 OK\r\nLOCATION: http://{host}{path}\r\n\r\n", IPAddress.Parse(host));
        // The hostile device answers first: one address whose description calls it a DIGA, and many that accept the request and then say nothing.
        var responses = Enumerable.Range(0, 40).Select(index => From("192.168.1.66", index == 0 ? "/named-diga" : "/silent" + index)).Append(From("192.168.1.8", "/desc")).ToArray();
        var requests = new List<Uri>();
        using var http = new HttpClient(new AsyncHandler(async (request, cancellationToken) =>
        {
            lock (requests) requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.StartsWith("/silent", StringComparison.Ordinal)) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DlnaTestHandler.Xml(Description().Replace("uuid:recorder", "uuid:" + request.RequestUri.Host));
        }));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp(responses), TimeSpan.FromMilliseconds(300));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.DiscoverWithRespondersAsync(TimeSpan.FromMilliseconds(100));
        // Without the limits the four workers wait ten seconds each on silent addresses until the 20-second budget is gone.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Discovery took {elapsed.Elapsed}.");
        Assert.Equal(2, result.ResponderCount);
        Assert.Equal(["192.168.1.66", "192.168.1.8"], result.Devices.Select(device => device.Address).Order(StringComparer.Ordinal));
        Assert.Contains(result.Devices, device => device.Address == "192.168.1.8" && device.DescriptionUri.AbsolutePath == "/desc");
        // Of the forty addresses of the one device, eight are read and the others are not.
        Assert.Equal(DlnaDiscoveryService.MaximumLocationsPerResponder, requests.Count(uri => uri.Host == "192.168.1.66"));
        Assert.Equal(DlnaDiscoveryService.MaximumLocationsPerResponder + 1, requests.Count);
    }

    [Fact]
    public async Task DiscoverAsyncKeepsItsResultAndTheDeviceCarriesItsAddress()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml(Description())));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp([Reply("http://192.168.1.8:55000/desc")]));
        var device = Assert.Single(await service.DiscoverAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal("192.168.1.8", device.Address);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaDiscoveryService(http, descriptionLimit: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaDiscoveryService(http, descriptionLimit: TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public async Task PreCancelledDiscoveryAndDescriptionDoNotUseNetwork()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Network must not run")));
        var transport = new FakeSsdp([]);
        using var service = new DlnaDiscoveryService(http, transport);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAtAsync("192.168.1.8", cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDeviceAsync(new("http://127.0.0.1/desc"), cancelled.Token));
        Assert.Empty(transport.Asked);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("9.255.255.255", false)]
    [InlineData("11.0.0.1", false)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.167.1.1", false)]
    [InlineData("192.169.1.1", false)]
    [InlineData("169.254.1.1", true)]
    [InlineData("169.253.1.1", false)]
    // The range internet providers share among their customers is not a home network.
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.8", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fec0::1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::", false)]
    [InlineData("ff02::c", false)]
    public async Task OneRuleDecidesWhichAddressesAreContactedForTheCatalogueAndForRecordings(string text, bool local)
    {
        var address = IPAddress.Parse(text);
        Assert.Equal(local, DlnaDiscoveryService.IsLocalAddress(address));
        // An address that is asked directly is held to the same rule, and has to be IPv4 besides.
        Assert.Equal(local && address.AddressFamily == AddressFamily.InterNetwork, DlnaDiscoveryService.TryParseAddress(text, out _));
        if (local) return;
        // Both ways to a recorder refuse the address, and neither opens a connection to find out.
        var uri = new Uri(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{text}]:8080/x" : $"http://{text}:8080/x");
        using var discovery = new DlnaDiscoveryService();
        var catalogue = await Assert.ThrowsAsync<HttpRequestException>(() => discovery.GetDeviceAsync(uri));
        Assert.StartsWith(AppText.T("Core.Dlna.Endpoint.LocalRequired"), catalogue.Message, StringComparison.Ordinal);
        using var download = new DlnaDownloadService();
        var recording = await Assert.ThrowsAsync<ArgumentException>(() => download.DownloadAsync(new(uri, Path.Combine(Path.GetTempPath(), $"diga-never-written-{Guid.NewGuid():N}.ts"))));
        Assert.Equal(AppText.T("Core.Dlna.Download.LocalAddressRequired"), recording.Message);
    }

    [Fact]
    public async Task ARecorderCanBeAskedDirectlyAtItsAddress()
    {
        // The reply of the address that was asked, twice, and one from another address that came in on the same socket.
        var responses = new[] { Reply("http://192.168.1.8:55000/desc"), Reply("http://192.168.1.8:55000/desc"),
            new DlnaSsdpResponse("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.66/desc\r\n\r\n", IPAddress.Parse("192.168.1.66")) };
        var requests = new List<Uri>();
        using var http = new HttpClient(new DlnaTestHandler(request => { lock (requests) requests.Add(request.RequestUri!); return DlnaTestHandler.Xml(Description()); }));
        var transport = new FakeSsdp(responses);
        using var service = new DlnaDiscoveryService(http, transport);
        var device = Assert.Single(await service.DiscoverAtAsync(IPAddress.Parse("192.168.1.8"), TimeSpan.FromMilliseconds(100)));
        Assert.Equal("192.168.1.8", device.Address);
        Assert.Equal("DMR-BS850", device.ModelName);
        Assert.Equal([new Uri("http://192.168.1.8:55000/desc")], requests);
        // As a person types it, with a space before and after.
        Assert.Single(await service.DiscoverAtAsync(" 192.168.1.8 ", TimeSpan.FromMilliseconds(100)));
        Assert.Equal([IPAddress.Parse("192.168.1.8"), IPAddress.Parse("192.168.1.8")], transport.Asked);
        Assert.Equal(0, transport.Searches);
        // An address where nothing answers gives an empty list, not an error.
        using var silent = new DlnaDiscoveryService(http, new FakeSsdp([]));
        Assert.Empty(await silent.DiscoverAtAsync("192.168.1.9", TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DiscoverAtAsync("192.168.1.8", TimeSpan.FromSeconds(21)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("100.64.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("fe80::1")]
    [InlineData("recorder.local")]
    [InlineData("http://192.168.1.8/")]
    [InlineData("192.168.1.8:1900")]
    // Forms Windows also reads as addresses: 192.168.0.1, 192.168.1.8 and 192.168.8.1. Nobody who types them means those.
    [InlineData("192.168.1")]
    [InlineData("3232235784")]
    [InlineData("192.168.010.1")]
    [InlineData("")]
    [InlineData(null)]
    public async Task OnlyALocalAddressWrittenAsFourNumbersIsAskedDirectly(string? text)
    {
        var transport = new FakeSsdp([Reply("http://192.168.1.8/desc")]);
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Network must not run")));
        using var service = new DlnaDiscoveryService(http, transport);
        Assert.False(DlnaDiscoveryService.TryParseAddress(text, out var parsed));
        Assert.Null(parsed);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.DiscoverAtAsync(text!, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(AppText.T("Core.Dlna.Discovery.AddressInvalid"), error.Message);
        Assert.Empty(transport.Asked);
    }

    [Fact]
    public async Task AnAddressObjectOutsideTheLocalNetworkIsRefusedToo()
    {
        var transport = new FakeSsdp([Reply("http://192.168.1.8/desc")]);
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Network must not run")));
        using var service = new DlnaDiscoveryService(http, transport);
        foreach (var address in new[] { "8.8.8.8", "100.64.0.1", "239.255.255.250", "fe80::1", "::1", "::ffff:192.168.1.8" })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.DiscoverAtAsync(IPAddress.Parse(address), TimeSpan.FromMilliseconds(100)));
            Assert.Equal(AppText.T("Core.Dlna.Discovery.AddressInvalid"), error.Message);
        }
        Assert.Empty(transport.Asked);
        Assert.True(DlnaDiscoveryService.TryParseAddress(" 10.0.0.5\t", out var parsed));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), parsed);
        // A transport written before single addresses could be asked says that it cannot, and is not taken for an empty network.
        using var older = new DlnaDiscoveryService(http, new MulticastOnlySsdp());
        await Assert.ThrowsAsync<NotSupportedException>(() => older.DiscoverAtAsync("192.168.1.8", TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ASearchOnAPcThatIsOnNoNetworkSaysSoAndDoesNotReturnAnEmptyList()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Network must not run")));
        using var service = new DlnaDiscoveryService(http, new DlnaSsdpTransport(() => []));
        var error = await Assert.ThrowsAsync<IOException>(() => service.DiscoverAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(AppText.T("Core.Dlna.Discovery.NoNetwork"), error.Message);
        // The application shows the message of an IOException that carries no Windows code as it is.
        Assert.Equal(unchecked((int)0x80131620), error.HResult);
        error = await Assert.ThrowsAsync<IOException>(() => service.DiscoverWithRespondersAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(AppText.T("Core.Dlna.Discovery.NoNetwork"), error.Message);
        error = await Assert.ThrowsAsync<IOException>(() => service.DiscoverAtAsync("192.168.1.8", TimeSpan.FromMilliseconds(100)));
        Assert.Equal(AppText.T("Core.Dlna.Discovery.NoNetwork"), error.Message);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(TimeSpan.FromMilliseconds(100), cancelled.Token));
        // What this PC really has is listed without an error, whatever it is.
        Assert.All(DlnaSsdpTransport.LocalInterfaces(), local => Assert.Equal(AddressFamily.InterNetwork, local.Address.AddressFamily));
    }

    /// <summary>
    /// The built-in transport itself, with real datagrams that never leave this PC: its searches go to a responder on 127.0.0.2
    /// in place of the multicast group. That address lies inside the subnet 127.0.0.0/8 and outside 127.0.0.1/32.
    /// </summary>
    [Fact]
    public async Task TheBuiltInTransportSendsBothSearchesAndKeepsOnlyRepliesFromTheSubnetOfItsInterface()
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] targets = ["urn:schemas-upnp-org:device:MediaServer:1", "urn:schemas-upnp-org:service:ContentDirectory:1"];
        static string Target(string search) => search.Split("\r\n").Single(line => line.StartsWith("ST: ", StringComparison.Ordinal))[4..];
        // The device answers each search twice, as many devices do.
        using var device = new LoopbackDevice();
        var (address, mask) = (IPAddress.Parse(LoopbackDevice.Address), IPAddress.Parse("255.0.0.0"));
        var wide = new DlnaSsdpTransport(() => [new(IPAddress.Loopback, mask)], device.EndPoint);
        var (replies, searches) = await SearchPatiently(device, window => wide.SearchAsync(window), found => found.Count == 0);
        // Four replies that name the same description address are one.
        var reply = Assert.Single(replies);
        Assert.Equal((address, IPAddress.Loopback, mask), (reply.RemoteAddress, reply.LocalAddress, reply.LocalMask));
        Assert.True(DlnaDiscoveryService.TryParseSearchResponse(reply, out var location));
        Assert.Equal(device.Location, location!.AbsoluteUri);
        Assert.Equal(targets, searches.Select(Target).Order(StringComparer.Ordinal));
        Assert.All(searches, search => Assert.Matches("^M-SEARCH \\* HTTP/1\\.1\r\nHOST: 239\\.255\\.255\\.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: [1-5]\r\nST: [^\r\n]+\r\n\r\n$", search));

        // The same device, now outside the subnet of the interface that hears it: asked, and not listened to.
        var narrow = new DlnaSsdpTransport(() => [new(IPAddress.Loopback, IPAddress.Parse("255.255.255.255"))], device.EndPoint);
        (replies, searches) = await SearchPatiently(device, window => narrow.SearchAsync(window));
        Assert.Empty(replies);
        Assert.Equal(targets, searches.Select(Target).Order(StringComparer.Ordinal));

        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml(Description())));
        using var service = new DlnaDiscoveryService(http, wide);
        var (result, _) = await SearchPatiently(device, window => service.DiscoverWithRespondersAsync(window), found => found.Devices.Count == 0);
        Assert.Equal(LoopbackDevice.Address, Assert.Single(result.Devices).Address);
        Assert.Equal(1, result.ResponderCount);

        // Asked directly, the device gets the same two datagrams, and the search ends soon after the answer, not after the whole time.
        var before = device.Searches(0).Length;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var direct = Assert.Single(await service.DiscoverAtAsync(address, TimeSpan.FromSeconds(20)));
        Assert.Equal(new Uri(device.Location), direct.DescriptionUri);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"Asking one address took {elapsed.Elapsed}.");
        var asked = device.Searches(before + 2)[before..].Select(search => search.Text).ToArray();
        Assert.Equal(targets, asked.Select(Target).Order(StringComparer.Ordinal));
        Assert.All(asked, search => Assert.StartsWith("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\n", search, StringComparison.Ordinal));
        // Its neighbour 127.0.0.3 has nothing listening on that port: no devices, and no error.
        Assert.Empty(await service.DiscoverAtAsync(IPAddress.Parse("127.0.0.3"), TimeSpan.FromMilliseconds(700)));
    }

    /// <summary>
    /// What one address may put into the list of the built-in transport, again with real datagrams: a device that answers every
    /// search with thirty description addresses, each of them twice, with a reply that names no address and with one that names
    /// another host's, and after it a second device at 127.0.0.3.
    /// </summary>
    [Fact]
    public async Task TheBuiltInTransportKeepsWhatAnAddressSaysOnceAndNoMoreAddressesThanTheSearchReads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = new LoopbackDevice(port => Enumerable.Range(0, 30).SelectMany(index => Enumerable.Repeat((LoopbackDevice.Address, $"http://127.0.0.2:{port}/fake{index}"), 2))
            .Append((LoopbackDevice.Address, "")).Append((LoopbackDevice.Address, "http://127.0.0.9/other-host")).Append(("127.0.0.3", "http://127.0.0.3:1/desc")));
        var transport = new DlnaSsdpTransport(() => [new(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"))], device.EndPoint);
        var (replies, _) = await SearchPatiently(device, window => transport.SearchAsync(window), found => found.Count < DlnaDiscoveryService.MaximumLocationsPerResponder + 1);
        static string Location(DlnaSsdpResponse reply) => DlnaDiscoveryService.TryParseSearchResponse(reply, out var uri) ? uri!.AbsoluteUri : "";
        var first = replies.Where(reply => reply.RemoteAddress.ToString() == LoopbackDevice.Address).Select(Location).ToArray();
        Assert.Equal(DlnaDiscoveryService.MaximumLocationsPerResponder, first.Length);
        Assert.Equal(first.Length, first.Distinct().Count());
        Assert.All(first, uri => Assert.StartsWith($"http://127.0.0.2:{device.EndPoint.Port}/fake", uri, StringComparison.Ordinal));
        // The second device is in the list, after the 62 replies the first sent ahead of its one.
        Assert.Equal("http://127.0.0.3:1/desc", Location(Assert.Single(replies, reply => reply.RemoteAddress.ToString() == "127.0.0.3")));
        Assert.Equal(first.Length + 1, replies.Count);
        // Asked directly, the first device gets no more places either, and the search does not wait for more once they are taken.
        // It stops listening 1.2 seconds after the first reply; should a busy PC not deliver eight by then, it is asked again.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<DlnaSsdpResponse> direct = [];
        for (var attempt = 0; attempt < 3 && direct.Count < DlnaDiscoveryService.MaximumLocationsPerResponder; attempt++)
        {
            elapsed.Restart();
            direct = await transport.SearchAsync(IPAddress.Parse(LoopbackDevice.Address), TimeSpan.FromSeconds(20));
        }
        Assert.Equal(DlnaDiscoveryService.MaximumLocationsPerResponder, direct.Select(Location).Distinct().Count());
        Assert.Equal(DlnaDiscoveryService.MaximumLocationsPerResponder, direct.Count);
        Assert.All(direct, reply => Assert.Equal(LoopbackDevice.Address, reply.RemoteAddress.ToString()));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"Asking one address took {elapsed.Elapsed}.");
    }

    /// <summary>
    /// Runs one search of the built-in transport against the device and returns what it found together with the two searches the
    /// device received for it. On a busy PC the device's thread, or the transport's reading, may get no time before a short search
    /// ends. That says nothing about the transport, so the search is then made again with a longer one, and its result is judged
    /// only when the device had answered well before the search ended, or after the longest search.
    /// </summary>
    /// <param name="missing">Tells that a reply that was sent in time has not been read yet, where the test expects one.</param>
    private static async Task<(T Found, string[] Searches)> SearchPatiently<T>(LoopbackDevice device, Func<TimeSpan, Task<T>> search, Func<T, bool>? missing = null)
    {
        int[] seconds = [1, 4, 15];
        for (var attempt = 0; ; attempt++)
        {
            var before = device.Searches(0).Length;
            var found = await search(TimeSpan.FromSeconds(seconds[attempt]));
            var ended = System.Diagnostics.Stopwatch.GetTimestamp();
            // Also when the search is over: the next one must not find answers to this one still on their way.
            var searches = device.Searches(before + 2);
            Assert.Equal(before + 2, searches.Length);
            var inTime = searches[before..].All(answered => System.Diagnostics.Stopwatch.GetElapsedTime(answered.AnsweredAt, ended) > TimeSpan.FromMilliseconds(100));
            if (attempt == seconds.Length - 1 || inTime && missing?.Invoke(found) != true) return (found, searches[before..].Select(answered => answered.Text).ToArray());
        }
    }

    /// <summary>
    /// A device that answers searches with real datagrams that never leave this PC, at 127.0.0.2: inside the subnet 127.0.0.0/8
    /// and outside 127.0.0.1/32. It serves on a thread of its own with blocking sockets. When it was served on the thread pool
    /// of the test host and a search lasted 0.7 seconds, one of fourteen runs of the unit tests on two processors failed: the
    /// search had ended without a reply.
    /// </summary>
    /// <param name="answers">
    /// What is sent in answer to one search, given the device's port: for each reply the address it is sent from and the
    /// description address it names. When not given, the device names its own description, twice.
    /// </param>
    private sealed class LoopbackDevice : IDisposable
    {
        public const string Address = "127.0.0.2";
        private readonly Dictionary<string, Socket> _senders = [];
        private readonly List<(string Text, long AnsweredAt)> _searches = [];
        private readonly Func<int, IEnumerable<(string From, string Location)>> _answers;
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly Thread _thread;
        private volatile bool _closed;
        private Exception? _failure;

        public LoopbackDevice(Func<int, IEnumerable<(string From, string Location)>>? answers = null)
        {
            Open(_socket, Address);
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _senders.Add(Address, _socket);
            _answers = answers ?? (_ => [(Address, Location), (Address, Location)]);
            _thread = new Thread(Serve) { IsBackground = true, Name = "SSDP test device" };
            _thread.Start();
        }

        public IPEndPoint EndPoint { get; }
        public string Location => $"http://{Address}:{EndPoint.Port}/desc";

        /// <summary>The searches answered so far, each with the moment its answer had been sent, after waiting up to half a minute for there to be <paramref name="count"/>.</summary>
        public (string Text, long AnsweredAt)[] Searches(int count)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            lock (_searches)
            {
                while (_searches.Count < count && _failure is null && waited.Elapsed < TimeSpan.FromSeconds(30)) Monitor.Wait(_searches, 100);
                return _failure is null ? [.. _searches] : throw new IOException("The test device stopped answering.", _failure);
            }
        }

        private void Serve()
        {
            var buffer = new byte[4096];
            while (!_closed)
            {
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    var search = Encoding.ASCII.GetString(buffer, 0, _socket.ReceiveFrom(buffer, ref from));
                    var target = search.Split("\r\n").FirstOrDefault(line => line.StartsWith("ST: ", StringComparison.Ordinal)) ?? "ST: ";
                    foreach (var (source, location) in _answers(EndPoint.Port))
                    {
                        if (!_senders.TryGetValue(source, out var sender))
                            _senders.Add(source, sender = Open(new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp), source));
                        sender.SendTo(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=60\r\n{target}\r\nLOCATION: {location}\r\n\r\n"), from);
                    }
                    lock (_searches) { _searches.Add((search, System.Diagnostics.Stopwatch.GetTimestamp())); Monitor.PulseAll(_searches); }
                }
                // The socket was closed because the test is over. Anything else is told to the test, which waits for answers.
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    if (!_closed) lock (_searches) { _failure = ex; Monitor.PulseAll(_searches); }
                    return;
                }
            }
        }

        private static Socket Open(Socket socket, string address)
        {
            // An answer that finds its search already ended comes back as "port unreachable", and Windows would report that as
            // a reset connection on a later call of this socket, in the middle of other answers. SIO_UDP_CONNRESET turns it off.
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(IPAddress.Parse(address), 0));
            return socket;
        }

        public void Dispose()
        {
            _closed = true;
            _socket.Dispose();
            _thread.Join(TimeSpan.FromSeconds(10));
            foreach (var sender in _senders.Values) sender.Dispose();
        }
    }

    [Fact]
    public async Task DefaultTransportDoesNotFollowLoopbackRedirect()
    {
        using var redirectTarget = new TcpListener(IPAddress.Loopback, 0); redirectTarget.Start();
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            using var connection = await server.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            var wire = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{((IPEndPoint)redirectTarget.LocalEndpoint).Port}/target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(wire, timeout.Token);
        });
        using var service = new DlnaDiscoveryService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDeviceAsync(new($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/desc"), timeout.Token));
        await serve;
        Assert.False(redirectTarget.Pending());
    }

    [Fact]
    public async Task DefaultTransportRejectsPublicLiteralAddressWithoutConnecting()
    {
        using var service = new DlnaDiscoveryService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetDeviceAsync(new("http://8.8.8.8/description.xml"), timeout.Token));
    }

    [Fact]
    public async Task InFlightDescriptionCancellationStopsHttpRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new WaitingHandler(entered));
        using var service = new DlnaDiscoveryService(http);
        using var cancel = new CancellationTokenSource();
        var pending = service.GetDeviceAsync(new("http://127.0.0.1/description.xml"), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class WaitingHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }

    private static DlnaSsdpResponse Reply(string uri) => new($"HTTP/1.1 200 OK\r\nLOCATION: {uri}\r\n\r\n", IPAddress.Parse("192.168.1.8"));
    private sealed class FakeSsdp(IReadOnlyList<DlnaSsdpResponse> responses) : IDlnaSsdpTransport
    {
        /// <summary>How often the whole network was searched, and which single addresses were asked.</summary>
        public int Searches { get; private set; }
        public List<IPAddress> Asked { get; } = [];
        public Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Searches++; return Task.FromResult(responses); }
        public Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(IPAddress address, TimeSpan duration, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Asked.Add(address); return Task.FromResult(responses); }
    }
    private sealed class MulticastOnlySsdp : IDlnaSsdpTransport
    {
        public Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DlnaSsdpResponse>>([]);
    }
    private sealed class NonSeekableBytes(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}

internal sealed class DlnaTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(handle(request)); }
    internal static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
}
