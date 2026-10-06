using Diga.Core.Storage;
using System.Security.Cryptography;
using System.Text;

namespace Diga.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DigaStorageTests-" + Guid.NewGuid().ToString("N"));
    private string Save(byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        string path = System.IO.Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".dd");
        File.WriteAllBytes(path, bytes); return path;
    }
    private static byte[] Payload(int length = 5093) => Enumerable.Range(0, length).Select(x => (byte)(x * 17 + 3)).ToArray();

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task MeihdfsListsAndReassemblesFragmentedFilesWithoutChangingSource(int version)
    {
        byte[] payload = Payload(); string path = Save(StorageFixtureBuilder.BuildMeihdfs(payload, version: version));
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        using var fs = await PanasonicReader.OpenAsync(path);
        Assert.Equal($"MEIHDFS 2.{version}", fs.FileSystemName);
        var recording = Assert.Single(fs.Recordings);
        Assert.Equal(2, fs.Files.Count);
        Assert.Equal("/DVD_RTAV/0001.VRO", recording.FullPath);
        Assert.Equal(payload.Length, recording.SizeBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(12345 + (version < 3 ? 315532800L : 0)), recording.RecordedAt);
        using var input = fs.OpenRead(recording); using var output = new MemoryStream(); input.CopyTo(output);
        Assert.Equal(payload, output.ToArray());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.False(input.CanWrite);
        Assert.Throws<NotSupportedException>(() => input.WriteByte(4));
    }

    [Fact]
    public async Task SeekCrossesExtentsAndIndependentStreamsDoNotShareCursor()
    {
        byte[] payload = Payload(); using var fs = await PanasonicReader.OpenAsync(Save(StorageFixtureBuilder.BuildMeihdfs(payload)));
        using var stream = fs.OpenRead(fs.Recordings[0]); using var second = fs.OpenRead(fs.Recordings[0]);
        stream.Seek(500, SeekOrigin.Begin); byte[] bytes = new byte[80]; stream.ReadExactly(bytes);
        Assert.Equal(payload.AsSpan(500, 80).ToArray(), bytes); Assert.Equal(payload[0], second.ReadByte());
        stream.Seek(-3, SeekOrigin.End); Assert.Equal(payload[^3], stream.ReadByte());
        stream.Seek(10, SeekOrigin.End); Assert.Equal(-1, stream.ReadByte());
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        stream.Position = long.MaxValue; Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.Current));
    }

    [Fact]
    public async Task ZeroLengthFileIsValid()
    {
        using var fs = await PanasonicReader.OpenAsync(Save(StorageFixtureBuilder.BuildMeihdfs([])));
        using var input = fs.OpenRead(fs.Recordings[0]); Assert.Equal(0, input.Length); Assert.Equal(-1, input.ReadByte());
    }

    [Fact]
    public async Task ExtractIsAtomicExactAndRefusesOverwriteOrSource()
    {
        byte[] payload = Payload(); string path = Save(StorageFixtureBuilder.BuildMeihdfs(payload));
        using var fs = await PanasonicReader.OpenAsync(path);
        string destination = System.IO.Path.Combine(_directory, "export.vro");
        await fs.ExtractAsync(fs.Recordings[0], destination);
        Assert.Equal(payload, File.ReadAllBytes(destination));
        await Assert.ThrowsAsync<IOException>(() => fs.ExtractAsync(fs.Recordings[0], destination));
        await Assert.ThrowsAsync<IOException>(() => fs.ExtractAsync(fs.Recordings[0], path));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    public async Task CancellationCleansPartialExtraction()
    {
        string path = Save(StorageFixtureBuilder.BuildMeihdfs(Payload(3 * 1024 * 1024), fragmented: false));
        using var fs = await PanasonicReader.OpenAsync(path); using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value > 0) cancellation.Cancel(); });
        string destination = System.IO.Path.Combine(_directory, "cancelled.vro");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.ExtractAsync(fs.Recordings[0], destination, progress, cancellation.Token));
        Assert.False(File.Exists(destination)); Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    public async Task CancelledScanAndUnsupportedImagesDoNotLeakHandle()
    {
        string path = Save(new byte[500000]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PanasonicReader.OpenAsync(path, cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => PanasonicReader.OpenAsync(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("extent")] [InlineData("size")] [InlineData("pages")] [InlineData("cycle")] [InlineData("name")] [InlineData("inode")] [InlineData("factor")] [InlineData("truncated")]
    public async Task MalformedMeihdfsIsRejected(string fault)
    {
        byte[] image = StorageFixtureBuilder.BuildMeihdfs(Payload());
        int b = StorageFixtureBuilder.DefaultBase, file = b + StorageFixtureBuilder.FileOffset, directory = b + StorageFixtureBuilder.DirectoryOffset;
        switch (fault)
        {
            case "extent": StorageFixtureBuilder.U32(image, file + 256, uint.MaxValue); break;
            case "size": StorageFixtureBuilder.U64(image, file + 16, 100000000); break;
            case "pages": StorageFixtureBuilder.U32(image, directory + 12, uint.MaxValue); break;
            case "cycle": StorageFixtureBuilder.U32(image, directory + 0x420, 1); StorageFixtureBuilder.U16(image, directory + 0x424, 2); break;
            case "name": Encoding.ASCII.GetBytes("../x.VRO").CopyTo(image, directory + 0x428); break;
            case "inode": StorageFixtureBuilder.U32(image, directory + 0x420, 2040); break;
            case "factor": StorageFixtureBuilder.U32(image, file + 8, 0); break;
            case "truncated": image = image[..(b + 4 * StorageFixtureBuilder.AllocationSize + 0x600 + 10)]; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PanasonicReader.OpenAsync(Save(image)));
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(false, false, true)]
    public async Task PanasonicUdfReassemblesShortLongEmbeddedAndUnicodeFiles(bool longAddresses, bool embedded, bool unicode)
    {
        byte[] payload = Payload(embedded ? 511 : 5093);
        string path = Save(StorageFixtureBuilder.BuildUdf(payload, longAddresses, embedded, unicode));
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        using var fs = await PanasonicReader.OpenAsync(path);
        Assert.Equal("Panasonic UDF", fs.FileSystemName);
        var file = Assert.Single(fs.Recordings); Assert.Equal(unicode ? "録画.VRO" : "VR_MOVIE.VRO", file.Name);
        using var input = fs.OpenRead(file); using var output = new MemoryStream(); input.CopyTo(output);
        Assert.Equal(payload, output.ToArray()); Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData("crc")] [InlineData("range")] [InlineData("length")] [InlineData("cycle")]
    public async Task MalformedUdfIsRejected(string fault)
    {
        byte[] image = StorageFixtureBuilder.BuildUdf(Payload());
        int b = StorageFixtureBuilder.DefaultBase, file = b + 3 * 2048;
        switch (fault)
        {
            case "crc": image[file + 56] ^= 1; break;
            case "range": StorageFixtureBuilder.U32(image, file + 180, uint.MaxValue); StorageFixtureBuilder.SetUdfTag(image, file, 261, 176, 3); break;
            case "length": StorageFixtureBuilder.U32(image, file + 172, 2048); StorageFixtureBuilder.SetUdfTag(image, file, 261, 176, 3); break;
            case "cycle":
                int fid = b + 2 * 2048; image[fid + 18] = 2; StorageFixtureBuilder.U32(image, fid + 24, 1);
                StorageFixtureBuilder.SetUdfTag(image, fid, 257, 34, 2); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PanasonicReader.OpenAsync(Save(image)));
    }

    [Fact]
    public async Task DisposingFilesystemInvalidatesItsStreams()
    {
        var fs = await PanasonicReader.OpenAsync(Save(StorageFixtureBuilder.BuildMeihdfs(Payload())));
        using var input = fs.OpenRead(fs.Recordings[0]); fs.Dispose();
        Assert.Throws<ObjectDisposedException>(() => input.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => fs.OpenRead(fs.Recordings[0]));
    }

    [Fact]
    public async Task CannotOpenForeignOrModifiedRecording()
    {
        using var fs = await PanasonicReader.OpenAsync(Save(StorageFixtureBuilder.BuildMeihdfs(Payload())));
        Assert.Throws<ArgumentException>(() => fs.OpenRead(fs.Recordings[0] with { SizeBytes = 1 }));
    }

    [Fact]
    public async Task HeaderCanStartAtImageBeginningAndHdfsAliasWorks()
    {
        byte[] image = StorageFixtureBuilder.BuildMeihdfs(Payload(), baseOffset: 0);
        image.AsSpan(8, 20).Clear(); Encoding.ASCII.GetBytes("HDFS2.0").CopyTo(image, 8);
        using var fs = await PanasonicReader.OpenAsync(Save(image));
        Assert.Single(fs.Recordings);
    }

    [Fact]
    public async Task UnknownFilesystemVersionAndRawVolumePathsAreRejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => PanasonicReader.OpenAsync(Save(StorageFixtureBuilder.BuildMeihdfs(Payload(), version: 9))));
        await Assert.ThrowsAsync<ArgumentException>(() => PanasonicReader.OpenAsync(@"\\.\C:"));
    }

    [Fact]
    public async Task MalformedInodePointerDoesNotEscapeTheImage()
    {
        byte[] image = StorageFixtureBuilder.BuildMeihdfs(Payload());
        StorageFixtureBuilder.U64(image, StorageFixtureBuilder.DefaultBase + StorageFixtureBuilder.InodeTableOffset + 16 + 24, ulong.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(() => PanasonicReader.OpenAsync(Save(image)));
    }

    [Fact]
    public async Task UdfDirectoryPermitsShortTrailingZeroPadding()
    {
        byte[] image = StorageFixtureBuilder.BuildUdf(Payload());
        int root = StorageFixtureBuilder.DefaultBase + 2048;
        StorageFixtureBuilder.U64(image, root + 56, 64); StorageFixtureBuilder.U32(image, root + 176, 64);
        StorageFixtureBuilder.SetUdfTag(image, root, 261, 168, 1);
        using var fs = await PanasonicReader.OpenAsync(Save(image)); Assert.Single(fs.Recordings);
    }

    [Fact]
    public async Task UdfUnrecordedExtentReadsAsZeros()
    {
        byte[] image = StorageFixtureBuilder.BuildUdf(Payload(64));
        int file = StorageFixtureBuilder.DefaultBase + 3 * 2048;
        StorageFixtureBuilder.U32(image, file + 176, 0x80000040); StorageFixtureBuilder.U32(image, file + 180, uint.MaxValue);
        StorageFixtureBuilder.SetUdfTag(image, file, 261, 168, 3);
        using var fs = await PanasonicReader.OpenAsync(Save(image)); using var input = fs.OpenRead(fs.Recordings[0]);
        byte[] bytes = new byte[64]; input.ReadExactly(bytes); Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task UdfContinuationAllocationIsExplicitlyUnsupported()
    {
        byte[] image = StorageFixtureBuilder.BuildUdf(Payload(64));
        int file = StorageFixtureBuilder.DefaultBase + 3 * 2048;
        StorageFixtureBuilder.U32(image, file + 176, 0xC0000040);
        StorageFixtureBuilder.SetUdfTag(image, file, 261, 168, 3);
        await Assert.ThrowsAsync<NotSupportedException>(() => PanasonicReader.OpenAsync(Save(image)));
    }

    [Fact]
    public void DestinationGuardRejectsSourceAndDeviceAliases()
    {
        string source = System.IO.Path.Combine(_directory, "source.dd");
        Assert.Throws<IOException>(() => DiskDiscovery.EnsureSafeDestination(source, source));
        Assert.Throws<IOException>(() => DiskDiscovery.EnsureSafeDestination(source, @"\\.\PhysicalDrive0"));
        Assert.Throws<IOException>(() => DiskDiscovery.EnsureSafeDestination(source, @"\\?\C:\output.mkv"));
        if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => DiskDiscovery.EnsureSafeDestination(@"\\.\PhysicalDrive0", @"\\localhost\C$\output.mkv"));
        DiskDiscovery.EnsureSafeDestination(source, System.IO.Path.Combine(_directory, "output.mkv"));
    }

    [Fact]
    public async Task UdfNestedSparseDirectoriesRespectAggregateMetadataBudget()
    {
        byte[] image = StorageFixtureBuilder.BuildUdf(Payload(64));
        int b = StorageFixtureBuilder.DefaultBase;
        const uint size = 64 * 1024 * 1024;
        // Three 64 MiB logical directories in a tiny image. Unrecorded tail extents
        // exercise the actual traversal budget without allocating a giant source.
        for (uint level = 0; level < 3; level++)
        {
            uint inodeBlock = 1 + level * 2, dataBlock = 2 + level * 2;
            int inode = b + (int)inodeBlock * 2048;
            image.AsSpan(inode, 2048).Clear();
            StorageFixtureBuilder.U16(image, inode + 20, 4); image[inode + 27] = 4;
            StorageFixtureBuilder.U64(image, inode + 56, size); StorageFixtureBuilder.U32(image, inode + 172, 16);
            StorageFixtureBuilder.U32(image, inode + 176, 52); StorageFixtureBuilder.U32(image, inode + 180, dataBlock);
            StorageFixtureBuilder.U32(image, inode + 184, 0x80000000 | (size - 52));
            StorageFixtureBuilder.SetUdfTag(image, inode, 261, 176, inodeBlock);
            int fid = b + (int)dataBlock * 2048;
            image.AsSpan(fid, 2048).Clear(); image[fid + 18] = 2; image[fid + 19] = 6;
            StorageFixtureBuilder.U32(image, fid + 20, 2048); StorageFixtureBuilder.U32(image, fid + 24, inodeBlock + 2);
            image[fid + 38] = 8; Encoding.ASCII.GetBytes("child").CopyTo(image, fid + 39);
            StorageFixtureBuilder.SetUdfTag(image, fid, 257, 28, dataBlock);
        }
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => PanasonicReader.OpenAsync(Save(image)));
        Assert.Contains("128 MiB", exception.Message);
    }

    [Fact]
    public async Task CorruptUnknownEntryWarningsAreCapped()
    {
        byte[] image = StorageFixtureBuilder.BuildMeihdfs(Payload());
        int b = StorageFixtureBuilder.DefaultBase, directory = b + StorageFixtureBuilder.DirectoryOffset;
        image.AsSpan(b + StorageFixtureBuilder.FileOffset, 4096).CopyTo(image.AsSpan(b + 0x140000));
        StorageFixtureBuilder.U64(image, b + StorageFixtureBuilder.InodeTableOffset + 16 + 24, 0x140);
        StorageFixtureBuilder.U32(image, directory + 12, 11);
        image.AsSpan(directory + 0x420, 4096 - 0x420).Clear(); image.AsSpan(directory + 4096, 10 * 4096).Clear();
        for (int page = 0; page < 11; page++)
        {
            int count = page == 0 ? 95 : 100, start = page == 0 ? 0x420 : 0x320;
            for (int index = 0; index < count; index++)
            {
                int at = directory + page * 4096 + start + index * 32;
                StorageFixtureBuilder.U32(image, at, 2); StorageFixtureBuilder.U16(image, at + 4, 3); StorageFixtureBuilder.U16(image, at + 6, 7);
                Encoding.ASCII.GetBytes("unknown").CopyTo(image, at + 8);
            }
        }
        using var fs = await PanasonicReader.OpenAsync(Save(image));
        Assert.Equal(1001, fs.Warnings.Count); Assert.Contains("omitted", fs.Warnings[^1]);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class InlineProgress(Action<double> action) : IProgress<double> { public void Report(double value) => action(value); }
}
