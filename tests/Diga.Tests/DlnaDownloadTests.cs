using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Diga.Core.Dlna;

namespace Diga.Tests;

public sealed class DlnaDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaDlnaDownloadTests-" + Guid.NewGuid().ToString("N"));
    public DlnaDownloadTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private string Destination(string name = "recording.ts") => Path.Combine(_directory, name);
    private static byte[] RecordingBytes(int count) { var bytes = new byte[count]; new Random(731).NextBytes(bytes); bytes[0] = 0x47; return bytes; }

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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(DlnaProtocolInfo.Parse(server.Uri, protocol), Destination())));
        Assert.Equal(0, server.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task ExplicitProtectionFlagCannotBeOverriddenBySafeProtocolText()
    {
        using var service = new DlnaDownloadService();
        var resource = new DlnaResource(new Uri("http://127.0.0.1:9/video"), "http-get:*:video/mp2t:DLNA.ORG_CI=0", null, null, true, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(resource, Destination())));
    }

    [Theory]
    [InlineData("text/html", "<html><form>Login</form></html>")]
    [InlineData("application/xml", "<?xml version=\"1.0\"?><error>Login</error>")]
    [InlineData("application/octet-stream", "  \r\n<!DOCTYPE html><html>Login</html>")]
    [InlineData("video/mp2t", "<?xml version=\"1.0\"?><error/>")]
    public async Task Http200DocumentsAreNotSavedAsRecordings(string mime, string document)
    {
        var bytes = Encoding.UTF8.GetBytes(document);
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, $"Content-Type: {mime}\r\nContent-Length: {bytes.Length}\r\n", ct);
            // Small writes ensure the sniffing prefix is assembled across reads.
            foreach (var value in bytes) await stream.WriteAsync(new[] { value }, ct);
        });
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Equal(1, server.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task UnknownLengthEmptyResponseFails()
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "", ct));
        using var service = new DlnaDownloadService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(15000)]
    public async Task EarlyEofRemovesIncompleteOutput(int bytesSent)
    {
        await using var server = new DlnaDownloadHttpFixture(async (_, stream, ct) =>
        {
            await DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Length: 100000\r\n", ct);
            await stream.WriteAsync(RecordingBytes(bytesSent), ct);
        });
        using var service = new DlnaDownloadService();
        await Assert.ThrowsAnyAsync<IOException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task AdvertisedAndHttpLengthMismatchFailsBeforeOutput()
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 200, "Content-Length: 20000\r\n", ct));
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(new(server.Uri, Destination())));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverRequestedOrOverwritten()
    {
        var existing = RecordingBytes(59);
        await File.WriteAllBytesAsync(Destination(), existing);
        await using var server = new DlnaDownloadHttpFixture((_, _, _) => throw new InvalidOperationException("Must not download."));
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(start.Uri, Destination())));
        Assert.Equal(0, target.RequestCount);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task FourthRedirectIsRejected()
    {
        await using var server = new DlnaDownloadHttpFixture((_, stream, ct) => DlnaDownloadHttpFixture.HeadersAsync(stream, 302, "Location: /again\r\nContent-Length: 0\r\n", ct));
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService();
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
        using var service = new DlnaDownloadService(TimeSpan.FromSeconds(5));
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
