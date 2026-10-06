using System.Net;
using System.Xml;
using System.Xml.Linq;
using Diga.Core.Dlna;
using Diga.Core.Localization;

namespace Diga.Tests;

public sealed class DlnaBrowseTests
{
    private static readonly DlnaDevice Device = new("uuid:1", "Recorder", "Panasonic", "DMR-BS850",
        new("http://192.168.1.8:6000/device.xml"), new("http://192.168.1.8:7000/control"), "urn:schemas-upnp-org:service:ContentDirectory:1");
    private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public async Task EscapedDidlPreservesTitlesFoldersAndLargeSizesAndDoesNotTreatRestrictedAsDrm()
    {
        var folder = new XElement(Didl + "container", new XAttribute("id", "folder"), new XAttribute("parentID", "0"), new XAttribute("restricted", 1), new XElement(Dc + "title", "TV & film"));
        var item = Item("recording", Res("http://192.168.1.8:8000/video", "http-get:*:video/mpeg:DLNA.ORG_CI=0", 5L << 30, "27:00:02.500"));
        item.Add(new XElement(Dc + "date", "2026-09-30T12:34:56Z"));
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(2, 2, folder, item)));
        using var browser = new DlnaContentDirectoryClient(http);
        var entries = await browser.BrowseAsync(Device);
        Assert.True(entries[0].IsContainer); Assert.Equal("TV & film", entries[0].Title);
        Assert.Equal("News & Sport", entries[1].Title); Assert.Equal("0", entries[1].ParentId);
        Assert.Equal(5L << 30, entries[1].PreferredResource!.SizeBytes);
        Assert.Equal(TimeSpan.FromHours(27) + TimeSpan.FromSeconds(2.5), entries[1].PreferredResource!.Duration);
        Assert.Equal("video/mpeg", entries[1].PreferredResource!.MimeType);
        Assert.Equal(DlnaTransferStatus.OriginalAvailable, entries[1].TransferStatus);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.Zero), entries[1].RecordedAt);
    }

    [Fact]
    public async Task PaginationUsesReturnedCountAndSafelyEscapesObjectId()
    {
        var starts = new List<uint>();
        const string objectId = "folder<&\"123";
        using var http = new HttpClient(new DlnaTestHandler(request =>
        {
            var xml = XDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(objectId, xml.Descendants().Single(e => e.Name.LocalName == "ObjectID").Value);
            Assert.Equal("BrowseDirectChildren", xml.Descendants().Single(e => e.Name.LocalName == "BrowseFlag").Value);
            Assert.Equal("100", xml.Descendants().Single(e => e.Name.LocalName == "RequestedCount").Value);
            Assert.Equal("\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"", request.Headers.GetValues("SOAPAction").Single());
            starts.Add(uint.Parse(xml.Descendants().Single(e => e.Name.LocalName == "StartingIndex").Value));
            return starts.Count == 1 ? Response(2, 3, Item("a"), Item("b")) : Response(1, 3, Item("c"));
        }));
        using var browser = new DlnaContentDirectoryClient(http);
        Assert.Equal(3, (await browser.BrowseAsync(Device, objectId)).Count);
        Assert.Equal(new uint[] { 0, 2 }, starts);
    }

    [Fact]
    public async Task UnknownTotalContinuesDespiteShortPageUntilZeroReturned()
    {
        int calls = 0;
        using var http = new HttpClient(new DlnaTestHandler(_ => ++calls switch { 1 => Response(1, 0, Item("a")), 2 => Response(1, 0, Item("b")), _ => Response(0, 0) }));
        using var browser = new DlnaContentDirectoryClient(http);
        Assert.Equal(2, (await browser.BrowseAsync(Device)).Count); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task UnknownTotalMayEndWithUpnp720AfterSuccessfulPages()
    {
        int calls = 0;
        using var http = new HttpClient(new DlnaTestHandler(_ => ++calls == 1 ? Response(1, 0, Item("a")) : Fault("720")));
        using var browser = new DlnaContentDirectoryClient(http);
        Assert.Single(await browser.BrowseAsync(Device));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("repeat")]
    [InlineData("short")]
    [InlineData("changed")]
    [InlineData("limit")]
    [InlineData("overrun")]
    [InlineData("shrinking")]
    [InlineData("growing")]
    [InlineData("forgotten")]
    public async Task RejectsInconsistentOrUnboundedPaging(string kind)
    {
        int calls = 0;
        using var http = new HttpClient(new DlnaTestHandler(_ =>
        {
            calls++;
            if (kind == "count") return Response(2, 2, Item("a"));
            if (kind == "limit") return Response(1, 10001, Item("a"));
            if (kind == "overrun") return Response(2, 1, Item("a"), Item("b"));
            if (calls == 1) return Response(1, kind == "shrinking" ? 3u : 2u, Item("a"));
            if (kind == "short") return Response(0, 2);
            if (kind == "shrinking") return Response(1, 2, Item("b"));
            if (kind == "growing") return Response(1, 3, Item("b"));
            if (kind == "forgotten") return Response(0, 0);
            if (kind == "changed") return Response(1, 2, Item("b"), update: "2");
            return Response(1, 2, Item("a"));
        }));
        using var browser = new DlnaContentDirectoryClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => browser.BrowseAsync(Device));
    }

    [Fact]
    public async Task PageCapStopsUnknownTotalServer()
    {
        int calls = 0;
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(1, 0, Item((++calls).ToString()))));
        using var browser = new DlnaContentDirectoryClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => browser.BrowseAsync(Device));
        Assert.Equal(200, calls);
    }

    [Fact]
    public async Task ProtectionAndConversionPreferenceIsConservative()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(3, 3,
            Item("mixed", Res("http://192.168.1.8/converted", "http-get:*:video/mpeg:DLNA.ORG_CI=1"), Res("http://192.168.1.8/unknown", "http-get:*:video/mpeg:*"), Res("http://192.168.1.8/original", "http-get:*:video/mpeg:DLNA.ORG_CI=0")),
            Item("protected", Res("http://192.168.1.8/protected", "http-get:*:application/x-dtcp1:DLNA.ORG_CI=0")),
            Item("converted", Res("http://192.168.1.8/converted", "http-get:*:video/mpeg:DLNA.ORG_CI=1")))));
        using var browser = new DlnaContentDirectoryClient(http);
        var rows = await browser.BrowseAsync(Device);
        Assert.Equal("/original", rows[0].PreferredResource!.Uri.AbsolutePath);
        Assert.Null(rows[1].PreferredResource); Assert.Equal(DlnaTransferStatus.Protected, rows[1].TransferStatus);
        Assert.Null(rows[2].PreferredResource); Assert.Equal(DlnaTransferStatus.ConvertedOnly, rows[2].TransferStatus);
    }

    [Theory]
    [InlineData("*", null)]
    [InlineData("DLNA.ORG_CI=0", false)]
    [InlineData("DLNA.ORG_CI=1", true)]
    [InlineData("DLNA.ORG_CI=0;DLNA.ORG_CI=1", true)]
    [InlineData("DLNA.ORG_CI=potato", true)]
    public void ProtocolConversionIndicator(string attributes, bool? converted) =>
        Assert.Equal(converted, DlnaProtocolInfo.Parse(new("http://192.168.1.8/video"), "http-get:*:video/mpeg:" + attributes).IsConverted);

    [Fact]
    public async Task ExplicitProtectionAttributeAndInvalidOptionalMetadataAreHandled()
    {
        var resource = Res("http://192.168.1.8/protected", "http-get:*:video/mpeg:*", duration: "99:99:99");
        resource.Add(new XAttribute("protection", "vendor-scheme"), new XAttribute("size", "-1"));
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(1, 1, Item("a", resource))));
        using var browser = new DlnaContentDirectoryClient(http);
        var row = Assert.Single(await browser.BrowseAsync(Device));
        Assert.Null(row.PreferredResource); Assert.True(row.Resources[0].IsProtected);
        Assert.Null(row.Resources[0].Duration); Assert.Null(row.Resources[0].SizeBytes);
    }

    [Fact]
    public async Task ExternalAndCredentialResourceUrlsRemainUnavailable()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(1, 1, Item("a",
            Res("http://other.example/video", "http-get:*:video/mpeg:*"), Res("http://user:secret@192.168.1.8/video", "http-get:*:video/mpeg:*"), Res("file:///C:/private", "http-get:*:video/mpeg:*")))));
        using var browser = new DlnaContentDirectoryClient(http);
        var row = Assert.Single(await browser.BrowseAsync(Device));
        Assert.Empty(row.Resources); Assert.Equal(DlnaTransferStatus.Unavailable, row.TransferStatus);
    }

    [Fact]
    public async Task ZeroCatalogueSizeIsTreatedAsUnknown()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(1, 1, Item("a", Res("http://192.168.1.8/video", "http-get:*:video/mpeg:*", 0)))));
        using var browser = new DlnaContentDirectoryClient(http);
        var row = Assert.Single(await browser.BrowseAsync(Device));
        Assert.Null(row.PreferredResource!.SizeBytes);
    }

    [Fact]
    public async Task EmptyResourceReferenceIsNeverOfferedButKeepsProtectionStatus()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(3, 3,
            Item("a", Res("", "http-get:*:video/mpeg:DLNA.ORG_CI=0", 123), Res("http:", "http-get:*:video/mpeg:DLNA.ORG_CI=0"), Res("?", "http-get:*:video/mpeg:DLNA.ORG_CI=0"),
                Res(Device.DescriptionUri.AbsoluteUri, "http-get:*:video/mpeg:DLNA.ORG_CI=0")),
            Item("b", Res(" ", "http-get:*:application/x-dtcp1:*")),
            Item("c", Res("?id=5", "http-get:*:video/mpeg:DLNA.ORG_CI=0")))));
        using var browser = new DlnaContentDirectoryClient(http);
        var rows = await browser.BrowseAsync(Device);
        Assert.Empty(rows[0].Resources); Assert.Null(rows[0].PreferredResource);
        Assert.Equal(DlnaTransferStatus.Unavailable, rows[0].TransferStatus);
        Assert.Null(rows[1].PreferredResource); Assert.Equal(DlnaTransferStatus.Protected, rows[1].TransferStatus);
        // The same path with a real query is a different resource and stays downloadable.
        Assert.Equal("http://192.168.1.8:6000/device.xml?id=5", rows[2].PreferredResource!.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task RecorderTimeoutIsReportedAsFailureAndUserCancellationStaysCancellation()
    {
        // HttpClient reports its own limits (connect timeout, our exchange limit) as cancellation without a cancelled caller token.
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new TaskCanceledException("timed out", new TimeoutException())));
        using var browser = new DlnaContentDirectoryClient(http);
        var browse = await Assert.ThrowsAsync<TimeoutException>(() => browser.BrowseAsync(Device));
        Assert.Equal(AppText.T("Core.Dlna.Xml.Timeout"), browse.Message);
        using var discovery = new DlnaDiscoveryService(http);
        await Assert.ThrowsAsync<TimeoutException>(() => discovery.GetDeviceAsync(Device.DescriptionUri));

        using var cancel = new CancellationTokenSource();
        using var cancelling = new HttpClient(new DlnaTestHandler(_ => { cancel.Cancel(); throw new TaskCanceledException(); }));
        using var cancelled = new DlnaContentDirectoryClient(cancelling);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.BrowseAsync(Device, cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task ListingLimitIsReportedAsTimeoutAndUserCancellationStaysCancellation()
    {
        // The listing limit cancels the token the request reader was given, so only BrowseAsync can tell it from a user cancellation.
        using var http = new HttpClient(new HangingHandler());
        using var browser = new DlnaContentDirectoryClient(http, TimeSpan.FromMilliseconds(200));
        var browse = await Assert.ThrowsAsync<TimeoutException>(() => browser.BrowseAsync(Device));
        Assert.Equal(AppText.T("Core.Dlna.Xml.Timeout"), browse.Message);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var patient = new DlnaContentDirectoryClient(http, TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patient.BrowseAsync(Device, cancellationToken: cancel.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaContentDirectoryClient(http, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaContentDirectoryClient(http, TimeSpan.FromMinutes(3)));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("Unreachable"); }
    }

    [Fact]
    public async Task EmbeddedDidlDtdIsRejected()
    {
        var response = Envelope("<!DOCTYPE DIDL-Lite [<!ENTITY x SYSTEM 'file:///C:/private'>]><DIDL-Lite>&x;</DIDL-Lite>", 0, 0);
        using var http = new HttpClient(new DlnaTestHandler(_ => DlnaTestHandler.Xml(response)));
        using var browser = new DlnaContentDirectoryClient(http);
        await Assert.ThrowsAsync<XmlException>(() => browser.BrowseAsync(Device));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("protocol")]
    [InlineData("parent")]
    public async Task OversizedDisplayAndResourceFieldsAreRejected(string field)
    {
        var item = Item("a", Res("http://192.168.1.8/video", field == "protocol" ? new string('x', 8193) : "http-get:*:video/mpeg:*"));
        if (field == "title") item.Element(Dc + "title")!.Value = new string('x', 4097);
        if (field == "parent") item.SetAttributeValue("parentID", new string('x', 4097));
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(1, 1, item)));
        using var browser = new DlnaContentDirectoryClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => browser.BrowseAsync(Device));
    }

    [Fact]
    public async Task CancellationAndCrossHostDeviceAreRejectedBeforeNetwork()
    {
        using var http = new HttpClient(new DlnaTestHandler(_ => throw new Exception("Unexpected network")));
        using var browser = new DlnaContentDirectoryClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => browser.BrowseAsync(Device with { ContentDirectoryControlUri = new("http://other.example/control") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => browser.BrowseAsync(Device with { ContentDirectoryServiceType = "bad\r\nheader" }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.BrowseAsync(Device, cancellationToken: cancelled.Token));
    }

    private static XElement Item(string id, params XElement[] resources) => new(Didl + "item", new XAttribute("id", id), new XAttribute("parentID", "0"), new XAttribute("restricted", "1"), new XElement(Dc + "title", "News & Sport"), resources);
    private static XElement Res(string uri, string protocol, long? size = null, string? duration = null) => new(Didl + "res", new XAttribute("protocolInfo", protocol), size.HasValue ? new XAttribute("size", size) : null, duration is not null ? new XAttribute("duration", duration) : null, uri);
    private static HttpResponseMessage Response(uint number, uint total, params XElement[] items) => Response(number, total, items, "1");
    private static HttpResponseMessage Response(uint number, uint total, XElement item, string update) => Response(number, total, [item], update);
    private static HttpResponseMessage Response(uint number, uint total, XElement[] items, string update) => DlnaTestHandler.Xml(Envelope(new XElement(Didl + "DIDL-Lite", items).ToString(SaveOptions.DisableFormatting), number, total, update));
    private static string Envelope(string didl, uint number, uint total, string update = "1") => new XElement(XName.Get("Envelope", "http://schemas.xmlsoap.org/soap/envelope/"), new XElement("Body", new XElement(XName.Get("BrowseResponse", Device.ContentDirectoryServiceType), new XElement("Result", didl), new XElement("NumberReturned", number), new XElement("TotalMatches", total), new XElement("UpdateID", update)))).ToString();
    private static HttpResponseMessage Fault(string code) => new(HttpStatusCode.InternalServerError) { Content = new StringContent($"<Envelope><Body><Fault><detail><UPnPError><errorCode>{code}</errorCode><errorDescription>Cannot process</errorDescription></UPnPError></detail></Fault></Body></Envelope>") };
}
