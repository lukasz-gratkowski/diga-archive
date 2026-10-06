using System.Security.Cryptography;
using Diga.Core.Files;
using Xunit;

namespace Diga.Tests;

/// <summary>The working files of a save: which names are the application's own, what a sweep removes, and how a verified copy is published.</summary>
public sealed class WorkingFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaWorkingFiles-" + Guid.NewGuid().ToString("N"));
    public WorkingFilesTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void TheNamesTheApplicationGivesAreRecognised()
    {
        Assert.True(WorkingFiles.IsDelivered(WorkingFiles.DeliveredName(".ts")));
        Assert.True(WorkingFiles.IsDelivered(WorkingFiles.DeliveredName("")));
        Assert.True(WorkingFiles.IsPartial(WorkingFiles.PartialName("2026-0142 Wakacje nad morzem.mkv")));
        // The download of a delivered recording is itself written under a partial name first.
        Assert.True(WorkingFiles.IsPartial(WorkingFiles.PartialName(WorkingFiles.DeliveredName(".m2ts"))));
        Assert.False(WorkingFiles.IsDelivered(WorkingFiles.PartialName("x.ts")));
    }

    [Theory]
    // What a person, a recorder or another program may put into the folder: none of it is ever the application's working file.
    [InlineData("2026-0142.mkv")]
    [InlineData("Wakacje.partial")]
    [InlineData("film.0123456789abcdef0123456789abcdef.partial")]
    [InlineData(".film.partial")]
    [InlineData(".film.0123456789ABCDEF0123456789ABCDEF.partial")]
    [InlineData(".film.0123456789abcdef0123456789abcde.partial")]
    [InlineData(".film.0123456789abcdef0123456789abcdef.partial.ts")]
    [InlineData(".diga-0123456789abcdef0123456789abcdef.ts")]
    [InlineData(".diga-recording.download.ts")]
    [InlineData("diga-0123456789abcdef0123456789abcdef.download.ts")]
    [InlineData(".diga-0123456789abcdef0123456789abcdef.download.ts.bak")]
    [InlineData(".gitignore")]
    [InlineData(".nomedia")]
    public void OtherNamesAreNot(string name)
    {
        Assert.False(WorkingFiles.IsDelivered(name), name);
        Assert.False(WorkingFiles.IsPartial(name), name);
    }

    [Fact]
    public void ASweepRemovesOldIncompleteFilesAndOnlyReportsCompleteDownloads()
    {
        var partial = Create(WorkingFiles.PartialName("recording.ts"), 100, old: true);
        var delivered = Create(WorkingFiles.DeliveredName(".ts"), 200, old: true);
        var fresh = Create(WorkingFiles.PartialName("being written.ts"), 50, old: false);
        var known = Create(WorkingFiles.DeliveredName(".m2ts"), 70, old: true);
        var recording = Create("2026-0142.mkv", 10, old: true);
        var lookalike = Create(".film.partial", 10, old: true);
        var below = Path.Combine(_directory, "sub", WorkingFiles.PartialName("x.ts"));
        Directory.CreateDirectory(Path.GetDirectoryName(below)!);
        File.WriteAllBytes(below, new byte[5]);
        File.SetLastWriteTimeUtc(below, DateTime.UtcNow.AddDays(-1));

        var result = WorkingFiles.Sweep(_directory, [known]);

        Assert.Equal((1, 100L), (result.PartialFilesRemoved, result.PartialBytesRemoved));
        Assert.Equal([delivered], result.DeliveredFiles);
        Assert.Equal(200, result.DeliveredBytes);
        Assert.False(File.Exists(partial));
        // Everything else is where it was: a whole download, a file still young, a file this session knows, the user's files, a subfolder.
        Assert.All(new[] { delivered, fresh, known, recording, lookalike, below }, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void AFileSomebodyHoldsOpenIsNotALeftover()
    {
        var partial = Create(WorkingFiles.PartialName("recording.ts"), 100, old: true);
        var delivered = Create(WorkingFiles.DeliveredName(".ts"), 100, old: true);
        // As another instance writing a download, and as FFmpeg reading one.
        using (new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.Read))
        using (new FileStream(delivered, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            File.SetLastWriteTimeUtc(delivered, DateTime.UtcNow.AddDays(-1));
            var result = WorkingFiles.Sweep(_directory, []);
            Assert.Equal(0, result.PartialFilesRemoved);
            Assert.Empty(result.DeliveredFiles);
        }
        Assert.True(File.Exists(partial));
    }

    [Fact]
    public void ASweepOfAFolderThatIsNotThereFindsNothing() =>
        Assert.Equal(new WorkingFiles.Leftovers(0, 0, [], 0), WorkingFiles.Sweep(Path.Combine(_directory, "missing"), []) with { DeliveredFiles = [] });

    [Fact]
    public async Task AVerifiedCopyGetsItsNameOnlyWhenItsFingerprintIsTheExpectedOne()
    {
        var source = Path.Combine(_directory, "source.ts");
        var content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        await File.WriteAllBytesAsync(source, content);
        var fingerprint = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var target = Path.Combine(_directory, "out", "copy.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var reports = new List<long>();

        Assert.True(await WorkingFiles.CopyVerifiedAsync(source, target, fingerprint, new Collector(reports)));
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal(content.Length, reports[^1]);
        Assert.Equal(["copy.ts"], Names(Path.GetDirectoryName(target)!));

        // Another fingerprint: no file under the real name, nothing left under the working name.
        var refused = Path.Combine(_directory, "out", "refused.ts");
        Assert.False(await WorkingFiles.CopyVerifiedAsync(source, refused, new string('0', 64)));
        Assert.Equal(["copy.ts"], Names(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task AFileThatAlreadyHasTheNameIsNeitherReplacedNorRemoved()
    {
        var source = Path.Combine(_directory, "source.ts");
        await File.WriteAllTextAsync(source, "the copy");
        var fingerprint = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var target = Path.Combine(_directory, "taken.ts");
        await File.WriteAllTextAsync(target, "somebody else's file");
        await Assert.ThrowsAsync<IOException>(() => WorkingFiles.CopyVerifiedAsync(source, target, fingerprint));
        Assert.Equal("somebody else's file", await File.ReadAllTextAsync(target));
        Assert.Equal(["source.ts", "taken.ts"], Names(_directory));
    }

    [Fact]
    public async Task ACancelledCopyLeavesNothingBehind()
    {
        var source = Path.Combine(_directory, "source.ts");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkingFiles.CopyVerifiedAsync(source, Path.Combine(_directory, "copy.ts"), new string('0', 64), null, cancel.Token));
        Assert.Equal(["source.ts"], Names(_directory));
    }

    [Fact]
    public void ADownloadThatIsNoLongerNeededIsDeleted()
    {
        var working = Create(WorkingFiles.DeliveredName(".ts"), 10, old: false);
        Assert.Equal(new WorkingFiles.Settled(null, false), WorkingFiles.SettleDelivered(working, keep: false, _directory, "2026-0142", ".ts"));
        Assert.Empty(Directory.GetFiles(_directory));
        // Deleted already, by whatever means: there is nothing to keep and nothing failed.
        Assert.Equal(new WorkingFiles.Settled(null, false), WorkingFiles.SettleDelivered(working, keep: false, _directory, "2026-0142", ".ts"));
    }

    [Fact]
    public void ADownloadThatMustBeKeptGetsAProperNameAndNeverReplacesAFile()
    {
        var taken = Create("2026-0142.ts", 5, old: false);
        var working = Create(WorkingFiles.DeliveredName(".ts"), 10, old: false);
        var settled = WorkingFiles.SettleDelivered(working, keep: true, _directory, "2026-0142", ".ts");
        Assert.Equal(Path.Combine(_directory, "2026-0142 (2).ts"), settled.KeptPath);
        Assert.False(settled.DeleteFailed);
        Assert.Equal(10, new FileInfo(settled.KeptPath!).Length);
        Assert.Equal(5, new FileInfo(taken).Length);
        Assert.False(File.Exists(working));
    }

    [Fact]
    public void ADownloadThatCannotBeDeletedOrRenamedIsKeptWhereItIs()
    {
        var working = Create(WorkingFiles.DeliveredName(".ts"), 10, old: false);
        // Held by a player or a virus scanner: neither the delete nor the rename can happen.
        using (new FileStream(working, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(new WorkingFiles.Settled(working, true), WorkingFiles.SettleDelivered(working, keep: false, _directory, "2026-0142", ".ts"));
            Assert.Equal(new WorkingFiles.Settled(working, false), WorkingFiles.SettleDelivered(working, keep: true, _directory, "2026-0142", ".ts"));
        }
        Assert.Equal(10, new FileInfo(working).Length);
        Assert.Equal([Path.GetFileName(working)], Names(_directory));
    }

    private string Create(string name, int length, bool old)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[length]);
        if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));
        return path;
    }

    private static string[] Names(string directory) => Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();

    private sealed class Collector(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }
}
