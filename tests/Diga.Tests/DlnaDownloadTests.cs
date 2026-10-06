using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Diga.Core.Dlna;
using Diga.Core.Files;
using Diga.Core.Localization;

namespace Diga.Tests;

public sealed class DlnaDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaDlnaDownloadTests-" + Guid.NewGuid().ToString("N"));
    public DlnaDownloadTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private string Destination(string name = "recording.ts") => Path.Combine(_directory, name);
    private static byte[] RecordingBytes(int count) { var bytes = new byte[count]; new Random(731).NextBytes(bytes); bytes[0] = 0x47; return bytes; }
    // These tests are not about how much is free on the PC that runs them; the ones about free space give their own number.
    private static DlnaDownloadService Service(TimeSpan? inactivityTimeout = null) => new(inactivityTimeout, _ => long.MaxValue);

    [Fact]
    public async Task CompleteDownloadPreservesEveryByteAndHashesWithoutAuthenticationOrEncoding()
    {
        var bytes = RecordingBytes(350_017);
        await using var server = new DlnaDownloadHttpFixture(async (request, stream, ct) =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("1", request.Headers["getcontentFeatures.dlna.org"]);
            Assert.Equal("Streaming", request.Headers["transferMode.dlna.org"]);
            Assert.Equal("identity", request.Headers["Accept-Encoding"]);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.False(request.Headers.ContainsKey("Proxy-Authorization"));
            Assert.False(request.Headers.ContainsKey("Cookie"));
            Assert.False(request.Headers.ContainsKey("Range"));
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200,
                $"Content-Length: {bytes.Length}\r\nContent-Type: video/mp2t\r\ncontentFeatures.dlna.org: DLNA.ORG_CI=0\r\nContent-Disposition: attachment; filename=\"../../untrusted.exe\"\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        var updates = new ConcurrentQueue<DlnaDownloadProgress>();
        var result = await service.DownloadAsync(new(server.Uri, Destination(), DlnaProtocolInfo.Parse(server.Uri, "http-get:*:video/mp2t:DLNA.ORG_CI=0", bytes.Length)), new DlnaDownloadProgressSink(updates.Enqueue));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.DestinationPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(bytes.Length, result.BytesWritten);
        Assert.Equal(bytes.Length, result.ContentLength);
        Assert.Equal(bytes.Length, result.AdvertisedSizeBytes);
        Assert.Equal(server.Uri, result.FinalUri);
        Assert.Equal("video/mp2t", result.ContentType);
        Assert.False(result.MetadataUnknown);
        Assert.True(result.LengthVerified);
        Assert.Equal(1d, updates.Last().Fraction);
        Assert.Equal([Destination()], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UnknownChunkedLengthReportsBytesWithoutInventingPercentageOrVerification()
    {
        var bytes = RecordingBytes(9007);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Transfer-Encoding: chunked\r\nContent-Type: application/octet-stream\r\n", ct);
            await DlnaDownloadHttpFixture.ChunkAsync(stream, bytes.AsMemory(0, 7), ct);
            await DlnaDownloadHttpFixture.ChunkAsync(stream, bytes.AsMemory(7), ct);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
        });
        using var service = Service();
        var updates = new ConcurrentQueue<DlnaDownloadProgress>();
        var result = await service.DownloadAsync(new(server.Uri, Destination()), new DlnaDownloadProgressSink(updates.Enqueue));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.DestinationPath));
        Assert.True(result.MetadataUnknown);
        Assert.False(result.LengthVerified);
        Assert.Null(result.ContentLength);
        Assert.All(updates, update => { Assert.Null(update.TotalBytes); Assert.Null(update.Fraction); });
        Assert.Equal(bytes.Length, updates.Last().BytesReceived);
    }

    [Fact]
    public async Task AdvertisedSizeAloneIsVerifiedForConnectionDelimitedBody()
    {
        var bytes = RecordingBytes(8500);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Type: video/mp2t\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        var resource = DlnaProtocolInfo.Parse(server.Uri, "http-get:*:video/mp2t:DLNA.ORG_CI=0", bytes.Length);
        var result = await service.DownloadAsync(new(resource, Destination()));
        Assert.Null(result.ContentLength);
        Assert.Equal(bytes.Length, result.AdvertisedSizeBytes);
        Assert.True(result.LengthVerified);
        Assert.False(result.MetadataUnknown);
    }

    [Theory]
    [InlineData("Content-Type: application/x-dtcp1\r\n")]
    [InlineData("DTCP1HOST: 127.0.0.1\r\n")]
    [InlineData("contentFeatures.dlna.org: DLNA.ORG_PN=DTCP_MPEG_TS\r\n")]
    [InlineData("contentProtection.dlna.org: DTCP1\r\n")]
    [InlineData("contentFeatures.dlna.org: DLNA.ORG_CI=1\r\n")]
    [InlineData("contentFeatures.dlna.org: DLNA.ORG_CI=0;DLNA.ORG_CI=1\r\n")]
    [InlineData("contentFeatures.dlna.org: DLNA.ORG_CI=unknown\r\n")]
    [InlineData("Content-Encoding: gzip\r\n")]
    public async Task ProtectionConversionAndEncodingHeadersFailBeforeCreatingDestination(string headers)
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, headers + "Content-Length: 4\r\n", ct);
            await stream.WriteAsync(new byte[] { 0x47, 1, 2, 3 }, ct);
        });
        using var service = Service();
        var resource = DlnaProtocolInfo.Parse(server.Uri, "http-get:*:video/mp2t:DLNA.ORG_CI=0", 4);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(resource, Destination("new-folder/output.ts"))));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData("http-get:*:application/x-dtcp1:*")]
    [InlineData("http-get:*:video/mp2t:DLNA.ORG_CI=1")]
    [InlineData("http-get:*:video/mp2t:DLNA.ORG_CI=invalid")]
    public async Task AdvertisedUnsafeResourceIsRejectedWithoutNetwork(string protocol)
    {
        await using var server = new DlnaDownloadHttpFixture((_, _, _) => throw new InvalidOperationException("No request expected."));
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(DlnaProtocolInfo.Parse(server.Uri, protocol), Destination())));
        Assert.Equal(0, server.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task ExplicitProtectionFlagCannotBeOverriddenBySafeProtocolText()
    {
        using var service = Service();
        var resource = new DlnaResource(new Uri("http://127.0.0.1:9/video"), "http-get:*:video/mp2t:DLNA.ORG_CI=0", null, null, true, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(resource, Destination())));
    }

    [Theory]
    [InlineData("text/html", "<html><form>Login</form></html>")]
    [InlineData("application/xml", "<?xml version=\"1.0\"?><error>Login</error>")]
    [InlineData("application/octet-stream", "  \r\n<!DOCTYPE html><html>Login</html>")]
    [InlineData("video/mp2t", "<?xml version=\"1.0\"?><error/>")]
    // FFmpeg reads these two as lists of other files to open, whatever the name and the Content-Type say.
    [InlineData("video/mp2t", "ffconcat version 1.0\nfile '2026-0142.ts'\n")]
    [InlineData("video/mpeg", " \r\nFFCONCAT version 1.0\nfile 'neighbour.mpg'\n")]
    [InlineData("application/octet-stream", "#EXTM3U\n#EXTINF:10,\nhttp://192.168.1.8/segment.ts\n")]
    [InlineData("video/vnd.dlna.mpeg-tts", "#extm3u\n2026-0142.ts\n")]
    public async Task Http200DocumentsAreNotSavedAsRecordings(string mime, string document)
    {
        var bytes = Encoding.UTF8.GetBytes(document);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Type: {mime}\r\nContent-Length: {bytes.Length}\r\n", ct);
            // Small writes ensure the sniffing prefix is assembled across reads.
            foreach (var value in bytes) await stream.WriteAsync(new[] { value }, ct);
        });
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task Utf16XmlWithBinaryMimeIsRejected()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?><login/>")).ToArray();
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task BinaryTimestampBeginningWithLessThanIsNotMistakenForXml()
    {
        var bytes = RecordingBytes(8192);
        bytes[0] = (byte)'<'; bytes[1] = (byte)'M'; bytes[2] = 0; bytes[3] = 0xD2; bytes[4] = 0x47;
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\nContent-Type: video/vnd.dlna.mpeg-tts\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        var result = await service.DownloadAsync(new(server.Uri, Destination()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.DestinationPath));
    }

    [Theory]
    [InlineData(206, "Content-Length: 4\r\nContent-Range: bytes 0-3/9000\r\n")]
    [InlineData(200, "Content-Length: 4\r\nContent-Range: bytes 0-3/9000\r\n")]
    [InlineData(200, "Content-Length: 0\r\n")]
    [InlineData(401, "Content-Length: 0\r\nWWW-Authenticate: Negotiate\r\n")]
    public async Task PartialEmptyAndAuthenticationResponsesAreRejected(int status, string headers)
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, status, headers, ct);
            if (headers.Contains("Content-Length: 4")) await stream.WriteAsync(new byte[] { 0x47, 1, 2, 3 }, ct);
        });
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(1, server.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task UnknownLengthEmptyResponseFails()
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "", ct));
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    // The connection ends while the first bytes are being examined (4000) and after the partial file exists (15000): closed, as
    // by a recorder that goes to standby; cut, as by one that is switched off; and in the middle of a chunk, with no declared size.
    [InlineData(4000, "closed")]
    [InlineData(15000, "closed")]
    [InlineData(4000, "reset")]
    [InlineData(15000, "reset")]
    [InlineData(4000, "chunk")]
    [InlineData(15000, "chunk")]
    public async Task ABrokenConnectionIsReportedInTheApplicationsWordsAndLeavesNoFile(int bytesSent, string ending)
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, ending == "chunk" ? "Transfer-Encoding: chunked\r\n" : "Content-Length: 100000\r\n", ct);
            if (ending == "chunk") await stream.WriteAsync(Encoding.ASCII.GetBytes("186a0\r\n"), ct);
            await stream.WriteAsync(RecordingBytes(bytesSent), ct);
            if (ending != "reset") return;
            // Time for the bytes to arrive, then no orderly close: the other side sees the connection reset.
            await Task.Delay(300, ct);
            stream.Socket.Close(0);
        });
        using var service = Service();
        var error = await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(AppText.T("Core.Dlna.Download.ConnectionLost"), error.Message);
        // What .NET said is kept for the log. The error itself carries no Windows code: it is not a storage failure.
        Assert.True(error.InnerException is IOException or HttpRequestException, error.InnerException?.ToString());
        Assert.NotEqual(error.Message, error.InnerException!.Message);
        Assert.Equal(unchecked((int)0x80131620), error.HResult);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task APartialFileThatCannotBeDeletedDoesNotTakeThePlaceOfTheFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Length: 9999999\r\n", ct);
            await stream.WriteAsync(RecordingBytes(8192), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var service = Service(TimeSpan.FromMilliseconds(300));
        string? partial = null;
        try
        {
            // Made read-only, which Windows refuses to delete just as it refuses a file that a virus scanner holds open. The test
            // cannot hold the file open itself: the download has it to itself while it writes.
            var error = await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(new(server.Uri, Destination()), new DlnaDownloadProgressSink(_ =>
            {
                if (partial is not null) return;
                partial = Assert.Single(Directory.GetFiles(_directory, "*.partial"));
                File.SetAttributes(partial, FileAttributes.ReadOnly);
            })));
            Assert.Equal(AppText.T("Core.Dlna.Download.InactivityTimeout"), error.Message);
            // Left behind under a name the sweep of working files knows, and nothing under the real name.
            Assert.True(File.Exists(partial));
            Assert.True(WorkingFiles.IsPartial(Path.GetFileName(partial!)));
            Assert.False(File.Exists(Destination()));
        }
        finally { if (partial is not null && File.Exists(partial)) File.SetAttributes(partial, FileAttributes.Normal); }
    }

    [Fact]
    public async Task AdvertisedAndHttpLengthMismatchFailsBeforeOutput()
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Length: 20000\r\n", ct));
        using var service = Service();
        var resource = DlnaProtocolInfo.Parse(server.Uri, "http-get:*:video/mp2t:*", 20001);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(resource, Destination("new-folder/video.ts"))));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData(4999)]
    [InlineData(5001)]
    public async Task AdvertisedSizeMustMatchWhenHttpSizeIsUnknown(int advertised)
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "", ct);
            await stream.WriteAsync(RecordingBytes(5000), ct);
        });
        using var service = Service();
        var resource = DlnaProtocolInfo.Parse(server.Uri, "http-get:*:video/mp2t:*", advertised);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(resource, Destination())));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("Content-Length: 100\r\nContent-Length: 101\r\n")]
    [InlineData("Content-Length: 100\r\nTransfer-Encoding: chunked\r\n")]
    [InlineData("Content-Length: -1\r\n")]
    public async Task InvalidHttpFramingFailsWithoutOutput(string headers)
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 200, headers, ct));
        using var service = Service();
        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancelAfterPartialFileExistsRemovesItAndPreservesCallerCancellation()
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Length: 5368709120\r\n", ct);
            await stream.WriteAsync(RecordingBytes(8192), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var service = Service();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = service.DownloadAsync(new(server.Uri, Destination()), new DlnaDownloadProgressSink(progress =>
        {
            Assert.Equal(5L * 1024 * 1024 * 1024, progress.TotalBytes);
            started.TrySetResult();
        }), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.GetFiles(_directory, "*.partial"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeclaredSizeThatDoesNotFitIsRefusedBeforeAnythingIsWritten(bool declaredByHttp)
    {
        // The size comes from Content-Length, or, without one, from the recorder's catalogue.
        const long size = 5L * 1024 * 1024 * 1024;
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, declaredByHttp ? $"Content-Length: {size}\r\n" : "", ct);
            await stream.WriteAsync(RecordingBytes(8192), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var asked = new List<string>();
        using var service = new DlnaDownloadService(freeSpace: folder => { asked.Add(folder); return 2L * 1024 * 1024 * 1024; });
        var destination = Destination(Path.Combine("new-folder", "output.ts"));
        var request = new DlnaDownloadRequest(server.Uri, destination, declaredByHttp ? null : DlnaProtocolInfo.Parse(server.Uri, "http-get:*:video/mp2t:*", size));
        var error = await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(request));
        Assert.Contains(Path.Combine(_directory, "new-folder"), error.Message, StringComparison.Ordinal);
        Assert.Matches("5[.,]0 GB", error.Message);
        Assert.Matches("2[.,]0 GB", error.Message);
        Assert.DoesNotContain("Core.Dlna", error.Message, StringComparison.Ordinal);
        Assert.Equal([Path.Combine(_directory, "new-folder")], asked);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task TheReserveStaysFreeAfterARecordingOfDeclaredSize(int beyondReserve, bool fits)
    {
        var bytes = RecordingBytes(10_000);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = new DlnaDownloadService(freeSpace: folder => bytes.Length + DiskSpace.Reserve(folder) + beyondReserve);
        if (fits) Assert.Equal(bytes, await File.ReadAllBytesAsync((await service.DownloadAsync(new(server.Uri, Destination()))).DestinationPath));
        else await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(fits ? 1 : 0, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task RecordingOfUnknownSizeIsStoppedWhenTheFreeSpaceRunsLow()
    {
        var block = RecordingBytes(1024 * 1024);
        long sent = 0;
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Transfer-Encoding: chunked\r\nContent-Type: video/mp2t\r\n", ct);
            // A stream without a declared size and without an end: only the receiver can stop it.
            while (true) { await DlnaDownloadHttpFixture.ChunkAsync(stream, block, ct); Interlocked.Add(ref sent, block.Length); }
        });
        var looks = 0;
        // Plenty of room when the download starts; none to spare at the first look after that.
        using var service = new DlnaDownloadService(freeSpace: folder => Interlocked.Increment(ref looks) == 1 ? long.MaxValue : DiskSpace.Reserve(folder) + DlnaDownloadService.FreeSpaceCheckInterval - 1);
        long received = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination()), new DlnaDownloadProgressSink(progress => received = progress.BytesReceived)));
        Assert.Equal(2, looks);
        // The second look comes once 64 MB are written, not before and not much later.
        Assert.True(Interlocked.Read(ref sent) >= DlnaDownloadService.FreeSpaceCheckInterval - block.Length, $"Stopped after {sent} bytes were sent.");
        Assert.InRange(received, 1, DlnaDownloadService.FreeSpaceCheckInterval + 128 * 1024);
        Assert.Contains(_directory, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Dlna", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RecordingOfUnknownSizeIsNotStartedOnADriveThatIsNearlyFull()
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "", ct);
            await stream.WriteAsync(RecordingBytes(8192), ct);
        });
        using var service = new DlnaDownloadService(freeSpace: folder => DiskSpace.Reserve(folder) + 1024 * 1024);
        var error = await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination(Path.Combine("new-folder", "output.ts")))));
        Assert.Contains(Path.Combine(_directory, "new-folder"), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Dlna", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADriveThatDoesNotSayHowMuchIsFreeDoesNotStopADownload(bool declared)
    {
        // A network share that cannot be asked, for instance: writing itself fails when such a drive is full.
        var bytes = RecordingBytes(9000);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, declared ? $"Content-Length: {bytes.Length}\r\n" : "", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = new DlnaDownloadService(freeSpace: _ => null);
        var result = await service.DownloadAsync(new(server.Uri, Destination()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.DestinationPath));
    }

    [Fact]
    public async Task WithoutAGivenNumberTheFreeSpaceOfTheDestinationDriveDecides()
    {
        var bytes = RecordingBytes(9000);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = new DlnaDownloadService();
        // True on any PC whose temporary folder is not on a drive that is all but full; the test holds either way.
        var fits = DiskSpace.Available(_directory) is not { } free || free - DiskSpace.Reserve(_directory) >= bytes.Length;
        if (fits) Assert.Equal(bytes, await File.ReadAllBytesAsync((await service.DownloadAsync(new(server.Uri, Destination()))).DestinationPath));
        else await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(fits ? 1 : 0, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void FreeSpaceIsReadForFoldersThatExistOrWillAndIsUnknownRatherThanAnError()
    {
        Assert.True(Assert.NotNull(DiskSpace.Available(_directory)) >= 0);
        // The folder a download is about to create is on the drive of the nearest folder that exists.
        Assert.NotNull(DiskSpace.Available(Path.Combine(_directory, "not", "created", "yet")));
        if (!OperatingSystem.IsWindows()) return;
        // A share that does not exist: no answer, and no exception.
        Assert.Null(DiskSpace.Available(@"\\127.0.0.1\diga-test-no-such-share\folder"));
    }

    [Fact]
    public void TheWindowsDriveKeepsItsLargerReserveHoweverItsFolderIsWritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        const long windowsDrive = 1024L * 1024 * 1024, elsewhere = 64L * 1024 * 1024;
        var folder = Environment.SystemDirectory;
        var root = Path.GetPathRoot(folder)!;
        Assert.Equal(windowsDrive, DiskSpace.Reserve(folder));
        Assert.Equal(windowsDrive, DiskSpace.Reserve(folder.ToUpperInvariant()));
        Assert.Equal(windowsDrive, DiskSpace.Reserve(Path.Combine(folder, "not", "created", "yet")));
        // The forms that the comparison of two texts took for another drive, which left 64 MB of the Windows drive free.
        Assert.Equal(windowsDrive, DiskSpace.Reserve(@"\\?\" + folder));
        Assert.Equal(windowsDrive, DiskSpace.Reserve(@"\\.\" + folder));
        // By the name of its volume (\\?\Volume{...}\Windows\System32) no text says which drive it is; only Windows does.
        var volume = new char[64];
        Assert.True(GetVolumeNameForVolumeMountPointW(root, volume, (uint)volume.Length));
        var byVolumeName = new string(volume, 0, Array.IndexOf(volume, '\0')) + folder[root.Length..];
        Assert.StartsWith(@"\\?\Volume{", byVolumeName, StringComparison.Ordinal);
        Assert.Equal(windowsDrive, DiskSpace.Reserve(byVolumeName));
        // Every other drive of this PC keeps the small reserve, unless it is an exact copy of the Windows drive with its number.
        Assert.True(GetVolumeInformationW(root, IntPtr.Zero, 0, out var windowsSerial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0));
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady && !drive.Name.Equals(root, StringComparison.OrdinalIgnoreCase)))
            if (GetVolumeInformationW(drive.Name, IntPtr.Zero, 0, out var serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0) && serial != windowsSerial)
                Assert.Equal(elsewhere, DiskSpace.Reserve(Path.Combine(drive.Name, "Recordings")));
        // A share that does not answer counts as a share, in both ways of writing one, and a drive that does not exist as any drive.
        Assert.Equal(elsewhere, DiskSpace.Reserve(@"\\127.0.0.1\diga-test-no-such-share\folder"));
        Assert.Equal(elsewhere, DiskSpace.Reserve(@"\\?\UNC\127.0.0.1\diga-test-no-such-share\folder"));
        var letters = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        if (Enumerable.Range('D', 23).Select(letter => (char)letter).Where(letter => !letters.Contains(letter)).Select(letter => letter + @":\").FirstOrDefault() is { } missing)
            Assert.Equal(elsewhere, DiskSpace.Reserve(missing + "Recordings"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, IntPtr name, uint nameLength, out uint serial, IntPtr maximumComponentLength, IntPtr flags, IntPtr fileSystem, uint fileSystemLength);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderAndBodyInactivityTimeoutsAreBoundedAndClean(bool sendHeaders)
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            if (sendHeaders)
            {
                await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Length: 9999999\r\n", ct);
                await stream.WriteAsync(RecordingBytes(8192), ct);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var service = Service(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverRequestedOrOverwritten()
    {
        var existing = RecordingBytes(59);
        await File.WriteAllBytesAsync(Destination(), existing);
        await using var server = new DlnaDownloadHttpFixture((_, _, _) => throw new InvalidOperationException("Must not download."));
        using var service = Service();
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(existing, await File.ReadAllBytesAsync(Destination()));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task DestinationCreatedDuringDownloadWinsWithoutBeingOverwritten()
    {
        var existing = "other application owns this file"u8.ToArray();
        var bytes = RecordingBytes(15000);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        var created = false;
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination()), new DlnaDownloadProgressSink(_ =>
        {
            if (!created) { File.WriteAllBytes(Destination(), existing); created = true; }
        })));
        Assert.Equal(existing, await File.ReadAllBytesAsync(Destination()));
        Assert.Equal([Destination()], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailingCompletionObserverCannotHideAnAlreadyPublishedFile()
    {
        var bytes = RecordingBytes(5000);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(new(server.Uri, Destination()), new DlnaDownloadProgressSink(progress =>
        {
            if (progress.BytesReceived == bytes.Length) throw new InvalidOperationException("Observer failed.");
        })));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RedirectMayChangePortOnSameRecorderWithoutForwardingCookies()
    {
        var bytes = RecordingBytes(6000);
        await using var target = new DlnaDownloadHttpFixture(async (request, stream, ct) =>
        {
            Assert.False(request.Headers.ContainsKey("Cookie"));
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        await using var start = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 302,
            $"Location: {target.Uri}\r\nSet-Cookie: token=secret\r\nContent-Length: 0\r\n", ct));
        using var service = Service();
        var result = await service.DownloadAsync(new(start.Uri, Destination()));
        Assert.Equal(target.Uri, result.FinalUri);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Destination()));
    }

    [Fact]
    public async Task DifferentHostRedirectIsNotFollowedEvenIfBothHostsAreLoopback()
    {
        await using var target = new DlnaDownloadHttpFixture((_, _, _) => throw new InvalidOperationException("Redirect must not be followed."));
        await using var start = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 302,
            $"Location: http://localhost:{target.Uri.Port}/video\r\nContent-Length: 0\r\n", ct));
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(start.Uri, Destination())));
        Assert.Equal(0, target.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task FourthRedirectIsRejected()
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 302, "Location: /again\r\nContent-Length: 0\r\n", ct));
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(4, server.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/video")]
    [InlineData("http://name:secret@127.0.0.1/video")]
    [InlineData("http://127.0.0.1/video#fragment")]
    [InlineData("http://8.8.8.8/video")]
    [InlineData("http://[2001:4860:4860::8888]/video")]
    public async Task UnsafeUrlsAreRejectedWithoutNetwork(string uri)
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadAsync(new(new Uri(uri), Destination())));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task LargeResponseUsesBoundedChunksWithExactOutput()
    {
        var block = RecordingBytes(64 * 1024);
        const int repetitions = 513;
        var length = (long)block.Length * repetitions;
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < repetitions; i++) expectedHash.AppendData(block);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {length}\r\n", ct);
            for (var i = 0; i < repetitions; i++) await stream.WriteAsync(block, ct);
        });
        using var service = Service();
        var result = await service.DownloadAsync(new(server.Uri, Destination()));
        Assert.Equal(length, result.BytesWritten);
        Assert.Equal(Convert.ToHexString(expectedHash.GetHashAndReset()).ToLowerInvariant(), result.Sha256);
        await using var file = File.OpenRead(Destination());
        var actual = new byte[block.Length];
        for (var i = 0; i < repetitions; i++) { await file.ReadExactlyAsync(actual); Assert.Equal(block, actual); }
        Assert.Equal(-1, file.ReadByte());
    }

    [Fact]
    public async Task HttpsDoesNotDisableCertificateValidation()
    {
        await using var server = new DlnaDownloadTlsFixture();
        using var service = Service(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(HttpRequestError.SecureConnectionError, failure.HttpRequestError);
        Assert.True(failure.InnerException is System.Security.Authentication.AuthenticationException, failure + "\nFixture TLS failure: " + server.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData("http://127.0.0.1/video.exe", null, ".bin")]
    [InlineData("http://127.0.0.1/video.M2TS?key=foo", null, ".m2ts")]
    [InlineData("http://127.0.0.1/get/123", "video/mp2t", ".ts")]
    [InlineData("http://127.0.0.1/get/123", "video/vnd.dlna.mpeg-tts", ".m2ts")]
    public void ExtensionGuessUsesOnlyAllowlistedMediaExtensions(string uri, string? mime, string expected) =>
        Assert.Equal(expected, DlnaDownloadService.GuessSafeExtension(new Uri(uri), mime));

    /// <summary>The first bytes of a file in one of the containers a recorder delivers, or of something else.</summary>
    private static byte[] Head(string kind)
    {
        var bytes = new byte[4096];
        new Random(97).NextBytes(bytes);
        // No chance byte of the filling may look like the start of a packet.
        for (var index = 0; index < bytes.Length; index++) if (bytes[index] == 0x47) bytes[index] = 0x48;
        switch (kind)
        {
            case "ts": for (var offset = 0; offset < bytes.Length; offset += 188) bytes[offset] = 0x47; break;
            case "m2ts": for (var offset = 4; offset < bytes.Length; offset += 192) bytes[offset] = 0x47; break;
            case "four packets": for (var offset = 0; offset < 4 * 188; offset += 188) bytes[offset] = 0x47; break;
            case "ps": new byte[] { 0, 0, 1, 0xBA, 0x44 }.CopyTo(bytes, 0); break;
            case "mp4": new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }.CopyTo(bytes, 0); break;
            case "mkv": new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(bytes, 0); break;
            case "text": Encoding.ASCII.GetBytes("Gossip: not a recording at all.").CopyTo(bytes, 0); break;
        }
        return bytes;
    }

    [Theory]
    [InlineData("ts", ".ts")]
    [InlineData("m2ts", ".m2ts")]
    [InlineData("ps", ".mpg")]
    [InlineData("mp4", ".mp4")]
    [InlineData("mkv", ".mkv")]
    // One 'G' at the start, or four packet starts, is not yet a transport stream.
    [InlineData("text", null)]
    [InlineData("four packets", null)]
    [InlineData("noise", null)]
    public void TheContainerOfARecordingIsToldFromItsFirstBytes(string kind, string? expected)
    {
        var head = Head(kind);
        Assert.Equal(expected, DlnaDownloadService.ExtensionFromContent(head));
        // No more than the stated number of bytes is needed, and a file is read like the bytes.
        Assert.Equal(expected, DlnaDownloadService.ExtensionFromContent(head.AsSpan(0, DlnaDownloadService.ContentHeadLength)));
        File.WriteAllBytes(Destination("content.bin"), head);
        Assert.Equal(expected, DlnaDownloadService.ExtensionFromContent(Destination("content.bin")));
    }

    [Fact]
    public void TooFewBytesToTellGiveNoExtension()
    {
        Assert.Null(DlnaDownloadService.ExtensionFromContent([]));
        Assert.Null(DlnaDownloadService.ExtensionFromContent([0x47]));
        Assert.Null(DlnaDownloadService.ExtensionFromContent(Head("ts").AsSpan(0, 4 * 188)));
        Assert.Equal(".ts", DlnaDownloadService.ExtensionFromContent(Head("ts").AsSpan(0, 4 * 188 + 1)));
        Assert.Null(DlnaDownloadService.ExtensionFromContent(Head("m2ts").AsSpan(0, 4 + 4 * 192)));
        Assert.Equal(".m2ts", DlnaDownloadService.ExtensionFromContent(Head("m2ts").AsSpan(0, 4 + 4 * 192 + 1)));
        Assert.Null(DlnaDownloadService.ExtensionFromContent([0, 0, 1]));
        Assert.Null(DlnaDownloadService.ExtensionFromContent([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y']));
        File.WriteAllBytes(Destination("empty.bin"), []);
        Assert.Null(DlnaDownloadService.ExtensionFromContent(Destination("empty.bin")));
    }

    [Theory]
    [InlineData("m2ts", ".m2ts")]
    [InlineData("ps", ".mpg")]
    [InlineData("noise", null)]
    public async Task ADownloadSaysWhichContainerItReceivedWhenTheRecorderNamesAnUnknownMediaType(string kind, string? expected)
    {
        var bytes = Head(kind);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Length: {bytes.Length}\r\nContent-Type: video/x-panasonic-own\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        using var service = Service();
        // The name and the media type say nothing, so the file gets the extension nothing opens.
        Assert.Equal(".bin", DlnaDownloadService.GuessSafeExtension(server.Uri, "video/x-panasonic-own"));
        var result = await service.DownloadAsync(new(server.Uri, Destination("recording.bin")));
        Assert.Equal(expected, result.ContentExtension);
        // The file itself is where the caller wanted it; renaming is the caller's decision.
        Assert.Equal(Destination("recording.bin"), result.DestinationPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.DestinationPath));
    }
}

internal sealed class DlnaDownloadProgressSink(Action<DlnaDownloadProgress> report) : IProgress<DlnaDownloadProgress>
{
    public void Report(DlnaDownloadProgress value) => report(value);
}

internal sealed record DlnaDownloadHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

/// <summary>Raw loopback HTTP responses exercise the actual HTTP parser, TCP truncation, streaming and cancellation. Never binds a LAN interface.</summary>
internal sealed class DlnaDownloadHttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly ConcurrentQueue<Exception> _errors = [];
    private readonly Task _accept;
    private int _requestCount;
    public Uri Uri { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    public DlnaDownloadHttpFixture(Func<DlnaDownloadHttpRequest, NetworkStream, CancellationToken, Task> handler)
    {
        _listener.Start();
        Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/video");
        _accept = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(HandleAsync(client, handler));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            // Stopping the listener can also end a pending accept with ObjectDisposedException; seen on hosted runners.
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        });
    }

    private async Task HandleAsync(TcpClient client, Func<DlnaDownloadHttpRequest, NetworkStream, CancellationToken, Task> handler)
    {
        using (client)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("Missing request.");
                var parts = line.Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var bytes = line.Length;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                {
                    bytes += line.Length;
                    if (bytes > 32 * 1024) throw new InvalidDataException("Test request headers exceeded limit.");
                    var colon = line.IndexOf(':');
                    headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                Interlocked.Increment(ref _requestCount);
                await handler(new(parts[0], parts[1], headers), client.GetStream(), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { } // Closing a rejected/cancelled response may interrupt the fixture writer.
            catch (SocketException) { }
            catch (Exception ex) { _errors.Enqueue(ex); }
        }
    }

    public static async Task HeadersAsync(Stream stream, int status, string headers, CancellationToken ct) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nConnection: close\r\n{headers}\r\n"), ct);

    public static async Task ChunkAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:X}\r\n"), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
        if (!_errors.IsEmpty) throw new AggregateException(_errors);
    }
}

