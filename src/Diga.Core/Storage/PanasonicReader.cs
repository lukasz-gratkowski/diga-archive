using L = Diga.Core.Localization.AppText;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Diga.Core.Storage;

/// <summary>Read-only bounded managed port of the Panasonic layout detection in leecher1337/panasonic-rec.</summary>
public static class PanasonicReader
{
    public static Task<PanasonicFileSystem> OpenAsync(string sourcePath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(sourcePath, progress, cancellationToken), cancellationToken);

    private static PanasonicFileSystem Open(string path, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = new DiskByteSource(path);
        try
        {
            var watch = Stopwatch.StartNew();
            progress?.Report(new ScanProgress(0, source.Length, L.T("Core.Storage.Scan.Searching")));
            for (long offset = 0; offset <= source.Length - 512; offset = checked(offset + 0x10000))
            {
                token.ThrowIfCancellationRequested();
                byte[] header = source.ReadBytes(offset, 512);
                if (header.AsSpan(8, 11).SequenceEqual("MEIHDFS-V2."u8) || header.AsSpan(8, 6).SequenceEqual("HDFS2."u8))
                {
                    int version = header[8] == 'M' ? header[19] - '0' : header[14] - '0';
                    if (version is < 0 or > 3) throw new NotSupportedException(L.T("Core.Storage.Meihdfs.VersionUnsupported", version));
                    progress?.Report(new ScanProgress(offset, source.Length, L.T("Core.Storage.Scan.ReadingDirectory")));
                    var result = new MeihdfsParser(source, offset, version, token).Parse();
                    progress?.Report(new ScanProgress(source.Length, source.Length, L.Plural("Core.Storage.Scan.FoundVideos", result.Recordings.Count)));
                    return result;
                }
                if (UdfParser.IsTag(header, 256))
                {
                    progress?.Report(new ScanProgress(offset, source.Length, L.T("Core.Storage.Scan.ReadingUdfDirectory")));
                    var result = new UdfParser(source, offset, token).Parse();
                    progress?.Report(new ScanProgress(source.Length, source.Length, L.Plural("Core.Storage.Scan.FoundVideos", result.Recordings.Count)));
                    return result;
                }
                if (watch.ElapsedMilliseconds >= 200)
                {
                    progress?.Report(new ScanProgress(offset, source.Length, L.T("Core.Storage.Scan.Searching")));
                    watch.Restart();
                }
            }
            throw new NotSupportedException(L.T("Core.Storage.Scan.Unsupported"));
        }
        catch (OverflowException ex) { source.Dispose(); throw new InvalidDataException(L.T("Core.Storage.Format.OutOfRange"), ex); }
        catch { source.Dispose(); throw; }
    }
}

internal static class DiskFormat
{
    internal static ushort U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(at, 2));
    internal static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4));
    internal static ulong U64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(at, 8));
    internal static bool IsVideo(string name) => System.IO.Path.GetExtension(name).ToUpperInvariant() is ".VRO" or ".VOB" or ".MTS" or ".M2TS" or ".TS" or ".MPG" or ".MPEG" or ".TOD";
    internal static string SafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(['/', '\\', '\0', ':']) >= 0 || value.Any(char.IsControl))
            throw new InvalidDataException(L.T("Core.Storage.Format.UnsafeFileName"));
        return value;
    }
}

// Derived from extract_meihdfs.c and meihdfs1.h, Copyright (C) 2012 Honza Maly,
// Copyright (C) 2015-2016 leecher@dose.0wnz.at. GPL-2.0-or-later.
// Managed bounds, cancellation and stream support Copyright (C) 2026 DIGA contributors.
internal sealed class MeihdfsParser(DiskByteSource source, long start, int version, CancellationToken token)
{
    private const long AllocationSize = 0xC0000;
    private const int InodeSize = 0x1000;
    private const int TableEntryCount = 0x154;
    private readonly List<FileLayout> _files = [];
    private readonly List<string> _warnings = [L.T("Core.Storage.Format.WholeContainers")];
    private readonly HashSet<long> _visitedDirectories = [];
    private ulong[] _inodes = [];
    private readonly List<ulong[]> _backupTables = [];
    private bool _backupsLoaded;
    private long _fileMetadataBytes;
    private long _directoryBytes;
    private int _visitedEntries;

