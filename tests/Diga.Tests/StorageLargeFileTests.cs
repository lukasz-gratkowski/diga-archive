using Diga.Core.Storage;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Diga.Tests;

/// <summary>Exercises 64-bit recorder sizes/addresses using newly created sparse NTFS test images.</summary>
public sealed class StorageLargeFileTests
{
    [Fact]
    public async Task RecordingLargerThanFourGiBListsAndSeeksWithoutSizeTruncation()
    {
        if (!OperatingSystem.IsWindows()) return; // This regression specifically exercises Windows NTFS sparse I/O.
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        const long fileLength = fourGiB + 16_387;
        long dataOffset = StorageFixtureBuilder.DefaultBase + 2L * StorageFixtureBuilder.AllocationSize;
        byte[] fixture = StorageFixtureBuilder.BuildMeihdfs([]);
        int inode = StorageFixtureBuilder.DefaultBase + StorageFixtureBuilder.FileOffset;
        StorageFixtureBuilder.U64(fixture, inode + 16, (ulong)fileLength);
        StorageFixtureBuilder.U32(fixture, inode + 256, 2);
        StorageFixtureBuilder.U32(fixture, inode + 264, checked((uint)((fileLength + 511) / 512)));

        byte[] start = Encoding.ASCII.GetBytes("START-OF-LARGE-VRO");
        byte[] crossing = Encoding.ASCII.GetBytes("CROSS-4GiB-BOUNDARY");
        byte[] tail = Encoding.ASCII.GetBytes("FINAL-VRO-BYTES");
        using var image = new SparseImage(fixture, checked(dataOffset + fileLength), writer =>
        {
            WriteAt(writer, dataOffset, start);
            WriteAt(writer, dataOffset + fourGiB - 9, crossing);
            WriteAt(writer, dataOffset + fileLength - tail.Length, tail);
        });
        Assert.Equal(dataOffset + fileLength, new FileInfo(image.Path).Length);
        AssertSparseAllocationIsSmall(image.Path);

        using var filesystem = await PanasonicReader.OpenAsync(image.Path);
        var recording = Assert.Single(filesystem.Recordings);
        Assert.Equal(fileLength, recording.SizeBytes);
        using var stream = filesystem.OpenRead(recording);
        Assert.Equal(fileLength, stream.Length);
        byte[] startRead = new byte[start.Length];
        stream.ReadExactly(startRead);
        Assert.Equal(start, startRead);

        Assert.Equal(fourGiB - 17, stream.Seek(fourGiB - 17, SeekOrigin.Begin));
        byte[] crossingRead = new byte[64];
        stream.ReadExactly(crossingRead);
        byte[] crossingExpected = new byte[64];
        crossing.CopyTo(crossingExpected, 8);
        Assert.Equal(crossingExpected, crossingRead);
        Assert.Equal(fourGiB + 47, stream.Position);

        Assert.Equal(fileLength - tail.Length, stream.Seek(-tail.Length, SeekOrigin.End));
        byte[] tailRead = new byte[tail.Length];
        stream.ReadExactly(tailRead);
        Assert.Equal(tail, tailRead);
        Assert.Equal(fileLength, stream.Position);
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public async Task FragmentedRecordingCanReadAnExtentBeyondFourGiB()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] payload = Enumerable.Range(0, 8193).Select(value => (byte)(value * 29 + 7)).ToArray();
        byte[] fixture = StorageFixtureBuilder.BuildMeihdfs(payload);
        int inode = StorageFixtureBuilder.DefaultBase + StorageFixtureBuilder.FileOffset;
        const uint highBlock = 8192; // 8192 * 0xC0000 = 6 GiB, plus filesystem base and run offset.
        long highOffset = StorageFixtureBuilder.DefaultBase + (long)highBlock * StorageFixtureBuilder.AllocationSize + 0x600;
        StorageFixtureBuilder.U32(fixture, inode + 268, highBlock);
        using var image = new SparseImage(fixture, highOffset + payload.Length - 512,
            writer => WriteAt(writer, highOffset, payload.AsSpan(512)));
        Assert.True(highOffset > uint.MaxValue);
        AssertSparseAllocationIsSmall(image.Path);

        using var filesystem = await PanasonicReader.OpenAsync(image.Path);
        var recording = Assert.Single(filesystem.Recordings);
        Assert.Equal(payload.LongLength, recording.SizeBytes);
        using var stream = filesystem.OpenRead(recording);
        byte[] actual = new byte[payload.Length];
        stream.ReadExactly(actual); // Reads only 8193 bytes across low and >4 GiB physical extents.
        Assert.Equal(payload, actual);
        Assert.Equal(-1, stream.ReadByte());
        stream.Seek(500, SeekOrigin.Begin);
        byte[] boundary = new byte[64];
        stream.ReadExactly(boundary);
        Assert.Equal(payload.AsSpan(500, boundary.Length).ToArray(), boundary);
    }

    private static void WriteAt(FileStream writer, long offset, ReadOnlySpan<byte> bytes)
    {
        writer.Position = offset;
        writer.Write(bytes);
    }

    private static void AssertSparseAllocationIsSmall(string path)
    {
        Assert.True((File.GetAttributes(path) & FileAttributes.SparseFile) != 0);
        uint low = GetCompressedFileSizeW(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
            throw new IOException("Cannot query the sparse fixture's allocated size.", new Win32Exception(Marshal.GetLastWin32Error()));
        long allocated = checked((long)(((ulong)high << 32) | low));
        // Metadata fixture plus a few marker clusters: never allocate the logical 4–6 GiB.
        Assert.InRange(allocated, 1, 16L * 1024 * 1024);
    }

    private sealed class SparseImage : IDisposable
    {
        private readonly string _directory;
        public string Path { get; }

        internal SparseImage(byte[] initialData, long logicalLength, Action<FileStream> writeMarkers)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DigaLargeStorageTests-" + Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(_directory, "sparse-recorder.dd");
            Assert.Equal("NTFS", new DriveInfo(System.IO.Path.GetPathRoot(_directory)!).DriveFormat.ToUpperInvariant());
            Directory.CreateDirectory(_directory);
            try
            {
                // CreateNew and a fresh GUID directory ensure the sparse control can only
                // affect this newly owned fixture, never a recorder or an existing user file.
                using var writer = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                if (!DeviceIoControl(writer.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new IOException("Could not mark the new NTFS fixture sparse.", new Win32Exception(Marshal.GetLastWin32Error()));
                writer.Write(initialData);
                writer.SetLength(logicalLength);
                writeMarkers(writer);
                writer.Flush(flushToDisk: true);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            // Remove only our exact new file and then its empty directory; no recursive cleanup.
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, IntPtr input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string filename, out uint high);
}
