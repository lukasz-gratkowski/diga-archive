using Diga.Core.Dlna;
using Diga.Core.Media;
using Diga.Core.Naming;

namespace Diga.Tests;

public sealed class SavedFileNamesTests
{
    [Theory]
    [InlineData("2026-0158", "2026-0158")]
    [InlineData("  AMG 17 / ", null)]
    [InlineData("  AMG 17_b  ", "AMG 17_b")]
    [InlineData("Zamówienie 12 (ż)", "Zamówienie 12 (ż)")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    [InlineData("12:30", null)]
    [InlineData("a\\b", null)]
    [InlineData("what?", null)]
    [InlineData("12345.", null)]
    [InlineData("tab\there", null)]
    [InlineData("soft\u0085break", null)]
    [InlineData("CON", null)]
    [InlineData("com1", null)]
    [InlineData("LPT9.2026", null)]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("CON .2026", null)]
    [InlineData("\uFEFF2026-0158\u200B", "2026-0158")]
    [InlineData("C\u200BON", null)]
    [InlineData("abc\u202Egpj", "abcgpj")]
    [InlineData("A\U000E0001B", "AB")]
    [InlineData("a\u2028b", null)]
    [InlineData("a\u2029b", null)]
    public void OrderNumberIsTrimmedAndMustBeUsableAsFileName(string? text, string? expected)
    {
        Assert.Equal(expected is not null, SavedFileNames.TryNormalizeOrderNumber(text, out var order));
        Assert.Equal(expected ?? "", order);
    }

    [Fact]
    public void OrderNumberLengthIsLimited()
    {
        Assert.True(SavedFileNames.TryNormalizeOrderNumber(new string('7', SavedFileNames.MaximumOrderNumberLength), out _));
        Assert.False(SavedFileNames.TryNormalizeOrderNumber(new string('7', SavedFileNames.MaximumOrderNumberLength + 1), out var order));
        Assert.Equal("", order);
        // Half of a surrogate pair cannot be encoded for the cloud or passed to FFmpeg.
        Assert.False(SavedFileNames.TryNormalizeOrderNumber("a" + (char)0xD83C + "b", out _));
        Assert.True(SavedFileNames.TryNormalizeOrderNumber("\U0001F3AC 7", out _));
    }

    [Theory]
    [InlineData("0001.m2ts", "", 1, "0001")]
    [InlineData("0001.m2ts", "2026-0158", 1, "2026-0158")]
    [InlineData("0001.m2ts", " 2026-0158 ", 1, "2026-0158")]
    [InlineData("0001.m2ts", "2026-0158", 2, "2026-0158_0001")]
    [InlineData("0001.m2ts", "2026-0158", 0, "2026-0158")]
    [InlineData("0001.m2ts", "bad:order", 3, "0001")]
    [InlineData("  ..  ", "", 1, "Fallback")]
    [InlineData("  ..  ", "A1", 5, "A1_Fallback")]
    [InlineData("NUL.ts", "", 1, "Recording_NUL")]
    [InlineData("com¹.ts", "", 1, "Recording_com¹")]
    [InlineData("NUL.ts", "A1", 2, "A1_NUL")]
    public void FileNamesUseTheOrderNumberAloneOrAsPrefix(string title, string order, int batch, string expected) =>
        Assert.Equal(expected, SavedFileNames.Stem(title, "Fallback", order, batch));

    [Theory]
    [InlineData("Wiadomości 19.30", "Wiadomości 19.30")]
    [InlineData("Fakty 12/03: wydanie \"główne\"?", "Fakty 12_03_ wydanie _główne__")]
    [InlineData("VIDEO-0001.MPG", "VIDEO-0001")]
    [InlineData("clip.m2ts", "clip")]
    [InlineData(".ts", "ts")]
    [InlineData("Film. ", "Film")]
    public void CatalogueTitlesKeepDotsAndSlashesAreReplaced(string title, string expected) =>
        Assert.Equal(expected, SavedFileNames.Stem(title, "Fallback", titleIsFileName: false));