    internal PanasonicFileSystem Parse()
    {
        _inodes = ReadTables(start);
        if (_inodes.Length == 0 || _inodes[0] == 0) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.RootTableMissing"));
        long rootAt = InodeOffset(_inodes[0]);
        byte[] root = source.ReadBytes(rootAt, InodeSize);
        if (DiskFormat.U32(root, 28) != 0x41FF0001) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.RootSignatureInvalid"));
        WalkDirectory(rootAt, root, "", 0);
        return new PanasonicFileSystem(source, FormattableString.Invariant($"MEIHDFS 2.{version}"), _files, _warnings);
    }

    private ulong[] ReadTables(long headerAt)
    {
        List<ulong> entries = [];
        int required = version < 3 ? 6 : 9;
        int tables = 0;
        for (int relative = 0; relative < 0x20000 && tables < required; relative += InodeSize)
        {
            token.ThrowIfCancellationRequested();
            long position = checked(headerAt + AllocationSize + 0x6000 + relative);
            if (position > source.Length - InodeSize) break;
            byte[] table = source.ReadBytes(position, InodeSize);
            uint generation = DiskFormat.U32(table, 0);
            bool headerMatches = generation is > 0 and <= 0xFFFF && DiskFormat.U32(table, 4) != 0 && DiskFormat.U32(table, 8) == 0 && DiskFormat.U32(table, 12) == 0;
            if (!(tables > 0 && tables != 3) && !headerMatches) continue;
            bool valid = true;
            bool nonzero = false;
            var pointers = new ulong[TableEntryCount];
            for (int entry = 0; entry < TableEntryCount; entry++)
            {
                int at = 16 + entry * 12;
                ulong pointer = DiskFormat.U64(table, at);
                ushort flag1 = DiskFormat.U16(table, at + 8), flag2 = DiskFormat.U16(table, at + 10);
                if (pointer == 0 && flag1 == 0 && flag2 == 0) continue;
                nonzero = true;
                if (pointer == 0 || flag1 != 1 || flag2 != 1 || pointer > (ulong)Math.Max(0, (source.Length - start - InodeSize) / InodeSize)) { valid = false; break; }
                pointers[entry] = pointer;
            }
            if (!valid || generation != 0 && !nonzero) continue;
            entries.AddRange(pointers);
            tables++;
        }
        if (headerAt == start && tables < required) AddWarning(L.T("Core.Storage.Meihdfs.TablesMissing", tables, required));
        return entries.ToArray();
    }

    private long InodeOffset(ulong inode) => checked(start + checked((long)inode * InodeSize));

