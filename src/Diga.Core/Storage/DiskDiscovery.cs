using L = Diga.Core.Localization.AppText;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Diga.Core.Storage;

public static class DiskDiscovery
{
    /// <summary>Enumerates Windows physical disks using metadata-only handles. Raw reading still requires elevation.</summary>
    public static Task<IReadOnlyList<DiskSource>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DiskSource>>(() =>
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DiskSource>();
        List<DiskSource> disks = [];
        for (int number = 0; number < 256; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = FormattableString.Invariant($@"\\.\PhysicalDrive{number}");
            using var handle = NativeDisk.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            try
            {
                byte[] descriptor = NativeDisk.Query(handle, 0x2D1400, 4096, new byte[12]);
                if (descriptor.Length < 36) continue;
                bool usb = BitConverter.ToInt32(descriptor, 28) == 7;
                string vendor = ReadString(descriptor, BitConverter.ToInt32(descriptor, 12));
                string product = ReadString(descriptor, BitConverter.ToInt32(descriptor, 16));
                byte[] geometry = NativeDisk.Query(handle, 0x700A0, 256);
                long size = geometry.Length >= 32 ? BitConverter.ToInt64(geometry, 24) : 0;
                string label = L.T("Core.Storage.Disk.Label", number, vendor, product).Trim();
                disks.Add(new DiskSource(FormattableString.Invariant($"physical:{number}"), label, path, size, true, usb));
            }
            catch (IOException) { /* A disk removed during enumeration is not selectable. */ }
        }
        return disks.OrderByDescending(x => x.IsUsb).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }, cancellationToken);

    private static string ReadString(byte[] descriptor, int offset)
    {
        if (offset <= 0 || offset >= descriptor.Length) return "";
        int end = Array.IndexOf(descriptor, (byte)0, offset);
        return Encoding.ASCII.GetString(descriptor, offset, (end < 0 ? descriptor.Length : end) - offset).Trim();
    }

    /// <summary>Rejects source-image overwrite and local destinations on the original physical source disk.</summary>
    public static void EnsureSafeDestination(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = System.IO.Path.GetFullPath(destinationPath);
        if (destination.StartsWith(@"\\.\", StringComparison.Ordinal) || destination.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new IOException(L.T("Core.Storage.Destination.DevicePath"));
        if (string.Equals(destination, System.IO.Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("Core.Storage.Destination.SourceImage"));
        EnsureDifferentDestinationDisk(sourcePath, destination);
    }

    private static void EnsureDifferentDestinationDisk(string source, string destination)
    {
        if (!DiskByteSource.IsPhysicalPath(source)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        int number = int.Parse(source.AsSpan(source.LastIndexOf("Drive", StringComparison.OrdinalIgnoreCase) + 5), System.Globalization.CultureInfo.InvariantCulture);
        // A UNC share may resolve back to this machine/source disk (including administrative
        // shares and DNS aliases). We cannot establish physical separation for it here.
        if (destination.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException(L.T("Core.Storage.Destination.LocalDiskRequired"));
        var volumePath = new StringBuilder(1024);
        string existing = System.IO.Path.GetDirectoryName(destination)!;
        while (!Directory.Exists(existing)) existing = System.IO.Path.GetDirectoryName(existing) ?? throw new IOException(L.T("Core.Storage.Destination.VolumeUnknown"));
        if (!GetVolumePathNameW(existing, volumePath, volumePath.Capacity)) throw new IOException(L.T("Core.Storage.Destination.VolumeUnidentified"), new Win32Exception(Marshal.GetLastWin32Error()));
        var volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPointW(volumePath.ToString(), volumeName, volumeName.Capacity)) throw new IOException(L.T("Core.Storage.Destination.VolumeUnidentified"));
        using var handle = NativeDisk.CreateFileW(volumeName.ToString().TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(L.T("Core.Storage.Destination.VolumeUnreadable"));
        byte[] extents = NativeDisk.Query(handle, 0x560000, 65536);
        if (extents.Length < 8) throw new IOException(L.T("Core.Storage.Destination.ExtentsInvalid"));
        uint count = BitConverter.ToUInt32(extents, 0);
        if (count == 0 || count > (extents.Length - 8) / 24) throw new IOException(L.T("Core.Storage.Destination.ExtentsIncomplete"));
        for (int i = 0; i < count; i++)
            if (BitConverter.ToUInt32(extents, 8 + i * 24) == number)
                throw new IOException(L.T("Core.Storage.Destination.SourceDisk"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, int length);
}
