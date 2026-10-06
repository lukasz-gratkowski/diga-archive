using System.Globalization;
using System.Text;

namespace Diga.Core.Naming;

/// <summary>Names saved recordings from an optional order number and the recording title.</summary>
public static class SavedFileNames
{
    public const int MaximumOrderNumberLength = 64;
    private const int MaximumTitleStemLength = 100;
    private const int MaximumOrderStemLength = 120;
    private static readonly string[] MediaExtensions = [".ts", ".mts", ".m2ts", ".m2t", ".tts", ".mpg", ".mpeg", ".vob", ".vro", ".mp4", ".mkv"];

    /// <summary>Trims an order number and drops invisible format characters. Returns false, with an empty result, when it cannot be part of a Windows or cloud file name.</summary>
    public static bool TryNormalizeOrderNumber(string? text, out string orderNumber)
    {
        // Pasted text can carry invisible format characters (zero-width, byte-order mark, direction marks); they would make look-alike names.
        orderNumber = string.Concat((text ?? "").EnumerateRunes().Where(rune => Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format).Select(rune => rune.ToString())).Trim();
        if (orderNumber.Length == 0) return true;
        if (orderNumber.Length <= MaximumOrderNumberLength && orderNumber.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !orderNumber.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            && orderNumber.EnumerateRunes().All(rune => rune != Rune.ReplacementChar)
            && !orderNumber.EndsWith('.') && !IsReservedDeviceName(orderNumber)) return true;
        orderNumber = "";
        return false;
    }

    /// <summary>
    /// File name without extension. Without an order number it is the safe recording title; with a batchCount of 1
    /// it is the order number alone; with a larger batchCount the order number is a prefix. The caller decides what
    /// the count covers: the app passes the different recordings saved under the order number in the session.
    /// </summary>
    /// <param name="title">The title from the recorder's catalogue. It may contain dots and slashes; a media extension at its end is dropped.</param>
    public static string Stem(string title, string fallback, string? orderNumber = null, int batchCount = 1)
    {
        var name = SafeTitle(title, fallback);
        if (!TryNormalizeOrderNumber(orderNumber, out var order) || order.Length == 0)
        {
            // Prefix device-reserved Windows names with the fallback name, which is in the user's language; preserve
            // user-visible recorder names otherwise.
            // Checked after the cut, because cutting a long title can leave a bare device name.
            name = Limit(name, MaximumTitleStemLength);
            return IsReservedDeviceName(name) ? Limit(fallback + "_" + name, MaximumTitleStemLength) : name;
        }
        return batchCount <= 1 ? order : Limit(order + "_" + name, MaximumOrderStemLength);
    }

    /// <summary>The first path for this name that is neither a file nor a directory; existing files are never replaced.</summary>
    public static string NextAvailablePath(string directory, string stem, string extension)
    {
        var path = Path.Combine(directory, stem + extension);
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++) path = Path.Combine(directory, $"{stem} ({suffix}){extension}");
        return path;
    }

    private static string SafeTitle(string title, string fallback)
    {
        // Before the extension is looked for: an invisible character after ".ts" would otherwise keep it in the name.
        var stem = WithoutMediaExtension(WithoutInvisibleCharacters(title));
        foreach (var invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        stem = stem.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(stem) ? fallback : stem;
    }

    // A title comes from the recorder and is not trusted. The rule is the one for order numbers: invisible format characters are
    // dropped (zero-width ones give two names that look the same; the direction mark U+202E shows "Film<U+202E>4pm.exe" as
    // "Filmexe.mp4"), and what an order number may not contain at all (control characters such as U+0085, line and paragraph
    // separators) becomes '_' like every other character a file name cannot hold. Half of a surrogate pair, which can be neither
    // uploaded nor passed to FFmpeg, comes out of the enumeration as U+FFFD.
    private static string WithoutInvisibleCharacters(string title)
    {
        var text = new StringBuilder(title.Length);
        foreach (var rune in title.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format) continue;
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) text.Append('_');
            else text.Append(rune.ToString());
        }
        return text.ToString();
    }

    private static string WithoutMediaExtension(string title)
    {
        foreach (var extension in MediaExtensions)
            if (title.Length > extension.Length && title.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return title[..^extension.Length];
        return title;
    }

    private static string Limit(string stem, int maximum)
    {
        if (stem.Length <= maximum) return stem;
        // Never cut a surrogate pair in half.
        var length = char.IsHighSurrogate(stem[maximum - 1]) ? maximum - 1 : maximum;
        return stem[..length].TrimEnd(' ', '.');
    }

    private static bool IsReservedDeviceName(string stem)
    {
        var name = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || (name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && (char.IsDigit(name[3]) || "¹²³".Contains(name[3])));
    }
}
