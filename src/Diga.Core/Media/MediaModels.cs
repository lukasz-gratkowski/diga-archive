namespace Diga.Core.Media;

public enum OutputFormat { Matroska, Mpeg2, Mp4 }

public sealed record MediaStreamInfo(int Index, string Type, string Codec, int? Width = null, int? Height = null, string? Language = null);
public sealed record MediaProbeResult(string Path, TimeSpan? Duration, long SizeBytes, IReadOnlyList<MediaStreamInfo> Streams, string Json);
public sealed record MediaInfoResult(bool Available, string Json, string? Error = null);
public sealed record RemuxRequest(string SourcePath, string DestinationPath, OutputFormat Format);
public sealed record RemuxProgress(double? Fraction, string Message);
public sealed record RemuxResult(string DestinationPath, MediaProbeResult Source, MediaProbeResult Destination)
{
    /// <summary>
    /// True when the container's duration is within two seconds of its source's.
    /// An unknown duration does not agree. Callers use this before letting go of the source file.
    /// </summary>
    public bool DurationsAgree => Source.Duration is { } source && Destination.Duration is { } destination
        && Math.Abs((destination - source).TotalSeconds) <= 2;
}
public sealed record PreviewResult(string Path, string Notice);

public static class OutputFormats
{
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Matroska => ".mkv", OutputFormat.Mpeg2 => ".mpg", OutputFormat.Mp4 => ".mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
