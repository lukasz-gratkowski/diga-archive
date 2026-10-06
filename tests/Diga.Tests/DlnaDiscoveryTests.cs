using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Diga.Core.Dlna;

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

    [Fact]
    public async Task PreCancelledDiscoveryAndDescriptionDoNotUseNetwork()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Network must not run")));
        using var service = new DlnaDiscoveryService(http, new FakeSsdp([]));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDeviceAsync(new("http://127.0.0.1/desc"), cancelled.Token));
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

    private static DlnaSsdpResponse Reply(string uri) => new($"HTTP/1.1 200 OK\r\nLOCATION: {uri}\r\n\r\n", IPAddress.Parse("192.168.1.8"));
    private sealed class FakeSsdp(IReadOnlyList<DlnaSsdpResponse> responses) : IDlnaSsdpTransport
    {
        public Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(responses); }
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
