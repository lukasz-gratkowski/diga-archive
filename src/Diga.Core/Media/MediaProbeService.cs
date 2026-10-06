using L = Diga.Core.Localization.AppText;
using System.Globalization;
using System.Text.Json;

namespace Diga.Core.Media;

public sealed class MediaProbeService(string ffprobePath = "ffprobe.exe", IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();
    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(L.T("Core.Media.Probe.SourceMissing"), fullPath);
        var result = await _runner.RunAsync(ffprobePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-show_format", "-of", "json", fullPath], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidDataException(L.T("Core.Media.Probe.Failed", result.StandardError));
        return Parse(fullPath, result.StandardOutput, new FileInfo(fullPath).Length);
    }

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
