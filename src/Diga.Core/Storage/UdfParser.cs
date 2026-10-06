using L = Diga.Core.Localization.AppText;
using System.Text;

namespace Diga.Core.Storage;

// Panasonic-specific filesystem discovery and allocation traversal adapted from
// panasonic-rec/udf/pana-udf: Copyright (C) 2005, 2006, 2008 Rocky Bernstein;
// Copyright (C) 2015 leecher@dose.0wnz.at. GPL-3.0-or-later.
// See docs/STORAGE.md for Scott Long BSD attribution retained from udf_fs.c.
internal sealed class UdfParser(DiskByteSource source, long partitionStart, CancellationToken token)
{
    private const int BlockSize = 2048;
    private readonly List<FileLayout> _files = [];
    private readonly HashSet<uint> _directories = [];
    private long _directoryBytes;
    private long _fileMetadataBytes;

    internal static bool IsTag(ReadOnlySpan<byte> data, ushort id)
    {
        if (data.Length < 16 || DiskFormat.U16(data, 0) != id) return false;
        int checksum = 0;
        for (int i = 0; i < 16; i++) if (i != 4) checksum += data[i];
        return (byte)checksum == data[4];
    }

    private static void ValidateTag(ReadOnlySpan<byte> data, ushort id)
    {
        if (!IsTag(data, id)) throw new InvalidDataException(L.T("Core.Storage.Udf.SignatureInvalid", id));
        int length = DiskFormat.U16(data, 10);
        if (length > data.Length - 16) throw new InvalidDataException(L.T("Core.Storage.Udf.CrcLengthInvalid"));
        ushort crc = 0;
        for (int i = 16; i < 16 + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        if (crc != DiskFormat.U16(data, 8)) throw new InvalidDataException(L.T("Core.Storage.Udf.CrcInvalid"));
    }

    internal PanasonicFileSystem Parse()
    {
        byte[] fsd = source.ReadBytes(partitionStart, BlockSize);
        ValidateTag(fsd, 256);
        if (DiskFormat.U16(fsd, 408) != 0) throw new NotSupportedException(L.T("Core.Storage.Udf.PartitionMappingUnsupported"));
        uint root = DiskFormat.U32(fsd, 404);
        WalkDirectory(root, "", 0);
        return new PanasonicFileSystem(source, "Panasonic UDF", _files,
            [L.T("Core.Storage.Format.WholeContainers")]);
    }

    private long BlockOffset(uint block) => checked(partitionStart + (long)block * BlockSize);
    private (byte[] Data, long Offset, long Size, List<DataExtent> Extents) ReadEntry(uint block)
    {
        token.ThrowIfCancellationRequested();
        long offset = BlockOffset(block);
        byte[] entry = source.ReadBytes(offset, BlockSize);
        if (DiskFormat.U16(entry, 0) == 266) throw new NotSupportedException(L.T("Core.Storage.Udf.ExtendedEntriesUnsupported"));
        ValidateTag(entry, 261);
        if (DiskFormat.U16(entry, 20) != 4) throw new NotSupportedException(L.T("Core.Storage.Udf.AllocationStrategyUnsupported"));
        long size = checked((long)DiskFormat.U64(entry, 56));
        uint extended = DiskFormat.U32(entry, 168), allocations = DiskFormat.U32(entry, 172);
        if (extended > BlockSize - 176 || allocations > BlockSize - 176 - extended) throw new InvalidDataException(L.T("Core.Storage.Udf.DescriptorsTooLarge"));
        int descriptorOffset = checked(176 + (int)extended);
        int addressType = DiskFormat.U16(entry, 34) & 7;
        List<DataExtent> extents = [];
        if (addressType == 3)
        {
            if (size > allocations) throw new InvalidDataException(L.T("Core.Storage.Udf.EmbeddedDataShort"));
            if (size != 0) extents.Add(new DataExtent(offset + descriptorOffset, size));
        }
        else
        {
            int descriptorSize = addressType switch { 0 => 8, 1 => 16, _ => throw new NotSupportedException(L.T("Core.Storage.Udf.ExtendedAllocationUnsupported")) };
            if (allocations % descriptorSize != 0) throw new InvalidDataException(L.T("Core.Storage.Udf.DescriptorTruncated"));
            long remaining = size;
            for (int at = descriptorOffset; at < descriptorOffset + allocations && remaining > 0; at += descriptorSize)
            {
                uint rawLength = DiskFormat.U32(entry, at);
                uint extentType = rawLength >> 30;
                long capacity = rawLength & 0x3FFFFFFF;
                if (extentType == 3) throw new NotSupportedException(L.T("Core.Storage.Udf.ContinuationUnsupported"));
                if (addressType == 1 && DiskFormat.U16(entry, at + 8) != 0) throw new NotSupportedException(L.T("Core.Storage.Udf.MultiPartitionAllocationUnsupported"));
                if (capacity == 0) continue;
                long count = Math.Min(capacity, remaining);
                long dataAt = BlockOffset(DiskFormat.U32(entry, at + 4));
                if (extentType == 0) source.CheckRange(dataAt, count);
                extents.Add(new DataExtent(dataAt, count, extentType is 1 or 2));
                remaining -= count;
            }
            if (remaining != 0) throw new InvalidDataException(L.T("Core.Storage.Udf.AllocationShort"));
        }
        return (entry, offset, size, extents);
    }

    private void WalkDirectory(uint block, string parent, int depth)
    {
        if (depth > 64 || _directories.Count >= 20000 || !_directories.Add(block)) throw new InvalidDataException(L.T("Core.Storage.Udf.DirectoryLimit"));
        var directory = ReadEntry(block);
        if (directory.Data[27] != 4) throw new InvalidDataException(L.T("Core.Storage.Udf.DirectoryTypeInvalid"));
        if (directory.Size > 64 * 1024 * 1024) throw new InvalidDataException(L.T("Core.Storage.Udf.DirectorySizeLimit"));
        _directoryBytes = checked(_directoryBytes + directory.Size);
        if (_directoryBytes > 128 * 1024 * 1024) throw new InvalidDataException(L.T("Core.Storage.Udf.DirectoryMetadataLimit"));
        byte[] data = new byte[(int)directory.Size];
        using (var stream = new ExtentReadStream(source, directory.Extents, directory.Size)) stream.ReadExactly(data);
        for (int offset = 0; offset < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> fid = data.AsSpan(offset);
            if (fid.Length < 38)
            {
                if (!fid.ContainsAnyExcept((byte)0)) break;
                throw new InvalidDataException(L.T("Core.Storage.Udf.EntryTruncated"));
            }
            if (DiskFormat.U16(fid, 0) == 0)
            {
                // Some directories pad the final block with zero bytes.
                if (!fid.ContainsAnyExcept((byte)0)) break;
                throw new InvalidDataException(L.T("Core.Storage.Udf.PaddingInvalid"));
            }
            int nameLength = fid[19], implementationLength = DiskFormat.U16(fid, 36);
            int entryLength = (38 + nameLength + implementationLength + 3) & ~3;
            if (entryLength > fid.Length) throw new InvalidDataException(L.T("Core.Storage.Udf.FileNameAllocationExceeded"));
            ValidateTag(fid[..entryLength], 257);
            byte characteristics = fid[18];
            if ((characteristics & (4 | 8)) == 0)
            {
                if (DiskFormat.U16(fid, 28) != 0) throw new NotSupportedException(L.T("Core.Storage.Udf.MultiPartitionReferenceUnsupported"));
                string name = DecodeName(fid.Slice(38 + implementationLength, nameLength));
                string fullPath = parent + "/" + name;
                if (fullPath.Length > 8192 || _files.Count >= 200000) throw new InvalidDataException(L.T("Core.Storage.Format.TraversalLimit"));
                uint child = DiskFormat.U32(fid, 24);
                if ((characteristics & 2) != 0) WalkDirectory(child, fullPath, depth + 1);
                else
                {
                    var entry = ReadEntry(child);
                    if (entry.Data[27] != 5) throw new NotSupportedException(L.T("Core.Storage.Udf.FileTypeUnsupported"));
                    var file = new RecordingFile(FormattableString.Invariant($"udf:{child}:{fullPath}"), name, fullPath, entry.Size, ReadTime(entry.Data.AsSpan(84, 12)), DiskFormat.IsVideo(name));
                    _fileMetadataBytes = checked(_fileMetadataBytes + 256 + fullPath.Length * 4L + name.Length * 2L + entry.Extents.Count * 32L);
                    if (_fileMetadataBytes > 64 * 1024 * 1024) throw new InvalidDataException(L.T("Core.Storage.Udf.FileMetadataLimit"));
                    _files.Add(new FileLayout(file, entry.Extents));
                }
            }
            offset += entryLength;
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty) throw new InvalidDataException(L.T("Core.Storage.Udf.FileNameEmpty"));
        string decoded = name[0] switch
        {
            8 => Encoding.Latin1.GetString(name[1..]),
            16 when (name.Length - 1) % 2 == 0 => Encoding.BigEndianUnicode.GetString(name[1..]),
            _ => throw new InvalidDataException(L.T("Core.Storage.Udf.FileNameEncodingUnsupported"))
        };
        return DiskFormat.SafeName(decoded);
    }

    private static DateTimeOffset? ReadTime(ReadOnlySpan<byte> time)
    {
        int minutes = DiskFormat.U16(time, 0) & 0xFFF;
        if ((minutes & 0x800) != 0) minutes -= 0x1000;
        if (minutes is < -840 or > 840) return null;
        try { return new DateTimeOffset(DiskFormat.U16(time, 2), time[4], time[5], time[6], time[7], time[8], TimeSpan.FromMinutes(minutes)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
