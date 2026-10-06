using L = Diga.Core.Localization.AppText;
namespace Diga.Core.Media;

/// <summary>Creates a bounded stream-copy preview from an already staged recording.</summary>
public sealed class PreviewService(string ffmpegPath = "ffmpeg.exe", string ffprobePath = "ffprobe.exe", IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();
    public async Task<PreviewResult> PrepareAsync(string sourcePath, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        var source = await new MediaProbeService(ffprobePath, _runner).ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var firstVideo = source.Streams.FirstOrDefault(s => s.Type == "video") ?? throw new InvalidDataException(L.T("Core.Media.Preview.VideoMissing"));
        var firstAudio = source.Streams.FirstOrDefault(s => s.Type == "audio");
        // Preview is explicitly limited to the first video/audio track; export preserves all media tracks.
        Directory.CreateDirectory(cacheDirectory);
        var output = Path.Combine(Path.GetFullPath(cacheDirectory), $"preview-{Guid.NewGuid():N}.mkv");
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-n", "-protocol_whitelist", "file,pipe", "-i", Path.GetFullPath(sourcePath), "-t", "45", "-map", FormattableString.Invariant($"0:{firstVideo.Index}") };
        if (firstAudio != null) args.AddRange(["-map", FormattableString.Invariant($"0:{firstAudio.Index}")]);
        args.AddRange(["-c", "copy", "-f", "matroska", output]);
        try
        {
            var result = await _runner.RunAsync(ffmpegPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidDataException(L.T("Core.Media.Preview.Unavailable", result.StandardError));
            return new(output, L.T("Core.Media.Preview.Notice"));
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }
}
