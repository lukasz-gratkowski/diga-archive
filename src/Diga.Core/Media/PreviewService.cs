using L = Diga.Core.Localization.AppText;
namespace Diga.Core.Media;

/// <summary>Copies the first 45 seconds of a downloaded recording, unchanged, into a file the preview player can open.</summary>
public sealed class PreviewService(string ffmpegPath = "ffmpeg.exe", string ffprobePath = "ffprobe.exe", IProcessRunner? runner = null)
{
    /// <summary>Copying 45 seconds out of a file on this PC takes seconds. After this long FFmpeg is stopped instead of leaving the application busy.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();
    public async Task<PreviewResult> PrepareAsync(string sourcePath, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        var source = await new MediaProbeService(ffprobePath, _runner).ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var firstVideo = source.Streams.FirstOrDefault(s => s.Type == "video") ?? throw new InvalidDataException(L.T("Core.Media.Preview.VideoMissing"));
        var firstAudio = source.Streams.FirstOrDefault(s => s.Type == "audio");
        // Preview is explicitly limited to the first video/audio track; export preserves all media tracks.
        Directory.CreateDirectory(cacheDirectory);
        var output = Path.Combine(Path.GetFullPath(cacheDirectory), $"preview-{Guid.NewGuid():N}.mkv");
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-n", "-protocol_whitelist", "file,pipe", "-format_whitelist", MediaProbeService.InputFormats, "-i", Path.GetFullPath(sourcePath), "-t", "45", "-map", FormattableString.Invariant($"0:{firstVideo.Index}") };
        if (firstAudio != null) args.AddRange(["-map", FormattableString.Invariant($"0:{firstAudio.Index}")]);
        args.AddRange(["-c", "copy", "-f", "matroska", output]);
        try
        {
            var result = await _runner.RunAsync(ffmpegPath, args, new ProcessLimits(Overall: TimeLimit), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidDataException(L.T("Core.Media.Preview.Unavailable", result.ErrorSummary)) { Data = { ["Output"] = result.ErrorLog } };
            return new(output, L.T("Core.Media.Preview.Notice"));
        }
        catch (Exception ex)
        {
            // A delete that fails as well must not be reported in place of the failure. The temporary folder is emptied later.
            try { if (File.Exists(output)) File.Delete(output); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            if (ex is ProcessTimeoutException stopped) throw new TimeoutException(L.T("Core.Media.Preview.TimedOut", (long)stopped.Limit.TotalSeconds), ex);
            throw;
        }
    }
}
