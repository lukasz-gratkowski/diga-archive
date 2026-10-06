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
    // What the Settings page held when it was last rebuilt or left; null until the page was opened.
    private AppSettings? _settingsDraft;
    // Set by a sign-in that stored the typed Google secret, so that the page stops carrying it.
    private bool _googleSecretStored;
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly CloudAuthService _cloudAuth = new();

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        var (loaded, setAside) = await _settingsStore.LoadOrSetAsideAsync(ct);
        // A tool location saved by an earlier version that is no longer a file means "find it for me"; the next save writes that down.
        static string Usable(string path) => Path.IsPathFullyQualified(path) && File.Exists(path) ? path : "";
        _settings = loaded with { FfmpegPath = Usable(loaded.FfmpegPath), FfprobePath = Usable(loaded.FfprobePath), MediaInfoPath = Usable(loaded.MediaInfoPath) };
        _outputDirectory = _settings.OutputDirectory;
        _format = _settings.DefaultFormat;
        _wizard = _settings.UseWizard;
        _dlnaKeepDelivered = !_settings.SaveAsContainer;
        var cache = _settings.CacheDirectory;
        // Earlier sessions that ended without cleaning up leave whole recordings behind; nothing waits for this.
        _ = Task.Run(() => RemoveStaleSessionCaches(cache, _sessionId), CancellationToken.None);
        // Which FFmpeg build is on this PC is learned by reading the programs, a hundred megabytes each; nothing waits for that either.
        _ = Task.Run(() => MediaToolLocator.Examine(), CancellationToken.None);
        await LoadCloudAccountsAsync(ct);
        // Left where it was, the file would be overwritten by the next thing the application saves.
        if (setAside is not null) ShowBanner(L.T("Shell.SettingsSetAsideTitle"), L.T("Shell.SettingsSetAside", setAside), InfoBarSeverity.Warning);
    }

    private void BuildSettingsPage(StackPanel page)
    {
        AddHeading(page, L.T("Shell.SettingsTitle"), L.T("Shell.SettingsDescription"), L.T("Shell.Preferences"));
        // The fields show what was typed last, saved or not; see _leavingPage below.
        var shown = _settingsDraft ?? _settings;
        // The destination and the way of saving are remembered from other pages; they are not this page's unsaved changes.
        if (_settingsDraft is not null && _settingsDraft != _settings with { UploadProvider = _settingsDraft.UploadProvider, SaveAsContainer = _settingsDraft.SaveAsContainer })
            page.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = L.T("Shell.UnsavedChanges") });
        var languageCard = new StackPanel { Spacing = 12 };
        languageCard.Children.Add(SectionTitle(L.T("Shell.LanguageTitle"), Symbol.World));
        var language = new ComboBox { Header = JourneyHeader(L.T("Shell.LanguagePicker")), HorizontalAlignment = HorizontalAlignment.Stretch };
        var systemLanguage = L.ResolveLanguage("system", AppLocalization.SystemLanguages) == "pl" ? "Polski" : "English";
        language.Items.Add(new ComboBoxItem { Content = L.T("Shell.LanguageSystem", systemLanguage), Tag = "system" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.Items.Add(new ComboBoxItem { Content = "Polski", Tag = "pl" });
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, L.NormalizePreference(shown.Language)));
        AutomationProperties.SetName(language, L.T("Shell.LanguagePicker"));
        languageCard.Children.Add(language);
        languageCard.Children.Add(Body(L.T("Shell.LanguageHelp")));
        languageCard.Children.Add(Muted(L.T("Shell.LanguageRestartHelp"), 12));
        if (L.ResolveLanguage(_settings.Language, AppLocalization.SystemLanguages) != L.LanguageCode)
            languageCard.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = L.T("Shell.LanguagePending") });
        page.Children.Add(Card(languageCard));
        var folders = new StackPanel { Spacing = 16 };
        folders.Children.Add(SectionTitle(L.T("Shell.EverydayDefaults"), Symbol.Folder));
        var output = LabeledInput(folders, L.T("Shell.DefaultOutput"), shown.OutputDirectory);
        folders.Children.Add(ActionButton(L.T("Shell.ChooseOutput"), async () => { var directory = await PickFolderAsync(); if (directory is not null) output.Text = directory; }));
        folders.Children.Add(Muted(L.T("Shell.DifferentOutput"), 12));
        var defaultFormat = new ComboBox { Header = JourneyHeader(L.T("Shell.DefaultFormat")), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var format in Enum.GetValues<OutputFormat>()) defaultFormat.Items.Add(new ComboBoxItem { Content = format switch { OutputFormat.Matroska => L.T("Journey.Format.Mkv"), OutputFormat.Mpeg2 => L.T("Journey.Format.Mpeg"), _ => L.T("Journey.Dlna.Mp4Compatible") }, Tag = format });
        defaultFormat.SelectedItem = defaultFormat.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, shown.DefaultFormat));
        AutomationProperties.SetName(defaultFormat, L.T("Shell.DefaultFormat"));
        folders.Children.Add(defaultFormat);
        var workflow = new StackPanel { Spacing = 16 };
        workflow.Children.Add(StageBadge(L.T("Shell.YourPace"), "Teal"));
        workflow.Children.Add(SectionTitle(L.T("Shell.Guidance"), Symbol.Forward));
        var wizard = new CheckBox { Content = new TextBlock { Text = L.T("Shell.GuideNext"), TextWrapping = TextWrapping.Wrap },
            IsChecked = shown.UseWizard, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(wizard, L.T("Shell.GuideNext"));
        workflow.Children.Add(wizard);
        workflow.Children.Add(Body(L.T("Shell.GuideDescription")));
        workflow.Children.Add(Muted(L.T("Shell.GuideManual"), 12));
        page.Children.Add(ResponsivePair(Card(folders), TintCard(workflow), 820));

        var cacheOptions = new StackPanel { Spacing = 14 };
        var cache = LabeledInput(cacheOptions, L.T("Shell.CacheFolder"), shown.CacheDirectory);
        cacheOptions.Children.Add(ActionButton(L.T("Shell.ChooseCache"), async () => { var directory = await PickFolderAsync(); if (directory is not null) cache.Text = directory; }));
        cacheOptions.Children.Add(Muted(L.T("Shell.CacheDescription"), 12));
        cacheOptions.Children.Add(ActionButton(L.T("Shell.ClearCache"), ClearSessionCacheAsync));
        cacheOptions.Children.Add(Muted(L.T("Shell.ClearCacheHelp"), 11));

        var tools = new StackPanel { Spacing = 14 };
        tools.Children.Add(Body(L.T("Shell.ToolsDescription")));
        var inUse = Ffmpeg;
        var pinnedVersion = FfmpegPackage.Pinned.Version;
        tools.Children.Add(Body(inUse switch
        {
            null => L.T("Shell.Ffmpeg.Missing"),
            { Source: FfmpegSource.SearchPath } => L.T("Shell.Ffmpeg.InUseOnPath", inUse.Ffmpeg, pinnedVersion),
            { Pinned: true } => L.T("Shell.Ffmpeg.InUsePinned", inUse.Ffmpeg, pinnedVersion),
            { Pinned: false } => L.T("Shell.Ffmpeg.InUseOther", inUse.Ffmpeg, pinnedVersion),
            _ => L.T("Shell.Ffmpeg.InUse", inUse.Ffmpeg)
        }));
        // With the pinned FFmpeg in place, or the user's own, the button would fetch all of it a second time; it says so and is
        // no longer the main action. A missing FFmpeg, or one of another version, makes it the thing to do.
        tools.Children.Add(inUse is null or { Pinned: false } ? ActionButton(FfmpegDownloadLabel, DownloadFfmpegAsync, true)
            : inUse.Source == FfmpegSource.SearchPath ? ActionButton(FfmpegDownloadLabel, DownloadFfmpegAsync)
            : ActionButton(L.T("Shell.Ffmpeg.DownloadAgain", pinnedVersion, FormatBytes(FfmpegPackage.Pinned.SizeBytes)), DownloadFfmpegAsync));
        tools.Children.Add(Muted(L.T("Shell.Ffmpeg.DownloadHelp", FfmpegPackage.Pinned.Version, MediaToolLocator.UserToolsDirectory), 12));
        // What is used when a field stays empty. Looked up once for the page: without an FFmpeg in the application's own folders
        // the lookup walks the whole PATH.
        var automatic = MediaToolLocator.FindFfmpeg(null, null);
        TextBox ToolInput(string label, string saved, string fileName)
        {
            var input = LabeledInput(tools, label, saved);
            // For the two programs the pair found together, for the library its one place.
            var found = fileName == MediaToolLocator.Ffmpeg ? automatic?.Ffmpeg : fileName == MediaToolLocator.Ffprobe ? automatic?.Ffprobe : MediaToolLocator.Find(null, fileName);
            input.PlaceholderText = found is not null ? L.T("Shell.ToolAutomatic", found) : L.T("Shell.ToolNotFound");
            return input;
        }
        var ffmpeg = ToolInput(L.T("Shell.FfmpegExe"), shown.FfmpegPath, MediaToolLocator.Ffmpeg);
        tools.Children.Add(ActionButton(L.T("Shell.ChooseFfmpeg"), async () => { var file = await PickFileAsync([".exe"]); if (file is not null) ffmpeg.Text = file.Path; }));
        var ffprobe = ToolInput(L.T("Shell.FfprobeExe"), shown.FfprobePath, MediaToolLocator.Ffprobe);
        tools.Children.Add(ActionButton(L.T("Shell.ChooseFfprobe"), async () => { var file = await PickFileAsync([".exe"]); if (file is not null) ffprobe.Text = file.Path; }));
        var mediaInfo = ToolInput(L.T("Shell.MediaInfoLib"), shown.MediaInfoPath, MediaToolLocator.MediaInfo);
        tools.Children.Add(ActionButton(L.T("Shell.ChooseMediaInfo"), async () => { var file = await PickFileAsync([".dll"]); if (file is not null) mediaInfo.Text = file.Path; }));

        var cloudHeading = new StackPanel { Spacing = 9 };
        cloudHeading.Children.Add(SectionTitle(L.T("Shell.CloudConnections"), Symbol.Upload));
        cloudHeading.Children.Add(Muted(L.T("Shell.CloudDescription"), 12));
        cloudHeading.Children.Add(ProviderSelector());
        cloudHeading.Children.Add(Muted(L.T("Shell.UploadToHelp"), 12));
        page.Children.Add(cloudHeading);

        // What a card says about its sign-in: in use, ended by the provider, or not there yet.
        string CloudStatus(CloudProvider provider, string notConnected) =>
            _expiredSignIns.Contains(provider) ? L.T("Shell.SignInEnded")
            : _accountNames.TryGetValue(provider, out var account) ? L.T("Shell.ConnectedAccount", account) : L.T(notConnected);

        var microsoftSaved = _accountNames.ContainsKey(CloudProvider.OneDrive);
        var microsoftCard = new StackPanel { Spacing = 15 };
        microsoftCard.Children.Add(StageBadge("Microsoft OneDrive", "Teal"));
        microsoftCard.Children.Add(Body(CloudStatus(CloudProvider.OneDrive, "Shell.NotConnected")));
        microsoftCard.Children.Add(Muted(L.T("Shell.MicrosoftConsent"), 12));
        // The application ID concerns only people with a registration of their own, so it lives in their section.
        var microsoftSetup = new StackPanel { Spacing = 12 };
        microsoftSetup.Children.Add(Body(L.T("Shell.MicrosoftSetupDescription")));
        var onedrive = LabeledInput(microsoftSetup, L.T("Shell.PublicClient"), shown.OneDriveClientId);
        onedrive.PlaceholderText = L.T("Shell.BuiltInPlaceholder");
        AutomationProperties.SetName(onedrive, L.T("Shell.MicrosoftClient"));
        microsoftSetup.Children.Add(Muted(L.T("Shell.BuiltInClient", AppSettings.BuiltInOneDriveClientId), 12));
        microsoftSetup.Children.Add(Muted(L.T("Shell.MicrosoftPermissions"), 12));
        microsoftSetup.Children.Add(CloudGuideLink(CloudProvider.OneDrive, Links.Document("CLOUD-SETUP.md", "onedrive-with-your-own-registration")));

        var googleSaved = _accountNames.ContainsKey(CloudProvider.GoogleDrive);
        var googleConnected = IsCloudConnected(CloudProvider.GoogleDrive);
        var googleCard = new StackPanel { Spacing = 15 };
        googleCard.Children.Add(StageBadge(ProviderName(CloudProvider.GoogleDrive), "Violet"));
        googleCard.Children.Add(Body(CloudStatus(CloudProvider.GoogleDrive, "Shell.GoogleNotConnected")));
        // Before the two fields mean anything, the user has to know that a setup at Google comes first, and why.
        if (!googleConnected)
        {
            googleCard.Children.Add(Muted(L.T("Shell.GoogleSetupDescription"), 12));
            googleCard.Children.Add(CloudGuideLink(CloudProvider.GoogleDrive, Links.Document("CLOUD-SETUP.md", "google-drive")));
        }
        var google = LabeledInput(googleCard, L.T("Shell.GoogleClientId"), shown.GoogleClientId);
        var googleSecret = new PasswordBox { Header = new TextBlock { Text = L.T("Shell.GoogleClientSecret"), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch,
            Password = _googleSecretDraft, PlaceholderText = googleSaved ? L.T("Shell.GoogleSecretSaved") : "" };
        AutomationProperties.SetName(googleSecret, L.T("Shell.GoogleClientSecret"));
        googleCard.Children.Add(googleSecret);
        googleCard.Children.Add(Muted(L.T("Shell.GoogleSecretHelp"), 12));
        var googleDetails = new StackPanel { Spacing = 12, Children = {
            Body(L.T("Shell.GooglePermissions")),
            Muted(L.T("Shell.GoogleSevenDays"), 12),
            CloudGuideLink(CloudProvider.GoogleDrive, Links.Document("CLOUD-SETUP.md", "google-drive"))
        } };

        AppSettings GatherSettings() => _settings with
        {
            Language = (string)((ComboBoxItem)language.SelectedItem).Tag,
            OutputDirectory = output.Text.Trim(), CacheDirectory = cache.Text.Trim(),
            FfmpegPath = ffmpeg.Text.Trim(), FfprobePath = ffprobe.Text.Trim(), MediaInfoPath = mediaInfo.Text.Trim(),
            OneDriveClientId = onedrive.Text.Trim(), GoogleClientId = google.Text.Trim(), UploadProvider = _uploadProvider,
            UseWizard = wizard.IsChecked == true,
            DefaultFormat = (OutputFormat)((ComboBoxItem)defaultFormat.SelectedItem).Tag
        };
        // Every action on this page rebuilds it. What was typed and not yet saved is carried over, the Google secret included:
        // Google shows a client secret once, so it must not be lost to a failed sign-in or to a click on another button.
        _leavingPage = () =>
        {
            _settingsDraft = GatherSettings();
            _googleSecretDraft = _googleSecretStored ? "" : googleSecret.Password;
            _googleSecretStored = false;
        };

        async Task SaveDraftAsync(CancellationToken ct)
        {
            var draft = GatherSettings();
            JsonSettingsStore.Validate(draft);
            foreach (var tool in new[] { draft.FfmpegPath, draft.FfprobePath, draft.MediaInfoPath })
                if (tool.Length != 0 && !(Path.IsPathFullyQualified(tool) && File.Exists(tool))) throw new InvalidDataException(L.T("Shell.Configuration.Tools"));
            // A value pasted into the wrong field (an e-mail address, the client secret) would end in the settings file and in the browser's address.
            if (draft.OneDriveClientId.Length != 0 && !Guid.TryParseExact(draft.OneDriveClientId, "D", out _)) throw new InvalidDataException(L.T("Shell.MicrosoftClientShape"));
            if (draft.GoogleClientId.Length != 0 && !draft.GoogleClientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("Shell.GoogleClientShape"));
            var changed = CloudProviders.Where(provider => !StringComparer.Ordinal.Equals(ClientIdFor(_settings, provider), ClientIdFor(draft, provider))).ToArray();
            var (previousCache, previousOutput, previousFormat, previousWizard) = (_settings.CacheDirectory, _settings.OutputDirectory, _settings.DefaultFormat, _settings.UseWizard);
            // The page is saved by several buttons, so what the page does not show must survive it: the destination and the way of
            // saving are taken from the file as it is now.
            draft = await _settingsStore.UpdateAsync(stored => draft with { UploadProvider = _uploadProvider, SaveAsContainer = stored.SaveAsContainer }, ct);
            _settings = draft;
            // A sign-in belongs to the application or client ID it was made with. With another ID the application is not connected;
            // the saved sign-in stays where it is, is found again if the ID comes back, and is removed only by Disconnect, which asks.
            foreach (var provider in changed) ForgetCloudAccount(provider);
            // This session keeps the folder it started with. What earlier sessions left in it is removed now, because the next
            // start will look only in the new folder.
            if (!string.Equals(previousCache, draft.CacheDirectory, StringComparison.OrdinalIgnoreCase))
                _ = Task.Run(() => RemoveStaleSessionCaches(previousCache, _sessionId), CancellationToken.None);
            // What was chosen for this session on the Preserve page gives way only to a default that was itself just edited.
            if (draft.OutputDirectory != previousOutput) _outputDirectory = draft.OutputDirectory;
            if (draft.DefaultFormat != previousFormat) _format = draft.DefaultFormat;
            if (draft.UseWizard != previousWizard) _wizard = draft.UseWizard;
            _ffmpegSeen = null;
            foreach (var provider in changed)
            {
                var current = ClientIdFor(provider);
                if (current.Length == 0 || await _cloudAuth.GetSavedAccountAsync(provider, current, ct) is not { } account) continue;
                _accountNames[provider] = account.DisplayName;
                if (account.SignInEnded) _expiredSignIns.Add(provider);
            }
        }

        Task SavePreferencesAsync() => RunOperationAsync(async ct =>
        {
            await SaveDraftAsync(ct);
            var restartRequired = L.ResolveLanguage(_settings.Language, AppLocalization.SystemLanguages) != L.LanguageCode;
            ShowBanner(L.T(restartRequired ? "Shell.LanguageRestartTitle" : "Shell.PreferencesSaved"),
                L.T(restartRequired ? "Shell.LanguageRestartMessage" : "Shell.PreferencesSavedHelp"), InfoBarSeverity.Success);
        }, L.T("Shell.PreferencesSavedStatus"));
        // One Save for the whole page, always in view: the page is long, and a Save button inside one card reads as that card's own.
        var saveBar = new Grid { Padding = new Thickness(26, 10, 26, 10), Style = AppStyle("DigaStatusBarStyle") };
        var save = ActionButton(L.T("Shell.SavePreferences"), SavePreferencesAsync, true);
        save.MaxWidth = 1300;
        saveBar.Children.Add(save);
        _pageFooter.Content = saveBar;

        // Disconnect is offered only when there is a saved sign-in to remove. That includes one made with an earlier ID: changing
        // the ID does not remove it, and Disconnect is the only thing in the application that does.
        var savedSignIns = new ProtectedTokenStore();
        var microsoftDisconnect = ActionButton(L.T("Shell.Disconnect"), () => DisconnectCloudAsync(CloudProvider.OneDrive, SaveDraftAsync));
        AutomationProperties.SetName(microsoftDisconnect, L.T("Shell.DisconnectNamed", "OneDrive"));
        microsoftDisconnect.IsEnabled = microsoftSaved || savedSignIns.HasAny(CloudProvider.OneDrive);
        microsoftCard.Children.Add(ActionRow(
            ActionButton(L.T("Shell.ConnectOneDrive"), () => ConnectCloudAsync(CloudProvider.OneDrive, SaveDraftAsync), !IsCloudConnected(CloudProvider.OneDrive) && _cloudAskedFor is null or CloudProvider.OneDrive),
            microsoftDisconnect));
        if (microsoftSaved) microsoftCard.Children.Add(Muted(L.T("Shell.SwitchAccount"), 12));
        microsoftCard.Children.Add(Section("settings:microsoft", L.T("Shell.MicrosoftFirstSetup"), microsoftSetup, shown.OneDriveClientId.Length != 0));
        var googleDisconnect = ActionButton(L.T("Shell.Disconnect"), () => DisconnectCloudAsync(CloudProvider.GoogleDrive, SaveDraftAsync));
        AutomationProperties.SetName(googleDisconnect, L.T("Shell.DisconnectNamed", ProviderName(CloudProvider.GoogleDrive)));
        googleDisconnect.IsEnabled = googleSaved || savedSignIns.HasAny(CloudProvider.GoogleDrive);
        googleCard.Children.Add(ActionRow(
            ActionButton(L.T("Shell.ConnectGoogle"), () => ConnectCloudAsync(CloudProvider.GoogleDrive, SaveDraftAsync, googleSecret.Password), !googleConnected && _cloudAskedFor is null or CloudProvider.GoogleDrive),
            googleDisconnect));
        if (googleSaved) googleCard.Children.Add(Muted(L.T("Shell.SwitchAccount"), 12));
        googleCard.Children.Add(Section("settings:google", L.T("Shell.GoogleSetupTitle"), googleDetails));
        page.Children.Add(ResponsivePair(Card(microsoftCard), Card(googleCard), 850));
        page.Children.Add(ActionButton(L.T("Journey.Cloud.SeeFiles"), () => { Navigate("cloud"); return Task.CompletedTask; }));
        page.Children.Add(Muted(L.T("Shell.CloudSaveHelp"), 11));
        page.Children.Add(Section("settings:temporary", L.T("Shell.TemporaryCache"), cacheOptions));
        tools.Children.Add(ActionButton(L.T("Shell.CheckTools"), () => RunOperationAsync(async ct => { await SaveDraftAsync(ct); await ValidateToolsAsync(ct); }, L.T("Shell.ToolsAvailable"))));
        page.Children.Add(Section("settings:tools", L.T("Shell.AdvancedTools"), tools, !FfmpegAvailable || FfmpegOutdated));
        var about = new StackPanel { Spacing = 12 };
        about.Children.Add(StageBadge("AMG / DIGA Archive", "Teal"));
        about.Children.Add(SectionTitle($"AMG DIGA Archive {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", Symbol.Help));
        about.Children.Add(Body(L.T("Shell.AboutDescription")));
        about.Children.Add(LinkButton(L.T("Shell.ProjectLink"), Links.Project));
        about.Children.Add(LinkButton(L.T("Shell.MediaInfoLink"), Links.MediaInfo));
        // The application is self-contained: Windows Update does not renew what it carries, and it never asks a server whether
        // a newer version exists. Looking is the user's own step.
        about.Children.Add(LinkButton(L.T("Shell.ReleasesLink"), Links.Releases));
        about.Children.Add(Muted(L.T("Shell.ReleasesHelp", Environment.Version.ToString(3), WindowsAppSdkVersion, MediaInfoVersion()), 12));
        about.Children.Add(Muted(L.T("Shell.LogHelp", App.LogDirectory), 12));
        var openLog = ActionButton(L.T("Shell.OpenLogFolder"), () => { OpenFolder(App.LogDirectory); return Task.CompletedTask; });
        var deleteLog = ActionButton(L.T("Shell.DeleteLog"), () => RunOperationAsync(async ct =>
        {
            await Task.Run(App.DeleteLog, ct);
            ShowBanner(L.T("Shell.LogDeletedTitle"), L.T("Shell.LogDeleted"), InfoBarSeverity.Success);
        }, L.T("Shell.LogDeleted")));
        openLog.IsEnabled = deleteLog.IsEnabled = Directory.Exists(App.LogDirectory);
        about.Children.Add(ActionRow(openLog, deleteLog));
        about.Children.Add(Muted(L.T("Shell.LicenseNotice"), 11));
        page.Children.Add(Section("settings:about", L.T("Shell.About"), about));
    }

    // Recorded when the application is built: the runtime's own files do not carry the version of the package they came from.
    private static string WindowsAppSdkVersion => typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "WindowsAppSdkVersion")?.Value ?? "–";

    /// <summary>The version of the MediaInfo library beside the application, in the form its maker writes it (26.05); a dash when it cannot be read.</summary>
    private string MediaInfoVersion()
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(MediaInfoLibrary);
            return version.FileMajorPart == 0 ? "–" : $"{version.FileMajorPart}.{version.FileMinorPart:00}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return "–"; }
    }

    private async Task ValidateToolsAsync(CancellationToken ct)
    {
        var (ffmpeg, ffprobe) = (FfmpegExecutable, FfprobeExecutable);
        if (ffmpeg is null || ffprobe is null) throw new FileNotFoundException(L.T("Shell.Ffmpeg.RequiredForCheck"));
        foreach (var executable in new[] { ffmpeg, ffprobe })
        {
            SetProgress(null, L.T("Shell.CheckingTool", Path.GetFileName(executable)));
            // Started as every media tool is: by the library's runner, which ends the program with the application and after a time limit.
            var run = await new ProcessRunner().RunAsync(executable, ["-version"], new ProcessLimits(Overall: TimeSpan.FromSeconds(30)), cancellationToken: ct);
            if (run.ExitCode != 0) throw new IOException(L.T("Shell.ToolExitError", Path.GetFileName(executable), run.ExitCode, run.ErrorSummary));
        }
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!NativeLibrary.TryLoad(MediaInfoLibrary, out var library)) throw new FileNotFoundException(L.T("Shell.MediaInfoLoadError"));
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
                    // Refused for anything that is not directly inside this session's own folder.
                    SessionFiles.Delete(file);
                }
            }, ct);
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
    /// <summary>
    /// The link to the setup guide. It reads the same for both services and leads to different sections, so its name for a
    /// screen reader, whose list of links shows names alone, says which service it is for.
    /// </summary>
    private static HyperlinkButton CloudGuideLink(CloudProvider provider, string url)
    {
        var link = LinkButton(L.T("Shell.CloudGuideLink"), url);
        AutomationProperties.SetName(link, L.T("Shell.CloudGuideLinkFor", ProviderName(provider)));
        return link;
    }

    private static HyperlinkButton LinkButton(string label, string url)
    {
        var link = new HyperlinkButton { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, NavigateUri = new Uri(url), Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(link, SpokenLabel(label));
        return link;
    }
}
