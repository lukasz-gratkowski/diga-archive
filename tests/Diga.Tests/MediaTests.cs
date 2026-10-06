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
    private const string ProbeJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"mpeg2video"},{"index":1,"codec_type":"audio","codec_name":"ac3"}],"format":{"duration":"1"}}""";
    private sealed class FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult> action) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(executable, arguments)); }
    }
}
