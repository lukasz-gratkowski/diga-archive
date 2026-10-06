using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Diga.Core.Dlna;

namespace Diga.Tests;

/// <summary>Models the observed Panasonic firmware: default action namespace gets an empty 412; u:Browse succeeds.</summary>
public sealed class DlnaCanonicalBrowseTests
{
    private const string FolderId = "folder<&\"家";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task CanonicalActionBrowsesEveryRootAndNestedPageWhenLegacyNamespaceGets412(int serviceVersion)
    {
        var recorder = new PrefixSensitiveRecorder(serviceVersion);
        using var http = new HttpClient(recorder);

        // Negative control: the same valid action and unqualified arguments in the old spelling are refused.
        using var legacy = new HttpRequestMessage(HttpMethod.Post, recorder.Device.ContentDirectoryControlUri)
        {
            Content = new StringContent($$"""
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><Browse xmlns="{{recorder.Device.ContentDirectoryServiceType}}"><ObjectID xmlns="">0</ObjectID><BrowseFlag xmlns="">BrowseDirectChildren</BrowseFlag><Filter xmlns="">*</Filter><StartingIndex xmlns="">0</StartingIndex><RequestedCount xmlns="">100</RequestedCount><SortCriteria xmlns=""></SortCriteria></Browse></s:Body></s:Envelope>
                """, Encoding.UTF8, "text/xml")
        };
        legacy.Headers.Add("SOAPAction", $"\"{recorder.Device.ContentDirectoryServiceType}#Browse\"");
        using (var refused = await http.SendAsync(legacy))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);
            Assert.Empty(await refused.Content.ReadAsByteArrayAsync());
        }

        using var browser = new DlnaContentDirectoryClient(http);
        var root = await browser.BrowseAsync(recorder.Device);
        Assert.Equal(2, root.Count);
        Assert.All(root, folder => Assert.True(folder.IsContainer));
        Assert.Equal(FolderId, root[0].Id);
        var children = await browser.BrowseAsync(recorder.Device, root[0].Id);
        Assert.Equal(new[] { "recording-1", "recording-2" }, children.Select(item => item.Id));
        Assert.All(children, item =>
        {
            Assert.Equal(FolderId, item.ParentId);
            Assert.Equal("Video & family 家", item.Title);
            Assert.Equal(DlnaTransferStatus.OriginalAvailable, item.TransferStatus);
        });
        Assert.Equal(1, recorder.LegacyRejections);
        Assert.Equal(new[] { ("0", 0u), ("0", 1u), (FolderId, 0u), (FolderId, 1u) },
            recorder.Accepted.Select(request => (request.ObjectId, request.Start)));
        Assert.All(recorder.Accepted, request =>
        {
            Assert.Contains("<u:Browse xmlns:u=\"" + recorder.Device.ContentDirectoryServiceType + "\">", request.Body);
            Assert.DoesNotContain("xmlns=\"\"", request.Body);
            if (request.ObjectId == FolderId) Assert.Contains("folder&lt;&amp;\"家", request.Body);
            if (request.ObjectId == "0" && request.Start == 0)
            {
                // Exact spelling the reported DMR-BS850 accepted on 2026-10-01 (415 bytes for ContentDirectory:2).
                Assert.Equal("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:Browse xmlns:u=\"" +
                    recorder.Device.ContentDirectoryServiceType + "\"><ObjectID>0</ObjectID><BrowseFlag>BrowseDirectChildren</BrowseFlag><Filter>*</Filter><StartingIndex>0</StartingIndex>" +
                    "<RequestedCount>100</RequestedCount><SortCriteria></SortCriteria></u:Browse></s:Body></s:Envelope>", request.Body);
                if (serviceVersion == 2) Assert.Equal(415, Encoding.UTF8.GetByteCount(request.Body));
            }
        });
    }

    private sealed record Invocation(string ObjectId, uint Start, string Body);

    private sealed class PrefixSensitiveRecorder : HttpMessageHandler
    {
        private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
        public DlnaDevice Device { get; }
        public int LegacyRejections { get; private set; }
        public List<Invocation> Accepted { get; } = [];

        public PrefixSensitiveRecorder(int version)
        {
            Device = new("uuid:canonical-fixture", "Panasonic namespace fixture", "Panasonic", "Synthetic",
                new("http://127.0.0.1:9000/description.xml"), new("http://127.0.0.1:9001/control"),
                "urn:schemas-upnp-org:service:ContentDirectory:" + version.ToString(CultureInfo.InvariantCulture));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Device.ContentDirectoryControlUri, request.RequestUri);
            Assert.Equal($"\"{Device.ContentDirectoryServiceType}#Browse\"", Assert.Single(request.Headers.GetValues("SOAPAction")));
            Assert.Equal("text/xml; charset=utf-8", request.Content!.Headers.ContentType!.ToString());
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            var envelope = Assert.IsType<XmlElement>(document.DocumentElement);
            Assert.Equal("Envelope", envelope.LocalName);
            Assert.Equal(Soap.NamespaceName, envelope.NamespaceURI);
            var action = Assert.IsType<XmlElement>(document.SelectSingleNode("/*[local-name()='Envelope']/*[local-name()='Body']/*[local-name()='Browse']"));
            Assert.Equal(Device.ContentDirectoryServiceType, action.NamespaceURI);
            var arguments = action.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().ToArray();
            Assert.Equal(new[] { "ObjectID", "BrowseFlag", "Filter", "StartingIndex", "RequestedCount", "SortCriteria" }, arguments.Select(argument => argument.LocalName));
            Assert.All(arguments, argument => Assert.Equal("", argument.NamespaceURI));
            Assert.Equal("BrowseDirectChildren", arguments[1].InnerText);
            Assert.Equal("*", arguments[2].InnerText);
            Assert.Equal("100", arguments[4].InnerText);
            Assert.Equal("", arguments[5].InnerText);
            if (action.Prefix != "u")
            {
                LegacyRejections++;
                return new(HttpStatusCode.PreconditionFailed) { Content = new ByteArrayContent([]) };
            }

            var objectId = arguments[0].InnerText;
            var start = uint.Parse(arguments[3].InnerText, NumberStyles.None, CultureInfo.InvariantCulture);
            Assert.True(start is 0 or 1, "Fixture expects two short pages, each returning one object.");
            Assert.Contains(objectId, new[] { "0", FolderId });
            Accepted.Add(new(objectId, start, body));
            var item = objectId == "0"
                ? new XElement(Didl + "container", new XAttribute("id", start == 0 ? FolderId : "archive"), new XAttribute("parentID", "0"),
                    new XElement(Dc + "title", "Folder & family 家"))
                : new XElement(Didl + "item", new XAttribute("id", "recording-" + (start + 1).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("parentID", FolderId), new XAttribute("restricted", "1"), new XElement(Dc + "title", "Video & family 家"),
                    new XElement(Didl + "res", new XAttribute("protocolInfo", "http-get:*:video/mpeg:DLNA.ORG_CI=0"),
                        "http://127.0.0.1:9002/recording"));
            var response = new XElement(Soap + "Envelope", new XElement(Soap + "Body", new XElement(XName.Get("BrowseResponse", Device.ContentDirectoryServiceType),
                new XElement("Result", new XElement(Didl + "DIDL-Lite", item).ToString(SaveOptions.DisableFormatting)),
                new XElement("NumberReturned", 1), new XElement("TotalMatches", 2), new XElement("UpdateID", 7))));
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        }
    }
}
