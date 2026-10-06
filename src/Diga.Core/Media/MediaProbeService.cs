using L = Diga.Core.Localization.AppText;
using System.Globalization;
using System.Text.Json;

namespace Diga.Core.Media;

public sealed class MediaProbeService(string ffprobePath = "ffprobe.exe", IProcessRunner? runner = null)
{
    /// <summary>
    /// The container formats FFmpeg and FFprobe may read, as the value of their -format_whitelist option: what a recorder delivers
    /// (MPEG transport stream, MPEG program stream, MP4) and what this application writes and then reads back (Matroska, MP4, MPEG
    /// program stream). Without the list FFmpeg chooses among several hundred readers by the first bytes of the file, and a
    /// "recording" that is really an ffconcat script makes it read other files of the same folder into the saved one.
    /// One name of a reader is enough: "mov" covers MP4 and "matroska" covers MKV.
    /// </summary>
    public const string InputFormats = "mpegts,mpeg,mov,matroska";

    /// <summary>Reading the description of a recording takes FFprobe about a second. After this long it is stopped: an unattended batch must not wait for ever on one damaged file.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();
    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(L.T("Core.Media.Probe.SourceMissing"), fullPath);
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(ffprobePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", InputFormats, "-show_streams", "-show_format", "-of", "json", fullPath],
                new ProcessLimits(Overall: TimeLimit), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessTimeoutException ex) { throw new TimeoutException(L.T("Core.Media.Probe.TimedOut", (long)ex.Limit.TotalSeconds), ex); }
        if (result.ExitCode != 0) throw Failed(result);
        try { return Parse(fullPath, result.StandardOutput, new FileInfo(fullPath).Length); }
        // FFprobe said it succeeded and wrote nothing, or something that is not the description asked for. The user is told
        // that the recording could not be read, in the application's words, and not at which character a parser gave up.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw Failed(result, ex); }
    }

    private static InvalidDataException Failed(ProcessResult result, Exception? cause = null) =>
        new(L.T("Core.Media.Probe.Failed", result.ErrorSummary).TrimEnd(), cause) { Data = { ["Output"] = result.ErrorLog } };

    public static MediaProbeResult Parse(string path, string json, long fallbackSize = 0)
    {
        using var document = JsonDocument.Parse(json);
        var streams = new List<MediaStreamInfo>();
        if (document.RootElement.TryGetProperty("streams", out var items))
            foreach (var stream in items.EnumerateArray())
                streams.Add(new(Int(stream, "index") ?? streams.Count, Text(stream, "codec_type") ?? "unknown", Text(stream, "codec_name") ?? "unknown",
                    Int(stream, "width"), Int(stream, "height"), stream.TryGetProperty("tags", out var tags) ? Text(tags, "language") : null));
        TimeSpan? duration = null;
        var size = fallbackSize;
        if (document.RootElement.TryGetProperty("format", out var format))
        {
            if (double.TryParse(Text(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds) duration = TimeSpan.FromSeconds(seconds);
            if (long.TryParse(Text(format, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize) && parsedSize >= 0) size = parsedSize;
        }
        return new(path, duration, size, streams, json);
    }

    private static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static int? Int(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
