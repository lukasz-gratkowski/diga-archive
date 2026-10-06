using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Diga.Core.Dlna;
using Diga.Core.Media;
using Xunit.Abstractions;

namespace Diga.Tests;

/// <summary>Runs the real FFmpeg, FFprobe and MediaInfo of tools/bin (scripts/Get-Dependencies.ps1 fetches them) on generated media, never on a user's recording.</summary>
[Trait("Category", "Integration")]
public sealed class IntegrationTests : IClassFixture<MediaIntegrationFixture>
{
    private readonly MediaIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;
    public IntegrationTests(MediaIntegrationFixture fixture, ITestOutputHelper output) { _fixture = fixture; _output = output; }

    [Theory]
    [InlineData(OutputFormat.Matroska)]
    [InlineData(OutputFormat.Mpeg2)]
    public async Task RecordingWithUnusualNameRemuxesWithIdenticalCompressedPayloads(OutputFormat format)
    {
        // Real FFmpeg and FFprobe on paths with non-ASCII characters, spaces, brackets and an ampersand.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = _fixture.NewPath("original recording (録画).mpg");
        File.Copy(_fixture.Mpeg, source);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var destination = _fixture.NewPath("archive & family" + OutputFormats.Extension(format));
        var result = await new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(source, destination, format), cancellationToken: timeout.Token);
        Assert.Equal(2, result.Destination.Streams.Count);
        Assert.Equal(await _fixture.PacketHashes(source), await _fixture.PacketHashes(destination));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
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

    /// <summary>
    /// A "recording" that is really an ffconcat script naming another recording of the same folder. FFmpeg knows such a script by its
    /// first line, whatever the file is called, and reads the other file in its place. The list of formats the tools may read stops that.
    /// </summary>
    [Fact]
    public async Task AnFfconcatScriptNamedLikeARecordingCannotPullInItsNeighbour()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var folder = Directory.CreateDirectory(_fixture.NewPath("orders")).FullName;
        File.Copy(_fixture.H264, Path.Combine(folder, "2026-0142.ts"));
        var script = Path.Combine(folder, "x.ts");
        await File.WriteAllTextAsync(script, "ffconcat version 1.0\nfile '2026-0142.ts'\n", timeout.Token);

