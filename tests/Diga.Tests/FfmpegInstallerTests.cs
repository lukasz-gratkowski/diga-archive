using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Diga.Core.Configuration;
using Diga.Core.Media;
using Xunit;

namespace Diga.Tests;

/// <summary>The FFmpeg download, against simulated servers and small stand-in packages. Nothing here contacts a server.</summary>
public sealed class FfmpegInstallerTests : IDisposable
{
    private const string Root = "ffmpeg-test-essentials_build";
    private static readonly Uri First = new("https://first.example/ffmpeg.zip");
    private static readonly Uri Second = new("https://second.example/ffmpeg.zip");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaFfmpegTests-" + Guid.NewGuid().ToString("N"));
    public FfmpegInstallerTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ThePinnedPackageIsCompletelyDescribed()
    {
        var pinned = FfmpegPackage.Pinned;
        Assert.Matches(@"^\d+\.\d+(\.\d+)?$", pinned.Version);
        Assert.Equal(2, pinned.Urls.Count);
        Assert.All(pinned.Urls, url => { Assert.Equal(Uri.UriSchemeHttps, url.Scheme); Assert.EndsWith(".zip", url.AbsolutePath); Assert.Contains(pinned.Version, url.AbsolutePath); });
        Assert.All(new[] { pinned.Sha256, pinned.FfmpegSha256, pinned.FfprobeSha256 }, hash => Assert.Matches("^[0-9A-F]{64}$", hash));
        Assert.InRange(pinned.SizeBytes, 50L * 1024 * 1024, 500L * 1024 * 1024);
        Assert.Contains(pinned.Version, pinned.Root);
        Assert.StartsWith("GPL-3.0", pinned.License);
    }

