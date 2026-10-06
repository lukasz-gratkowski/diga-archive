using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Diga.Core.Naming;

namespace Diga.Core.Files;

/// <summary>
/// The files written under working names while a recording is being saved. Every way a save can end removes them, except the
/// ones nobody gets to handle: a crash, a power cut, an ended task. Those leave them in the folder the recordings are saved to.
/// </summary>
public static partial class WorkingFiles
{
    /// <summary>
    /// Has Windows write a file to the disk itself before it gets its real name. From then on it counts as saved: the recording
    /// may be deleted from the recorder, and the download it was made from is deleted. What was still only in the write cache
    /// would be lost with a power cut.
    /// </summary>
    public static void FlushToDisk(FileStream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        try { output.Flush(flushToDisk: true); }
        // ERROR_INVALID_FUNCTION and ERROR_NOT_SUPPORTED: a network share or device that has no such call. Every byte was
        // written without an error, so hours of work are not thrown away for it.
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070001) or unchecked((int)0x80070032)) { }
    }

    /// <summary>The same for a file another program has written and closed, such as the one FFmpeg leaves.</summary>
    public static void FlushToDisk(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        FlushToDisk(file);
    }

    // ".diga-<32 hex>.download.ts": a whole recording as the recorder delivered it, waiting for its easy-to-play file.
    [GeneratedRegex(@"^\.diga-[0-9a-f]{32}\.download(\.[A-Za-z0-9]{1,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Delivered();

    // ".<name>.<32 hex>.partial": a file that was still being written, so it is never complete.
    [GeneratedRegex(@"^\..+\.[0-9a-f]{32}\.partial$", RegexOptions.CultureInvariant)]
    private static partial Regex Partial();

    public static string DeliveredName(string extension) => $".diga-{Guid.NewGuid():N}.download{extension}";
    public static string PartialName(string fileName) => $".{fileName}.{Guid.NewGuid():N}.partial";
    public static bool IsDelivered(string fileName) => Delivered().IsMatch(fileName);
    public static bool IsPartial(string fileName) => Partial().IsMatch(fileName);

    /// <summary>What <see cref="Sweep"/> found: incomplete files it removed, and complete downloads it left where they are.</summary>
    public sealed record Leftovers(int PartialFilesRemoved, long PartialBytesRemoved, IReadOnlyList<string> DeliveredFiles, long DeliveredBytes);

    /// <summary>
    /// Looks in the folder itself, not below it, for working files that nothing is using. Incomplete files are removed. Complete
    /// downloads are only reported: each is a whole recording, and one may have been kept on purpose when its rename failed.
    /// A file counts as unused when it was last written longer ago than <paramref name="minimumAge"/> and nothing holds it open.
    /// </summary>
    /// <param name="keep">Files this session knows about and must not touch.</param>
    public static Leftovers Sweep(string directory, IEnumerable<string> keep, TimeSpan? minimumAge = null)
    {
        var age = minimumAge ?? TimeSpan.FromMinutes(10);
        var kept = new HashSet<string>(keep.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var delivered = new List<string>();
        var removed = 0;
        long removedBytes = 0, deliveredBytes = 0;
        string[] files;
        try { files = Directory.GetFiles(Path.GetFullPath(directory)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return new(0, 0, [], 0); }
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var partial = IsPartial(name);
            if (!partial && !IsDelivered(name) || kept.Contains(path)) continue;
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || DateTime.UtcNow - file.LastWriteTimeUtc < age) continue;
                var length = file.Length;
                // Whoever is writing or reading the file holds it open; then it is somebody's work in progress, not a leftover.
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                if (partial) { File.Delete(path); removed++; removedBytes += length; }
                else { delivered.Add(path); deliveredBytes += length; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return new(removed, removedBytes, delivered, deliveredBytes);
    }

    /// <summary>What became of a delivered download: the name it was kept under, if it was kept, and whether a delete that was wanted failed.</summary>
    public sealed record Settled(string? KeptPath, bool DeleteFailed);

    /// <summary>
    /// Ends the life of a delivered download under its working name, once its easy-to-play file was attempted. A download that is
    /// no longer needed is deleted. One that must be kept, or that cannot be deleted, gets a proper name beside the other files,
    /// and stays under its working name if even the rename fails. The download is never lost: the only outcome without a kept
    /// file is a delete that succeeded.
    /// </summary>
    public static Settled SettleDelivered(string working, bool keep, string directory, string stem, string extension)
    {
        var deleteFailed = false;
        if (!keep)
        {
            try { File.Delete(working); return new(null, false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { deleteFailed = true; }
        }
        if (!File.Exists(working)) return new(null, deleteFailed);
        var kept = SavedFileNames.NextAvailablePath(directory, stem, extension);
        try { File.Move(working, kept, overwrite: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { kept = working; }
        return new(kept, deleteFailed);
    }

    /// <summary>
    /// Copies a file whose SHA-256 is known. The copy is written under a working name in the destination folder and gets its real
    /// name only when it is complete and has that SHA-256. A file that already carries the name is neither replaced nor removed.
    /// </summary>
    /// <returns>False when the copy did not have the expected SHA-256; nothing is left behind then.</returns>
    public static async Task<bool> CopyVerifiedAsync(string source, string destination, string expectedSha256, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        var working = Path.Combine(Path.GetDirectoryName(destination)!, PartialName(Path.GetFileName(destination)));
        var created = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(working, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                created = true;
                var buffer = new byte[1 << 20];
                long copied = 0, reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (copied - reported < 16 << 20) continue;
                    reported = copied;
                    progress?.Report(copied);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                FlushToDisk(output);
                progress?.Report(copied);
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return false;
            File.Move(working, destination, overwrite: false);
            created = false;
            return true;
        }
        // Only the working file this call created is ever deleted.
        finally { if (created && File.Exists(working)) File.Delete(working); }
    }
}
