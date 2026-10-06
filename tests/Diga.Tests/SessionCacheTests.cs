using Diga.Core.Files;
using Xunit;

namespace Diga.Tests;

/// <summary>The session's folder for temporary files: what may be deleted, and what is left alone whatever it is called.</summary>
public sealed class SessionCacheTests : IDisposable
{
    private static string NewId() => Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DigaSessionCache-" + Guid.NewGuid().ToString("N"));
    public SessionCacheTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void ASessionHasItsOwnLockedFolderAndRemovesItWithEverythingInIt()
    {
        var id = NewId();
        using var session = new SessionCache(_root, id);
        Assert.Equal(Path.Combine(_root, "session-" + id), session.Directory);
        Assert.False(Directory.Exists(session.Directory));
        session.Prepare();
        session.Prepare();
        File.WriteAllText(Path.Combine(session.Directory, "recording.ts"), "a whole recording");
        // The lock is what tells another instance, or a later start, that the folder is in use.
        Assert.Throws<IOException>(() => new FileStream(Path.Combine(session.Directory, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
        session.Remove();
        Assert.False(Directory.Exists(session.Directory));
        // A session that never downloaded anything has nothing to remove.
        new SessionCache(_root, NewId()).Remove();
    }

    [Fact]
    public void OnlyFilesDirectlyInTheSessionsFolderAreDeleted()
    {
        using var session = new SessionCache(_root, NewId());
        session.Prepare();
        var own = Path.Combine(session.Directory, "preview.mkv");
        File.WriteAllText(own, "clip");
        var outside = Path.Combine(_root, "2026-0142.mkv");
        File.WriteAllText(outside, "a saved recording");
        var below = Path.Combine(session.Directory, "sub", "x.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(below)!);
        File.WriteAllText(below, "x");
        using var other = new SessionCache(_root, NewId());
        other.Prepare();
        var foreign = Path.Combine(other.Directory, "theirs.ts");
        File.WriteAllText(foreign, "another session's file");

        Assert.True(session.Owns(own));
        session.Delete(own);
        Assert.False(File.Exists(own));
        foreach (var path in new[] { outside, below, foreign, Path.Combine(session.Directory, "..", "2026-0142.mkv") })
        {
            Assert.False(session.Owns(path), path);
            var refusal = Assert.Throws<IOException>(() => session.Delete(path));
            Assert.Equal(Diga.Core.Localization.AppText.T("Core.Files.OutsideSession"), refusal.Message);
        }
        Assert.All(new[] { outside, below, foreign }, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void FoldersOfEndedSessionsAreRemovedAndNothingElse()
    {
        var current = NewId();
        string Folder(string name, bool withLock, params string[] files)
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            if (withLock) File.WriteAllText(Path.Combine(folder, ".lock"), "");
            foreach (var file in files) File.WriteAllText(Path.Combine(folder, file), "content");
            return folder;
        }
        var ended = Folder("session-" + NewId(), true, "recording.ts", "preview-1.mkv");
        var mine = Folder("session-" + current, true, "recording.ts");
        // Called like a session folder, but nothing shows that this application made it.
        var noLock = Folder("session-" + NewId(), false, "somebody's file.ts");
        var otherName = Folder("session-notes", true, "notes.txt");
        var upperCase = Folder("session-" + NewId().ToUpperInvariant(), true, "x.ts");
        var withSubfolder = Folder("session-" + NewId(), true, "x.ts");
        Directory.CreateDirectory(Path.Combine(withSubfolder, "kept"));
        var unrelated = Folder("Recordings", true, "film.mkv");
        // A session that is still running in another window holds its lock.
        using var running = new SessionCache(_root, NewId());
        running.Prepare();
        File.WriteAllText(Path.Combine(running.Directory, "recording.ts"), "in use");

        SessionCache.RemoveStale(_root, current);

        Assert.False(Directory.Exists(ended));
        Assert.All(new[] { mine, noLock, otherName, upperCase, withSubfolder, unrelated, running.Directory }, folder => Assert.True(Directory.Exists(folder), folder));
        Assert.True(File.Exists(Path.Combine(running.Directory, "recording.ts")));
        Assert.True(File.Exists(Path.Combine(noLock, "somebody's file.ts")));
        // A folder for temporary files that does not exist yet is not an error.
        SessionCache.RemoveStale(Path.Combine(_root, "missing"), current);
    }

    [Fact]
    public void AFolderThatCannotBeEmptiedKeepsItsLockFileSoThatTheNextStartTriesAgain()
    {
        var folder = Path.Combine(_root, "session-" + NewId());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".lock"), "");
        var held = Path.Combine(folder, "recording.ts");
        File.WriteAllText(held, "still open in a player");
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            SessionCache.RemoveStale(_root, NewId());
        Assert.True(File.Exists(Path.Combine(folder, ".lock")));
        SessionCache.RemoveStale(_root, NewId());
        Assert.False(Directory.Exists(folder));
    }
}