    [Fact]
    public void LongNamesAreLimitedWithoutSplittingCharacters()
    {
        Assert.Equal(100, SavedFileNames.Stem(new string('x', 300) + ".ts", "Fallback").Length);
        var emoji = string.Concat(Enumerable.Repeat("\U0001F3AC", 80));
        var limited = SavedFileNames.Stem(emoji, "Fallback", titleIsFileName: false);
        Assert.True(limited.Length is 99 or 100);
        Assert.False(char.IsHighSurrogate(limited[^1]));
        var split = SavedFileNames.Stem("x" + emoji, "Fallback", titleIsFileName: false);
        Assert.Equal(99, split.Length);
        Assert.True(char.IsLowSurrogate(split[^1]));
        Assert.Equal(new string('x', 99), SavedFileNames.Stem(new string('x', 99) + " tail", "Fallback", titleIsFileName: false));
        Assert.Equal(new string('x', 98), SavedFileNames.Stem(new string('x', 98) + ". .tail", "Fallback", titleIsFileName: false));
        var order = new string('7', SavedFileNames.MaximumOrderNumberLength);
        var prefixed = SavedFileNames.Stem(new string('x', 300), "Fallback", order, 2, titleIsFileName: false);
        Assert.StartsWith(order + "_x", prefixed);
        Assert.Equal(120, prefixed.Length);
        Assert.Equal(order, SavedFileNames.Stem(new string('x', 300), "Fallback", order, 1));
        Assert.Equal("Recording_CON", SavedFileNames.Stem("CON" + new string(' ', 100) + "x", "Fallback", titleIsFileName: false));
        Assert.Equal("Recording_CON." + new string('x', 86), SavedFileNames.Stem("CON." + new string('x', 300), "Fallback", titleIsFileName: false));
    }

    [Fact]
    public void ExistingFilesAndFoldersAreNeverReplaced()
    {
        var directory = Directory.CreateTempSubdirectory("diga-names-").FullName;
        try
        {
            Assert.Equal(Path.Combine(directory, "2026-0158.mkv"), SavedFileNames.NextAvailablePath(directory, "2026-0158", ".mkv"));
            File.WriteAllText(Path.Combine(directory, "2026-0158.mkv"), "");
            Directory.CreateDirectory(Path.Combine(directory, "2026-0158 (2).mkv"));
            Assert.Equal(Path.Combine(directory, "2026-0158 (3).mkv"), SavedFileNames.NextAvailablePath(directory, "2026-0158", ".mkv"));
            Assert.Equal(Path.Combine(directory, "2026-0158.mpg"), SavedFileNames.NextAvailablePath(directory, "2026-0158", ".mpg"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FirstDigaNamedDeviceIsPreferredOverEarlierDevices()
    {
        static DlnaDevice Device(string name, string model) => new("uuid:" + name, name, "Maker", model,
            new("http://192.168.1.8/" + Uri.EscapeDataString(name)), new("http://192.168.1.8/control"), "urn:schemas-upnp-org:service:ContentDirectory:1");
        var nas = Device("Home NAS", "DS220");
        var tv = Device("Living room TV", "Viera");
        var byModel = Device("Recorder", "Panasonic DIGA DMR");
        var first = Device("diga BD/DVD Recorder", "DMR-BS850");
        var second = Device("DIGA upstairs", "DMR-BS750");
        Assert.Same(first, DlnaDevicePreference.Preferred([nas, first, tv, second]));
        Assert.Same(first, DlnaDevicePreference.Preferred([nas, byModel, first]));
        Assert.Same(byModel, DlnaDevicePreference.Preferred([nas, byModel, tv]));
        Assert.Same(nas, DlnaDevicePreference.Preferred([nas, tv]));
        Assert.Null(DlnaDevicePreference.Preferred([]));
    }

    [Theory]
    [InlineData(3600.0, 3600.9, true)]
    [InlineData(3600.0, 3601.9, true)]
    [InlineData(3600.0, 3545.2, false)]
    [InlineData(3600.0, 3602.5, false)]
    [InlineData(8.0, 9.9, true)]
    [InlineData(8.0, 10.5, false)]
    [InlineData(null, 10.0, false)]
    [InlineData(10.0, null, false)]
    [InlineData(null, null, false)]
    public void ContainerDurationMustAgreeWithItsSourceBeforeTheSourceIsRemoved(double? source, double? destination, bool agree)
    {
        static MediaProbeResult Probe(double? seconds) => new("file", seconds is { } value ? TimeSpan.FromSeconds(value) : null, 1, [], "{}");
        Assert.Equal(agree, new RemuxResult("out.mkv", Probe(source), Probe(destination)).DurationsAgree);
    }
}
