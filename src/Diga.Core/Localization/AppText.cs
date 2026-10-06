using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Diga.Core.Localization;

/// <summary>Shared application text. UI language and regional formatting are independent.</summary>
public static class AppText
{
    private static readonly FrozenDictionary<string, string> English = Load("en");
    private static readonly FrozenDictionary<string, string> Polish = Load("pl");

    public static string LanguageCode => Supported(CultureInfo.CurrentUICulture.Name) ?? "en";

    public static string NormalizePreference(string? preference) => Supported(preference) ?? "system";

    public static string ResolveLanguage(string? preference, IEnumerable<string>? systemLanguages)
        => Supported(preference) ?? systemLanguages?.Select(Supported).FirstOrDefault(language => language is not null) ?? "en";

    public static void Configure(string? preference, IEnumerable<string>? systemLanguages)
    {
        var culture = CultureInfo.GetCultureInfo(ResolveLanguage(preference, systemLanguages) == "pl" ? "pl-PL" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static IReadOnlyDictionary<string, string> Catalog(string language) => Supported(language) == "pl" ? Polish : English;

    public static string T(string key, params object?[] values) => TForLanguage(LanguageCode, key, values);

    public static string TForLanguage(string language, string key, params object?[] values)
    {
        if (!Catalog(language).TryGetValue(key, out var text) && !English.TryGetValue(key, out text)) return key;
        return values.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, values);
    }

    public static string Plural(string baseKey, long count, params object?[] extraArgs)
    {
        var arguments = new object?[extraArgs.Length + 1];
        arguments[0] = count;
        extraArgs.CopyTo(arguments, 1);
        return T(baseKey + "." + PluralCategory(count, LanguageCode), arguments);
    }

    public static string PluralCategory(long count, string language)
    {
        var magnitude = count < 0 ? (ulong)(-(count + 1)) + 1 : (ulong)count;
        if (magnitude == 1) return "one";
        if (Supported(language) != "pl") return "other";
        return magnitude % 10 is >= 2 and <= 4 && magnitude % 100 is not (>= 12 and <= 14) ? "few" : "many";
    }

    private static string? Supported(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var primary = language.Trim().Split('-')[0];
        return primary.Equals("pl", StringComparison.OrdinalIgnoreCase) ? "pl"
            : primary.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : null;
    }

    private static FrozenDictionary<string, string> Load(string language)
    {
        var assembly = typeof(AppText).Assembly;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith("." + language + ".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value) || !entries.TryAdd(property.Name, value))
                    throw new InvalidDataException($"Invalid or duplicate localization entry: {resource}: {property.Name}");
            }
        }
        if (entries.Count == 0) throw new InvalidDataException($"Missing application localization catalog: {language}");
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
