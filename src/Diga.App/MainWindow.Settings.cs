using L = Diga.Core.Localization.AppText;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Diga.Core.Cloud;
using Diga.Core.Configuration;
using Diga.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Diga.App;

public sealed partial class MainWindow
{
    private AppSettings _settings = new();
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly CloudAuthService _cloudAuth = new();
    private string? _oneDriveAccount;

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        _settings = await _settingsStore.LoadAsync(ct);
        _outputDirectory = _settings.OutputDirectory;
        _format = _settings.DefaultFormat;
        _wizard = _settings.UseWizard;
        _oneDriveAccount = (await _cloudAuth.GetSavedAccountAsync(CloudProvider.OneDrive, _settings.EffectiveOneDriveClientId, ct))?.DisplayName;
        // Google Drive upload was removed in 0.5.1; a sign-in an earlier version saved for it is no longer usable here, so it is not kept.
        if (!string.IsNullOrWhiteSpace(_settings.GoogleClientId))
            try { await _cloudAuth.DisconnectAsync(CloudProvider.GoogleDrive, _settings.GoogleClientId, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void BuildSettingsPage(StackPanel page)
    {
        AddHeading(page, L.T("Shell.SettingsTitle"), L.T("Shell.SettingsDescription"), L.T("Shell.Preferences"));
        var languageCard = new StackPanel { Spacing = 12 };
        languageCard.Children.Add(SectionTitle(L.T("Shell.LanguageTitle"), Symbol.World));
        var language = new ComboBox { Header = JourneyHeader(L.T("Shell.LanguagePicker")), HorizontalAlignment = HorizontalAlignment.Stretch };
        var systemLanguage = L.ResolveLanguage("system", AppLocalization.SystemLanguages) == "pl" ? "Polski" : "English";
        language.Items.Add(new ComboBoxItem { Content = L.T("Shell.LanguageSystem", systemLanguage), Tag = "system" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.Items.Add(new ComboBoxItem { Content = "Polski", Tag = "pl" });
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, L.NormalizePreference(_settings.Language)));
        AutomationProperties.SetName(language, L.T("Shell.LanguagePicker"));
        languageCard.Children.Add(language);
        languageCard.Children.Add(Body(L.T("Shell.LanguageHelp")));
        languageCard.Children.Add(Muted(L.T("Shell.LanguageRestartHelp"), 12));
        if (L.ResolveLanguage(_settings.Language, AppLocalization.SystemLanguages) != L.LanguageCode)
            languageCard.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = L.T("Shell.LanguagePending") });
        page.Children.Add(Card(languageCard));
        var folders = new StackPanel { Spacing = 16 };
        folders.Children.Add(SectionTitle(L.T("Shell.EverydayDefaults"), Symbol.Folder));
        var output = LabeledInput(folders, L.T("Shell.DefaultOutput"), _settings.OutputDirectory);
        folders.Children.Add(ActionButton(L.T("Shell.ChooseOutput"), async () => { var directory = await PickFolderAsync(); if (directory is not null) output.Text = directory; }));
        folders.Children.Add(Muted(L.T("Shell.DifferentOutput"), 12));
        var defaultFormat = new ComboBox { Header = JourneyHeader(L.T("Shell.DefaultFormat")), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var format in Enum.GetValues<OutputFormat>()) defaultFormat.Items.Add(new ComboBoxItem { Content = format switch { OutputFormat.Matroska => L.T("Shell.MkvRecommended"), OutputFormat.Mpeg2 => L.T("Shell.MpegContainer"), _ => L.T("Shell.Mp4Container") }, Tag = format });
        defaultFormat.SelectedItem = defaultFormat.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, _settings.DefaultFormat));
        AutomationProperties.SetName(defaultFormat, L.T("Shell.DefaultFormat"));
        folders.Children.Add(defaultFormat);
        var workflow = new StackPanel { Spacing = 16 };
        workflow.Children.Add(StageBadge(L.T("Shell.YourPace"), "Teal"));
        workflow.Children.Add(SectionTitle(L.T("Shell.Guidance"), Symbol.Forward));
        var wizard = new CheckBox { Content = new TextBlock { Text = L.T("Shell.GuideNext"), TextWrapping = TextWrapping.Wrap },
            IsChecked = _settings.UseWizard, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(wizard, L.T("Shell.GuideNext"));
        workflow.Children.Add(wizard);
        workflow.Children.Add(Body(L.T("Shell.GuideDescription")));
        workflow.Children.Add(Muted(L.T("Shell.GuideManual"), 12));
        page.Children.Add(ResponsivePair(Card(folders), TintCard(workflow), 820));

        var cacheOptions = new StackPanel { Spacing = 14 };
        var cache = LabeledInput(cacheOptions, L.T("Shell.CacheFolder"), _settings.CacheDirectory);
        cacheOptions.Children.Add(ActionButton(L.T("Shell.ChooseCache"), async () => { var directory = await PickFolderAsync(); if (directory is not null) cache.Text = directory; }));
        cacheOptions.Children.Add(Muted(L.T("Shell.CacheDescription"), 12));
        cacheOptions.Children.Add(ActionButton(L.T("Shell.ClearCache"), ClearSessionCacheAsync));
        cacheOptions.Children.Add(Muted(L.T("Shell.ClearCacheHelp"), 11));

        var tools = new StackPanel { Spacing = 14 };
        tools.Children.Add(Body(L.T("Shell.ToolsDescription")));
        var ffmpeg = LabeledInput(tools, L.T("Shell.FfmpegExe"), _settings.FfmpegPath);
        tools.Children.Add(ActionButton(L.T("Shell.ChooseFfmpeg"), async () => { var file = await PickFileAsync([".exe"]); if (file is not null) ffmpeg.Text = file.Path; }));
        var ffprobe = LabeledInput(tools, L.T("Shell.FfprobeExe"), _settings.FfprobePath);
        tools.Children.Add(ActionButton(L.T("Shell.ChooseFfprobe"), async () => { var file = await PickFileAsync([".exe"]); if (file is not null) ffprobe.Text = file.Path; }));
        var mediaInfo = LabeledInput(tools, L.T("Shell.MediaInfoLib"), _settings.MediaInfoPath);
        tools.Children.Add(ActionButton(L.T("Shell.ChooseMediaInfo"), async () => { var file = await PickFileAsync([".dll"]); if (file is not null) mediaInfo.Text = file.Path; }));

        var cloudHeading = new StackPanel { Spacing = 9 };
        cloudHeading.Children.Add(SectionTitle(L.T("Shell.CloudConnections"), Symbol.Upload));
        cloudHeading.Children.Add(Muted(L.T("Shell.CloudDescription"), 12));
        page.Children.Add(cloudHeading);
        var microsoftCard = new StackPanel { Spacing = 15 };
        microsoftCard.Children.Add(StageBadge("Microsoft OneDrive", "Teal"));
        microsoftCard.Children.Add(Body(_oneDriveAccount is { } microsoftAccount
            ? L.T("Shell.ConnectedAccount", microsoftAccount) : L.T("Shell.NotConnected")));
        var onedrive = LabeledInput(microsoftCard, L.T("Shell.PublicClient"), _settings.OneDriveClientId);
        onedrive.PlaceholderText = AppSettings.BuiltInOneDriveClientId;
        AutomationProperties.SetName(onedrive, L.T("Shell.MicrosoftClient"));
        microsoftCard.Children.Add(Muted(L.T("Shell.BuiltInClient", AppSettings.BuiltInOneDriveClientId), 12));
        var microsoftSetup = new StackPanel { Spacing = 12, Children = {
            Body(L.T("Shell.MicrosoftSetupDescription")),
            Muted(L.T("Shell.MicrosoftPermissions"), 12),
            LinkButton(L.T("Shell.MicrosoftSetupLink"), "https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app")
        } };

        AppSettings GatherSettings() => _settings with
        {
            Language = (string)((ComboBoxItem)language.SelectedItem).Tag,
            OutputDirectory = output.Text.Trim(), CacheDirectory = cache.Text.Trim(),
            FfmpegPath = ffmpeg.Text.Trim(), FfprobePath = ffprobe.Text.Trim(), MediaInfoPath = mediaInfo.Text.Trim(),
            OneDriveClientId = onedrive.Text.Trim(),
            UseWizard = wizard.IsChecked == true,
            DefaultFormat = (OutputFormat)((ComboBoxItem)defaultFormat.SelectedItem).Tag
        };

        async Task SaveDraftAsync(CancellationToken ct)
        {
            var draft = GatherSettings();
            JsonSettingsStore.Validate(draft);
            var previousClient = _settings.EffectiveOneDriveClientId;
            var clientChanged = !StringComparer.Ordinal.Equals(previousClient, draft.EffectiveOneDriveClientId);
            // A sign-in belongs to one application ID. The one saved for the previous ID is removed before the new ID is saved,
            // so a removal that fails leaves the old ID in place and is tried again by the next save.
            if (clientChanged)
            {
                await _cloudAuth.DisconnectAsync(CloudProvider.OneDrive, previousClient, ct);
                _oneDriveAccount = null;
            }
            await _settingsStore.SaveAsync(draft, ct);
            _settings = draft;
            _outputDirectory = draft.OutputDirectory;
            _format = draft.DefaultFormat;
            _wizard = draft.UseWizard;
            if (clientChanged)
                _oneDriveAccount = (await _cloudAuth.GetSavedAccountAsync(CloudProvider.OneDrive, draft.EffectiveOneDriveClientId, ct))?.DisplayName;
        }

        Task SavePreferencesAsync() => RunOperationAsync(async ct =>
        {
            await SaveDraftAsync(ct);
            var restartRequired = L.ResolveLanguage(_settings.Language, AppLocalization.SystemLanguages) != L.LanguageCode;
            ShowBanner(L.T(restartRequired ? "Shell.LanguageRestartTitle" : "Shell.PreferencesSaved"),
                L.T(restartRequired ? "Shell.LanguageRestartMessage" : "Shell.PreferencesSavedHelp"), InfoBarSeverity.Success);
        }, L.T("Shell.PreferencesSavedStatus"));
        // Keep the language action close to its selector on this long settings page.
        languageCard.Children.Add(ActionButton(L.T("Shell.SavePreferences"), SavePreferencesAsync, true));

        microsoftCard.Children.Add(ActionRow(
            ActionButton(L.T("Shell.ConnectOneDrive"), () => ConnectOneDriveAsync(SaveDraftAsync)),
            ActionButton(L.T("Shell.Disconnect"), () => DisconnectOneDriveAsync(SaveDraftAsync))));
        microsoftCard.Children.Add(new Expander { Header = JourneyHeader(L.T("Shell.MicrosoftFirstSetup")), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = microsoftSetup });
        page.Children.Add(Card(microsoftCard));
        page.Children.Add(Muted(L.T("Shell.CloudSaveHelp"), 11));
        page.Children.Add(new Expander { Header = JourneyHeader(L.T("Shell.TemporaryCache")), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = cacheOptions });
        tools.Children.Add(ActionButton(L.T("Shell.CheckTools"), () => RunOperationAsync(async ct => { await SaveDraftAsync(ct); await ValidateToolsAsync(ct); }, L.T("Shell.ToolsAvailable"))));
        page.Children.Add(new Expander { Header = JourneyHeader(L.T("Shell.AdvancedTools")), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = tools });
        page.Children.Add(ActionButton(L.T("Shell.SavePreferences"), SavePreferencesAsync, true));
        var about = new StackPanel { Spacing = 12 };
        about.Children.Add(StageBadge("AMG / DIGA Archive", "Teal"));
        about.Children.Add(SectionTitle($"AMG DIGA Archive {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", Symbol.Help));
        about.Children.Add(Body(L.T("Shell.AboutDescription")));
        about.Children.Add(LinkButton(L.T("Shell.ProjectLink"), "https://github.com/lukasz-gratkowski/AmgDigaArchive"));
        about.Children.Add(LinkButton(L.T("Shell.PanasonicLink"), "https://github.com/leecher1337/panasonic-rec"));
        about.Children.Add(LinkButton(L.T("Shell.MediaInfoLink"), "https://mediaarea.net/en/MediaInfo"));
        about.Children.Add(Muted(L.T("Shell.LicenseNotice"), 11));
        page.Children.Add(new Expander { Header = JourneyHeader(L.T("Shell.About")), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = about });
    }

    private async Task ConnectOneDriveAsync(Func<CancellationToken, Task> saveSettings)
    {
        await RunOperationAsync(async ct =>
        {
            await saveSettings(ct);
            var client = _settings.EffectiveOneDriveClientId;
            SetProgress(null, L.T("Shell.WaitingSignIn"));
            var account = await _cloudAuth.AuthenticateAsync(new OAuthClientOptions(CloudProvider.OneDrive, client), ct);
            _oneDriveAccount = account.DisplayName;
            ShowBanner(L.T("Shell.AccountConnected"), L.T("Shell.AccountReady", account.DisplayName), InfoBarSeverity.Success);
        }, L.T("Shell.AccountConnectedStatus"));
    }

    private async Task DisconnectOneDriveAsync(Func<CancellationToken, Task> saveSettings)
    {
        await RunOperationAsync(async ct =>
        {
            // The fields on the page are saved so that they are not lost, but a page that cannot be saved must not keep the sign-in.
            var clientId = _settings.EffectiveOneDriveClientId;
            string? unsaved = null;
            try { await saveSettings(ct); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { unsaved = ex.Message; }
            foreach (var id in new[] { clientId, _settings.EffectiveOneDriveClientId }.Distinct(StringComparer.Ordinal))
                await _cloudAuth.DisconnectAsync(CloudProvider.OneDrive, id, ct);
            _oneDriveAccount = null;
            if (unsaved is null) ShowBanner(L.T("Shell.AccountDisconnected"), L.T("Shell.AccountDisconnectedHelp"), InfoBarSeverity.Success);
            else ShowBanner(L.T("Shell.AccountDisconnected"), L.T("Shell.AccountDisconnectedUnsaved", unsaved), InfoBarSeverity.Warning);
        }, L.T("Shell.AccountDisconnectedStatus"));
    }

    private async Task ValidateToolsAsync(CancellationToken ct)
    {
        foreach (var executable in new[] { _settings.FfmpegPath, _settings.FfprobePath })
        {
            SetProgress(null, L.T("Shell.CheckingTool", Path.GetFileName(executable)));
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start) ?? throw new IOException(L.T("Shell.ToolStartError", executable));
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException(L.T("Shell.ToolExitError", Path.GetFileName(executable), process.ExitCode, await stderr));
        }
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!NativeLibrary.TryLoad(_settings.MediaInfoPath, out var library)) throw new FileNotFoundException(L.T("Shell.MediaInfoLoadError"));
            NativeLibrary.Free(library);
        }, ct);
        ShowBanner(L.T("Shell.ToolsReady"), L.T("Shell.ToolsReadyHelp"), InfoBarSeverity.Success);
    }

    private async Task ClearSessionCacheAsync()
    {
        await RunOperationAsync(async ct =>
        {
            if (_player is not null) _player.Source = null;
            var temporaryFiles = _ownedCacheFiles.ToArray();
            var exports = _exports.Select(e => Path.GetFullPath(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Task.Run(() =>
            {
                foreach (var file in temporaryFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(file) || exports.Contains(Path.GetFullPath(file))) continue;
                    var parent = Path.GetFullPath(Path.GetDirectoryName(file)!);
                    if (Path.GetFileName(parent) != "session-" + _sessionId || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(L.T("Shell.CacheSafety"));
                    if (_fileSystem is not null) Diga.Core.Storage.DiskDiscovery.EnsureSafeDestination(_fileSystem.SourcePath, file);
                    File.Delete(file);
                }
            }, ct);
            _cached.Clear();
            _ownedCacheFiles.Clear();
            _stagedCacheFiles.Clear();
            ClearDlnaPreviewState();
            ShowBanner(L.T("Shell.CacheCleared"), L.T("Shell.CacheClearedHelp"), InfoBarSeverity.Success);
        }, L.T("Shell.CacheClearedStatus"));
    }

    private static TextBox LabeledInput(StackPanel parent, string header, string value)
    {
        var input = new TextBox { Header = new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap }, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, header);
        parent.Children.Add(input);
        return input;
    }
    private static HyperlinkButton LinkButton(string label, string url)
    {
        var link = new HyperlinkButton { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, NavigateUri = new Uri(url), Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(link, label);
        return link;
    }
}
