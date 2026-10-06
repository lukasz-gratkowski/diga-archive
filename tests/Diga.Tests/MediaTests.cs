using Diga.Core.Media;
using Xunit;

namespace Diga.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaMediaTests-" + Guid.NewGuid().ToString("N"));
    public MediaTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private static MediaProbeResult Source(params MediaStreamInfo[] streams) => new("source.ts", TimeSpan.FromMinutes(1), 42, streams, "{}");

    [Theory]
    [InlineData(OutputFormat.Matroska, "mpeg2video", "ac3")]
    [InlineData(OutputFormat.Matroska, "h264", "aac")]
    [InlineData(OutputFormat.Matroska, "hevc", "eac3")]
    [InlineData(OutputFormat.Mpeg2, "mpeg2video", "mp2")]
    [InlineData(OutputFormat.Mpeg2, "mpeg2video", "ac3")]
    [InlineData(OutputFormat.Mp4, "h264", "aac")]
    [InlineData(OutputFormat.Mp4, "hevc", "ac3")]
    public void CompatibleOriginalStreamsUseOnlyCopy(OutputFormat format, string video, string audio)
    {
        var args = RemuxService.BuildArguments(Source(new(0, "video", video), new(1, "audio", audio)), "destination", format).ToArray();
        Assert.Equal("copy", args[Array.IndexOf(args, "-c") + 1]);
        Assert.Contains("-n", args);
        Assert.DoesNotContain("-y", args);
        Assert.Equal("+genpts", args[Array.IndexOf(args, "-fflags") + 1]);
        Assert.True(Array.IndexOf(args, "-fflags") < Array.IndexOf(args, "-i"));
        Assert.Equal("mpegts,mpeg,mov,matroska", args[Array.IndexOf(args, "-format_whitelist") + 1]);
        Assert.InRange(Array.IndexOf(args, "-format_whitelist"), 0, Array.IndexOf(args, "-i") - 1);
        Assert.Contains("0:0", args);
        Assert.Contains("0:1", args);
    }

    [Theory]
    [InlineData(OutputFormat.Mp4, "mpeg2video", "ac3")]
    [InlineData(OutputFormat.Mp4, "h264", "mp2")]
    [InlineData(OutputFormat.Mpeg2, "h264", "ac3")]
    [InlineData(OutputFormat.Mpeg2, "mpeg2video", "aac")]
    [InlineData(OutputFormat.Matroska, "encrypted", "ac3")]
    public void IncompatibleContainerCannotTranscode(OutputFormat format, string video, string audio) =>
        Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(new(0, "video", video), new(1, "audio", audio)), format));

    [Fact] public void Mp4RefusesSubtitleLoss() => Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(new(0, "video", "h264"), new(1, "subtitle", "dvd_subtitle")), OutputFormat.Mp4));
    [Fact] public void MissingVideoIsRejected() => Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(new MediaStreamInfo(0, "audio", "aac")), OutputFormat.Matroska));
    [Fact] public void RecorderDataIsNotMappedButAllMediaIsMapped()
    {
        var args = RemuxService.BuildArguments(Source(new(0, "video", "mpeg2video"), new(1, "audio", "ac3"), new(2, "audio", "mp2"), new(3, "data", "bin_data")), "out", OutputFormat.Matroska);
        Assert.Contains("0:2", args); Assert.DoesNotContain("0:3", args);
    }
    [Fact] public void ProbeParsesDurationLanguageDimensionsAndSize()
    {
        var probe = MediaProbeService.Parse("movie.ts", """{"streams":[{"index":2,"codec_type":"video","codec_name":"mpeg2video","width":720,"height":576,"tags":{"language":"eng"}}],"format":{"duration":"61.250","size":"123456"}}""");
        Assert.Equal(TimeSpan.FromSeconds(61.25), probe.Duration);
        Assert.Equal(123456, probe.SizeBytes);
        Assert.Equal(720, probe.Streams[0].Width);
        Assert.Equal("eng", probe.Streams[0].Language);
    }
    [Theory] [InlineData("N/A")] [InlineData("NaN")] [InlineData("Infinity")] [InlineData("-1")]
    public void InvalidDurationRemainsUnknown(string duration) => Assert.Null(MediaProbeService.Parse("x", "{\"format\":{\"duration\":\"" + duration + "\"}}").Duration);

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    [InlineData("ffprobe version 9.0.2")]
    [InlineData("{\"streams\":[{\"index\":0,")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"streams\":{}}")]
    [InlineData("{\"streams\":[7]}")]
    public async Task AProbeThatSucceedsWithoutADescriptionFailsInTheApplicationsWords(string output)
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runner = new FakeProcessRunner((_, _) => new(0, output, ""));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MediaProbeService("probe", runner).ProbeAsync(source));
        // The sentence of every failed probe, with nothing after it: FFprobe wrote no reason. Not the position at which a parser gave up.
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Probe.Failed", "").TrimEnd(), error.Message);
        Assert.EndsWith(".", error.Message, StringComparison.Ordinal);
        Assert.True(error.InnerException is System.Text.Json.JsonException or InvalidOperationException, error.InnerException?.ToString());
        Assert.Equal("", error.Data["Output"]);
        // A save meets the same failure in the same words, and writes nothing.
        var saved = await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Equal(error.Message, saved.Message);
        Assert.Equal([source], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task AProbeThatFailsNamesWhatFfprobeWroteLast()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runner = new FakeProcessRunner((_, _) => new(1, "{\n\n}", "[mpegts @ 000001] warning\nsource.ts: Invalid data found when processing input\n"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MediaProbeService("probe", runner).ProbeAsync(source));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Probe.Failed", "[mpegts @ 000001] warning source.ts: Invalid data found when processing input"), error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void DvbSubtitlesAreCopiedIntoMkvAndFitNoOtherFileType()
    {
        static MediaStreamInfo[] Recording(string video, string audio, string subtitles) => [new(0, "video", video), new(1, "audio", audio), new(2, "subtitle", subtitles)];
        // The pictures a broadcast carries as subtitles. FFmpeg 9.0.2 copies them into Matroska and reads them back as what they were.
        var arguments = RemuxService.BuildArguments(Source(Recording("mpeg2video", "mp2", "dvb_subtitle")), "out.mkv", OutputFormat.Matroska).ToList();
        Assert.Contains("0:2", arguments);
        Assert.Equal("copy", arguments[arguments.IndexOf("-c") + 1]);
        RemuxService.ValidateCompatibility(Source(Recording("h264", "aac", "dvb_subtitle")), OutputFormat.Matroska);
        // An MPEG file would present them as DVD subtitles, which they are not, and FFmpeg does not write them into MP4 at all.
        var subtitles = Diga.Core.Localization.AppText.T("Core.Media.Kind.Subtitle");
        var mpeg = Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(Recording("mpeg2video", "mp2", "dvb_subtitle")), OutputFormat.Mpeg2));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Remux.IncompatibleStream", "MPEG", subtitles, "DVB_SUBTITLE"), mpeg.Message);
        var mp4 = Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(Recording("h264", "aac", "dvb_subtitle")), OutputFormat.Mp4));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Remux.IncompatibleStream", "MP4", subtitles, "DVB_SUBTITLE"), mp4.Message);
        // Teletext fits none of the three: FFmpeg refuses to write it into Matroska and MP4, and mislabels it in an MPEG file.
        Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(Recording("mpeg2video", "mp2", "dvb_teletext")), OutputFormat.Matroska));
        Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(Recording("mpeg2video", "mp2", "dvb_teletext")), OutputFormat.Mpeg2));
        Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(Recording("h264", "aac", "dvb_teletext")), OutputFormat.Mp4));
    }

    [Fact] public async Task ExistingDestinationIsNeverOverwritten()
    {
        var destination = Path.Combine(_directory, "existing.mkv");
        await File.WriteAllTextAsync(destination, "keep");
        await Assert.ThrowsAsync<IOException>(() => new RemuxService().RemuxAsync(new("source", destination, OutputFormat.Matroska)));
        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
    }
    [Fact] public async Task SourceCannotBeDestination()
    {
        var source = Path.Combine(_directory, "source.mkv");
        await Assert.ThrowsAsync<IOException>(() => new RemuxService().RemuxAsync(new(source, source, OutputFormat.Matroska)));
    }
    [Fact]
    public void ARecordingThatDoesNotFitIsExplainedWithoutInternalNames()
    {
        var mp4 = Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(new(0, "video", "h264"), new(1, "audio", "mp2")), OutputFormat.Mp4));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Remux.IncompatibleStream", "MP4", Diga.Core.Localization.AppText.T("Core.Media.Kind.Audio"), "MP2"), mp4.Message);
        // Neither the name of the enumeration value nor a stream number: nobody choosing a file type knows either.
        Assert.DoesNotContain("Mp4", mp4.Message, StringComparison.Ordinal);
        Assert.Contains("MKV", mp4.Message, StringComparison.Ordinal);
        var mpeg = Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(new(0, "video", "h264"), new(1, "audio", "ac3")), OutputFormat.Mpeg2));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Remux.IncompatibleStream", "MPEG", Diga.Core.Localization.AppText.T("Core.Media.Kind.Video"), "H264"), mpeg.Message);
        // MKV itself cannot be the advice when MKV is what does not fit.
        var mkv = Assert.Throws<InvalidDataException>(() => RemuxService.ValidateCompatibility(Source(new(0, "video", "mpeg2video"), new(1, "subtitle", "dvb_teletext")), OutputFormat.Matroska));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Media.Remux.IncompatibleMkv", Diga.Core.Localization.AppText.T("Core.Media.Kind.Subtitle"), "DVB_TELETEXT"), mkv.Message);
    }

    [Fact]
    public void TheSummaryOfAToolsErrorOutputIsItsEndAndStaysShort()
    {
        var noisy = string.Join("\n", Enumerable.Range(0, 20000).Select(index => $"[mpeg2video @ 000001] warning {index}: invalid frame")) + "\r\nout.mp4: Invalid argument\r\n";
        var result = new ProcessResult(1, "", noisy);
        Assert.EndsWith("out.mp4: Invalid argument", result.ErrorSummary, StringComparison.Ordinal);
        Assert.InRange(result.ErrorSummary.Length, 25, 400);
        Assert.StartsWith("[mpeg2video", result.ErrorSummary, StringComparison.Ordinal);
        Assert.InRange(result.ErrorLog.Length, 1, 32 * 1024);
        Assert.Equal("", new ProcessResult(1, "", " \r\n ").ErrorSummary);
        Assert.Equal("unsupported", new ProcessResult(1, "", "unsupported\n").ErrorSummary);
        Assert.Equal(401, new ProcessResult(1, "", new string('x', 5000)).ErrorSummary.Length);
    }

    [Fact]
    public async Task AFailedRemuxNamesTheLastLinesInItsMessageAndKeepsTheRestForTheLog()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var output = string.Join("\n", Enumerable.Range(0, 5000).Select(index => $"warning {index}")) + "\nConversion failed!";
        var runner = new FakeProcessRunner((exe, _) => exe == "probe" ? new(0, ProbeJson, "") : new(1, "", output));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Contains("Conversion failed!", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 700, error.Message);
        Assert.Contains("warning 4000", Assert.IsType<string>(error.Data["Output"]), StringComparison.Ordinal);
    }

    [Fact] public async Task FailedRemuxDeletesPartialOutput()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runner = new FakeProcessRunner((exe, args) =>
        {
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "partial"); return new(1, "", "unsupported");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact] public async Task InputReadErrorFailsRemuxAlthoughFfmpegExitsWithZero()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runner = new FakeProcessRunner((exe, args) =>
        {
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "truncated"); return new(0, "", "[in#0/mpegts @ 000001] Error during demuxing: Permission denied");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact] public async Task VerificationPreventsPublishingMissingStreams()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var probes = 0;
        var runner = new FakeProcessRunner((exe, args) =>
        {
            if (exe == "probe") return new(0, ++probes == 1 ? ProbeJson : "{\"streams\":[]}", "");
            File.WriteAllText(args[^1], "partial"); return new(0, "", "");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact] public async Task SuccessfulVerifiedRemuxPublishesOnce()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var destination = Path.Combine(_directory, "out.mkv");
        var runner = new FakeProcessRunner((exe, args) =>
        {
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "completed"); return new(0, "", "");
        });
        var result = await new RemuxService("mux", "probe", runner).RemuxAsync(new(source, destination, OutputFormat.Matroska));
        Assert.Equal(destination, result.Destination.Path);
        Assert.Equal("completed", File.ReadAllText(destination));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }
    [Fact] public async Task MissingMediaInfoReturnsActionableDependencyError()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var result = await new MediaInfoService(Path.Combine(_directory, "missing.dll")).InspectAsync(source);
        Assert.False(result.Available); Assert.NotEmpty(result.Error!);
    }
    [Fact] public async Task CancellationBeforeProcessLaunchDoesNotStartTool()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync("does-not-exist.exe", [], cancellationToken: new(true)));
    }
    [Fact] public async Task CancelledRemuxRemovesPartialAndLeavesSource()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runner = new FakeProcessRunner((exe, args) =>
        {
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "partial"); throw new OperationCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Equal("media", File.ReadAllText(source));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact] public async Task DestinationCreatedDuringExportIsNeverOverwritten()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var destination = Path.Combine(_directory, "out.mkv");
        var runner = new FakeProcessRunner((exe, args) =>
        {
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "our-export"); File.WriteAllText(destination, "another-process"); return new(0, "", "");
        });
        await Assert.ThrowsAsync<IOException>(() => new RemuxService("mux", "probe", runner).RemuxAsync(new(source, destination, OutputFormat.Matroska)));
        Assert.Equal("another-process", File.ReadAllText(destination));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }
    [Fact] public async Task ThrowingProgressCallbackTerminatesProcessInsteadOfHanging()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProcessRunner().RunAsync(
            "powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'ready'; Start-Sleep -Seconds 60"],
            _ => throw new InvalidOperationException("progress failed"), timeout.Token));
        Assert.Equal("progress failed", error.Message);
    }
    [Fact]
    public async Task EveryToolReadsOnlyTheContainerFormatsOfRecordings()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runs = new List<(string Program, List<string> Arguments)>();
        var runner = new FakeProcessRunner((exe, args) =>
        {
            runs.Add((exe, args.ToList()));
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "written"); return new(0, "", "");
        });
        await new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska));
        await new PreviewService("mux", "probe", runner).PrepareAsync(source, Path.Combine(_directory, "cache"));
        // Source and written file are probed, the file is written, and the same for the preview without the second probe.
        Assert.Equal(["probe", "mux", "probe", "probe", "mux"], runs.Select(run => run.Program));
        foreach (var (program, arguments) in runs)
        {
            var whitelist = arguments.IndexOf("-format_whitelist");
            Assert.True(whitelist >= 0, program + " reads any format.");
            Assert.Equal(MediaProbeService.InputFormats, arguments[whitelist + 1]);
            Assert.Equal("file,pipe", arguments[arguments.IndexOf("-protocol_whitelist") + 1]);
            // The option restricts the input that follows it: for FFmpeg that is -i, for FFprobe the file, its last argument.
            Assert.True(whitelist < (program == "probe" ? arguments.Count - 1 : arguments.IndexOf("-i")));
        }
        // The formats this application writes are read back by the probe, so they have to be on the list as well.
        Assert.Equal(["mpegts", "mpeg", "mov", "matroska"], MediaProbeService.InputFormats.Split(','));
    }

    [Fact]
    public async Task EveryToolRunsUnderATimeLimit()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var runs = new List<(string Program, ProcessLimits Limits)>();
        var runner = new LimitedProcessRunner((exe, args, limits) =>
        {
            runs.Add((exe, limits));
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "written"); return new(0, "", "");
        });
        await new RemuxService("mux", "probe", runner).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska));
        await new PreviewService("mux", "probe", runner).PrepareAsync(source, Path.Combine(_directory, "cache"));
        Assert.Equal(["probe", "mux", "probe", "probe", "mux"], runs.Select(run => run.Program));
        Assert.All(runs.Where(run => run.Program == "probe"), run => Assert.Equal(new ProcessLimits(Overall: TimeSpan.FromMinutes(2)), run.Limits));
        // A recording of many hours has no sensible overall limit; one that stops moving has.
        var remux = runs[1].Limits;
        Assert.Null(remux.Overall);
        Assert.Equal(TimeSpan.FromMinutes(5), remux.Inactivity);
        Assert.NotNull(remux.Progress);
        Assert.Equal(new ProcessLimits(Overall: TimeSpan.FromMinutes(2)), runs[4].Limits);
    }

    [Fact]
    public async Task AToolEndedByItsLimitIsReportedInPlainWordsAndLeavesNoFile()
    {
        var source = Path.Combine(_directory, "source.ts"); await File.WriteAllTextAsync(source, "media");
        var cache = Path.Combine(_directory, "cache");
        static string Text(string key, long value) => Diga.Core.Localization.AppText.T(key, value);

        var stalled = new LimitedProcessRunner((exe, args, limits) =>
        {
            if (exe == "probe") return new(0, ProbeJson, "");
            File.WriteAllText(args[^1], "partial"); throw new ProcessTimeoutException(exe, limits.Inactivity!.Value, inactivity: true);
        });
        var error = await Assert.ThrowsAsync<TimeoutException>(() => new RemuxService("mux", "probe", stalled).RemuxAsync(new(source, Path.Combine(_directory, "out.mkv"), OutputFormat.Matroska)));
        Assert.Equal(Text("Core.Media.Remux.Stalled", 5), error.Message);
        Assert.True(Assert.IsType<ProcessTimeoutException>(error.InnerException).Inactivity);
        Assert.Equal([source], Directory.GetFiles(_directory));

        var slow = new LimitedProcessRunner((exe, args, limits) =>
        {
            if (exe == "probe" && args[^1] == source) return new(0, ProbeJson, "");
            if (exe == "mux") File.WriteAllText(args[^1], "partial");
            throw new ProcessTimeoutException(exe, limits.Overall!.Value, inactivity: false);
        });
        error = await Assert.ThrowsAsync<TimeoutException>(() => new PreviewService("mux", "probe", slow).PrepareAsync(source, cache));
        Assert.Equal(Text("Core.Media.Preview.TimedOut", 120), error.Message);
        Assert.Empty(Directory.GetFiles(cache));

        var other = Path.Combine(_directory, "other.ts"); await File.WriteAllTextAsync(other, "media");
        error = await Assert.ThrowsAsync<TimeoutException>(() => new MediaProbeService("probe", slow).ProbeAsync(other));
        Assert.Equal(Text("Core.Media.Probe.TimedOut", 120), error.Message);
        Assert.False(Assert.IsType<ProcessTimeoutException>(error.InnerException).Inactivity);
        // The user's own cancellation stays a cancellation.
        var cancelled = new LimitedProcessRunner((_, _, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaProbeService("probe", cancelled).ProbeAsync(other));
    }

    [Fact]
    public void AProgressLineThatRepeatsTheLastOneIsNotProgress()
    {
        var moved = RemuxService.ProgressMoved();
        Assert.True(moved("out_time_us=N/A"));
        Assert.True(moved("total_size=10747904"));
        Assert.True(moved("out_time_us=19776000"));
        Assert.False(moved("progress=continue"));
        // What FFmpeg 9.0.2 wrote twice a second while its input stayed silent: the same size and position, again and again.
        for (var repeat = 0; repeat < 6; repeat++)
            foreach (var line in new[] { "frame=494", "fps=0.00", "bitrate=4348.0kbits/s", "total_size=10747904", "out_time_us=19776000", "out_time=00:00:19.776000", "speed=39.5x", "progress=continue" })
                Assert.False(moved(line), line);
        Assert.True(moved("total_size=10984004"));
        Assert.True(moved("out_time_us=19850022"));
        Assert.False(moved("progress=end"));
        // Every run starts anew.
        Assert.True(RemuxService.ProgressMoved()("out_time_us=19850022"));
    }

    private const string ProbeJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"mpeg2video"},{"index":1,"codec_type":"audio","codec_name":"ac3"}],"format":{"duration":"1"}}""";
    private sealed class FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult> action) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(executable, arguments)); }
    }
    /// <summary>A runner that is told the limits of each run, as the real one is.</summary>
    private sealed class LimitedProcessRunner(Func<string, IReadOnlyList<string>, ProcessLimits, ProcessResult> action) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException(executable + " was started without a time limit.");
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessLimits limits, Action<string>? outputLine = null, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(executable, arguments, limits)); }
    }
}
