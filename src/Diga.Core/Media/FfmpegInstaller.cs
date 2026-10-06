using L = Diga.Core.Localization.AppText;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Diga.Core.Configuration;
using Diga.Core.Files;

namespace Diga.Core.Media;

/// <summary>
/// The FFmpeg build the application uses. It is not shipped with the application: the installer, or this class at the
/// user's request, downloads it from its distributor and checks it against the pinned SHA-256.
/// </summary>
public sealed record FfmpegPackage(string Version, IReadOnlyList<Uri> Urls, string Sha256, long SizeBytes, string Root,
    string FfmpegSha256, string FfprobeSha256, string License, Uri Distributor, Uri Source)
{
    public static FfmpegPackage Pinned { get; } = Load();

    private static FfmpegPackage Load()
    {
        using var stream = typeof(FfmpegPackage).Assembly.GetManifestResourceStream("Diga.Core.Media.FfmpegPackage.json")
            ?? throw new InvalidDataException("The FFmpeg package description is missing from the library.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        string Text(string name) => root.GetProperty(name).GetString()!;
        return new(Text("version"), root.GetProperty("urls").EnumerateArray().Select(url => new Uri(url.GetString()!)).ToArray(),
            Text("sha256"), root.GetProperty("sizeBytes").GetInt64(), Text("root"), Text("ffmpegSha256"), Text("ffprobeSha256"),
            Text("license"), new Uri(Text("distributor")), new Uri(Text("source")));
    }
}

/// <summary><paramref name="Unpacking"/> is set once the package has arrived and matched its checksum.</summary>
public sealed record FfmpegInstallProgress(long BytesReceived, long TotalBytes, bool Unpacking = false)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesReceived / TotalBytes;
}

/// <summary>Downloads the pinned FFmpeg package and installs ffmpeg.exe and ffprobe.exe with their licence texts.</summary>
public sealed class FfmpegInstaller(HttpClient? http = null, FfmpegPackage? package = null)
{
    // Redirects are followed (the first address redirects to its storage host); what arrives is accepted only if its size and SHA-256 match.
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };
    private const long ReportInterval = 1024 * 1024;
    private readonly HttpClient _http = http ?? DefaultHttp;
    private readonly FfmpegPackage _package = package ?? FfmpegPackage.Pinned;

    /// <summary>How long the server may send nothing before the address is given up.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The four files are placed in <paramref name="directory"/>; nothing else from the package is written.</summary>
    public async Task InstallAsync(string directory, IProgress<FfmpegInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // A download that a crash or a power cut interrupted left its hundred megabytes here; nothing else would remove them.
        WorkingFiles.Sweep(directory, []);
        var archive = Path.Combine(directory, WorkingFiles.PartialName("ffmpeg.zip"));
        try
        {
            Exception? last = null;
            var received = false;
            foreach (var url in _package.Urls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await DownloadAsync(url, archive, progress, cancellationToken).ConfigureAwait(false); received = true; break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { last = ex; }
            }
            if (!received) throw new InvalidOperationException(L.T("Core.Media.Ffmpeg.DownloadFailed", last?.Message ?? ""), last);
            progress?.Report(new(_package.SizeBytes, _package.SizeBytes, Unpacking: true));
            await Task.Run(() => Extract(archive, directory, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }

    private async Task DownloadAsync(Uri url, string archive, IProgress<FfmpegInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException(L.T("Core.Media.Ffmpeg.PackageInvalid"));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            stall.CancelAfter(StallTimeout);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(L.T("Core.Media.Ffmpeg.HttpError", (int)response.StatusCode), null, response.StatusCode);
            if (response.Content.Headers.ContentLength is { } announced && announced != _package.SizeBytes) throw new InvalidDataException(L.T("Core.Media.Ffmpeg.PackageInvalid"));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0, reported = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var count = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    // Never write more than the pinned size, whatever the server sends.
                    if (total > _package.SizeBytes) throw new InvalidDataException(L.T("Core.Media.Ffmpeg.PackageInvalid"));
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), stall.Token).ConfigureAwait(false);
                    if (total - reported < ReportInterval && total != _package.SizeBytes) continue;
                    reported = total;
                    progress?.Report(new(total, _package.SizeBytes));
                }
            }
            if (total != _package.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("Core.Media.Ffmpeg.PackageInvalid"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException(L.T("Core.Media.Ffmpeg.Stalled")); }
    }

    private void Extract(string archive, string directory, CancellationToken cancellationToken)
    {
        // Entries are looked up by their exact expected names and written under fixed names, so nothing in the archive chooses a path.
        (string Entry, string Target)[] files =
        [
            (_package.Root + "/bin/ffmpeg.exe", "ffmpeg.exe"), (_package.Root + "/bin/ffprobe.exe", "ffprobe.exe"),
            (_package.Root + "/LICENSE", "FFmpeg-LICENSE.txt"), (_package.Root + "/README.txt", "FFmpeg-README.txt")
        ];
        using var zip = ZipFile.OpenRead(archive);
        var staged = new List<(string Temporary, string Final)>();
        try
        {
            foreach (var (entryName, target) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException(L.T("Core.Media.Ffmpeg.PackageIncomplete"));
                var temporary = Path.Combine(directory, WorkingFiles.PartialName(target));
                staged.Add((temporary, Path.Combine(directory, target)));
                entry.ExtractToFile(temporary, overwrite: false);
            }
            // The archive matched its checksum; the two programs are compared with their own recorded checksums as well, as the
            // installer does, so that what is run is known file by file.
            var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (temporary, final) in staged)
            {
                var expected = Path.GetFileName(final) switch { MediaToolLocator.Ffmpeg => _package.FfmpegSha256, MediaToolLocator.Ffprobe => _package.FfprobeSha256, _ => null };
                if (expected is null) continue;
                cancellationToken.ThrowIfCancellationRequested();
                using var program = File.OpenRead(temporary);
                var actual = Convert.ToHexString(SHA256.HashData(program));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("Core.Media.Ffmpeg.PackageInvalid"));
                checksums[final] = actual;
            }
            // All four are complete before any of them replaces an existing file.
            foreach (var (temporary, final) in staged) File.Move(temporary, final, overwrite: true);
            foreach (var (path, checksum) in checksums) MediaToolLocator.Remember(path, checksum);
        }
        finally { foreach (var (temporary, _) in staged) if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
