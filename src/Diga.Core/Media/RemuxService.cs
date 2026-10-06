using L = Diga.Core.Localization.AppText;
using System.Globalization;
using Diga.Core.Files;

namespace Diga.Core.Media;

public sealed class RemuxService(string ffmpegPath = "ffmpeg.exe", string ffprobePath = "ffprobe.exe", IProcessRunner? runner = null)
{
    /// <summary>
    /// How long FFmpeg may go without progress before it is stopped. A recording of many hours has no sensible overall limit, but
    /// one that makes FFmpeg spin or wait for ever would hold up an unattended batch. Progress is a reported position or size that
    /// moved, or data written to the file: an MP4 is rewritten at the end, for minutes on a slow drive, without any report.
    /// </summary>
    public static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();

    public async Task<RemuxResult> RemuxAsync(RemuxRequest request, IProgress<RemuxProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(request.SourcePath);
        var destination = Path.GetFullPath(request.DestinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("Core.Media.Remux.SourceDestinationSame"));
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException(L.T("Core.Media.Remux.DestinationExists"));
        if (!string.Equals(Path.GetExtension(destination), OutputFormats.Extension(request.Format), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(L.T("Core.Media.Remux.ExtensionMismatch"), nameof(request));
        var probe = new MediaProbeService(ffprobePath, _runner);
        var sourceInfo = await probe.ProbeAsync(source, cancellationToken).ConfigureAwait(false);
        ValidateCompatibility(sourceInfo, request.Format);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, WorkingFiles.PartialName(Path.GetFileName(destination)));
        try
        {
            var args = BuildArguments(sourceInfo, temporary, request.Format);
            ProcessResult run;
            try
            {
                run = await _runner.RunAsync(ffmpegPath, args, new ProcessLimits(Inactivity: InactivityLimit, Progress: ProgressMoved()), line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                        progress?.Report(new(sourceInfo.Duration is { TotalSeconds: > 0 } duration ? Math.Clamp(microseconds / 1_000_000d / duration.TotalSeconds, 0, .99) : null, L.T("Core.Media.Remux.Copying")));
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessTimeoutException ex) { throw new TimeoutException(L.T("Core.Media.Remux.Stalled", (long)ex.Limit.TotalMinutes), ex); }
            if (run.ExitCode != 0) throw Failed(run);
            // FFmpeg treats an input read error as end of input and still exits with 0, leaving a truncated container.
            if (run.StandardError.Contains("Error during demuxing", StringComparison.Ordinal)) throw Failed(run);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new InvalidDataException(L.T("Core.Media.Remux.OutputInvalid"));
            var outputInfo = await probe.ProbeAsync(temporary, cancellationToken).ConfigureAwait(false);
            VerifyStreams(sourceInfo, outputInfo);
            cancellationToken.ThrowIfCancellationRequested();
            // FFmpeg has closed the file, but its last part may still be only in the Windows write cache. The caller deletes the
            // download once this file has its name, so it is written through first.
            WorkingFiles.FlushToDisk(temporary);
            File.Move(temporary, destination, overwrite: false);
            progress?.Report(new(1, L.T("Core.Media.Remux.Complete")));
            return new(destination, sourceInfo, outputInfo with { Path = destination });
        }
        // Still there only after a failure. A delete that fails as well must not be reported in place of that failure; the sweep
        // of working files (WorkingFiles.Sweep) removes what is left.
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Tells a progress line that shows movement from one that repeats the last. FFmpeg writes its progress twice a second whether
    /// or not anything moved: while its input is stuck, the same position and the same size come again and again.
    /// </summary>
    public static Func<string, bool> ProgressMoved()
    {
        string? position = null, size = null;
        return line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) return Changed(ref position, line);
            return line.StartsWith("total_size=", StringComparison.Ordinal) && Changed(ref size, line);
        };
        static bool Changed(ref string? last, string line) { if (line == last) return false; last = line; return true; }
    }

    // The message carries the last lines FFmpeg wrote; everything it wrote goes with the exception for the log.
    private static InvalidDataException Failed(ProcessResult run) => new(L.T("Core.Media.Remux.Failed", run.ErrorSummary)) { Data = { ["Output"] = run.ErrorLog } };

    public static void ValidateCompatibility(MediaProbeResult source, OutputFormat format)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (!source.Streams.Any(s => s.Type == "video")) throw new InvalidDataException(L.T("Core.Media.Remux.VideoMissing"));
        foreach (var stream in source.Streams)
        {
            if (stream.Type == "data") continue; // Recorder control/private data is not a playable media stream.
            var accepted = format switch
            {
                OutputFormat.Matroska => stream.Type switch
                {
                    "video" => stream.Codec is "mpeg1video" or "mpeg2video" or "h264" or "hevc" or "mpeg4" or "vc1" or "av1" or "vp8" or "vp9",
                    "audio" => stream.Codec is "aac" or "ac3" or "eac3" or "mp2" or "mp3" or "dts" or "flac" or "opus" or "vorbis" or "pcm_s16le" or "pcm_s24le" or "pcm_s16be" or "pcm_bluray",
                    // DVB subtitles, the pictures a broadcast carries, are copied into Matroska unchanged (checked with FFmpeg 9.0.2).
                    // Teletext is on no list: that FFmpeg refuses to write it into Matroska and into MP4, and writes it into an MPEG
                    // program stream as something that is then read as DVD subtitles.
                    "subtitle" => stream.Codec is "dvb_subtitle" or "dvd_subtitle" or "hdmv_pgs_subtitle" or "subrip" or "ass" or "ssa" or "webvtt",
                    _ => false
                },
                // FFmpeg writes DVB subtitles into an MPEG program stream without complaint, and the file then presents them as DVD
                // subtitles, which they are not; an MP4 file with them it refuses to write. So neither list below has them.
                OutputFormat.Mpeg2 => stream.Type switch { "video" => stream.Codec == "mpeg2video", "audio" => stream.Codec is "mp2" or "mp3" or "ac3" or "dts" or "pcm_dvd", "subtitle" => stream.Codec == "dvd_subtitle", _ => false },
                OutputFormat.Mp4 => stream.Type switch { "video" => stream.Codec is "h264" or "hevc" or "mpeg4" or "av1", "audio" => stream.Codec is "aac" or "ac3" or "eac3" or "mp3" or "alac", "subtitle" => stream.Codec == "mov_text", _ => false },
                _ => false
            };
            if (accepted) continue;
            // Said in the words of the Preserve page: which part of the recording does not fit, and what does work.
            var kind = L.T(stream.Type switch { "video" => "Core.Media.Kind.Video", "audio" => "Core.Media.Kind.Audio", "subtitle" => "Core.Media.Kind.Subtitle", _ => "Core.Media.Kind.Other" });
            var codec = stream.Codec.ToUpperInvariant();
            throw new InvalidDataException(format == OutputFormat.Matroska
                ? L.T("Core.Media.Remux.IncompatibleMkv", kind, codec)
                : L.T("Core.Media.Remux.IncompatibleStream", format == OutputFormat.Mp4 ? "MP4" : "MPEG", kind, codec));
        }
    }

