using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Diga.Core.Dlna;
using Diga.Core.Media;

namespace Diga.Tests;

/// <summary>A loopback recorder emulator with real FFmpeg/MediaInfo; no recorder or LAN discovery required.</summary>
[Trait("Category", "Integration")]
public sealed class DlnaIntegrationTests(MediaIntegrationFixture fixture) : IClassFixture<MediaIntegrationFixture>
{
    [Theory]
    [InlineData(OutputFormat.Matroska)]
    [InlineData(OutputFormat.Mpeg2)]
    [InlineData(OutputFormat.Mp4)]
    public async Task BrowseDownloadInspectAndRemuxRetainsDeliveredRecording(OutputFormat format)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = format == OutputFormat.Mp4 ? fixture.H264 : fixture.Mpeg;
        var bytes = await File.ReadAllBytesAsync(source, timeout.Token);
        await using var recorder = new LoopbackRecorder(bytes, format == OutputFormat.Mp4 ? "video/mp2t" : "video/mpeg");
        using var discovery = new DlnaDiscoveryService();
        var device = await discovery.GetDeviceAsync(new Uri(recorder.BaseUri, "description.xml"), timeout.Token);
        Assert.Equal("AMG DIGA test recorder", device.FriendlyName);
        using var browser = new DlnaContentDirectoryClient();
        var folder = Assert.Single(await browser.BrowseAsync(device, cancellationToken: timeout.Token));
        Assert.True(folder.IsContainer);
        var recording = Assert.Single(await browser.BrowseAsync(device, folder.Id, timeout.Token));
        Assert.Equal("Family & travel 録画", recording.Title);
        // DIDL restricted=1 means the object is read-only, not encrypted.
        Assert.Equal(DlnaTransferStatus.OriginalAvailable, recording.TransferStatus);
        var resource = Assert.IsType<DlnaResource>(recording.PreferredResource);
        var original = fixture.NewPath("delivered-original.ts");
        using var downloader = NewDownloader();
        var download = await downloader.DownloadAsync(new(resource, original), cancellationToken: timeout.Token);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), download.Sha256);
        Assert.True(download.LengthVerified);
        Assert.False(download.MetadataUnknown);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original, timeout.Token));
        var destination = fixture.NewPath("network-archive" + OutputFormats.Extension(format));
        await new RemuxService(fixture.Ffmpeg, fixture.Ffprobe).RemuxAsync(new(original, destination, format), cancellationToken: timeout.Token);
        var mediaInfo = new MediaInfoService(Path.Combine(fixture.Tools, "MediaInfo.dll"));
        foreach (var file in new[] { original, destination })
        {
            var info = await mediaInfo.InspectAsync(file, timeout.Token);
            Assert.True(info.Available, info.Error);
            using var json = JsonDocument.Parse(info.Json);
            Assert.Contains(json.RootElement.GetProperty("media").GetProperty("track").EnumerateArray(), t => t.GetProperty("@type").GetString() == "Video");
        }
        if (format == OutputFormat.Mp4)
            Assert.Equal(await fixture.DecodedVideoHash(original), await fixture.DecodedVideoHash(destination));
        else
            Assert.Equal(await fixture.PacketHashes(original), await fixture.PacketHashes(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original, timeout.Token));
        Assert.True(recorder.SawDownloadHeaders);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.partial"));
    }

    [Fact]
    public async Task DirectUrlKeepsRawCopyWhenOptionalMp4IsIncompatible()
    {
        var bytes = await File.ReadAllBytesAsync(fixture.Mpeg);
        await using var recorder = new LoopbackRecorder(bytes, "video/mpeg", advertiseOriginal: false);
        using var downloader = NewDownloader();
        var original = fixture.NewPath("direct-url.mpg");
        var result = await downloader.DownloadAsync(new(new Uri(recorder.BaseUri, "recording"), original));
        Assert.True(result.MetadataUnknown);
        var remuxed = fixture.NewPath("incompatible.mp4");
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService(fixture.Ffmpeg, fixture.Ffprobe)
            .RemuxAsync(new(original, remuxed, OutputFormat.Mp4)));
        Assert.False(File.Exists(remuxed));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
    }

    // A download leaves 1 GB free on the drive Windows runs from, where the temporary folder of these tests is. They are not
    // about how full the drive of the PC that runs them is, so the downloader is told that there is room.
    private static DlnaDownloadService NewDownloader() => new(freeSpace: _ => long.MaxValue);

    private sealed class LoopbackRecorder : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly byte[] _media;
        private readonly string _mime;
        private readonly bool _advertiseOriginal;
        public Uri BaseUri { get; }
        public bool SawDownloadHeaders { get; private set; }

        public LoopbackRecorder(byte[] media, string mime, bool advertiseOriginal = true)
        {
            _media = media; _mime = mime; _advertiseOriginal = advertiseOriginal;
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    var header = new List<byte>();
                    var one = new byte[1];
                    while (header.Count < 16384)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) throw new IOException("Incomplete emulator request.");
                        header.Add(one[0]);
                        if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    var path = lines[0].Split(' ')[1];
                    var contentLength = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var body = new byte[contentLength is null ? 0 : int.Parse(contentLength.Split(':')[1])];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    byte[] response;
                    string mime;
                    string extra = "";
                    if (path == "/recording")
                    {
                        SawDownloadHeaders = lines.Any(l => l.Equals("getcontentFeatures.dlna.org: 1", StringComparison.OrdinalIgnoreCase)) &&
                            lines.Any(l => l.Equals("Accept-Encoding: identity", StringComparison.OrdinalIgnoreCase));
                        response = _media; mime = _mime;
                        if (_advertiseOriginal) extra = "contentFeatures.dlna.org: DLNA.ORG_CI=0\r\n";
                    }
                    else
                    {
                        mime = "text/xml; charset=utf-8";
                        response = Encoding.UTF8.GetBytes(path == "/description.xml" ? Description() : Browse(Encoding.UTF8.GetString(body)));
                    }
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {mime}\r\nContent-Length: {response.Length}\r\n{extra}Connection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            // Stopping the listener can also end a pending accept with ObjectDisposedException; seen on hosted runners.
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private static string Description() => """
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
            <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
            <friendlyName>AMG DIGA test recorder</friendlyName><manufacturer>Test fixture</manufacturer>
            <modelName>Synthetic DLNA</modelName><UDN>uuid:diga-test-recorder</UDN>
            <serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
            <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId><controlURL>/control</controlURL>
            </service></serviceList></device></root>
            """;

        private string Browse(string request)
        {
            var xml = XDocument.Parse(request);
            var objectId = xml.Descendants().Single(e => e.Name.LocalName == "ObjectID").Value;
            Assert.Equal("0", xml.Descendants().Single(e => e.Name.LocalName == "StartingIndex").Value);
            Assert.Equal("BrowseDirectChildren", xml.Descendants().Single(e => e.Name.LocalName == "BrowseFlag").Value);
            XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            var item = objectId == "0"
                ? new XElement(didl + "container", new XAttribute("id", "recordings&folder"), new XAttribute("parentID", "0"), new XElement(dc + "title", "Recordings"))
                : new XElement(didl + "item", new XAttribute("id", "video1"), new XAttribute("parentID", objectId), new XAttribute("restricted", "1"),
                    new XElement(dc + "title", "Family & travel 録画"), new XElement(didl + "res", new XAttribute("protocolInfo", $"http-get:*:{_mime}:DLNA.ORG_CI=0"),
                        new XAttribute("size", _media.Length), new XAttribute("duration", "0:00:02.000"), new Uri(BaseUri, "recording").AbsoluteUri));
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace service = "urn:schemas-upnp-org:service:ContentDirectory:1";
            return new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(service + "BrowseResponse",
                new XElement("Result", new XElement(didl + "DIDL-Lite", item).ToString(SaveOptions.DisableFormatting)),
                new XElement("NumberReturned", 1), new XElement("TotalMatches", 1), new XElement("UpdateID", 1)))).ToString(SaveOptions.DisableFormatting);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _serve.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _stop.Dispose(); }
        }
    }
}
