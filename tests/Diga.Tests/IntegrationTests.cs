using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Diga.Core.Media;
using Diga.Core.Storage;
using Xunit.Abstractions;

namespace Diga.Tests;

/// <summary>Runs actual bundled programs against generated media, never a user's disk.</summary>
[Trait("Category", "Integration")]
public sealed class IntegrationTests : IClassFixture<MediaIntegrationFixture>
{
    private readonly MediaIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;
    public IntegrationTests(MediaIntegrationFixture fixture, ITestOutputHelper output) { _fixture = fixture; _output = output; }

    [Theory]
    [InlineData(OutputFormat.Matroska)]
    [InlineData(OutputFormat.Mpeg2)]
    public async Task ExtractedPanasonicVideoRemuxesWithIdenticalCompressedPayloads(OutputFormat format)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var payload = await File.ReadAllBytesAsync(_fixture.Mpeg);
        var image = _fixture.NewPath("recorder.img");
        await File.WriteAllBytesAsync(image, StorageFixtureBuilder.BuildMeihdfs(payload));
        var imageHash = SHA256.HashData(await File.ReadAllBytesAsync(image));
        using var disk = await PanasonicReader.OpenAsync(image, cancellationToken: timeout.Token);
        var recording = Assert.Single(disk.Recordings);
        var staged = _fixture.NewPath("original recording (録画).vro");
        await disk.ExtractAsync(recording, staged, cancellationToken: timeout.Token);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(staged)));
        var destination = _fixture.NewPath("archive & family" + OutputFormats.Extension(format));
        var result = await new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(staged, destination, format), cancellationToken: timeout.Token);
        Assert.Equal(2, result.Destination.Streams.Count);
        Assert.Equal(await _fixture.PacketHashes(staged), await _fixture.PacketHashes(destination));
        Assert.Equal(imageHash, SHA256.HashData(await File.ReadAllBytesAsync(image)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.partial"));
    }

    [Fact]
    public async Task H264AndAacCanBeCopiedIntoMp4()
    {
        var output = _fixture.NewPath("portable.mp4");
        var result = await new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(_fixture.H264, output, OutputFormat.Mp4));
        Assert.Contains(result.Destination.Streams, s => s.Type == "video" && s.Codec == "h264");
        Assert.Contains(result.Destination.Streams, s => s.Type == "audio" && s.Codec == "aac");
        Assert.Equal(await _fixture.DecodedVideoHash(_fixture.H264), await _fixture.DecodedVideoHash(output));
    }

    [Fact]
    public async Task IncompatibleMp4DoesNotTranscodeOrCreateDestination()
    {
        var output = _fixture.NewPath("unsupported.mp4");
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(_fixture.Mpeg, output, OutputFormat.Mp4)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task MediaInfoLibraryInspectsSourceAndMuxedOutput()
    {
        var output = _fixture.NewPath("metadata.mkv");
        await new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(_fixture.Mpeg, output, OutputFormat.Matroska));
        var service = new MediaInfoService(Path.Combine(_fixture.Tools, "MediaInfo.dll"));
        foreach (var path in new[] { _fixture.Mpeg, output })
        {
            var info = await service.InspectAsync(path);
            Assert.True(info.Available, info.Error);
            using var json = JsonDocument.Parse(info.Json);
            Assert.Contains(json.RootElement.GetProperty("media").GetProperty("track").EnumerateArray(), t => t.GetProperty("@type").GetString() == "Video");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMeihdfsMatchesManagedExtraction(bool latestVersion)
    {
        var payload = Enumerable.Range(0, 6001).Select(i => (byte)(i * 37)).ToArray();
        var image = _fixture.NewPath("native-mei.img");
        await File.WriteAllBytesAsync(image, StorageFixtureBuilder.BuildMeihdfs(payload, version: latestVersion ? 3 : 0));
        var output = _fixture.NewPath("native-mei"); Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var native = await new ProcessRunner().RunAsync(Path.Combine(_fixture.Tools, "native", "extract_meihdfs.exe"), [image, output], cancellationToken: timeout.Token);
        Assert.True(native.ExitCode == 0, native.StandardError + native.StandardOutput);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(output, "DVD_RTAV", "0001.VRO")));
        using var disk = await PanasonicReader.OpenAsync(image);
        using var stream = disk.OpenRead(Assert.Single(disk.Recordings));
        using var copied = new MemoryStream(); await stream.CopyToAsync(copied);
        Assert.Equal(payload, copied.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePanasonicUdfMatchesManagedExtraction(bool longAddresses)
    {
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray();
        var image = _fixture.NewPath("native-udf.img");
        await File.WriteAllBytesAsync(image, StorageFixtureBuilder.BuildUdf(payload, longAddresses));
        var output = _fixture.NewPath("native-udf"); Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var native = await new ProcessRunner().RunAsync(Path.Combine(_fixture.Tools, "native", "udf_dump.exe"), [image, output], cancellationToken: timeout.Token);
        Assert.True(native.ExitCode == 0, native.StandardError + native.StandardOutput);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(output, "VR_MOVIE.VRO")));
        using var disk = await PanasonicReader.OpenAsync(image);
        using var stream = disk.OpenRead(Assert.Single(disk.Recordings));
        using var copied = new MemoryStream(); await stream.CopyToAsync(copied);
        Assert.Equal(payload, copied.ToArray());
    }

    [Fact]
    public async Task ActualProcessCancellationTerminatesFfmpeg()
    {
        var elapsed = Stopwatch.StartNew();
        long requested = -1, ended = -1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var registration = timeout.Token.Register(() => requested = elapsed.ElapsedMilliseconds);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            try
            {
                await new ProcessRunner().RunAsync(_fixture.Ffmpeg,
                    ["-hide_banner", "-re", "-f", "lavfi", "-i", "testsrc2=size=32x32:rate=25", "-t", "120", "-f", "null", "-"], cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            finally { ended = elapsed.ElapsedMilliseconds; }
        });
        // Printed so that a slow run shows where the time went: a late timer (no free pool thread), a late end of FFmpeg, or a late test thread.
        _output.WriteLine($"Cancellation requested after {requested} ms, ProcessRunner ended after {ended} ms, test resumed after {elapsed.ElapsedMilliseconds} ms. " +
            $"Test host: {Environment.ProcessorCount} processors, {ThreadPool.ThreadCount} thread-pool threads, {ThreadPool.PendingWorkItemCount} queued work items.");
    }

    /// <summary>
    /// The same cancellation in a separate process with two thread-pool threads (<see cref="ProcessRunnerProbe"/>). When FFmpeg's
    /// output was read on pool threads, the two reads held both and the timer that cancels after half a second never ran.
    /// </summary>
    [Fact]
    public async Task ActualProcessCancellationIsOnTimeWithTwoThreadPoolThreads()
    {
        // testhost.exe runs the tests, so the dotnet host is taken from the runtime's own location.
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(File.Exists(dotnet) ? dotnet : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(ProcessRunnerProbe).Assembly.Location, ProcessRunnerProbe.Command, _fixture.Ffmpeg }) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        using var probe = Process.Start(start) ?? throw new IOException("Could not start the probe process.");
        // Dedicated threads here as well: this test host shares its pool with the tests running alongside.
        var stdout = Task.Factory.StartNew(probe.StandardOutput.ReadLine, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var stderr = Task.Factory.StartNew(probe.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        string? report;
        try { report = await stdout.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally
        {
            // The probe waits to be ended, so that an FFmpeg it could not cancel goes with it.
            try { if (!probe.HasExited) probe.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await probe.WaitForExitAsync();
        }
        _output.WriteLine("Probe: " + report);
        var fields = (report ?? "").Split(' ');
        Assert.True(fields is ["cancelled", var milliseconds, ..] && int.Parse(milliseconds, CultureInfo.InvariantCulture) < 1500,
            $"Cancellation was due after 500 ms and has to arrive within 1500 ms. Probe: '{report}'. {await stderr}");
    }
}

public sealed class MediaIntegrationFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Diga-integration-" + Guid.NewGuid().ToString("N"));
    public string Tools { get; private set; } = "";
    public string Ffmpeg => Path.Combine(Tools, "ffmpeg.exe");
    public string Ffprobe => Path.Combine(Tools, "ffprobe.exe");
    public string Mpeg => Path.Combine(Root, "source.mpg");
    public string H264 => Path.Combine(Root, "source.ts");
    public string NewPath(string filename) => Path.Combine(Root, Guid.NewGuid().ToString("N") + "-" + filename);

    public async Task InitializeAsync()
    {
        Tools = Environment.GetEnvironmentVariable("DIGA_TOOLS") ?? FindTools();
        foreach (var file in new[] { Ffmpeg, Ffprobe, Path.Combine(Tools, "MediaInfo.dll") })
            Assert.True(File.Exists(file), "Required integration dependency missing: " + file + ". Run scripts/Get-Dependencies.ps1 and Build-Native.ps1.");
        Directory.CreateDirectory(Root);
        await Run(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-t", "2", "-c:v", "mpeg2video", "-g", "12", "-c:a", "mp2", "-f", "mpeg", Mpeg]);
        await Run(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=frequency=600:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-f", "mpegts", H264]);
    }

    public async Task<string[]> PacketHashes(string path)
    {
        var result = await Run(Ffprobe, ["-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=stream_index,data_hash", "-of", "json", path]);
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("packets").EnumerateArray()
            .Select(p => (Index: p.GetProperty("stream_index").GetInt32(), Hash: p.GetProperty("data_hash").GetString()))
            .OrderBy(p => p.Index).Select(p => p.Index + ":" + p.Hash).ToArray();
    }

    public async Task<string> DecodedVideoHash(string path) => (await Run(Ffmpeg, ["-v", "error", "-i", path, "-map", "0:v:0", "-f", "hash", "-hash", "sha256", "-"])).StandardOutput.Trim();

    private static async Task<ProcessResult> Run(string program, IReadOnlyList<string> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new ProcessRunner().RunAsync(program, arguments, cancellationToken: timeout.Token);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    private static string FindTools()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null) { var candidate = Path.Combine(directory.FullName, "tools", "bin"); if (File.Exists(Path.Combine(candidate, "ffmpeg.exe"))) return candidate; directory = directory.Parent; }
        throw new DirectoryNotFoundException("Set DIGA_TOOLS to the bundled tools/bin directory for integration tests.");
    }

    public Task DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return Task.CompletedTask; }
}