internal sealed class DlnaDownloadTlsFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly Task _serve;
    public Uri Uri { get; }
    public Exception? Failure { get; private set; }

    public DlnaDownloadTlsFixture()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using (var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)))
        {
            // Windows Schannel cannot serve the ephemeral key returned by CreateSelfSigned.
            // Import a temporary user key (no PersistKeySet and no certificate-store install);
            // disposing the fixture certificate removes the imported key container.
            var pfx = generated.Export(X509ContentType.Pfx);
            try { _certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        _listener.Start();
        Uri = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/video");
        _serve = Task.Run(async () =>
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                // If a regression accepts this untrusted certificate, return a valid recording so
                // the test cannot accidentally pass because of a later HTTP EOF instead of TLS.
                using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                var characters = 0;
                while (true)
                {
                    var line = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("No HTTP request after TLS handshake.");
                    if (line.Length == 0) break;
                    characters += line.Length;
                    if (characters > 32 * 1024) throw new IOException("Fixture HTTP headers exceeded their limit.");
                }
                var body = new byte[8192];
                body[0] = 0x47;
                await DlnaDownloadHttpFixture.HeadersAsync(tls, 200, $"Content-Type: video/mp2t\r\nContent-Length: {body.Length}\r\n", _stop.Token);
                await tls.WriteAsync(body, _stop.Token);
            }
            catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException or SocketException or ObjectDisposedException) { Failure = ex; }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _serve; _certificate.Dispose(); _stop.Dispose();
    }
}
