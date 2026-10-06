using Diga.Core.Media;
using System.Runtime.InteropServices;

namespace Diga.Tests;

/// <summary>Independent review of destination aliases: a save must never write through a link to its own source.</summary>
public sealed class MediaSafetyReviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaAliasTests-" + Guid.NewGuid().ToString("N"));
    public MediaSafetyReviewTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task RemuxDoesNotOverwriteHardLinkToSourceMedia()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_directory, "source.ts");
        await File.WriteAllTextAsync(source, "original stream bytes");
        var alias = Path.Combine(_directory, "alias.mkv");
        Assert.True(CreateHardLinkW(alias, source, 0), $"Cannot create the test hard link (Win32 error {Marshal.GetLastWin32Error()}).");
        await Assert.ThrowsAsync<IOException>(() => new RemuxService().RemuxAsync(new(source, alias, OutputFormat.Matroska)));
        Assert.Equal("original stream bytes", await File.ReadAllTextAsync(source));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);
}