    [Fact]
    public async Task AVerifiedPackageInstallsOnlyTheTwoProgramsAndTheirLicenceTexts()
    {
        var zip = Package();
        var calls = new List<Uri>();
        using var http = Client(request => { calls.Add(request.RequestUri!); return Bytes(zip); });
        var reports = new List<FfmpegInstallProgress>();
        await new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory, new Collector(reports));
        Assert.Equal([First], calls);
        Assert.Equal(["FFmpeg-LICENSE.txt", "FFmpeg-README.txt", "ffmpeg.exe", "ffprobe.exe"], Installed());
        Assert.Equal("ffmpeg program", File.ReadAllText(Path.Combine(_directory, "ffmpeg.exe")));
        Assert.Equal("ffprobe program", File.ReadAllText(Path.Combine(_directory, "ffprobe.exe")));
        Assert.Equal("licence text", File.ReadAllText(Path.Combine(_directory, "FFmpeg-LICENSE.txt")));
        Assert.Equal(new FfmpegInstallProgress(zip.Length, zip.Length, Unpacking: true), reports[^1]);
        Assert.Contains(new FfmpegInstallProgress(zip.Length, zip.Length), reports);
    }

    [Fact]
    public async Task APackageWithAnotherChecksumIsDiscardedAndNothingIsInstalled()
    {
        var zip = Package();
        var tampered = (byte[])zip.Clone();
        tampered[^1] ^= 0xFF;
        using var http = Client(_ => Bytes(tampered));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task TheSecondAddressIsUsedWhenTheFirstFails()
    {
        var zip = Package();
        var calls = new List<Uri>();
        using var http = Client(request =>
        {
            calls.Add(request.RequestUri!);
            return request.RequestUri == First ? new HttpResponseMessage(HttpStatusCode.NotFound) : Bytes(zip);
        });
        await new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory);
        Assert.Equal([First, Second], calls);
        Assert.Equal(4, Installed().Length);
    }

    [Fact]
    public async Task EveryAddressFailingReportsTheLastServerAnswer()
    {
        var zip = Package();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<HttpRequestException>(error.InnerException).StatusCode);
        Assert.Contains("503", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task AnAnswerThatAnnouncesAnotherSizeIsRefusedBeforeItIsRead()
    {
        var zip = Package();
        var read = false;
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Observed(new byte[zip.Length + 1], () => read = true)) { Headers = { ContentLength = zip.Length + 1 } } });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.False(read);
    }

    [Fact]
    public async Task AnAnswerLongerThanThePackageIsCutOffAtThePinnedSize()
    {
        var zip = Package();
        // No announced length, and far more data than the package: the download stops as soon as the pinned size is exceeded.
        var endless = new Observed(new byte[64 * 1024 * 1024], () => { });
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.InRange(endless.BytesRead, zip.Length + 1, zip.Length + 1024 * 1024);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task APackageWithoutAnExpectedFileLeavesTheInstalledCopyUntouched()
    {
        File.WriteAllText(Path.Combine(_directory, "ffmpeg.exe"), "earlier ffmpeg");
        File.WriteAllText(Path.Combine(_directory, "ffprobe.exe"), "earlier ffprobe");
        var zip = Package(withFfprobe: false);
        using var http = Client(_ => Bytes(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory));
        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], Installed());
        Assert.Equal("earlier ffmpeg", File.ReadAllText(Path.Combine(_directory, "ffmpeg.exe")));
        Assert.Equal("earlier ffprobe", File.ReadAllText(Path.Combine(_directory, "ffprobe.exe")));
    }

    [Fact]
    public async Task AnEarlierCopyIsReplaced()
    {
        File.WriteAllText(Path.Combine(_directory, "ffmpeg.exe"), "earlier ffmpeg");
        var zip = Package();
        using var http = Client(_ => Bytes(zip));
        await new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory);
        Assert.Equal("ffmpeg program", File.ReadAllText(Path.Combine(_directory, "ffmpeg.exe")));
        Assert.Equal(4, Installed().Length);
    }

    [Fact]
    public async Task NothingOutsideTheFolderIsWrittenWhateverThePackageContains()
    {
        var zip = Package(extra: ["../outside.txt", Root + "/bin/ffplay.exe", Root + "/doc/ffmpeg.html", "/absolute.txt"]);
        using var http = Client(_ => Bytes(zip));
        await new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory);
        Assert.Equal(4, Installed().Length);
        Assert.Empty(Directory.EnumerateDirectories(_directory));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_directory)!, "outside.txt")));
    }

    [Fact]
    public async Task AnAddressThatIsNotHttpsIsNeverContacted()
    {
        var zip = Package();
        var called = false;
        using var http = Client(_ => { called = true; return Bytes(zip); });
        var package = Describe(zip) with { Urls = [new Uri("http://first.example/ffmpeg.zip")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegInstaller(http, package).InstallAsync(_directory));
        Assert.False(called);
    }

    [Fact]
    public async Task CancellationStopsTheDownloadAndLeavesNothingBehind()
    {
        var zip = Package();
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Bytes(zip);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FfmpegInstaller(http, Describe(zip)).InstallAsync(_directory, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task AServerThatStopsSendingIsGivenUpAndTheNextAddressIsTried()
    {
        var zip = Package();
        var calls = new List<Uri>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls.Add(request.RequestUri!);
            if (request.RequestUri == Second) return Bytes(zip);
            await Task.Delay(Timeout.Infinite, token);
            return Bytes(zip);
        }));
        // The limit applies to the second address as well. On a build machine that is short of threads, an answer that is
        // already in memory has taken longer than a fifth of a second to arrive, and the good server was then given up too;
        // the limit is long enough for that, and the test waits it out once, for the server that never sends.
        await new FfmpegInstaller(http, Describe(zip)) { StallTimeout = TimeSpan.FromSeconds(5) }.InstallAsync(_directory);
        Assert.Equal([First, Second], calls);
        Assert.Equal(4, Installed().Length);
    }

    [Fact]
    public async Task APackageWhoseProgramIsNotTheRecordedOneIsNotInstalled()
    {
        var zip = Package();
        // The archive is the described one, but the description names another ffprobe.
        var described = Describe(zip) with { FfprobeSha256 = Sha("another ffprobe") };
        File.WriteAllText(Path.Combine(_directory, "ffmpeg.exe"), "earlier copy");
        using var http = Client(_ => Bytes(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FfmpegInstaller(http, described).InstallAsync(_directory));
        Assert.Equal(["ffmpeg.exe"], Installed());
        Assert.Equal("earlier copy", File.ReadAllText(Path.Combine(_directory, "ffmpeg.exe")));
    }

    [Fact]
    public async Task TheTwoProgramsComeFromOneFolderAndThePinnedBuildIsPreferredOnceItWasExamined()
    {
        var zip = Package();
        var package = Describe(zip);
        var user = Path.Combine(_directory, "user");
        var application = Path.Combine(_directory, "application");
        // An FFmpeg downloaded long ago in the user's folder, and the pinned one an update installed beside the application.
        Touch("user", "ffmpeg.exe");
        Touch("user", "ffprobe.exe");
        using var http = Client(_ => Bytes(zip));
        await new FfmpegInstaller(http, package).InstallAsync(application);
        (string, FfmpegSource)[] managed = [(user, FfmpegSource.User), (application, FfmpegSource.Application)];
        // The installer of the pinned build has just recorded what it wrote, so that copy wins although the user's folder comes first.
        var found = MediaToolLocator.FindFfmpeg(null, null, managed, null, package)!;
        Assert.Equal((Path.Combine(application, "ffmpeg.exe"), Path.Combine(application, "ffprobe.exe"), FfmpegSource.Application, (bool?)true), (found.Ffmpeg, found.Ffprobe, found.Source, found.Pinned));
        // Without the pinned copy the older one is used, and after examination it is known not to be the pinned build.
        (string, FfmpegSource)[] onlyUser = [(user, FfmpegSource.User)];
        Assert.Null(MediaToolLocator.FindFfmpeg(null, null, onlyUser, null, package)!.Pinned);
        MediaToolLocator.Examine([user]);
        var old = MediaToolLocator.FindFfmpeg(null, null, onlyUser, null, package)!;
        Assert.Equal((FfmpegSource.User, (bool?)false), (old.Source, old.Pinned));
        // A changed file is not judged by what was read before the change.
        File.WriteAllText(Path.Combine(user, "ffmpeg.exe"), "ffmpeg program");
        File.WriteAllText(Path.Combine(user, "ffprobe.exe"), "ffprobe program");
        File.SetLastWriteTimeUtc(Path.Combine(user, "ffprobe.exe"), DateTime.UtcNow.AddMinutes(1));
        Assert.Null(MediaToolLocator.FindFfmpeg(null, null, onlyUser, null, package)!.Pinned);
        MediaToolLocator.Examine([user]);
        Assert.True(MediaToolLocator.FindFfmpeg(null, null, managed, null, package) is { Source: FfmpegSource.User, Pinned: true });
    }

    [Fact]
    public void AFolderWithOnlyOneOfTheTwoProgramsIsNotUsedAndTheUsersOwnFileReplacesEitherProgram()
    {
        var package = Describe(Package());
        var half = Path.GetDirectoryName(Touch("half", "ffmpeg.exe"))!;
        var onPath = Path.GetDirectoryName(Touch("on path", "ffmpeg.exe"))!;
        Touch("on path", "ffprobe.exe");
        (string, FfmpegSource)[] managed = [(half, FfmpegSource.User)];
        Assert.Null(MediaToolLocator.FindFfmpeg(null, null, managed, null, package));
        var fromPath = MediaToolLocator.FindFfmpeg(null, null, managed, "relative;\"" + onPath + "\"", package)!;
        Assert.Equal((Path.Combine(onPath, "ffmpeg.exe"), Path.Combine(onPath, "ffprobe.exe"), FfmpegSource.SearchPath, (bool?)null), (fromPath.Ffmpeg, fromPath.Ffprobe, fromPath.Source, fromPath.Pinned));
        var own = Touch("own", "my-ffmpeg.exe");
        var mixed = MediaToolLocator.FindFfmpeg(own, "ffprobe.exe", managed, onPath, package)!;
        Assert.Equal((own, Path.Combine(onPath, "ffprobe.exe"), FfmpegSource.Configured), (mixed.Ffmpeg, mixed.Ffprobe, mixed.Source));
        // The user's own ffmpeg alone is not enough: without any ffprobe there is no pair.
        Assert.Null(MediaToolLocator.FindFfmpeg(own, null, managed, null, package));
    }

    [Fact]
    public void AConfiguredToolThatExistsIsUsedBeforeAnyFolder()
    {
        var own = Touch("own", "ffmpeg.exe");
        var downloaded = Touch("downloaded", "ffmpeg.exe");
        Assert.Equal(own, MediaToolLocator.Find(own, "ffmpeg.exe", [Path.GetDirectoryName(downloaded)!], null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ffmpeg.exe")]
    [InlineData(@"tools\ffmpeg.exe")]
    [InlineData(@"Z:\no such folder\ffmpeg.exe")]
    public void WithoutAUsableConfiguredToolTheFoldersAreSearchedInOrder(string configured)
    {
        var first = Touch("first", "ffmpeg.exe");
        Touch("second", "ffmpeg.exe");
        Assert.Equal(first, MediaToolLocator.Find(configured, "ffmpeg.exe", [Path.Combine(_directory, "empty"), Path.Combine(_directory, "first"), Path.Combine(_directory, "second")], null));
    }

    [Fact]
    public void TheSearchPathIsUsedLastAndItsUnusableEntriesAreSkipped()
    {
        var onPath = Touch("on path", "ffmpeg.exe");
        var searchPath = string.Join(Path.PathSeparator, "", "relative\\folder", "\"" + Path.Combine(_directory, "missing") + "\"", "C:\\bad\0entry", "C:\\bad|entry?", "  ", "\"" + Path.GetDirectoryName(onPath) + "\"");
        Assert.Equal(onPath, MediaToolLocator.Find(null, "ffmpeg.exe", [Path.Combine(_directory, "empty")], searchPath));
        var downloaded = Touch("downloaded", "ffmpeg.exe");
        Assert.Equal(downloaded, MediaToolLocator.Find(null, "ffmpeg.exe", [Path.GetDirectoryName(downloaded)!], searchPath));
        Assert.Null(MediaToolLocator.Find(null, "ffprobe.exe", [Path.Combine(_directory, "empty")], searchPath));
    }

    [Fact]
    public void TheMediaInfoLibraryIsNotLookedForOnTheSearchPath()
    {
        var folder = Path.GetDirectoryName(Touch("planted", "DigaTestLibrary.dll"))!;
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + previous);
            Assert.Null(MediaToolLocator.Find(null, "DigaTestLibrary.dll"));
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Fact]
    public async Task ToolLocationsAreEmptyByDefaultAndSettingsOfEarlierVersionsStillLoad()
    {
        var settings = new AppSettings();
        Assert.Equal(("", "", ""), (settings.FfmpegPath, settings.FfprobePath, settings.MediaInfoPath));
        var store = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));
        await store.SaveAsync(settings with { OutputDirectory = _directory });
        Assert.Equal("", (await store.LoadAsync()).FfmpegPath);
        // Versions up to 0.5.2 saved the location they found, or the bare file name when they found none.
        await store.SaveAsync(settings with { OutputDirectory = _directory, FfmpegPath = "ffmpeg.exe", FfprobePath = @"C:\Removed\DIGA\tools\ffprobe.exe" });
        var loaded = await store.LoadAsync();
        Assert.Equal(("ffmpeg.exe", @"C:\Removed\DIGA\tools\ffprobe.exe"), (loaded.FfmpegPath, loaded.FfprobePath));
        Assert.Null(MediaToolLocator.Find(loaded.FfmpegPath, "ffmpeg.exe", [], null));
        Assert.Null(MediaToolLocator.Find(loaded.FfprobePath, "ffprobe.exe", [], null));
    }

    private string Touch(string folder, string name)
    {
        var path = Path.Combine(_directory, folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
        return path;
    }

    private string[] Installed() => Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private static byte[] Package(bool withFfprobe = true, string[]? extra = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Add(Root + "/bin/ffmpeg.exe", "ffmpeg program");
            if (withFfprobe) Add(Root + "/bin/ffprobe.exe", "ffprobe program");
            Add(Root + "/LICENSE", "licence text");
            Add(Root + "/README.txt", "read me");
            foreach (var name in extra ?? []) Add(name, "not wanted");
        }
        return buffer.ToArray();
    }

    private static FfmpegPackage Describe(byte[] zip) => new("0.0.1", [First, Second], Convert.ToHexString(SHA256.HashData(zip)), zip.Length, Root,
        Sha("ffmpeg program"), Sha("ffprobe program"), "GPL-3.0-or-later", new Uri("https://distributor.example/"), new Uri("https://source.example/"));

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handle) => new(new Handler((request, _) => Task.FromResult(handle(request))));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }

    private sealed class Collector(List<FfmpegInstallProgress> reports) : IProgress<FfmpegInstallProgress>
    {
        public void Report(FfmpegInstallProgress value) => reports.Add(value);
    }

    /// <summary>A body of unknown length that records how much of it was read.</summary>
    private sealed class Observed(byte[] body, Action onRead) : Stream
    {
        private int _position;
        public long BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead();
            var available = Math.Min(count, body.Length - _position);
            Array.Copy(body, _position, buffer, offset, available);
            _position += available;
            return available;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
