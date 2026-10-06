using L = Diga.Core.Localization.AppText;
using System.Text.RegularExpressions;

namespace Diga.Core.Files;

/// <summary>
/// One session's folder for temporary files: whole recordings downloaded for a preview or for their details, and preview clips.
/// The folder is named "session-" plus 32 hex digits and lies in the folder for temporary files the user chose; a lock file in
/// it is held open for as long as the session runs. Everything this class deletes is inside such a folder.
/// </summary>
public sealed partial class SessionCache(string root, string sessionId) : IDisposable
{
    private const string LockName = ".lock";
    private FileStream? _lock;

    [GeneratedRegex("^session-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex FolderName();

    public string Directory { get; } = Path.Combine(Path.GetFullPath(root), "session-" + sessionId);

    /// <summary>Creates the folder and takes its lock, which tells a later start, and another running instance, that the folder is in use.</summary>
    public void Prepare()
    {
        System.IO.Directory.CreateDirectory(Directory);
        _lock ??= new FileStream(Path.Combine(Directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>True for a file directly inside this session's folder, when that folder is a real folder and not a link to somewhere else.</summary>
    public bool Owns(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.Equals(parent, Directory, StringComparison.OrdinalIgnoreCase)
            && System.IO.Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0;
    }

    /// <summary>Deletes one temporary file of this session; a path that is not inside the session's folder is refused.</summary>
    public void Delete(string path)
    {
        if (!Owns(path)) throw new IOException(L.T("Core.Files.OutsideSession"));
        File.Delete(path);
    }

    /// <summary>Releases the lock and removes the folder with all its files. Nothing in it is worth keeping after the session.</summary>
    public void Remove()
    {
        _lock?.Dispose();
        _lock = null;
        RemoveFolder(Directory);
    }

    public void Dispose() => _lock?.Dispose();

    /// <summary>
    /// Removes the folders of sessions that ended without cleaning up (a crash, a power cut). A folder is removed only when its
    /// lock file exists, which shows that this application made it, and can be opened, which shows that no instance is using it.
    /// </summary>
    public static void RemoveStale(string root, string currentSessionId)
    {
        if (!System.IO.Directory.Exists(root)) return;
        foreach (var folder in System.IO.Directory.EnumerateDirectories(root, "session-*"))
        {
            if (Path.GetFileName(folder) == "session-" + currentSessionId) continue;
            try
            {
                var lockFile = Path.Combine(folder, LockName);
                if (!File.Exists(lockFile)) continue;
                using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                RemoveFolder(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* In use by another instance, or not removable now. */ }
        }
    }

    /// <summary>Deletes one folder, and only if it is what this class creates: the name pattern, a real folder, files only.</summary>
    private static void RemoveFolder(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists || !FolderName().IsMatch(info.Name)) return;
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.EnumerateDirectories().Any()) return;
        // The lock file goes last: a folder that could not be emptied keeps the proof of whose it is, and the next start tries again.
        foreach (var file in info.EnumerateFiles().OrderBy(file => file.Name == LockName)) file.Delete();
        info.Delete();
    }
}
