using Diga.Core.Cloud;
using Diga.Core.Configuration;
using Diga.Core.Media;
using Diga.Core.Localization;
using Xunit;

namespace Diga.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaSettingsTests-" + Guid.NewGuid().ToString("N"));
    public ConfigurationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private JsonSettingsStore Store() => new(Path.Combine(_directory, "settings.json"));

    [Fact] public async Task ASingleChoiceIsWrittenIntoTheFileAsItIsNowNotAsItWasWhenItWasRead()
    {
        var first = Store();
        var second = Store();
        await first.SaveAsync(new AppSettings { OutputDirectory = _directory });
        var readEarlier = await second.LoadAsync();
        // Another part of the application, or another window, saves something in between.
        await first.SaveAsync(new AppSettings { OutputDirectory = _directory, GoogleClientId = "typed-later.apps.googleusercontent.com", Language = "pl" });
        var updated = await second.UpdateAsync(stored => stored with { UploadProvider = CloudProvider.GoogleDrive });
        Assert.Empty(readEarlier.GoogleClientId);
        Assert.Equal(("typed-later.apps.googleusercontent.com", "pl", CloudProvider.GoogleDrive), (updated.GoogleClientId, updated.Language, updated.UploadProvider));
        Assert.Equal(updated, await first.LoadAsync());
        // A change that makes the settings invalid is refused and leaves the file as it was.
        await Assert.ThrowsAsync<InvalidDataException>(() => second.UpdateAsync(stored => stored with { OutputDirectory = "relative" }));
        Assert.Equal(updated, await first.LoadAsync());
        // Without a file the change is applied to the defaults.
        var fresh = new JsonSettingsStore(Path.Combine(_directory, "fresh", "settings.json"));
        Assert.True((await fresh.UpdateAsync(stored => stored with { SaveAsContainer = true })).SaveAsContainer);
        Assert.True((await fresh.LoadAsync()).SaveAsContainer);
    }

    [Fact]
    public async Task NullForATextInAHandEditedFileIsReadAsTheEmptyDefault()
    {
        var store = Store();
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"Language\":null,\"FfmpegPath\":null,\"FfprobePath\":null,\"MediaInfoPath\":null,\"GoogleClientId\":null,\"OneDriveClientId\":null,\"UseWizard\":false}");
        var (settings, setAside) = await store.LoadOrSetAsideAsync();
        Assert.Null(setAside);
        Assert.Equal(new AppSettings { UseWizard = false }, settings);
        Assert.Equal(AppSettings.BuiltInOneDriveClientId, settings.EffectiveOneDriveClientId);
        // A folder cannot be defaulted in the same way: the file is refused, as before.
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"OutputDirectory\":null}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Language\":\"pl\"}")]
    [InlineData("{\"Version\":1,\"OutputDirectory\":\"relative\\folder\"}")]
    public async Task ASettingsFileThatCannotBeUsedIsSetAsideNotOverwritten(string content)
    {
        var store = Store();
        await File.WriteAllTextAsync(store.FilePath, content);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        var (settings, setAside) = await store.LoadOrSetAsideAsync();
        Assert.Equal(new AppSettings(), settings);
        Assert.NotNull(setAside);
        Assert.StartsWith("settings.invalid-", Path.GetFileName(setAside), StringComparison.Ordinal);
        Assert.Equal(content, await File.ReadAllTextAsync(setAside!));
        Assert.False(File.Exists(store.FilePath));
        // The next save writes a new file; the one set aside stays as it was.
        await store.SaveAsync(settings with { OutputDirectory = _directory });
        Assert.Equal(content, await File.ReadAllTextAsync(setAside!));
        // A usable file is loaded as usual and nothing is set aside.
        var (loaded, none) = await store.LoadOrSetAsideAsync();
        Assert.Equal(_directory, loaded.OutputDirectory);
        Assert.Null(none);
        // A second unusable file on the same second gets its own name.
        await File.WriteAllTextAsync(store.FilePath, content);
        var (_, again) = await store.LoadOrSetAsideAsync();
        Assert.NotEqual(setAside, again);
        Assert.Equal(2, Directory.GetFiles(_directory, "settings.invalid-*").Length);
    }

    [Fact] public async Task MissingSettingsUsesSafeDefaults()
    {
        var settings = await Store().LoadAsync();
        Assert.Equal(OutputFormat.Matroska, settings.DefaultFormat);
        Assert.Empty(settings.GoogleClientId);
        Assert.True(settings.UseWizard);
        Assert.Equal("system", settings.Language);
    }
    [Theory]
    [InlineData("", AppSettings.BuiltInOneDriveClientId)]
    [InlineData("   ", AppSettings.BuiltInOneDriveClientId)]
    [InlineData(" my-own-registration ", "my-own-registration")]
    public void OneDriveUsesTheBuiltInApplicationIdUnlessTheUserSetsOne(string saved, string effective) =>
        Assert.Equal(effective, new AppSettings { OneDriveClientId = saved }.EffectiveOneDriveClientId);
    [Fact] public async Task BuiltInApplicationIdIsNotWrittenToTheSettingsFile()
    {
        Assert.True(Guid.TryParseExact(AppSettings.BuiltInOneDriveClientId, "D", out _));
        var store = Store();
        await store.SaveAsync(new AppSettings { OutputDirectory = _directory });
        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain(AppSettings.BuiltInOneDriveClientId, json);
        Assert.DoesNotContain("EffectiveOneDriveClientId", json);
        var loaded = await store.LoadAsync();
        Assert.Empty(loaded.OneDriveClientId);
        Assert.Equal(AppSettings.BuiltInOneDriveClientId, loaded.EffectiveOneDriveClientId);
    }
    [Fact] public async Task SettingsSavedByEarlierVersionsKeepTheirOwnApplicationIdOrFallBackToTheBuiltInOne()
    {
        var store = Store();
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"OneDriveClientId\":\"\",\"UseWizard\":false}");
        Assert.Equal(AppSettings.BuiltInOneDriveClientId, (await store.LoadAsync()).EffectiveOneDriveClientId);
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"OneDriveClientId\":\"earlier-own-id\",\"UseWizard\":false}");
        Assert.Equal("earlier-own-id", (await store.LoadAsync()).EffectiveOneDriveClientId);
    }
    [Fact] public async Task CloudDestinationDefaultsToOneDriveAndIsRemembered()
    {
        var store = Store();
        Assert.Equal(CloudProvider.OneDrive, (await store.LoadAsync()).UploadProvider);
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"GoogleClientId\":\"older-client\"}");
        Assert.Equal(CloudProvider.OneDrive, (await store.LoadAsync()).UploadProvider);
        await store.SaveAsync(new AppSettings { OutputDirectory = _directory, UploadProvider = CloudProvider.GoogleDrive });
        Assert.Contains("\"UploadProvider\": \"GoogleDrive\"", await File.ReadAllTextAsync(store.FilePath));
        Assert.Equal(CloudProvider.GoogleDrive, (await store.LoadAsync()).UploadProvider);
    }
    [Fact] public async Task UnknownCloudDestinationIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(new() { OutputDirectory = _directory, UploadProvider = (CloudProvider)9 }));
        var store = Store();
        await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"UploadProvider\":\"Dropbox\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }
    [Fact] public async Task SettingsRoundTripPreservesUserPreferences()
    {
        var store = Store();
        var settings = new AppSettings { OutputDirectory = _directory, DefaultFormat = OutputFormat.Mp4, GoogleClientId = "desktop-client", OneDriveClientId = "microsoft-client", UseWizard = false };
        await store.SaveAsync(settings);
        Assert.Equal(settings, await store.LoadAsync());
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact] public async Task OlderSettingsWithoutLanguageUseSystemWithoutRewritingTheFile()
    {
        var store = Store();
        const string legacyJson = "{\"Version\":1,\"GoogleClientId\":\"older-client\",\"UseWizard\":false}";
        await File.WriteAllTextAsync(store.FilePath, legacyJson);
        var settings = await store.LoadAsync();
        Assert.Equal("system", settings.Language);
        Assert.Equal("older-client", settings.GoogleClientId);
        Assert.False(settings.UseWizard);
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(store.FilePath));
    }
    [Fact] public async Task PolishLanguagePreferenceSurvivesSaveAndReload()
    {
        var store = Store();
        var expected = new AppSettings { Language = "pl", OutputDirectory = _directory, UseWizard = false };
        await store.SaveAsync(expected);
        var loaded = await store.LoadAsync();
        Assert.Equal(expected, loaded);
        Assert.Equal("pl", AppText.ResolveLanguage(loaded.Language, ["en-US"]));
    }
    [Fact] public async Task UnsupportedSavedLanguageIsPreservedWhileResolutionFallsBackToSystem()
    {
        var store = Store();
        await store.SaveAsync(new AppSettings { Language = "de-DE", OutputDirectory = _directory });
        var before = await File.ReadAllBytesAsync(store.FilePath);
        var loaded = await store.LoadAsync();
        Assert.Equal("de-DE", loaded.Language);
        Assert.Equal("pl", AppText.ResolveLanguage(loaded.Language, ["pl-PL", "en-US"]));
        Assert.Equal("en", AppText.ResolveLanguage(loaded.Language, ["de-DE", "fr-FR"]));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
    }
    [Fact] public async Task CorruptSettingsArePreservedAndReported()
    {
        var store = Store(); await File.WriteAllTextAsync(store.FilePath, "broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal("broken", await File.ReadAllTextAsync(store.FilePath));
    }
    [Fact] public async Task CancelledWritePreservesExistingSettings()
    {
        var store = Store(); await store.SaveAsync(new());
        var before = await File.ReadAllTextAsync(store.FilePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new() { GoogleClientId = "changed" }, new(true)));
        Assert.Equal(before, await File.ReadAllTextAsync(store.FilePath));
    }
    [Fact] public async Task ConcurrentSavesAlwaysProduceValidJson()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 15).Select(i => store.SaveAsync(new() { GoogleClientId = "client-" + i })));
        Assert.StartsWith("client-", (await store.LoadAsync()).GoogleClientId);
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Theory] [InlineData(0)] [InlineData(2)]
    public async Task UnsupportedVersionFailsWithoutWriting(int version)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(new() { Version = version }));
        Assert.Empty(Directory.GetFiles(_directory));
    }
    [Fact] public async Task RelativeOutputDirectoryIsRejected() => await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(new() { OutputDirectory = "relative" }));
    [Fact] public async Task InvalidFormatIsRejected() => await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(new() { DefaultFormat = (OutputFormat)900 }));
}
