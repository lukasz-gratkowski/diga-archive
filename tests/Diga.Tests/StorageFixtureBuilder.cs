using System.Buffers.Binary;
using System.Text;

namespace Diga.Tests;

/// <summary>Synthetic layouts based on the upstream disk structures, never a real recorder dump.</summary>
public static class StorageFixtureBuilder
{
    public const int AllocationSize = 0xC0000;
    public const int DefaultBase = 0x10000;
    public const int InodeTableOffset = AllocationSize + 0x6000;
    public const int RootOffset = 0x100000;
    public const int DirectoryOffset = 0x101000;
    public const int FileOffset = 0x102000;

    public static byte[] BuildMeihdfs(byte[] payload, bool fragmented = true, int version = 0, int baseOffset = DefaultBase)
    {
        int firstLength = fragmented ? Math.Min(payload.Length, 512) : payload.Length;
        int secondOffset = 4 * AllocationSize + 0x600;
        int total = baseOffset + Math.Max(secondOffset + payload.Length, 5 * AllocationSize);
        byte[] disk = new byte[total];
        Encoding.ASCII.GetBytes($"MEIHDFS-V2.{version}\0").CopyTo(disk, baseOffset + 8);
        int tables = version < 3 ? 6 : 9;
        for (int table = 0; table < tables; table++)
        {
            int at = baseOffset + InodeTableOffset + table * 0x1000;
            if (table is 0 or 3)
            {
                U32(disk, at, 1); U32(disk, at + 4, 4);
                TablePointer(disk, at + 16, RootOffset / 0x1000);
            }
            if (table == 0)
            {
                TablePointer(disk, at + 16 + 12, DirectoryOffset / 0x1000);
                TablePointer(disk, at + 16 + 24, FileOffset / 0x1000);
                TablePointer(disk, at + 16 + 36, 0x103);
            }
        }
        int root = baseOffset + RootOffset;
        U32(disk, root + 12, 1); U32(disk, root + 28, 0x41FF0001);
        MeiDirectoryEntry(disk, root + 0x420, 1, 2, "DVD_RTAV");
        int directory = baseOffset + DirectoryOffset;
        U32(disk, directory + 12, 1); U32(disk, directory + 28, 0x41C20001);
        MeiDirectoryEntry(disk, directory + 0x420, 2, 1, "0001.VRO");
        MeiDirectoryEntry(disk, directory + 0x440, 3, 1, "0001.IFO");
        int inode = baseOffset + FileOffset;
        U32(disk, inode + 4, 2); U32(disk, inode + 8, 1); U32(disk, inode + 12, 1);
        U64(disk, inode + 16, (ulong)payload.Length); U32(disk, inode + 28, 0x81C00001); U32(disk, inode + 40, 12345);
        if (payload.Length > 0)
        {
            U32(disk, inode + 256, 2); U32(disk, inode + 264, (uint)((firstLength + 511) / 512));
            payload.AsSpan(0, firstLength).CopyTo(disk.AsSpan(baseOffset + 2 * AllocationSize));
        }
        if (firstLength < payload.Length)
        {
            U32(disk, inode + 268, 4); U32(disk, inode + 272, 1); U32(disk, inode + 276, (uint)((payload.Length - firstLength + 511) / 512));
            payload.AsSpan(firstLength).CopyTo(disk.AsSpan(baseOffset + secondOffset));
        }
        U32(disk, baseOffset + 0x103000 + 28, 0x81C00001);
        return disk;
    }

