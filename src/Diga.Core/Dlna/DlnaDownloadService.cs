using L = Diga.Core.Localization.AppText;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Diga.Core.Files;

namespace Diga.Core.Dlna;

public sealed record DlnaDownloadRequest(Uri SourceUri, string DestinationPath, DlnaResource? Resource = null)
{
    public DlnaDownloadRequest(DlnaResource resource, string destinationPath) : this(resource.Uri, destinationPath, resource) { }
}

public sealed record DlnaDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <param name="MetadataUnknown">True if neither advertised nor HTTP metadata explicitly reports nonconverted delivery (CI=0). A false value is a server claim, not proof of equality with the recorder's disk.</param>
/// <param name="LengthVerified">True if the received byte count matched at least one declared size. SHA-256 describes the locally received bytes, not a recorder-provided checksum.</param>
public sealed record DlnaDownloadResult(string DestinationPath, Uri FinalUri, long BytesWritten, string Sha256,
    long? ContentLength, long? AdvertisedSizeBytes, string? ContentType, string? ContentFeatures,
    bool MetadataUnknown, bool LengthVerified)
{
    /// <summary>
    /// The extension for the container the received bytes are in (<see cref="DlnaDownloadService.ExtensionFromContent(ReadOnlySpan{byte})"/>),
    /// or null when they begin like none that is known. The file is saved under the name the caller chose, whatever this says.
    /// </summary>
    public string? ContentExtension { get; init; }
}

/// <summary>Downloads the recorder's HTTP representation unchanged. No decoding, decompression, authentication, resume or media conversion is performed.</summary>
public sealed class DlnaDownloadService : IDisposable
{
    private const int BufferSize = 128 * 1024;
    /// <summary>How much of a recording of unknown size is written between two looks at the free space.</summary>
    public const long FreeSpaceCheckInterval = 64L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _inactivityTimeout;
    private readonly Func<string, long?> _freeSpace;

