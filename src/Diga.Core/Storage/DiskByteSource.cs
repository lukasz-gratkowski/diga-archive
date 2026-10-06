using L = Diga.Core.Localization.AppText;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Diga.Core.Storage;

internal sealed class DiskByteSource : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly int _sectorSize;
    private bool _disposed;
    public string Path { get; }
    public long Length { get; }
    internal static bool IsPhysicalPath(string path) => Regex.IsMatch(path, @"^\\\\\.\\PhysicalDrive[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal DiskByteSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        if (IsPhysicalPath(path))
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Core.Storage.Disk.WindowsRequired"));
            _handle = NativeDisk.CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                _handle.Dispose();
                if (error == 5) throw new UnauthorizedAccessException(L.T("Core.Storage.Disk.AdministratorRequired"));
                throw new IOException(L.T("Core.Storage.Disk.OpenFailed", new Win32Exception(error).Message));
            }
            try
            {
                byte[] geometry = NativeDisk.Query(_handle, 0x700A0, 256);
                if (geometry.Length < 32) throw new IOException(L.T("Core.Storage.Disk.GeometryIncomplete"));
                _sectorSize = BitConverter.ToInt32(geometry, 20);
                Length = BitConverter.ToInt64(geometry, 24);
                if (_sectorSize is < 512 or > 65536 || (_sectorSize & (_sectorSize - 1)) != 0 || Length < _sectorSize)
                    throw new IOException(L.T("Core.Storage.Disk.GeometryUnsupported"));
            }
            catch { _handle.Dispose(); throw; }
        }
        else
        {
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                throw new ArgumentException(L.T("Core.Storage.Disk.SourceInvalid"), nameof(path));
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            try { Length = RandomAccess.GetLength(_handle); }
            catch { _handle.Dispose(); throw; }
        }
    }

    internal void CheckRange(long offset, long count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
            throw new InvalidDataException(L.T("Core.Storage.Disk.ExtentOutOfRange", offset, count, Length));
    }

    internal byte[] ReadBytes(long offset, int count)
    {
        byte[] result = new byte[count];
        ReadExactly(offset, result);
        return result;
    }

    internal void ReadExactly(long offset, Span<byte> buffer)
    {
        CheckRange(offset, buffer.Length);
        if (buffer.IsEmpty) return;
        if (_sectorSize != 0)
        {
            long alignedOffset = offset / _sectorSize * _sectorSize;
            int prefix = (int)(offset - alignedOffset);
            int alignedCount = checked((prefix + buffer.Length + _sectorSize - 1) / _sectorSize * _sectorSize);
            byte[] aligned = new byte[alignedCount];
            if (RandomAccess.Read(_handle, aligned, alignedOffset) != alignedCount)
                throw new EndOfStreamException(L.T("Core.Storage.Disk.ReadIncomplete"));
            aligned.AsSpan(prefix, buffer.Length).CopyTo(buffer);
            return;
        }
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (read == 0) throw new EndOfStreamException(L.T("Core.Storage.Disk.ImageTruncated"));
            total += read;
        }
    }

    public void Dispose() { _disposed = true; _handle.Dispose(); }
}

internal static class NativeDisk
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);

    internal static byte[] Query(SafeFileHandle handle, uint code, int capacity, byte[]? input = null)
    {
        byte[] output = new byte[capacity];
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out int returned, IntPtr.Zero))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (returned < 0 || returned > output.Length) throw new IOException(L.T("Core.Storage.Disk.QueryLengthInvalid"));
        return output[..returned];
    }
}
