using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diga.Core.Cloud;
using Diga.Core.Dlna;
using Diga.Core.Localization;
using Diga.Core.Media;
using Diga.Core.Storage;

namespace Diga.Tests;

[Collection("Localization culture")]
public sealed class CoreLocalizationTests : CoreLocalizationCultureScope
{
    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    public void PolishUiKeepsProbeDecimalsCodecIdsAndFfmpegMapsInvariant(string regionalCulture)
    {
        UsePolishUi(regionalCulture);
        const string sourcePath = "rodzinne żółwie.ts";
        var media = MediaProbeService.Parse(sourcePath, """
            {"streams":[{"index":123,"codec_type":"video","codec_name":"mpeg2video","width":720,"height":576},
            {"index":456,"codec_type":"audio","codec_name":"mp2","tags":{"language":"pol"}}],
            "format":{"duration":"61.250","size":"123456789"}}
            """);
        Assert.Equal(TimeSpan.FromSeconds(61.25), media.Duration);
        Assert.Equal(123456789, media.SizeBytes);
        Assert.Equal("pol", media.Streams[1].Language);
        var arguments = RemuxService.BuildArguments(media, "wakacje.mkv", OutputFormat.Matroska).ToArray();
        var maps = arguments.Select((value, index) => (value, index)).Where(x => x.value == "-map")
            .Select(x => arguments[x.index + 1]).ToArray();
        Assert.Equal(new[] { "0:123", "0:456" }, maps);
        Assert.Equal("copy", arguments[Array.IndexOf(arguments, "-c") + 1]);
        Assert.Equal("matroska", arguments[Array.IndexOf(arguments, "-f") + 1]);
        Assert.Equal(sourcePath, arguments[Array.IndexOf(arguments, "-i") + 1]);
        Assert.Equal(regionalCulture, CultureInfo.CurrentCulture.Name);
        Assert.Equal("pl", AppText.LanguageCode);
    }

    [Theory]
    [InlineData("pl-PL", 1, "Znaleziono 1 plik wideo.")]
    [InlineData("pl-PL", 2, "Znaleziono 2 pliki wideo.")]
    [InlineData("pl-PL", 5, "Znaleziono 5 plików wideo.")]
    [InlineData("de-DE", 1, "Znaleziono 1 plik wideo.")]
    [InlineData("de-DE", 2, "Znaleziono 2 pliki wideo.")]
    [InlineData("de-DE", 5, "Znaleziono 5 plików wideo.")]
    public async Task ActualFilesystemScanReportsPolishCountsWithoutChangingRecordingBytes(
        string regionalCulture, int count, string expectedMessage)
    {
        UsePolishUi(regionalCulture);
        byte[] payload = Enumerable.Range(0, 1703).Select(i => (byte)(i * 31)).ToArray();
        var image = StorageFixtureBuilder.BuildMeihdfs(payload);
        int directory = StorageFixtureBuilder.DefaultBase + StorageFixtureBuilder.DirectoryOffset;
        // Independent filenames may share the same validated inode/data extents.
        image.AsSpan(directory + 0x420, 5 * 32).Clear();
        for (var i = 0; i < count; i++)
        {
            int entry = directory + 0x420 + i * 32;
            var name = i.ToString("D4", CultureInfo.InvariantCulture) + ".VRO";
            StorageFixtureBuilder.U32(image, entry, 2);
            StorageFixtureBuilder.U16(image, entry + 4, 1);
            StorageFixtureBuilder.U16(image, entry + 6, (ushort)name.Length);
            Encoding.ASCII.GetBytes(name).CopyTo(image, entry + 8);
        }
        var path = NewPath("polskie-nagrania.img");
        await File.WriteAllBytesAsync(path, image);
        var progress = new CoreLocalizationProgress<ScanProgress>();
        using var filesystem = await PanasonicReader.OpenAsync(path, progress);
        Assert.Equal(count, filesystem.Recordings.Count);
        Assert.Equal("Wyszukiwanie nagłówków systemu plików Panasonic…", progress.Values[0].Message);
        Assert.Equal(expectedMessage, progress.Values[^1].Message);
        Assert.Contains(filesystem.Warnings, warning => warning.StartsWith("Pliki wideo stanowią całe kontenery Panasonic.", StringComparison.Ordinal));
        Assert.StartsWith("mei:2:", filesystem.Recordings[0].Id);
        using var recording = filesystem.OpenRead(filesystem.Recordings[0]);
        using var copy = new MemoryStream();
        await recording.CopyToAsync(copy);
        Assert.Equal(payload, copy.ToArray());
        Assert.Equal(SHA256.HashData(image), SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    public async Task PolishFailuresRetainExternalDiagnosticsAndFailBeforeProtectedDownload(string regionalCulture)
    {
        UsePolishUi(regionalCulture);
        const string diagnostic = "decoder: invalid_stream {42} & <raw detail>";
        var source = NewPath("źródło.ts");
        await File.WriteAllTextAsync(source, "fixture");
        var probe = new MediaProbeService("fixture-probe", new CoreLocalizationProcessRunner(new(1, "", diagnostic)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => probe.ProbeAsync(source));
        Assert.Equal("FFprobe nie mógł odczytać tego nagrania. Może być zaszyfrowane, niekompletne lub nieobsługiwane. " + diagnostic, error.Message);

        var incompatible = new MediaProbeResult(source, null, 7, [new(7, "video", "mpeg2video")], "{}");
        var compatibility = Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(incompatible, OutputFormat.Mp4));
        Assert.StartsWith("Kontener Mp4 nie pozwala bezpiecznie skopiować strumienia 7 (video: mpeg2video).", compatibility.Message);

        using var downloader = new DlnaDownloadService();
        var destination = NewPath("chronione.ts");
        var resource = DlnaProtocolInfo.Parse(new Uri("http://127.0.0.1:1/recording"), "http-get:*:application/x-dtcp1:DLNA.ORG_CI=0");
        var protectedError = await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new(resource, destination)));
        Assert.Equal("Nagrywarka udostępnia zawartość chronioną lub przekonwertowaną. Obsługiwane są tylko transmisje bez ochrony i bez konwersji.", protectedError.Message);
        Assert.False(File.Exists(destination));

        using var http = new HttpClient(new CoreLocalizationHttpHandler(_ => throw new InvalidOperationException("A missing client ID must not use the network.")));
        var auth = new CloudAuthService(http, new CoreLocalizationTokenStore(), _ => throw new InvalidOperationException("A missing client ID must not launch a browser."));
        var missingClient = await Assert.ThrowsAsync<ArgumentException>(() => auth.AuthenticateAsync(CloudProvider.GoogleDrive, ""));
        Assert.StartsWith("Przed połączeniem wpisz w Ustawieniach identyfikator klienta aplikacji komputerowej", missingClient.Message);
    }