    private void WalkDirectory(long directoryOffset, byte[] first, string parent, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 64 || !_visitedDirectories.Add(directoryOffset)) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.DirectoryCycle"));
        uint pageCount = DiskFormat.U32(first, 12);
        if (pageCount is 0 or > 4096) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.PageCountInvalid"));
        _directoryBytes = checked(_directoryBytes + (long)pageCount * InodeSize);
        if (_directoryBytes > 128 * 1024 * 1024) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.DirectoryMetadataLimit"));
        source.CheckRange(directoryOffset, checked((long)pageCount * InodeSize));
        for (int page = 0; page < pageCount; page++)
        {
            token.ThrowIfCancellationRequested();
            byte[] data = page == 0 ? first : source.ReadBytes(directoryOffset + page * InodeSize, InodeSize);
            int firstEntry = page == 0 ? 0x420 : 0x320;
            int count = page == 0 ? 95 : 100;
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                int at = firstEntry + index * 32;
                uint id = DiskFormat.U32(data, at);
                if (id is 0 or uint.MaxValue) continue;
                if (++_visitedEntries > 200000) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.EntryCountLimit"));
                if (id >= _inodes.Length || _inodes[id] == 0) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.InodeMissing", id));
                int nameLength = DiskFormat.U16(data, at + 6);
                if (nameLength is <= 0 or > 24) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.FileNameLimit"));
                string name = DiskFormat.SafeName(Encoding.Latin1.GetString(data, at + 8, nameLength).TrimEnd('\0'));
                string fullPath = parent + "/" + name;
                if (fullPath.Length > 8192 || _files.Count >= 200000) throw new InvalidDataException(L.T("Core.Storage.Format.TraversalLimit"));
                long inodeAt = InodeOffset(_inodes[id]);
                byte[] inode = source.ReadBytes(inodeAt, InodeSize);
                uint magic = DiskFormat.U32(inode, 28);
                ushort type = DiskFormat.U16(data, at + 4);
                if (type == 2)
                {
                    if ((magic & 0xFF00FFFF) != 0x41000001) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.DirectorySignatureInvalid", id));
                    WalkDirectory(inodeAt, inode, fullPath, depth + 1);
                }
                else if (type == 1)
                {
                    if ((magic & 0xFF00FFFF) != 0x81000001) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.FileSignatureInvalid", id));
                    if (DiskFormat.U64(inode, 16) != 0 && DiskFormat.U32(inode, 256) == 0) inode = FindBackup(id, inode);
                    long size = checked((long)DiskFormat.U64(inode, 16));
                    var extents = ReadExtents(inode, size);
                    DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(DiskFormat.U32(inode, 40) + (version < 3 ? 315532800L : 0));
                    var file = new RecordingFile(FormattableString.Invariant($"mei:{id}:{fullPath}"), name, fullPath, size, timestamp, DiskFormat.IsVideo(name));
                    _fileMetadataBytes = checked(_fileMetadataBytes + 256 + fullPath.Length * 4L + name.Length * 2L + extents.Count * 32L);
                    if (_fileMetadataBytes > 64 * 1024 * 1024) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.FileMetadataLimit"));
                    _files.Add(new FileLayout(file, extents));
                }
                else AddWarning(L.T("Core.Storage.Meihdfs.UnknownEntrySkipped", type, fullPath));
            }
        }
    }

    private byte[] FindBackup(uint id, byte[] original)
    {
        if (!_backupsLoaded)
        {
            _backupsLoaded = true;
            long interval = 0x10000 * AllocationSize;
            for (long at = checked(start + interval); at < source.Length; at = checked(at + interval)) _backupTables.Add(ReadTables(at));
        }
        foreach (var table in _backupTables)
        {
            token.ThrowIfCancellationRequested();
            if (id >= table.Length || table[id] == 0 || table[id] == _inodes[id]) continue;
            byte[] inode = source.ReadBytes(InodeOffset(table[id]), InodeSize);
            if ((DiskFormat.U32(inode, 28) & 0xFF00FFFF) == 0x81000001 && DiskFormat.U32(inode, 256) != 0)
            {
                AddWarning(L.T("Core.Storage.Meihdfs.RecoveredInode", id));
                return inode;
            }
        }
        return original;
    }

    private void AddWarning(string message)
    {
        if (_warnings.Count < 1000) _warnings.Add(message);
        else if (_warnings.Count == 1000) _warnings.Add(L.T("Core.Storage.Meihdfs.WarningsTruncated"));
    }

    private List<DataExtent> ReadExtents(byte[] inode, long size)
    {
        uint factor = DiskFormat.U32(inode, 8);
        List<DataExtent> extents = [];
        long remaining = size;
        for (int i = 0; i < 0x140 && remaining > 0; i++)
        {
            int at = 256 + i * 12;
            uint block = DiskFormat.U32(inode, at);
            if (block == 0) break;
            long offset = checked(start + block * AllocationSize + DiskFormat.U32(inode, at + 4) * 0x600L);
            // Upstream run.len * inode.factor units are 512 bytes (BSIZE / 4).
            long capacity = checked((long)DiskFormat.U32(inode, at + 8) * factor * 512);
            if (capacity <= 0) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.EmptyAllocation"));
            long count = Math.Min(remaining, capacity);
            source.CheckRange(offset, count);
            extents.Add(new DataExtent(offset, count));
            remaining -= count;
        }
        if (remaining != 0) throw new InvalidDataException(L.T("Core.Storage.Meihdfs.FileIncomplete"));
        return extents;
    }
}