    public static IReadOnlyList<string> BuildArguments(MediaProbeResult source, string destination, OutputFormat format)
    {
        ValidateCompatibility(source, format);
        // MPEG program streams may carry no PTS for a picture that starts inside another picture's PES packet; Matroska and MP4 refuse such packets.
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-n", "-fflags", "+genpts", "-protocol_whitelist", "file,pipe", "-format_whitelist", MediaProbeService.InputFormats, "-i", source.Path };
        foreach (var stream in source.Streams.Where(s => s.Type != "data")) { args.Add("-map"); args.Add(FormattableString.Invariant($"0:{stream.Index}")); }
        args.AddRange(["-map_metadata", "0", "-c", "copy", "-progress", "pipe:1", "-nostats"]);
        if (format == OutputFormat.Mp4) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", format switch { OutputFormat.Matroska => "matroska", OutputFormat.Mpeg2 => "mpeg", OutputFormat.Mp4 => "mp4", _ => throw new ArgumentOutOfRangeException(nameof(format)) }, destination]);
        return args;
    }

    private static void VerifyStreams(MediaProbeResult source, MediaProbeResult destination)
    {
        var expected = source.Streams.Where(s => s.Type != "data").Select(s => (s.Type, s.Codec)).OrderBy(s => s.Type).ThenBy(s => s.Codec).ToArray();
        var actual = destination.Streams.Where(s => s.Type != "data").Select(s => (s.Type, s.Codec)).OrderBy(s => s.Type).ThenBy(s => s.Codec).ToArray();
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException(L.T("Core.Media.Remux.VerificationFailed"));
    }
}