    [Theory]
    [InlineData("pl-PL", CloudProvider.GoogleDrive)]
    [InlineData("de-DE", CloudProvider.GoogleDrive)]
    [InlineData("pl-PL", CloudProvider.OneDrive)]
    [InlineData("de-DE", CloudProvider.OneDrive)]
    public async Task CloudChunkHeadersAndPayloadStayInvariantWithPolishUi(string regionalCulture, CloudProvider provider)
    {
        UsePolishUi(regionalCulture);
        var payload = Enumerable.Range(0, CloudUploadService.ChunkSize + 17).Select(i => (byte)(i * 13)).ToArray();
        var source = NewPath("podróż.mkv");
        await File.WriteAllBytesAsync(source, payload);
        using var received = new MemoryStream();
        var ranges = new List<string>();
        using var http = new HttpClient(new CoreLocalizationHttpHandler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                if (provider == CloudProvider.GoogleDrive)
                {
                    Assert.Equal("5242897", request.Headers.GetValues("X-Upload-Content-Length").Single());
                    Assert.Equal("podróż.mkv", metadata.RootElement.GetProperty("name").GetString());
                    var response = Json(HttpStatusCode.OK, "{}");
                    response.Headers.Location = new Uri("https://www.googleapis.com/upload/localization-fixture");
                    return response;
                }
                Assert.Equal("podróż.mkv", metadata.RootElement.GetProperty("item").GetProperty("name").GetString());
                Assert.Equal("rename", metadata.RootElement.GetProperty("item").GetProperty("@microsoft.graph.conflictBehavior").GetString());
                return Json(HttpStatusCode.OK, """{"uploadUrl":"https://up.1drv.com/localization-fixture"}""");
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            ranges.Add(request.Content!.Headers.ContentRange!.ToString());
            await received.WriteAsync(await request.Content.ReadAsByteArrayAsync());
            if (ranges.Count == 1)
            {
                if (provider == CloudProvider.OneDrive) return Json(HttpStatusCode.Accepted, """{"nextExpectedRanges":["5242880-"]}""");
                var response = Json((HttpStatusCode)308, "");
                response.Headers.TryAddWithoutValidation("Range", "bytes=0-5242879");
                return response;
            }
            return Json(HttpStatusCode.Created, """{"id":"fixture-file","name":"podróż.mkv"}""");
        }));
        var account = new CloudAccount { Provider = provider, ClientId = "fixture", AccessToken = "fixture-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var progress = new CoreLocalizationProgress<CloudUploadProgress>();
        var result = await new CloudUploadService(http).UploadAsync(new(account, source), progress);
        Assert.Equal(new[] { "bytes 0-5242879/5242897", "bytes 5242880-5242896/5242897" }, ranges);
        Assert.Equal(payload, received.ToArray());
        Assert.Equal("podróż.mkv", result.Name);
        Assert.Equal(payload.Length, progress.Values[^1].BytesUploaded);
        Assert.Equal(1d, progress.Values[^1].Fraction);
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    public async Task OAuthLoopbackShowsPolishUtf8AndKeepsProtocolFieldsUntranslated(string regionalCulture)
    {
        UsePolishUi(regionalCulture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task? callback = null;
        string? redirect = null;
        string? challenge = null;
        var tokens = new CoreLocalizationTokenStore();
        using var http = new HttpClient(new CoreLocalizationHttpHandler(async request =>
        {
            var form = ParseQuery(await request.Content!.ReadAsStringAsync(timeout.Token));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("fixture-code", form["code"]);
            Assert.Equal(redirect, form["redirect_uri"]);
            Assert.Equal(challenge, CloudAuthService.CreatePkceChallenge(form["code_verifier"]));
            return Json(HttpStatusCode.OK, """{"access_token":"fixture-access","refresh_token":"fixture-refresh","expires_in":3600}""");
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("https://www.googleapis.com/auth/drive.file", query["scope"]);
            redirect = query["redirect_uri"];
            challenge = query["code_challenge"];
            Assert.Matches(@"^http://localhost:[0-9]+$", redirect);
            callback = Task.Run(async () =>
            {
                using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
                using var response = await browser.GetAsync(redirect + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"]), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("utf-8", response.Content.Headers.ContentType!.CharSet);
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                Assert.Equal(bytes.LongLength, response.Content.Headers.ContentLength);
                Assert.Equal("Aplikacja DIGA otrzymała dane logowania. Możesz zamknąć tę kartę i wrócić do aplikacji.", Encoding.UTF8.GetString(bytes));
            }, timeout.Token);
        });
        var result = await auth.AuthenticateAsync(CloudProvider.GoogleDrive, "fixture-client", timeout.Token);
        await callback!;
        Assert.Equal("fixture-access", result.AccessToken);
        Assert.Same(result, tokens.Account);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static Dictionary<string, string> ParseQuery(string query) => query.Split('&').Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    private sealed class CoreLocalizationProcessRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
    private sealed class CoreLocalizationHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return handle(request); }
    }
    private sealed class CoreLocalizationTokenStore : ICloudTokenStore
    {
        public CloudAccount? Account { get; private set; }
        public Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => Task.FromResult(Account);
        public Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default) { Account = account; return Task.CompletedTask; }
        public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) { Account = null; return Task.CompletedTask; }
    }
}

