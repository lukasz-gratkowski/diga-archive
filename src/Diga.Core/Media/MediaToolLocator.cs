using System.Collections.Concurrent;
using System.Security.Cryptography;
using Diga.Core.Configuration;

namespace Diga.Core.Media;

/// <summary>Where a pair of FFmpeg programs was found.</summary>
public enum FfmpegSource { Configured, User, Application, SearchPath }

/// <summary>
/// The two FFmpeg programs the application runs. <paramref name="Pinned"/> is true for the build this version of the application
/// was made for, false for another build, and null when the files were not compared yet or are not the application's to judge
/// (the user's own files, a copy found on PATH).
/// </summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, FfmpegSource Source, bool? Pinned);

/// <summary>Where the application looks for its media tools when the settings do not name an existing file.</summary>
public static class MediaToolLocator
{
    // Reading a program of a hundred megabytes takes a moment, so its SHA-256 is kept until the file changes.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Sha256)> Checksums = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The two programs, taken from one folder: a copy the application manages (downloaded in the application, or installed with
    /// it), preferring one that is the pinned build, otherwise a pair on PATH. A file named in the settings replaces either program.
    /// Without the preference an FFmpeg downloaded long ago would hide every newer one an update brings.
    /// </summary>
    public static FfmpegTools? FindFfmpeg(string? configuredFfmpeg, string? configuredFfprobe) => FindFfmpeg(configuredFfmpeg, configuredFfprobe,
        [(UserToolsDirectory, FfmpegSource.User), (ApplicationToolsDirectory, FfmpegSource.Application)], Environment.GetEnvironmentVariable("PATH"), FfmpegPackage.Pinned);

    public static FfmpegTools? FindFfmpeg(string? configuredFfmpeg, string? configuredFfprobe, IReadOnlyList<(string Directory, FfmpegSource Source)> managed, string? searchPath, FfmpegPackage package)
    {
        static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);
        static (string Ffmpeg, string Ffprobe)? Pair(string folder)
        {
            // PATH is edited by hand and by installers: entries can be quoted, relative or not paths at all.
            var trimmed = folder.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed)) return null;
            var (ffmpeg, ffprobe) = (Path.Combine(trimmed, Ffmpeg), Path.Combine(trimmed, Ffprobe));
            return File.Exists(ffmpeg) && File.Exists(ffprobe) ? (ffmpeg, ffprobe) : null;
        }
        FfmpegTools? found = null;
        foreach (var (directory, source) in managed)
        {
            if (Pair(directory) is not { } pair) continue;
            var candidate = new FfmpegTools(pair.Ffmpeg, pair.Ffprobe, source, IsPinned(pair.Ffmpeg, pair.Ffprobe, package));
            if (candidate.Pinned == true) { found = candidate; break; }
            found ??= candidate;
        }
        if (found is null)
            foreach (var folder in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Pair(folder) is { } pair) { found = new(pair.Ffmpeg, pair.Ffprobe, FfmpegSource.SearchPath, null); break; }
        if (!Usable(configuredFfmpeg) && !Usable(configuredFfprobe)) return found;
        var (ownFfmpeg, ownFfprobe) = (Usable(configuredFfmpeg) ? configuredFfmpeg : found?.Ffmpeg, Usable(configuredFfprobe) ? configuredFfprobe : found?.Ffprobe);
        return ownFfmpeg is null || ownFfprobe is null ? null : new(ownFfmpeg, ownFfprobe, FfmpegSource.Configured, null);
    }

    /// <summary>
    /// Reads the copies the application manages and remembers their SHA-256, which is what <see cref="FindFfmpeg(string?, string?)"/>
    /// compares with the pinned build. It reads whole programs, so it belongs on a background thread.
    /// </summary>
    public static void Examine(IEnumerable<string>? directories = null)
    {
        foreach (var directory in directories ?? [UserToolsDirectory, ApplicationToolsDirectory])
            foreach (var name in new[] { Ffmpeg, Ffprobe })
            {
                var path = Path.Combine(directory, name);
                try
                {
                    if (!File.Exists(path) || Known(path) is not null) continue;
                    var file = new FileInfo(path);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    Checksums[path] = (file.Length, file.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
    }

    /// <summary>Records the SHA-256 of a program that was just written and checked, so that it is not read a second time.</summary>
    internal static void Remember(string path, string sha256)
    {
        var file = new FileInfo(path);
        if (file.Exists) Checksums[path] = (file.Length, file.LastWriteTimeUtc, sha256);
    }

    private static string? Known(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && Checksums.TryGetValue(path, out var seen) && seen.Length == file.Length && seen.WrittenUtc == file.LastWriteTimeUtc ? seen.Sha256 : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool? IsPinned(string ffmpeg, string ffprobe, FfmpegPackage package) => (Known(ffmpeg), Known(ffprobe)) is ({ } first, { } second)
        ? first.Equals(package.FfmpegSha256, StringComparison.OrdinalIgnoreCase) && second.Equals(package.FfprobeSha256, StringComparison.OrdinalIgnoreCase)
        : null;

    public const string Ffmpeg = "ffmpeg.exe";
    public const string Ffprobe = "ffprobe.exe";
    public const string MediaInfo = "MediaInfo.dll";

    /// <summary>Beside the application: MediaInfo ships there, and the installer puts FFmpeg there.</summary>
    public static string ApplicationToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>The per-user folder the application itself downloads FFmpeg into.</summary>
    public static string UserToolsDirectory => AppPaths.Tools;

    /// <summary>
    /// One tool by its file name: the configured file if it exists; otherwise, for a program, the folders the application manages
    /// and then PATH; for the MediaInfo library only the copy beside the application. Null when there is none.
    /// The two FFmpeg programs are looked up together with <see cref="FindFfmpeg(string?, string?)"/>.
    /// </summary>
    public static string? Find(string? configured, string fileName) => fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? Find(configured, fileName, [UserToolsDirectory, ApplicationToolsDirectory], Environment.GetEnvironmentVariable("PATH"))
        : Find(configured, fileName, [ApplicationToolsDirectory], null);

    public static string? Find(string? configured, string fileName, IEnumerable<string> directories, string? searchPath)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured)) return configured;
        foreach (var folder in directories.Concat((searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            // PATH is edited by hand and by installers: entries can be quoted, relative or not paths at all.
            var trimmed = folder.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed)) continue;
            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
