using System.Runtime.InteropServices;

namespace Diga.Core.Dlna;

/// <summary>Free space of the drive or network share that holds a folder.</summary>
public static class DiskSpace
{
    /// <summary>
    /// What a download leaves free: 1 GB on the drive Windows runs from, where a full drive stops Windows and other programs from
    /// working, and 64 MB on any other drive or share.
    /// <para>
    /// The Windows drive is known by its volume, not by how the folder is written: \\?\C:\Videos, the volume's own name
    /// (\\?\Volume{...}\Videos) and a share of this PC such as \\localhost\C$ are all that drive. Windows is asked for the serial
    /// number of the volume, which for a share means asking the PC that offers it. A share that does not answer counts as a
    /// share. Another drive that carries the same number, as an exact copy of the Windows drive does, gets the larger reserve
    /// as well, and so does a folder of the Windows drive that leads to another drive; neither does harm. The other way round
    /// matters: a folder of another drive that leads to the Windows drive, through a junction or a volume mounted in a folder,
    /// is recognised, because the folder itself is asked which volume it is on and not only the root of its path.
    /// </para>
    /// </summary>
    public static long Reserve(string folder) => IsOnSystemVolume(folder) ? 1024L * 1024 * 1024 : 64L * 1024 * 1024;

    private static bool IsOnSystemVolume(string folder)
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(root)) return false;
        // The usual case needs no question, and \\?\C:\ and \\.\C:\ are C:\ even if Windows should not answer the question below.
        var plain = root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal) ? root[4..] : root;
        if (string.Equals(plain, system, StringComparison.OrdinalIgnoreCase)) return true;
        // The folder itself is asked before its root: a folder of another drive can lead to the Windows drive, through a junction
        // or a volume mounted in a folder, and what is written there lands on the Windows drive.
        return OperatingSystem.IsWindows() && (FolderSerial(folder) ?? VolumeSerial(root)) is { } serial && serial == VolumeSerial(system);
    }

    /// <summary>
    /// The serial number of the volume a folder is really on, asked of the nearest folder that exists; null when Windows does not
    /// say. The folder is opened without any access, only to be asked this, and opening it follows junctions and mounted volumes.
    /// </summary>
    private static uint? FolderSerial(string folder)
    {
        try
        {
            var existing = Path.GetFullPath(folder);
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent)) return null;
                existing = parent;
            }
            using var handle = CreateFileW(existing, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, OpensAFolder, IntPtr.Zero);
            return !handle.IsInvalid && GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, out var serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0) && serial != 0 ? serial : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    /// <summary>The serial number Windows gave the volume when it was formatted; null when there is none or Windows does not say.</summary>
    private static uint? VolumeSerial(string root)
    {
        var path = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return GetVolumeInformationW(path, IntPtr.Zero, 0, out var serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0) && serial != 0 ? serial : null;
    }

    /// <summary>
    /// The bytes the current user can still write in the folder, or, when the folder does not exist yet, in the nearest folder above
    /// it that does. Null when the system does not say, for instance for a share that is not reachable: that is not an error here,
    /// writing will report it.
    /// </summary>
    public static long? Available(string folder)
    {
        try
        {
            var existing = Path.GetFullPath(folder);
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent)) return null;
                existing = parent;
            }
            if (OperatingSystem.IsWindows())
            {
                // Asked of the folder, not of a drive letter: a share (\\server\share\folder) has none, and a user's quota counts.
                // The root is asked when the folder cannot be, as with a path longer than this call accepts.
                foreach (var candidate in new[] { existing, Path.GetPathRoot(existing) })
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    var path = Path.EndsInDirectorySeparator(candidate) ? candidate : candidate + Path.DirectorySeparatorChar;
                    if (GetDiskFreeSpaceExW(path, out var available, out _, out _)) return (long)Math.Min(available, long.MaxValue);
                }
                return null;
            }
            var drive = DriveInfo.GetDrives().Where(candidate => existing.StartsWith(candidate.Name, StringComparison.Ordinal)).MaxBy(candidate => candidate.Name.Length);
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong availableToCaller, out ulong total, out ulong free);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, IntPtr name, uint nameLength, out uint serial, IntPtr maximumComponentLength, IntPtr flags, IntPtr fileSystem, uint fileSystemLength);

    // FILE_FLAG_BACKUP_SEMANTICS: without it Windows opens files only, not folders.
    private const uint OpensAFolder = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle folder, IntPtr name, uint nameLength, out uint serial, IntPtr maximumComponentLength, IntPtr flags, IntPtr fileSystem, uint fileSystemLength);
}