[Collection("Localization culture")]
[Trait("Category", "Integration")]
public sealed class CoreLocalizationMediaIntegrationTests(MediaIntegrationFixture fixture) : CoreLocalizationCultureScope, IClassFixture<MediaIntegrationFixture>
{
    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    public async Task RealPreviewAndRemuxShowPolishProgressAndKeepOriginalMedia(string regionalCulture)
    {
        UsePolishUi(regionalCulture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var preview = await new PreviewService(fixture.Ffmpeg, fixture.Ffprobe).PrepareAsync(fixture.Mpeg, DirectoryPath, timeout.Token);
        Assert.Equal("45-sekundowy podgląd z oryginalnymi kodekami. Odtwarzanie zależy od obsługi kodeków w systemie Windows; zapis pliku nie wymaga kodeków do odtwarzania.", preview.Notice);
        var previewInfo = await new MediaProbeService(fixture.Ffprobe).ProbeAsync(preview.Path, timeout.Token);
        Assert.Contains(previewInfo.Streams, stream => stream.Codec == "mpeg2video" && stream.Type == "video");
        Assert.Contains(previewInfo.Streams, stream => stream.Codec == "mp2" && stream.Type == "audio");

        var destination = NewPath("rodzinny-film.mkv");
        var progress = new CoreLocalizationProgress<RemuxProgress>();
        var result = await new RemuxService(fixture.Ffmpeg, fixture.Ffprobe).RemuxAsync(new(fixture.Mpeg, destination, OutputFormat.Matroska), progress, timeout.Token);
        Assert.Equal(destination, result.DestinationPath);
        Assert.Contains(progress.Values, value => value.Message == "Kopiowanie oryginalnego obrazu i dźwięku…");
        Assert.Equal("Zapis zakończony. Zachowano oryginalne kodeki.", progress.Values[^1].Message);
        Assert.Equal(1d, progress.Values[^1].Fraction);
        Assert.All(progress.Values.Where(value => value.Fraction.HasValue), value => Assert.InRange(value.Fraction!.Value, 0d, 1d));
        Assert.Equal(await fixture.PacketHashes(fixture.Mpeg), await fixture.PacketHashes(destination));
        var metadata = await new MediaInfoService(Path.Combine(fixture.Tools, "MediaInfo.dll")).InspectAsync(destination, timeout.Token);
        Assert.True(metadata.Available, metadata.Error);
        using var json = JsonDocument.Parse(metadata.Json);
        Assert.Contains(json.RootElement.GetProperty("media").GetProperty("track").EnumerateArray(), track => track.GetProperty("@type").GetString() == "Video");
    }
}

public abstract class CoreLocalizationCultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    protected string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Diga-CoreLocalization-" + Guid.NewGuid().ToString("N"));

    protected CoreLocalizationCultureScope() => Directory.CreateDirectory(DirectoryPath);
    protected string NewPath(string filename) => Path.Combine(DirectoryPath, filename);
    protected static void UsePolishUi(string regionalCulture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(regionalCulture);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture;
        AppText.Configure("pl", ["en-US"]);
    }
    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class CoreLocalizationProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];
    public void Report(T value) => Values.Add(value);
}