    /// <param name="freeSpace">
    /// Says how many bytes can still be written in a folder, or null when that cannot be told. The default is
    /// <see cref="DiskSpace.Available"/>; tests give their own.
    /// </param>
    public DlnaDownloadService(TimeSpan? inactivityTimeout = null, Func<string, long?>? freeSpace = null)
    {
        _freeSpace = freeSpace ?? DiskSpace.Available;
        _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromSeconds(30);
        if (_inactivityTimeout <= TimeSpan.Zero || _inactivityTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false, UseCookies = false, Credentials = null, PreAuthenticate = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 64,
            MaxConnectionsPerServer = 2, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = EndpointPolicy.ConnectLocalAsync
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<DlnaDownloadResult> DownloadAsync(DlnaDownloadRequest request,
        IProgress<DlnaDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUri(request.SourceUri);
        var destination = ValidateDestination(request.DestinationPath);
        var advertised = ValidateResource(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await GetResponseAsync(request.SourceUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException(response.StatusCode == HttpStatusCode.PartialContent
                ? L.T("Core.Dlna.Download.PartialResponse")
                : L.T("Core.Dlna.Download.HttpError", (int)response.StatusCode));
        if (response.Content.Headers.Contains("Content-Range")) throw new InvalidDataException(L.T("Core.Dlna.Download.UnexpectedRange"));
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L.T("Core.Dlna.Download.ContentEncoding"));
        var mime = response.Content.Headers.ContentType?.MediaType;
        RejectDocumentMime(mime);
        RejectProtectionHeaders(response);
        var features = Header(response, "contentFeatures.dlna.org");
        var responseConversion = ValidateConversion(features);
        var httpResource = DlnaProtocolInfo.Parse(request.SourceUri, $"http-get:*:{mime ?? "application/octet-stream"}:{features ?? "*"}");
        if (httpResource.IsProtected || httpResource.IsConverted == true)
            throw new InvalidDataException(L.T("Core.Dlna.Download.ResponseProtectedOrConverted"));
        var length = ReadContentLength(response);
        if (length is not null && response.Headers.TransferEncoding.Count != 0)
            throw new InvalidDataException(L.T("Core.Dlna.Download.ConflictingTransferHeaders"));
        if (response.Headers.TransferEncoding.Any(value => !value.Value.Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L.T("Core.Dlna.Download.TransferEncoding"));
        var advertisedSize = request.Resource?.SizeBytes;
        if (length is not null && advertisedSize is not null && length != advertisedSize)
            throw new InvalidDataException(L.T("Core.Dlna.Download.AdvertisedSizeMismatch"));
        if (length == 0) throw new InvalidDataException(L.T("Core.Dlna.Download.Empty"));
        var total = length ?? advertisedSize;
        var knownOriginal = advertised == false || responseConversion == false || httpResource.IsConverted == false;
        var finalUri = response.RequestMessage?.RequestUri ?? request.SourceUri;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // Inspect a bounded prefix before creating anything: some recorders serve an HTTP 200 login page.
        var prefix = new byte[4096];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var count = await ReadAsync(source, prefix.AsMemory(prefixLength), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            prefixLength += count;
        }
        if (prefixLength == 0) throw new InvalidDataException(L.T("Core.Dlna.Download.Empty"));
        RejectDocumentBody(prefix.AsSpan(0, prefixLength));
        if (total is not null && prefixLength > total) throw new InvalidDataException(L.T("Core.Dlna.Download.SizeExceeded"));

        var folder = Path.GetDirectoryName(destination)!;
        // The size is declared before the first byte is written: a recording that cannot fit is refused now, not after an hour.
        // One without a declared size has no end the application could know; the free space is looked at again while it is written.
        RequireFreeSpace(folder, total);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, WorkingFiles.PartialName(Path.GetFileName(destination)));
        var created = false;
        try
        {
            long written = 0, spaceCheckedAt = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                progress?.Report(new(0, total));
                await output.WriteAsync(prefix.AsMemory(0, prefixLength), cancellationToken).ConfigureAwait(false);
                hash.AppendData(prefix, 0, prefixLength);
                written = prefixLength;
                var buffer = new byte[BufferSize];
                var lastReport = Stopwatch.GetTimestamp();
                while (true)
                {
                    var count = await ReadAsync(source, buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    written = checked(written + count);
                    if (total is not null && written > total) throw new InvalidDataException(L.T("Core.Dlna.Download.SizeExceeded"));
                    if (total is null && written - spaceCheckedAt >= FreeSpaceCheckInterval)
                    {
                        spaceCheckedAt = written;
                        RequireFreeSpace(folder, null);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(100))
                    {
                        progress?.Report(new(written, total));
                        lastReport = Stopwatch.GetTimestamp();
                    }
                }
                if (length is not null && written != length || advertisedSize is not null && written != advertisedSize)
                    throw new InvalidDataException(L.T("Core.Dlna.Download.Incomplete"));
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                WorkingFiles.FlushToDisk(output);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            // A failing synchronous observer must not hide an already-published completed file.
            progress?.Report(new(written, total));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            created = false;
            return new(destination, finalUri, written, sha256, length, advertisedSize, mime, features, !knownOriginal, total is not null)
            { ContentExtension = ExtensionFromContent(prefix.AsSpan(0, prefixLength)) };
        }
        finally
        {
            // The partial file is still there only when the download has just failed. If it cannot be deleted either, because a
            // virus scanner or an indexer has it open, the user is told why the download failed, not that a file is in use;
            // the sweep of working files (WorkingFiles.Sweep) removes it later.
            if (created) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Refuses to write when the folder's drive would be left with less than its reserve (<see cref="DiskSpace.Reserve"/>): after
    /// <paramref name="size"/> bytes when the size of the recording is known, after the piece written until the next look when it
    /// is not. A drive that does not say how much is free is not refused; writing to it fails by itself when it is full.
    /// </summary>
    private void RequireFreeSpace(string folder, long? size)
    {
        if (_freeSpace(folder) is not { } free) return;
        var reserve = DiskSpace.Reserve(folder);
        if (size is { } known)
        {
            if (free - reserve < known) throw new IOException(L.T("Core.Dlna.Download.SpaceRequired", folder, Size(known), Size(reserve), Size(free)));
            return;
        }
        var floor = reserve + FreeSpaceCheckInterval;
        if (free < floor) throw new IOException(L.T("Core.Dlna.Download.SpaceLow", folder, Size(free), Size(floor)));
    }

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(0, bytes) / (double)(1L << 20):0.0} MB";

    private async Task<HttpResponseMessage> GetResponseAsync(Uri source, CancellationToken cancellationToken)
    {
        var current = source;
        for (var redirects = 0; ; redirects++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            message.Headers.TryAddWithoutValidation("getcontentFeatures.dlna.org", "1");
            message.Headers.TryAddWithoutValidation("transferMode.dlna.org", "Streaming");
            message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            message.Headers.ConnectionClose = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_inactivityTimeout);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException(L.T("Core.Dlna.Download.HeadersTimeout"), ex); }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            try
            {
                if (redirects >= 3) throw new InvalidDataException(L.T("Core.Dlna.Download.RedirectLimit"));
                var location = response.Headers.Location ?? throw new InvalidDataException(L.T("Core.Dlna.Download.RedirectTargetMissing"));
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidateUri(next);
                if (!next.IdnHost.Equals(source.IdnHost, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.T("Core.Dlna.Download.ExternalRedirect"));
                if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException(L.T("Core.Dlna.Download.InsecureRedirect"));
                current = next;
            }
            finally { response.Dispose(); }
        }
    }

    private async ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_inactivityTimeout);
        try { return await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(L.T("Core.Dlna.Download.InactivityTimeout"), ex); }
        // The recorder was switched off or went to standby, or the network dropped. What .NET says about that is English and
        // speaks of sockets and transport connections. Only this read from the recorder is put into the application's words:
        // a failure of the write to the disk, such as a full drive, keeps its own type and code.
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException(L.T("Core.Dlna.Download.ConnectionLost"), ex);
        }
    }

    private static bool? ValidateResource(DlnaDownloadRequest request)
    {
        if (request.Resource is not { } resource) return null;
        if (!resource.Uri.Equals(request.SourceUri)) throw new ArgumentException(L.T("Core.Dlna.Download.MetadataUrlMismatch"), nameof(request));
        if (resource.SizeBytes is <= 0) throw new InvalidDataException(L.T("Core.Dlna.Download.AdvertisedSizeInvalid"));
        var parsed = DlnaProtocolInfo.Parse(resource.Uri, resource.ProtocolInfo, resource.SizeBytes, resource.Duration, resource.Protection);
        var conversion = ValidateConversion(resource.ProtocolInfo.Split(':', 4).LastOrDefault());
        if (resource.IsProtected || parsed.IsProtected || resource.IsConverted == true || parsed.IsConverted == true || conversion == true)
            throw new InvalidDataException(L.T("Core.Dlna.Download.ResourceProtectedOrConverted"));
        return resource.IsConverted == false || parsed.IsConverted == false || conversion == false ? false : null;
    }

    private static bool? ValidateConversion(string? features)
    {
        bool? result = null;
        foreach (var part in (features ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (!pair[0].Equals("DLNA.ORG_CI", StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Length != 2 || pair[1] is not ("0" or "1")) throw new InvalidDataException(L.T("Core.Dlna.Download.ConversionMetadataInvalid"));
            if (pair[1] == "1") throw new InvalidDataException(L.T("Core.Dlna.Download.ConvertedResource"));
            result = false;
        }
        return result;
    }

    private static long? ReadContentLength(HttpResponseMessage response)
    {
        if (!response.Content.Headers.TryGetValues("Content-Length", out var values)) return null;
        long? result = null;
        foreach (var value in values.SelectMany(value => value.Split(',')))
        {
            if (!long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 || result is not null && result != length)
                throw new InvalidDataException(L.T("Core.Dlna.Download.ContentLengthInvalid"));
            result = length;
        }
        return result;
    }

    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values)
        ? string.Join(";", values) : response.Content.Headers.TryGetValues(name, out values) ? string.Join(";", values) : null;

    private static void RejectProtectionHeaders(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (header.Key.Contains("dtcp", StringComparison.OrdinalIgnoreCase)
                || header.Key.Contains("contentprotection", StringComparison.OrdinalIgnoreCase)
                || header.Key.Contains("content-protection", StringComparison.OrdinalIgnoreCase)
                || ((header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("contentFeatures.dlna.org", StringComparison.OrdinalIgnoreCase))
                    && header.Value.Any(value => value.Contains("dtcp", StringComparison.OrdinalIgnoreCase) || value.Contains("drm", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException(L.T("Core.Dlna.Download.ProtectedHeaders"));
        }
    }

    private static void RejectDocumentMime(string? mime)
    {
        if (mime is null) return;
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("json", StringComparison.OrdinalIgnoreCase) || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Core.Dlna.Download.DocumentResponse"));
    }

    private static void RejectDocumentBody(ReadOnlySpan<byte> prefix)
    {
        string text;
        if (prefix.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) text = Encoding.UTF32.GetString(prefix);
        else if (prefix.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) text = new UTF32Encoding(true, true).GetString(prefix);
        else if (prefix.StartsWith(new byte[] { 0xFF, 0xFE }) || prefix.Length > 1 && prefix[0] == '<' && prefix[1] == 0) text = Encoding.Unicode.GetString(prefix);
        else if (prefix.StartsWith(new byte[] { 0xFE, 0xFF }) || prefix.Length > 1 && prefix[0] == 0 && prefix[1] == '<') text = Encoding.BigEndianUnicode.GetString(prefix);
        else text = Encoding.UTF8.GetString(prefix);
        text = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        // FFmpeg takes a file that begins like this for a list of other files to read, whatever its name and Content-Type say.
        // No recording begins with these letters: a transport stream starts with 'G', or has it as its fifth byte.
        if (text.StartsWith("ffconcat", StringComparison.OrdinalIgnoreCase) || text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Core.Dlna.Download.DocumentResponse"));
        // A transport-stream timestamp can legitimately begin with '<'; require text-like markup,
        // not that byte alone, before classifying an otherwise binary response as a document.
        if (text.Length < 2 || text[0] != '<' || !(char.IsLetter(text[1]) || text[1] is '!' or '?' or '_')) return;
        var tagEnd = text.IndexOf('>');
        var examine = text.AsSpan(0, tagEnd is >= 0 and < 512 ? tagEnd + 1 : Math.Min(text.Length, 512));
        foreach (var character in examine)
            if (character == '\uFFFD' || char.IsControl(character) && character is not ('\t' or '\r' or '\n')) return;
        throw new InvalidDataException(L.T("Core.Dlna.Download.HtmlResponse"));
    }

    private static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException(L.T("Core.Dlna.Download.UrlInvalid"));
        // A name is resolved, and held to the same rule, when the connection is opened (EndpointPolicy.ConnectLocalAsync).
        if (IPAddress.TryParse(uri.DnsSafeHost, out var address) && !EndpointPolicy.IsLocalAddress(address))
            throw new ArgumentException(L.T("Core.Dlna.Download.LocalAddressRequired"));
    }

    private static string ValidateDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var destination = Path.GetFullPath(path);
        var name = Path.GetFileName(destination);
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (name.Length == 0 || name != name.TrimEnd(' ', '.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
            throw new ArgumentException(L.T("Core.Dlna.Download.FileNameInvalid"), nameof(path));
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException(L.T("Core.Dlna.Download.DestinationExists"));
        return destination;
    }

    public static string GuessSafeExtension(Uri sourceUri, string? mimeType = null)
    {
        var extension = Path.GetExtension(sourceUri.AbsolutePath).ToLowerInvariant();
        if (extension is ".ts" or ".mts" or ".m2ts" or ".m2t" or ".mpg" or ".mpeg" or ".vob" or ".vro" or ".mp4" or ".mkv") return extension;
        return mimeType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "video/mp2t" => ".ts", "video/vnd.dlna.mpeg-tts" => ".m2ts", "video/mpeg" => ".mpg",
            "video/mp4" => ".mp4", "video/x-matroska" or "video/matroska" => ".mkv", _ => ".bin"
        };
    }

    /// <summary>How many bytes from the start of a file <see cref="ExtensionFromContent(ReadOnlySpan{byte})"/> looks at, at most.</summary>
    public const int ContentHeadLength = 4 + 5 * 192;

    /// <summary>
    /// The extension for the container a recording is in, told from its first bytes, or null when they begin like none of the
    /// five a recorder delivers. It is for a recorder that names a media type <see cref="GuessSafeExtension"/> does not know:
    /// such a recording would be saved as ".bin", which nothing opens, although its bytes say plainly what it is.
    /// </summary>
    public static string? ExtensionFromContent(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith<byte>([0x1A, 0x45, 0xDF, 0xA3])) return ".mkv";
        if (head.StartsWith<byte>([0x00, 0x00, 0x01, 0xBA])) return ".mpg";
        if (head.Length >= 8 && head.Slice(4, 4).SequenceEqual("ftyp"u8)) return ".mp4";
        // A transport stream has no signature, only the byte 0x47 at the start of every packet: 188 bytes apart, or 192 bytes
        // apart behind a four-byte time code in the form Blu-ray recorders use. Five in a row are not there by chance.
        if (HasPacketStarts(head, 0, 188)) return ".ts";
        if (HasPacketStarts(head, 4, 192)) return ".m2ts";
        return null;

        static bool HasPacketStarts(ReadOnlySpan<byte> head, int first, int size)
        {
            for (var packet = 0; packet < 5; packet++)
                if (first + packet * size >= head.Length || head[first + packet * size] != 0x47) return false;
            return true;
        }
    }

    /// <summary>The same for a file, of which the first <see cref="ContentHeadLength"/> bytes are read.</summary>
    public static string? ExtensionFromContent(string path)
    {
        var head = new byte[ContentHeadLength];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ExtensionFromContent(head.AsSpan(0, file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)));
    }

    public void Dispose() => _http.Dispose();
}