    private static void TablePointer(byte[] disk, int at, uint units) { U64(disk, at, units); U16(disk, at + 8, 1); U16(disk, at + 10, 1); }
    private static void MeiDirectoryEntry(byte[] disk, int at, uint id, ushort type, string name)
    {
        U32(disk, at, id); U16(disk, at + 4, type); U16(disk, at + 6, (ushort)name.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(disk, at + 8);
    }

    public static byte[] BuildUdf(byte[] payload, bool longAddresses = false, bool embedded = false, bool unicode = false)
    {
        if (embedded && payload.Length > 1872) throw new ArgumentOutOfRangeException(nameof(payload));
        const int blockSize = 2048;
        byte[] disk = new byte[DefaultBase + 12 * blockSize + payload.Length];
        int fsd = DefaultBase;
        U32(disk, fsd + 400, blockSize); U32(disk, fsd + 404, 1);
        SetUdfTag(disk, fsd, 256, 496, 0);
        string name = unicode ? "録画.VRO" : "VR_MOVIE.VRO";
        byte[] nameBytes = unicode ? Encoding.BigEndianUnicode.GetBytes(name) : Encoding.Latin1.GetBytes(name);
        byte[] nameWithCompression = new byte[nameBytes.Length + 1]; nameWithCompression[0] = unicode ? (byte)16 : (byte)8;
        nameBytes.CopyTo(nameWithCompression, 1);
        int fidLength = (38 + nameWithCompression.Length + 3) & ~3;
        int root = DefaultBase + blockSize;
        U16(disk, root + 20, 4); disk[root + 27] = 4;
        U16(disk, root + 84, 0x1000); U16(disk, root + 86, 2024); disk[root + 88] = 1; disk[root + 89] = 2;
        U64(disk, root + 56, (ulong)fidLength); U32(disk, root + 172, 8);
        U32(disk, root + 176, (uint)fidLength); U32(disk, root + 180, 2);
        SetUdfTag(disk, root, 261, 168, 1);
        int fid = DefaultBase + 2 * blockSize;
        U16(disk, fid + 16, 1); disk[fid + 19] = (byte)nameWithCompression.Length;
        U32(disk, fid + 20, blockSize); U32(disk, fid + 24, 3);
        nameWithCompression.CopyTo(disk, fid + 38);
        SetUdfTag(disk, fid, 257, (ushort)(38 + nameWithCompression.Length - 16), 2);
        int file = DefaultBase + 3 * blockSize;
        U16(disk, file + 20, 4); disk[file + 27] = 5;
        U16(disk, file + 34, embedded ? (ushort)3 : longAddresses ? (ushort)1 : (ushort)0);
        U64(disk, file + 56, (ulong)payload.Length);
        U16(disk, file + 84, 0x1000); U16(disk, file + 86, 2024); disk[file + 88] = 1; disk[file + 89] = 2;
        if (embedded)
        {
            U32(disk, file + 172, (uint)payload.Length);
            payload.CopyTo(disk, file + 176);
            SetUdfTag(disk, file, 261, (ushort)(160 + payload.Length), 3);
        }
        else
        {
            int descriptorSize = longAddresses ? 16 : 8;
            int firstLength = Math.Min(2048, payload.Length);
            bool second = payload.Length > firstLength;
            U32(disk, file + 172, (uint)(descriptorSize * (second ? 2 : 1)));
            U32(disk, file + 176, (uint)firstLength); U32(disk, file + 180, 4);
            payload.AsSpan(0, firstLength).CopyTo(disk.AsSpan(DefaultBase + 4 * blockSize));
            if (second)
            {
                U32(disk, file + 176 + descriptorSize, (uint)(payload.Length - firstLength)); U32(disk, file + 180 + descriptorSize, 8);
                payload.AsSpan(firstLength).CopyTo(disk.AsSpan(DefaultBase + 8 * blockSize));
            }
            SetUdfTag(disk, file, 261, (ushort)(160 + descriptorSize * (second ? 2 : 1)), 3);
        }
        return disk;
    }

    public static void SetUdfTag(byte[] bytes, int at, ushort id, ushort crcLength, uint location)
    {
        U16(bytes, at, id); U16(bytes, at + 2, 2); U16(bytes, at + 6, 1); U16(bytes, at + 10, crcLength); U32(bytes, at + 12, location);
        ushort crc = 0;
        for (int i = at + 16; i < at + 16 + crcLength; i++)
        {
            crc ^= (ushort)(bytes[i] << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        U16(bytes, at + 8, crc);
        int checksum = 0; for (int i = 0; i < 16; i++) if (i != 4) checksum += bytes[at + i];
        bytes[at + 4] = (byte)checksum;
    }
    public static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at, 2), value);
    public static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
    public static void U64(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at, 8), value);
}
