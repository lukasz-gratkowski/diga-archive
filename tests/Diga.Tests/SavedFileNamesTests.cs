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
    [InlineData("Wiadomości", "", 1, "Wiadomości")]
    [InlineData("Wiadomości", "2026-0158", 1, "2026-0158")]
    [InlineData("Wiadomości", " 2026-0158 ", 1, "2026-0158")]
    [InlineData("Wiadomości", "2026-0158", 2, "2026-0158_Wiadomości")]
    [InlineData("Wiadomości", "2026-0158", 0, "2026-0158")]
    [InlineData("Wiadomości", "bad:order", 3, "Wiadomości")]
    [InlineData("  ..  ", "", 1, "Fallback")]
    [InlineData("  ..  ", "A1", 5, "A1_Fallback")]
    [InlineData("NUL.ts", "", 1, "Fallback_NUL")]
    [InlineData("com¹.ts", "", 1, "Fallback_com¹")]
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
        Assert.Equal(expected, SavedFileNames.Stem(title, "Fallback"));

    [Theory]
    // Written as {code}, because the characters themselves cannot be seen in this file: {202E} is U+202E, which turns the rest
    // of a name around on screen, so that "Film{202E}4pm.exe" is shown as "Filmexe.mp4".
    [InlineData("Film{202E}4pm.exe", "Film4pm.exe")]
    [InlineData("Wiado{200B}mości", "Wiadomości")]
    [InlineData("soft{0085}break", "soft_break")]
    [InlineData("first{2028}second{2029}third", "first_second_third")]
    [InlineData("{FEFF}Fakty{200E} {2066}TVN{2069}", "Fakty TVN")]
    [InlineData("A{E0001}B{00AD}C", "ABC")]
    [InlineData("tab{0009}here{000D}{000A}next", "tab_here__next")]
    [InlineData("bell{0007} and {009F}end", "bell_ and _end")]
    [InlineData("{200B}{202E}{2060}", "Fallback")]
    [InlineData(" {200B}. ", "Fallback")]
    [InlineData("Film.ts{200B}", "Film")]
    [InlineData("C{200B}ON", "Fallback_CON")]
    [InlineData("Zażółć gęślą jaźń 録画 {1F3AC}", "Zażółć gęślą jaźń 録画 {1F3AC}")]
    public void RecorderTitlesLoseInvisibleDirectionAndControlCharacters(string title, string expected)
    {
        var stem = SavedFileNames.Stem(Characters(title), "Fallback");
        Assert.Equal(Characters(expected), stem);
        Assert.DoesNotContain(stem, character => char.IsControl(character) || char.GetUnicodeCategory(character)
            is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);
    }

    [Fact]
    public void InvisibleCharactersAreRemovedAroundAnExtensionAndFromNamesUnderAnOrderNumber()
    {
        Assert.Equal(5, Characters("a{202E}b{1F3AC}").Length);
        Assert.Equal("Film", SavedFileNames.Stem(Characters("Film{200B}.m2ts{202E}"), "Fallback"));
        Assert.Equal("2026-0158_News_at 7", SavedFileNames.Stem(Characters("News{0085}at{200B} 7"), "Fallback", "2026-0158", 2));
        // Half of a surrogate pair cannot be encoded for the cloud or passed to FFmpeg; it is saved as U+FFFD.
        Assert.Equal(Characters("a{FFFD}b"), SavedFileNames.Stem("a" + (char)0xD83C + "b", "Fallback"));
        Assert.Equal(Characters("a{FFFD}"), SavedFileNames.Stem("a" + (char)0xDFAC, "Fallback"));
    }

    /// <summary>Replaces every {hexadecimal code point} by its character.</summary>
    private static string Characters(string text) => System.Text.RegularExpressions.Regex.Replace(text, "[{]([0-9A-F]{4,6})[}]",
        match => char.ConvertFromUtf32(Convert.ToInt32(match.Groups[1].Value, 16)));

    [Fact]
    public void LongNamesAreLimitedWithoutSplittingCharacters()
    {
        Assert.Equal(100, SavedFileNames.Stem(new string('x', 300) + ".ts", "Fallback").Length);
        var emoji = string.Concat(Enumerable.Repeat("\U0001F3AC", 80));
        var limited = SavedFileNames.Stem(emoji, "Fallback");
        Assert.True(limited.Length is 99 or 100);
        Assert.False(char.IsHighSurrogate(limited[^1]));
        var split = SavedFileNames.Stem("x" + emoji, "Fallback");
        Assert.Equal(99, split.Length);
        Assert.True(char.IsLowSurrogate(split[^1]));
        Assert.Equal(new string('x', 99), SavedFileNames.Stem(new string('x', 99) + " tail", "Fallback"));
        Assert.Equal(new string('x', 98), SavedFileNames.Stem(new string('x', 98) + ". .tail", "Fallback"));
        var order = new string('7', SavedFileNames.MaximumOrderNumberLength);
        var prefixed = SavedFileNames.Stem(new string('x', 300), "Fallback", order, 2);
        Assert.StartsWith(order + "_x", prefixed);
        Assert.Equal(120, prefixed.Length);
        Assert.Equal(order, SavedFileNames.Stem(new string('x', 300), "Fallback", order, 1));
        Assert.Equal("Fallback_CON", SavedFileNames.Stem("CON" + new string(' ', 100) + "x", "Fallback"));
        Assert.Equal("Fallback_CON." + new string('x', 87), SavedFileNames.Stem("CON." + new string('x', 300), "Fallback"));
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