        // The hole itself, with the arguments used before the list existed: FFprobe describes the neighbour as if it were x.ts.
        // If a later FFmpeg stops doing that, this fails and says that the rest of the test no longer proves anything.
        var open = await new ProcessRunner().RunAsync(_fixture.Ffprobe, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-show_format", "-of", "json", script], cancellationToken: timeout.Token);
        Assert.True(open.ExitCode == 0, open.StandardError);
        using (var json = JsonDocument.Parse(open.StandardOutput)) Assert.Equal("concat", json.RootElement.GetProperty("format").GetProperty("format_name").GetString());
        var neighbour = MediaProbeService.Parse(script, open.StandardOutput);
        Assert.Contains(neighbour.Streams, stream => stream.Type == "video" && stream.Codec == "h264");
        _output.WriteLine("Without -format_whitelist FFprobe reads x.ts as: " + string.Join(", ", neighbour.Streams.Select(stream => stream.Type + " " + stream.Codec)));

        var probe = await Assert.ThrowsAsync<InvalidDataException>(() => new MediaProbeService(_fixture.Ffprobe).ProbeAsync(script, timeout.Token));
        Assert.Contains("not on whitelist", probe.Message, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine("With it: " + probe.Message);
        var destination = Path.Combine(folder, "saved.mkv");
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(script, destination, OutputFormat.Matroska), cancellationToken: timeout.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PreviewService(_fixture.Ffmpeg, _fixture.Ffprobe).PrepareAsync(script, Path.Combine(folder, "cache"), timeout.Token));
        // FFmpeg refuses as well when it is given the arguments of a save directly; the probe before it is not the only guard.
        var direct = await new ProcessRunner().RunAsync(_fixture.Ffmpeg, RemuxService.BuildArguments(neighbour, destination, OutputFormat.Matroska), cancellationToken: timeout.Token);
        Assert.NotEqual(0, direct.ExitCode);
        Assert.Contains("not on whitelist", direct.StandardError, StringComparison.OrdinalIgnoreCase);
        // Nothing was written: no container, no partial file, no preview folder.
        Assert.Equal(["2026-0142.ts", "x.ts"], Directory.GetFileSystemEntries(folder).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A broadcast with DVB subtitles, the pictures European channels send with a programme. FFmpeg copies them into Matroska and
    /// reads them back as what they were. The other two file types stay refused, and the test shows why with FFmpeg itself.
    /// </summary>
    [Fact]
    public async Task DvbSubtitlesOfABroadcastAreCopiedIntoMkvUnchanged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // FFmpeg makes DVB subtitles from pictures only, never from text, so they come from a generated Blu-ray subtitle file.
        var captions = _fixture.NewPath("captions.sup");
        await File.WriteAllBytesAsync(captions, BluRayCaptions(), timeout.Token);
        var source = _fixture.NewPath("broadcast.ts");
        var made = await new ProcessRunner().RunAsync(_fixture.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=720x576:rate=25", "-f", "lavfi", "-i", "sine=frequency=700:sample_rate=48000", "-i", captions,
            "-t", "5", "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "mpeg2video", "-g", "12", "-c:a", "mp2", "-c:s", "dvbsub", "-f", "mpegts", source], cancellationToken: timeout.Token);
        Assert.True(made.ExitCode == 0, made.StandardError);
        static string[] Kinds(MediaProbeResult probe) => probe.Streams.Select(stream => stream.Type + " " + stream.Codec).Order(StringComparer.Ordinal).ToArray();
        var probed = await new MediaProbeService(_fixture.Ffprobe).ProbeAsync(source, timeout.Token);
        Assert.Equal(["audio mp2", "subtitle dvb_subtitle", "video mpeg2video"], Kinds(probed));
        var subtitles = await SubtitlePackets(source, timeout.Token);
        Assert.NotEmpty(subtitles);

        var destination = _fixture.NewPath("broadcast.mkv");
        var result = await new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(source, destination, OutputFormat.Matroska), cancellationToken: timeout.Token);
        Assert.Equal(["audio mp2", "subtitle dvb_subtitle", "video mpeg2video"], Kinds(result.Destination));
        // Every subtitle packet is in the file, byte for byte.
        Assert.Equal(subtitles, await SubtitlePackets(destination, timeout.Token));
        Assert.True(result.DurationsAgree, $"{result.Source.Duration} and {result.Destination.Duration}");
        _output.WriteLine($"MKV: {string.Join(", ", Kinds(result.Destination))}; {subtitles.Length} subtitle packets, the same as in the transport stream.");

        // The application refuses the other file types before FFmpeg is started, and names the subtitles as the reason.
        var mpeg = _fixture.NewPath("broadcast.mpg");
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(source, mpeg, OutputFormat.Mpeg2), cancellationToken: timeout.Token));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Remux.IncompatibleStream", "MPEG", Diga.Core.Localization.AppText.T("Core.Media.Kind.Subtitle"), "DVB_SUBTITLE"), refused.Message);
        var mp4 = _fixture.NewPath("broadcast.mp4");
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(source, mp4, OutputFormat.Mp4), cancellationToken: timeout.Token));
        Assert.False(File.Exists(mpeg) || File.Exists(mp4));

        // Why: left to itself, FFmpeg writes the MPEG file without complaint and the subtitles are then read as DVD subtitles,
        // which they are not; an MP4 file it does not write at all. If a later FFmpeg does better, this fails and says so.
        string[] Copy(string muxer, string path) => ["-hide_banner", "-nostdin", "-v", "error", "-n", "-fflags", "+genpts", "-protocol_whitelist", "file,pipe", "-format_whitelist", MediaProbeService.InputFormats, "-i", source, "-map", "0", "-c", "copy", "-f", muxer, path];
        var asMpeg = await new ProcessRunner().RunAsync(_fixture.Ffmpeg, Copy("mpeg", mpeg), cancellationToken: timeout.Token);
        Assert.True(asMpeg.ExitCode == 0, asMpeg.StandardError);
        var mislabelled = Kinds(await new MediaProbeService(_fixture.Ffprobe).ProbeAsync(mpeg, timeout.Token));
        Assert.Equal(["audio mp2", "subtitle dvd_subtitle", "video mpeg2video"], mislabelled);
        var asMp4 = await new ProcessRunner().RunAsync(_fixture.Ffmpeg, Copy("mp4", mp4), cancellationToken: timeout.Token);
        Assert.NotEqual(0, asMp4.ExitCode);
        Assert.Contains("dvb_subtitle", asMp4.StandardError, StringComparison.Ordinal);
        _output.WriteLine($"FFmpeg alone: MPEG is written and read back as {string.Join(", ", mislabelled)}; MP4 fails with: {asMp4.ErrorSummary}");
    }

    private async Task<string[]> SubtitlePackets(string path, CancellationToken cancellationToken)
    {
        var result = await new ProcessRunner().RunAsync(_fixture.Ffprobe, ["-v", "error", "-select_streams", "s", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=size,data_hash", "-of", "json", path], cancellationToken: cancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("packets").EnumerateArray().Select(packet => packet.GetProperty("size") + " bytes " + packet.GetProperty("data_hash")).ToArray();
    }

    /// <summary>
    /// A Blu-ray subtitle file (PGS, ".sup") as small as one can be: two captions, each a white rectangle of 48 by 16 pixels on a
    /// picture of 720 by 576, shown from second 1 to 2 and from second 3 to 4.
    /// </summary>
    private static byte[] BluRayCaptions()
    {
        const int width = 720, height = 576, w = 48, h = 16, x = 320, y = 480;
        var file = new List<byte>();
        static byte[] Number(int value) => [(byte)(value >> 8), (byte)value];
        void Segment(byte kind, int second, params byte[][] parts)
        {
            var data = parts.SelectMany(part => part).ToArray();
            var time = second * 90000;
            file.AddRange([(byte)'P', (byte)'G', (byte)(time >> 24), (byte)(time >> 16), (byte)(time >> 8), (byte)time, 0, 0, 0, 0, kind, .. Number(data.Length), .. data]);
        }
        // One row of the picture: a run of w pixels in colour 1, then the end of the row.
        var picture = Enumerable.Repeat(new byte[] { 0, (byte)(0x80 | w), 1, 0, 0 }, h).SelectMany(row => row).ToArray();
        for (var caption = 0; caption < 2; caption++)
        {
            var (shown, cleared) = (1 + 2 * caption, 2 + 2 * caption);
            // Composition (one object at x, y), window, palette (transparent and white), the picture, end of the set.
            Segment(0x16, shown, Number(width), Number(height), [0x10], Number(2 * caption), [0x80, 0, 0, 1], Number(0), [0, 0], Number(x), Number(y));
            Segment(0x17, shown, [1, 0], Number(x), Number(y), Number(w), Number(h));
            Segment(0x14, shown, [0, 0], [0, 16, 128, 128, 0], [1, 235, 128, 128, 255]);
            Segment(0x15, shown, Number(0), [0, 0xC0, 0], Number(picture.Length + 4), Number(w), Number(h), picture);
            Segment(0x80, shown);
            // A composition without an object takes the caption off the screen.
            Segment(0x16, cleared, Number(width), Number(height), [0x10], Number(2 * caption + 1), [0, 0, 0, 0]);
            Segment(0x17, cleared, [1, 0], Number(x), Number(y), Number(w), Number(h));
            Segment(0x80, cleared);
        }
        return [.. file];
    }

    /// <summary>FFprobe writes the name of a file it cannot read as UTF-8. It has to arrive in the message as it is on the disk.</summary>
    [Fact]
    public async Task AFileNameOutsideAsciiIsUnchangedInWhatFfprobeSaysAboutIt()
    {
        var path = _fixture.NewPath("Zażółć gęślą jaźń 録画.ts");
        await File.WriteAllTextAsync(path, "This is not a recording.");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MediaProbeService(_fixture.Ffprobe).ProbeAsync(path));
        _output.WriteLine(error.Message);
        Assert.Contains(path + ": Invalid data found", error.Message, StringComparison.Ordinal);
        Assert.Contains(path, Assert.IsType<string>(error.Data["Output"]), StringComparison.Ordinal);
    }

    /// <summary>The list of readable formats must not shut out a container a recorder delivers or one this application writes and reads back.</summary>
    [Theory]
    [InlineData("transport stream.ts", "mpeg2video", "mp2", "-f mpegts")]
    [InlineData("transport stream h264.ts", "libx264", "aac", "-f mpegts")]
    [InlineData("blu-ray packets.m2ts", "libx264", "ac3", "-mpegts_m2ts_mode 1 -f mpegts")]
    [InlineData("program stream.mpg", "mpeg2video", "mp2", "-f mpeg")]
    [InlineData("program stream with ac3.vob", "mpeg2video", "ac3", "-f vob")]
    [InlineData("written.mkv", "libx264", "aac", "-f matroska")]
    [InlineData("written.mp4", "libx264", "aac", "-movflags +faststart -f mp4")]
    public async Task EveryContainerOfARecordingIsProbedPreviewedAndSavedAsEveryFileTypeItFits(string name, string videoEncoder, string audio, string muxer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var source = _fixture.NewPath(name);
        string[] encode = videoEncoder == "libx264" ? ["-c:v", "libx264", "-preset", "ultrafast"] : ["-c:v", "mpeg2video", "-g", "12"];
        var made = await new ProcessRunner().RunAsync(_fixture.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=frequency=800:sample_rate=48000",
            "-t", "2", .. encode, "-c:a", audio, .. muxer.Split(' '), source], cancellationToken: timeout.Token);
        Assert.True(made.ExitCode == 0, made.StandardError);
        var video = videoEncoder == "libx264" ? "h264" : "mpeg2video";

        var probed = await new MediaProbeService(_fixture.Ffprobe).ProbeAsync(source, timeout.Token);
        Assert.Contains(probed.Streams, stream => stream.Type == "video" && stream.Codec == video);
        Assert.Contains(probed.Streams, stream => stream.Type == "audio" && stream.Codec == audio);
        // The first bytes FFmpeg wrote say which container this is: what a download falls back on when its media type says nothing.
        var extension = Path.GetExtension(name);
        Assert.Equal(extension == ".vob" ? ".mpg" : extension, DlnaDownloadService.ExtensionFromContent(source));

        var preview = await new PreviewService(_fixture.Ffmpeg, _fixture.Ffprobe).PrepareAsync(source, Path.Combine(Path.GetDirectoryName(source)!, "previews"), timeout.Token);
        var previewed = await new MediaProbeService(_fixture.Ffprobe).ProbeAsync(preview.Path, timeout.Token);
        Assert.Contains(previewed.Streams, stream => stream.Type == "video" && stream.Codec == video);

        // Every file type the codecs fit; the written file is probed again inside RemuxAsync, through the same list.
        var formats = new List<OutputFormat> { OutputFormat.Matroska };
        if (video == "mpeg2video") formats.Add(OutputFormat.Mpeg2);
        if (video == "h264" && audio is "aac" or "ac3") formats.Add(OutputFormat.Mp4);
        foreach (var format in formats)
        {
            var destination = _fixture.NewPath("saved" + OutputFormats.Extension(format));
            var result = await new RemuxService(_fixture.Ffmpeg, _fixture.Ffprobe).RemuxAsync(new(source, destination, format), cancellationToken: timeout.Token);
            Assert.Contains(result.Destination.Streams, stream => stream.Type == "video" && stream.Codec == video);
            Assert.Contains(result.Destination.Streams, stream => stream.Type == "audio" && stream.Codec == audio);
            Assert.True(result.DurationsAgree, $"{format}: {result.Source.Duration} and {result.Destination.Duration}");
        }
        _output.WriteLine($"{name}: probed, previewed, saved as {string.Join(", ", formats)}.");
    }

    [Fact]
    public async Task RealFfmpegIsEndedByItsTimeLimitButNotWhileItsPositionMoves()
    {
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ProcessTimeoutException>(() => new ProcessRunner().RunAsync(_fixture.Ffmpeg,
            ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=32x32:rate=25", "-t", "90", "-f", "null", "-"], new ProcessLimits(Overall: TimeSpan.FromSeconds(1))));
        Assert.False(error.Inactivity);
        Assert.Equal("ffmpeg", error.Program);
        _output.WriteLine($"FFmpeg with 90 seconds of work and a limit of one second was ended after {elapsed.ElapsedMilliseconds} ms.");

        // Fourteen seconds of work in real time under a limit of six seconds without progress: the position FFmpeg reports keeps
        // moving, so the run outlasts its limit. The limit is this long because the clock runs from the start of the program, and
        // on a busy PC or a hosted runner FFmpeg can take seconds to start and to write its first position.
        var positions = new List<string>();
        var result = await new ProcessRunner().RunAsync(_fixture.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=32x32:rate=25", "-t", "14", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
            new ProcessLimits(Inactivity: TimeSpan.FromSeconds(6), Progress: RemuxService.ProgressMoved()), line => { if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) positions.Add(line); });
        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(positions.Distinct().Count() >= 4, string.Join(" ", positions));
    }

    /// <summary>
    /// A source that goes silent in the middle of a recording. FFmpeg goes on writing its progress twice a second, with the same
    /// position and size every time, so counting lines would never end it; the limit counts a position that moves.
    /// </summary>
    [Fact]
    public async Task RealFfmpegWhoseInputGoesSilentIsEndedAlthoughItKeepsReportingProgress()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var recording = _fixture.NewPath("long.ts");
        var made = await new ProcessRunner().RunAsync(_fixture.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x480:rate=25", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "30", "-c:v", "mpeg2video", "-b:v", "4M", "-g", "12", "-c:a", "mp2", "-f", "mpegts", recording], cancellationToken: timeout.Token);
        Assert.True(made.ExitCode == 0, made.StandardError);
        var bytes = await File.ReadAllBytesAsync(recording, timeout.Token);
        // A named pipe is a file to FFmpeg's file protocol, and one whose writer can stop without closing it.
        var name = "diga-test-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var feed = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(silence.Token);
            await pipe.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), silence.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, silence.Token);
        });
        var destination = _fixture.NewPath("stalled.mkv");
        var reports = new List<string>();
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ProcessTimeoutException>(() => new ProcessRunner().RunAsync(_fixture.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "warning", "-n", "-fflags", "+genpts", "-protocol_whitelist", "file,pipe", "-format_whitelist", MediaProbeService.InputFormats, "-i", @"\\.\pipe\" + name,
                "-map", "0", "-c", "copy", "-progress", "pipe:1", "-nostats", "-f", "matroska", destination],
            new ProcessLimits(Inactivity: TimeSpan.FromSeconds(4), Progress: RemuxService.ProgressMoved()),
            line => { if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) lock (reports) reports.Add(line); }, timeout.Token));
        silence.Cancel();
        try { await feed; } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        Assert.True(error.Inactivity);
        // The same position came again and again for the four seconds: at least five reports, two different values at most apart from the first "N/A".
        var repeated = reports.GroupBy(line => line).Max(group => group.Count());
        _output.WriteLine($"Ended after {elapsed.ElapsedMilliseconds} ms; {reports.Count} progress reports, the last position repeated {repeated} times: {reports[^1]}.");
        Assert.True(repeated >= 5, string.Join(" ", reports));
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
            Assert.True(File.Exists(file), "Required integration dependency missing: " + file + ". Run scripts/Get-Dependencies.ps1.");
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
        throw new DirectoryNotFoundException("Run scripts/Get-Dependencies.ps1, which fills tools/bin, or set DIGA_TOOLS to a folder that holds ffmpeg.exe, ffprobe.exe and MediaInfo.dll.");
    }

    public Task DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return Task.CompletedTask; }
}
