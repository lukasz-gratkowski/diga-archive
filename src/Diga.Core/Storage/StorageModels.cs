using L = Diga.Core.Localization.AppText;
namespace Diga.Core.Storage;

public sealed record DiskSource(string Id, string DisplayName, string Path, long SizeBytes, bool IsPhysicalDisk, bool IsUsb);
public sealed record ScanProgress(long BytesScanned, long TotalBytes, string Message);
public sealed record RecordingFile(string Id, string Name, string FullPath, long SizeBytes, DateTimeOffset? RecordedAt, bool IsVideo);

internal readonly record struct DataExtent(long Offset, long Length, bool ZeroFill = false);
internal sealed record FileLayout(RecordingFile File, IReadOnlyList<DataExtent> Extents);

/// <summary>A read-only recording filesystem. Keep it alive while using streams returned by OpenRead.</summary>
public sealed class PanasonicFileSystem : IDisposable
{
    private readonly DiskByteSource _source;
    private readonly Dictionary<string, FileLayout> _layouts;
    private bool _disposed;

    internal PanasonicFileSystem(DiskByteSource source, string name, IEnumerable<FileLayout> files, IEnumerable<string> warnings)
    {
        _source = source;
        FileSystemName = name;
        _layouts = files.ToDictionary(x => x.File.Id, StringComparer.Ordinal);
        Files = Array.AsReadOnly(_layouts.Values.Select(x => x.File).OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToArray());
        Recordings = Array.AsReadOnly(Files.Where(x => x.IsVideo).ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
    }

    public string SourcePath => _source.Path;
    public string FileSystemName { get; }
    public IReadOnlyList<RecordingFile> Files { get; }
    public IReadOnlyList<RecordingFile> Recordings { get; }
    public IReadOnlyList<string> Warnings { get; }

    public Stream OpenRead(RecordingFile file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(file);
        if (!_layouts.TryGetValue(file.Id, out var layout) || layout.File != file)
            throw new ArgumentException(L.T("Core.Storage.File.WrongSource"), nameof(file));
        return new ExtentReadStream(_source, layout.Extents, file.SizeBytes);
    }

    /// <summary>Extracts atomically to a new file. Existing destination files are never overwritten.</summary>
    public async Task ExtractAsync(RecordingFile file, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = System.IO.Path.GetFullPath(destinationPath);
        DiskDiscovery.EnsureSafeDestination(SourcePath, destination);
        using Stream input = OpenRead(file);
        string directory = System.IO.Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(destination)) throw new IOException(L.T("Core.Storage.File.DestinationExists"));
        string temporary = System.IO.Path.Combine(directory, $".diga-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[1024 * 1024];
                long copied = 0;
                progress?.Report(0);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    copied += count;
                    progress?.Report(file.SizeBytes == 0 ? 1 : (double)copied / file.SizeBytes);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Dispose();
    }
}
