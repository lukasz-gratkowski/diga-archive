using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diga.Core.Localization;

namespace Diga.Tests;

// Configure changes a process-wide default UI culture. Keep these tests out of
// parallel execution and restore every culture setting even when an assertion fails.
[CollectionDefinition("Localization culture", DisableParallelization = true)]
public sealed class LocalizationCultureCollection { }

[Collection("Localization culture")]
public sealed class LocalizationTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = _originalDefaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _originalDefaultUiCulture;
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
    }

    [Theory]
    [InlineData(null, "pl-PL", "en-US", "pl")]
    [InlineData("system", "en-GB", "pl-PL", "en")]
    [InlineData("system", "de-DE", "pl-PL", "pl")]
    [InlineData("system", "fr-FR", "de-DE", "en")]
    [InlineData("", "pl-PL", "en-US", "pl")]
    [InlineData("unsupported", "pl-PL", "en-US", "pl")]
    [InlineData("pl", "en-US", "en-GB", "pl")]
    [InlineData("en", "pl-PL", "pl", "en")]
    [InlineData("pl-PL", "en-US", "en-GB", "pl")]
    [InlineData("en-GB", "pl-PL", "pl", "en")]
    [InlineData("PL", "en-US", "en-GB", "pl")]
    public void LanguageResolutionRespectsOverridesAndPreferredLanguageOrder(
        string? preference, string first, string second, string expected)
        => Assert.Equal(expected, AppText.ResolveLanguage(preference, [first, second]));

    [Fact]
    public void UnsupportedOrEmptyPreferredLanguagesFallBackToEnglish()
    {
        Assert.Equal("en", AppText.ResolveLanguage("system", []));
        Assert.Equal("en", AppText.ResolveLanguage("not-a-supported-language", ["de-DE", "ja-JP"]));
        Assert.Equal("pl", AppText.ResolveLanguage("system", ["", "garbage!!!", "pl-PL"]));
    }

    [Theory]
    [InlineData("pl-PL", "pl")]
    [InlineData("PL", "pl")]
    [InlineData(" pl ", "pl")]
    [InlineData("en-GB", "en")]
    [InlineData("EN", "en")]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("system", "system")]
    [InlineData("unknown", "system")]
    [InlineData("de-DE", "system")]
    public void SavedPreferenceNormalizationMatchesTheSettingsPicker(string? preference, string expected)
        => Assert.Equal(expected, AppText.NormalizePreference(preference));

    [Theory]
    [InlineData(0L, "many")]
    [InlineData(1L, "one")]
    [InlineData(2L, "few")]
    [InlineData(4L, "few")]
    [InlineData(5L, "many")]
    [InlineData(11L, "many")]
    [InlineData(12L, "many")]
    [InlineData(14L, "many")]
    [InlineData(21L, "many")]
    [InlineData(22L, "few")]
    [InlineData(25L, "many")]
    [InlineData(101L, "many")]
    [InlineData(112L, "many")]
    [InlineData(-1L, "one")]
    [InlineData(-22L, "few")]
    [InlineData(long.MinValue, "many")]
    [InlineData(long.MaxValue, "many")]
    public void PolishPluralRulesUseMagnitudeWithoutOverflow(long count, string expected)
        => Assert.Equal(expected, AppText.PluralCategory(count, "pl"));

    [Theory]
    [InlineData(0L, "other")]
    [InlineData(1L, "one")]
    [InlineData(-1L, "one")]
    [InlineData(2L, "other")]
    [InlineData(21L, "other")]
    [InlineData(long.MinValue, "other")]
    public void EnglishHasOnlySingularAndOtherIntegerForms(long count, string expected)
        => Assert.Equal(expected, AppText.PluralCategory(count, "en"));

    [Fact]
    public void CatalogsHaveMatchingNonemptyKeysAndValidEquivalentPlaceholders()
    {
        var english = AppText.Catalog("en");
        var polish = AppText.Catalog("pl");
        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), polish.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, englishText) in english)
        {
            var polishText = polish[key];
            Assert.False(string.IsNullOrWhiteSpace(key), "Catalog contains an empty key.");
            Assert.False(string.IsNullOrWhiteSpace(englishText), $"English text is empty: {key}");
            Assert.False(string.IsNullOrWhiteSpace(polishText), $"Polish text is empty: {key}");
            Assert.DoesNotContain('\uFFFD', englishText);
            Assert.DoesNotContain('\uFFFD', polishText);
            var englishFormat = ParseFormat(key, "en", englishText);
            var polishFormat = ParseFormat(key, "pl", polishText);
            Assert.Equal(englishFormat.MinimumArgumentCount, polishFormat.MinimumArgumentCount);
            Assert.Equal(Placeholders(englishText), Placeholders(polishText));
        }
    }

    [Fact]
    public void TheEnglishCatalogHoldsEnglish()
    {
        // A text pasted into the wrong file has the right key and the right placeholders, so only its words give it away.
        string[] sameInBothLanguages = ["Format", "MENU", "Folder", "min"];
        var polish = AppText.Catalog("pl");
        foreach (var (key, text) in AppText.Catalog("en"))
        {
            Assert.False(text.Any(character => "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ".Contains(character)), $"The English text of {key} contains Polish letters: {text}");
            if (text != polish[key]) continue;
            var words = System.Text.RegularExpressions.Regex.Matches(System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", " "), @"\p{L}{3,}").Select(match => match.Value);
            Assert.True(words.All(word => sameInBothLanguages.Contains(word)), $"The English and the Polish text of {key} are the same: {text}");
        }
    }

    [Fact]
    public void EveryTextKeyNamedInTheSourceExistsAndEveryCatalogKeyIsUsed()
    {
        // A key the catalogs do not have is shown as its own name, in both languages, and nothing else fails.
        var english = AppText.Catalog("en");
        var literal = new Regex("\"((?:Shell|Journey|Design|Core)[.][A-Za-z0-9_.]+)\"");
        var named = new HashSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var separator = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{separator}obj{separator}", StringComparison.Ordinal) || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)) continue;
            foreach (Match match in literal.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;
                named.Add(key);
                // A key ending in a full stop is completed at run time; a plural family is named by its stem.
                var known = key.EndsWith('.') ? english.Keys.Any(candidate => candidate.StartsWith(key, StringComparison.Ordinal)) : english.ContainsKey(key) || english.ContainsKey(key + ".other");
                if (!known) missing.Add($"{key} ({Path.GetFileName(file)})");
                // The library has its own catalogue; the window's texts are not its to use.
                if (file.Contains($"{separator}Diga.Core{separator}", StringComparison.Ordinal) && !key.StartsWith("Core.", StringComparison.Ordinal)) missing.Add($"{key} (named by the library in {Path.GetFileName(file)})");
            }
        }
        Assert.True(named.Count > 300, "The source files were not found.");
        Assert.True(missing.Count == 0, "Named in the source but missing from the catalogs: " + string.Join(", ", missing));
        string[] forms = [".one", ".few", ".many", ".other"];
        var unused = english.Keys.Where(key =>
        {
            var stem = forms.FirstOrDefault(form => key.EndsWith(form, StringComparison.Ordinal)) is { } form ? key[..^form.Length] : key;
            return !named.Contains(key) && !named.Contains(stem) && !named.Any(prefix => prefix.EndsWith('.') && key.StartsWith(prefix, StringComparison.Ordinal));
        }).ToArray();
        Assert.True(unused.Length == 0, "In the catalogs but named nowhere in the source: " + string.Join(", ", unused));
    }

    // Found from where the tests run, not from the compiler's idea of this file's path: a Release build replaces that path with "/_/".
    internal static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Diga.sln"))) return folder.FullName;
        throw new DirectoryNotFoundException("Diga.sln was not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public void ASentenceOfTheCatalogIsRecognisedWhateverFillsItsPlaceholdersAndASystemMessageIsNot(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var language = culture[..2];
        var unrecognised = new List<string>();
        foreach (var (key, sentence) in AppText.Catalog(language))
        {
            // Sentences with almost no words of their own cannot be told from anything else, and are not claimed to be.
            if (Regex.Replace(sentence, @"\{[^}]*\}", "").Count(char.IsLetter) < 12) continue;
            var filled = string.Format(CultureInfo.CurrentCulture, sentence, 42, 7, 3, 2, 1, 0);
            if (!AppText.IsCatalogText(filled) || !AppText.IsCatalogText(filled + " (HTTP 503: the service is busy)")) unrecognised.Add(key);
        }
        Assert.True(unrecognised.Count == 0, "Not recognised as the catalogue's own: " + string.Join(", ", unrecognised));
        // What .NET and Windows say when something fails; none of it is the application's wording.
        foreach (var system in new[]
        {
            "Access to the path 'D:\\Nagrania' is denied.", "The given key was not present in the dictionary.", "'<' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0.",
            "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host..", "Object reference not set to an instance of an object.",
            "The response ended prematurely, with at least 3221225472 additional bytes expected. (ResponseEnded)", "Value cannot be null. (Parameter 'path')", "42", "", "   "
        }) Assert.False(AppText.IsCatalogText(system), system);
        Assert.False(AppText.IsCatalogText(null));
    }

    [Fact]
    public void EveryPluralFamilyContainsAllCatalogForms()
    {
        foreach (var language in new[] { "en", "pl" })
        {
            var catalog = AppText.Catalog(language);
            var families = catalog.Keys.Where(key => key.EndsWith(".one", StringComparison.Ordinal))
                .Select(key => key[..^4]).ToArray();
            Assert.NotEmpty(families);
            foreach (var family in families)
                foreach (var category in new[] { "one", "few", "many", "other" })
                    Assert.True(catalog.ContainsKey(family + "." + category), $"Missing {language} plural form {family}.{category}");
        }
    }

    [Fact]
    public void BothLanguagesAreEmbeddedAndHaveNoDuplicateJsonKeys()
    {
        var assembly = typeof(AppText).Assembly;
        foreach (var language in new[] { "en", "pl" })
        {
            var names = assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith("." + language + ".json", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(names);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var json = JsonDocument.Parse(stream);
                Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
                foreach (var entry in json.RootElement.EnumerateObject())
                {
                    Assert.True(keys.Add(entry.Name), $"Duplicate {language} catalog key: {entry.Name}");
                    Assert.Equal(JsonValueKind.String, entry.Value.ValueKind);
                }
            }
            Assert.Equal(keys.Order(StringComparer.Ordinal), AppText.Catalog(language).Keys.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void UnknownRequestedLanguageUsesEnglishWithoutChangingThreadCulture()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
        var key = AppText.Catalog("en").First(pair => CompositeFormat.Parse(pair.Value).MinimumArgumentCount == 0).Key;
        Assert.Equal(AppText.Catalog("en")[key], AppText.TForLanguage("fr-FR", key));
        Assert.Equal("pl-PL", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void ConfigureChangesDisplayLanguageAndPreservesRegionalFormatting()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        AppText.Configure("pl", ["en-US"]);
        Assert.Equal("pl", AppText.LanguageCode);
        Assert.Equal("pl", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        Assert.Equal("pl", CultureInfo.DefaultThreadCurrentUICulture?.TwoLetterISOLanguageName);
        Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
        Assert.Equal("fr-FR", CultureInfo.DefaultThreadCurrentCulture?.Name);

        var english = AppText.Catalog("en");
        var polish = AppText.Catalog("pl");
        var translatedKey = english.First(pair => CompositeFormat.Parse(pair.Value).MinimumArgumentCount == 0
            && pair.Value != polish[pair.Key]).Key;
        Assert.Equal(polish[translatedKey], AppText.T(translatedKey));
        AppText.Configure("en", ["pl-PL"]);
        Assert.Equal(english[translatedKey], AppText.T(translatedKey));
        Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(4L)]
    [InlineData(5L)]
    [InlineData(11L)]
    [InlineData(12L)]
    [InlineData(14L)]
    [InlineData(21L)]
    [InlineData(22L)]
    [InlineData(25L)]
    [InlineData(101L)]
    [InlineData(112L)]
    [InlineData(1234L)]
    public void PluralRendersPolishFormAndCountUsingRegionalCulture(long count)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        AppText.Configure("pl", ["en-US"]);
        var catalog = AppText.Catalog("pl");
        const string baseKey = "Design.Selected";
        var suffix = count == 1 ? "one" : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? "few" : "many";
        var expected = string.Format(CultureInfo.CurrentCulture, catalog[baseKey + "." + suffix], count);
        var actual = AppText.Plural(baseKey, count);
        Assert.Equal(expected, actual);
        Assert.Contains(count.ToString("N0", CultureInfo.GetCultureInfo("en-US")), actual);
    }

    [Fact]
    public void CountFormattingDoesNotFollowTheTranslationLanguage()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
        AppText.Configure("en", ["pl-PL"]);
        const string key = "Design.Selected.other";
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, AppText.Catalog("en")[key], 1234567L), AppText.T(key, 1234567L));
        Assert.Equal("pl-PL", CultureInfo.CurrentCulture.Name);
    }

    [Fact]
    public void PluralPreservesAdditionalFormattingArguments()
    {
        AppText.Configure("pl", ["en-US"]);
        const string key = "Journey.Cloud.CompleteMessage";
        var expected = string.Format(CultureInfo.CurrentCulture, AppText.Catalog("pl")[key + ".few"], 2L, "OneDrive");
        Assert.Equal(expected, AppText.Plural(key, 2L, "OneDrive"));
    }

    [Fact]
    public async Task LookupUsesThreadUiCultureWithoutLeakingAcrossExecutionContexts()
    {
        AppText.Configure("en", ["en-US"]);
        var key = AppText.Catalog("en").First(pair => CompositeFormat.Parse(pair.Value).MinimumArgumentCount == 0
            && pair.Value != AppText.Catalog("pl")[pair.Key]).Key;
        await Task.Run(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
            Assert.Equal("pl", AppText.LanguageCode);
            Assert.Equal(AppText.Catalog("pl")[key], AppText.T(key));
        });
        Assert.Equal("en", AppText.LanguageCode);
        Assert.Equal(AppText.Catalog("en")[key], AppText.T(key));
    }

    private static CompositeFormat ParseFormat(string key, string language, string value)
    {
        var failure = Record.Exception(() => CompositeFormat.Parse(value));
        Assert.True(failure is null, $"Malformed {language} format {key}: {failure?.Message}");
        return CompositeFormat.Parse(value);
    }

    // CompositeFormat validates the grammar first. Compare the argument and format
    // specifier multiset, allowing translations to reorder placeholders naturally.
    private static string[] Placeholders(string value)
    {
        var result = new List<string>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '{') continue;
            if (i + 1 < value.Length && value[i + 1] == '{') { i++; continue; }
            var end = value.IndexOf('}', i + 1);
            var token = value[(i + 1)..end];
            var match = Regex.Match(token, @"^(?<index>\d+)(?:,\s*-?\d+)?(?::(?<format>.*))?$", RegexOptions.CultureInvariant);
            Assert.True(match.Success, $"Unexpected composite-format token: {token}");
            result.Add(match.Groups["index"].Value + ":" + match.Groups["format"].Value);
            i = end;
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
}
