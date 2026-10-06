using Diga.Core.Media;
using Diga.Core.Storage;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Diga.Tests;

/// <summary>Independent T17 review of destination aliases, outside the parser implementation tests.</summary>
public sealed class StorageSafetyReviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaAliasTests-" + Guid.NewGuid().ToString("N"));
    public StorageSafetyReviewTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task ExtractionDoesNotOverwriteHardLinkToSourceImage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_directory, "source.dd");
        await File.WriteAllBytesAsync(source, StorageFixtureBuilder.BuildMeihdfs([1, 2, 3, 4]));
        var alias = Path.Combine(_directory, "alias.vro");
        Assert.True(CreateHardLinkW(alias, source, 0), $"Cannot create the test hard link (Win32 error {Marshal.GetLastWin32Error()}).");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        using var fs = await PanasonicReader.OpenAsync(source);
        await Assert.ThrowsAsync<IOException>(() => fs.ExtractAsync(fs.Recordings[0], alias));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

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
