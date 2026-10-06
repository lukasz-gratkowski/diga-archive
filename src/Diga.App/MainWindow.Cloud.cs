using L = Diga.Core.Localization.AppText;
using Diga.Core.Cloud;
using Diga.Core.Configuration;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Diga.App;

/// <summary>Cloud destinations: which one is used, the saved sign-ins, upload, and the read-only view of what is stored there.</summary>
public sealed partial class MainWindow
{
    private static readonly CloudProvider[] CloudProviders = [CloudProvider.OneDrive, CloudProvider.GoogleDrive];
    private readonly Dictionary<CloudProvider, string> _accountNames = [];
    // A saved sign-in the provider refused. The file stays (for Google it holds the client secret); only a new sign-in clears this.
    private readonly HashSet<CloudProvider> _expiredSignIns = [];
    private CloudProvider _uploadProvider = CloudProvider.OneDrive;
    // The listing is kept in memory only and belongs to one destination and one sign-in.
    private CloudListing? _cloudListing;
    private CloudProvider _cloudListingProvider;
    private DateTimeOffset _cloudListedAt;
    // Typed on the Settings page and kept until a sign-in has stored it: Google shows a client secret only once.
    private string _googleSecretDraft = "";

    // Google calls its service "Dysk Google" in Polish; Microsoft keeps "OneDrive" everywhere.
    private static string ProviderName(CloudProvider provider) => provider == CloudProvider.GoogleDrive ? L.T("Core.Cloud.Account.Google") : "OneDrive";
    private static string ClientIdFor(AppSettings settings, CloudProvider provider) =>
        provider == CloudProvider.GoogleDrive ? settings.GoogleClientId.Trim() : settings.EffectiveOneDriveClientId;
    private string ClientIdFor(CloudProvider provider) => ClientIdFor(_settings, provider);
    private static string ConnectLabel(CloudProvider provider) => L.T(provider == CloudProvider.GoogleDrive ? "Shell.ConnectGoogle" : "Shell.ConnectOneDrive");
    private bool IsCloudConnected(CloudProvider provider) => _accountNames.ContainsKey(provider) && !_expiredSignIns.Contains(provider);

    private string CloudConnectionLabel(CloudProvider provider) =>
        _expiredSignIns.Contains(provider) ? L.T("Journey.Cloud.SignInEnded", ProviderName(provider))
        : _accountNames.TryGetValue(provider, out var account) ? L.T("Journey.Cloud.Connected", account)
        : L.T("Journey.Cloud.Disconnected", ProviderName(provider));

    private void ForgetCloudAccount(CloudProvider provider)
    {
        _accountNames.Remove(provider);
        _expiredSignIns.Remove(provider);
        // Another account's file names must not stay on screen after its sign-in is gone.
        if (_cloudListingProvider == provider) _cloudListing = null;
    }

    private async Task LoadCloudAccountsAsync(CancellationToken ct)
    {
        _uploadProvider = _settings.UploadProvider;
        foreach (var provider in CloudProviders)
        {
            _accountNames.Remove(provider);
            var client = ClientIdFor(provider);
            if (client.Length == 0 || await _cloudAuth.GetSavedAccountAsync(provider, client, ct) is not { } account) continue;
            _accountNames[provider] = account.DisplayName;
            // A sign-in the provider refused in an earlier session is still refused; it must not read as connected until the first upload fails.
            if (account.SignInEnded) _expiredSignIns.Add(provider);
        }
    }

    /// <summary>The same choice is offered in Settings, Archive and the cloud view; choosing it is remembered at once.</summary>
    private ComboBox ProviderSelector()
    {
        var selector = new ComboBox { Header = JourneyHeader(L.T("Shell.UploadTo")), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var provider in CloudProviders)
            selector.Items.Add(new ComboBoxItem { Tag = provider, Content = ProviderName(provider) + "  ·  " +
                L.T(_expiredSignIns.Contains(provider) ? "Journey.Cloud.StateEnded" : _accountNames.ContainsKey(provider) ? "Journey.Cloud.StateConnected" : "Journey.Cloud.StateNotConnected") });
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, _uploadProvider));
        AutomationProperties.SetName(selector, L.T("Shell.UploadToAccessible"));
        selector.SelectionChanged += async (_, _) =>
        {
            if (selector.SelectedItem is not ComboBoxItem { Tag: CloudProvider chosen } || chosen == _uploadProvider) return;
            await SetUploadProviderAsync(chosen);
            // The page shows the destination in several places; it is rebuilt where the user is, with the focus back on this list.
            if (!_busy) RenderPageInPlace(L.T("Shell.UploadToAccessible"));
        };
        return selector;
    }

    private async Task SetUploadProviderAsync(CloudProvider chosen, CancellationToken ct = default)
    {
        _uploadProvider = chosen;
        try
        {
            // Only this one choice is written, into the file as it is now; see JsonSettingsStore.UpdateAsync.
            await _settingsStore.UpdateAsync(stored => stored with { UploadProvider = chosen }, ct);
            _settings = _settings with { UploadProvider = chosen };
        }
        // The choice still applies to this session; it is saved with the next successful save of the settings.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { App.LogException("Saving the cloud destination", ex); }
    }

    private FrameworkElement BuildCloudUploadCard()
    {
        var provider = _uploadProvider;
        var cloud = new StackPanel { Spacing = 16 };
        cloud.Children.Add(StageBadge(L.T("Journey.Cloud.Optional"), "Violet"));
        cloud.Children.Add(SectionTitle(L.T("Journey.Cloud.Title"), Symbol.Upload));
        cloud.Children.Add(Body(L.T("Journey.Cloud.Help")));
        cloud.Children.Add(ProviderSelector());
        cloud.Children.Add(Muted(CloudConnectionLabel(provider), 12));
        cloud.Children.Add(Muted(L.T(provider == CloudProvider.GoogleDrive ? "Journey.Cloud.WhereGoogle" : "Journey.Cloud.WhereOneDrive"), 12));
        _uploadSelectionText = Muted("", 12);
        cloud.Children.Add(_uploadSelectionText);
        _uploadSelectedButton = ActionButton(L.T("Journey.Cloud.UploadTo", ProviderName(provider)), UploadSelectedAsync, true);
        cloud.Children.Add(ActionRow(_uploadSelectedButton,
            ActionButton(L.T("Journey.Cloud.AddFiles"), AddFilesForUploadAsync),
            ActionButton(L.T("Journey.Cloud.SeeFiles"), () => { Navigate("cloud"); return Task.CompletedTask; }),
            ActionButton(L.T("Journey.Cloud.Manage"), OpenCloudSettingsAsync)));
        cloud.Children.Add(Muted(L.T("Journey.Cloud.SetupHelp"), 11));
        UpdateUploadSelection();
        return Card(cloud);
    }

    private void UpdateUploadSelection()
    {
        var selected = _exports.Where(e => _selectedExports.Contains(e.Path)).ToArray();
        if (_uploadSelectionText is not null) _uploadSelectionText.Text = _exports.Count == 0 ? L.T("Journey.Cloud.NothingSaved")
            : selected.Length == 0 ? L.T("Journey.Cloud.SelectHelp") : L.Plural("Journey.Cloud.Selected", selected.Length) + " " + L.T("Journey.Cloud.SelectedSize", FormatBytes(selected.Sum(FileLength)));
        if (_uploadSelectedButton is not null) _uploadSelectedButton.IsEnabled = selected.Length > 0;
    }

    private static long FileLength(ExportedFile file)
    {
        try { return new FileInfo(file.Path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Files saved in an earlier session, or by other means, can be uploaded too: they are added to the list by hand.</summary>
    private async Task AddFilesForUploadAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        foreach (var extension in new[] { ".mkv", ".mp4", ".mpg", ".mpeg", ".m2ts", ".ts", ".mts", ".vob" }) picker.FileTypeFilter.Add(extension);
        var added = 0;
        foreach (var picked in await picker.PickMultipleFilesAsync())
        {
            var path = Path.GetFullPath(picked.Path);
            if (!File.Exists(path)) continue;
            if (!_exports.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                _exports.Add(new ExportedFile(Path.GetFileName(path), path, L.T("Journey.Cloud.AddedSource"), L.T("Journey.Cloud.AddedDetails", FormatBytes(new FileInfo(path).Length)), L.T("Journey.Cloud.AddedSummary")) { AddedByHand = true });
            _selectedExports.Add(path);
            added++;
        }
        if (added == 0) return;
        RefreshJourney();
        RenderPageInPlace(L.T("Journey.Cloud.AddFiles"));
    }

    /// <summary>True when the destination has a usable saved sign-in; otherwise the user is taken to its Connect button in Settings.</summary>
    private async Task<bool> EnsureCloudConnectedAsync(CloudProvider provider)
    {
        var client = ClientIdFor(provider);
        // The label is filled at startup; if that failed, a sign-in may still be saved, so the store is asked before sending the user to sign in.
        if (!_accountNames.ContainsKey(provider) && client.Length != 0 && await _cloudAuth.GetSavedAccountAsync(provider, client) is { } saved)
            _accountNames[provider] = saved.DisplayName;
        if (IsCloudConnected(provider)) return true;
        SendToCloudSettings(provider, L.T("Journey.Cloud.ConnectTitle", ProviderName(provider)),
            L.T(_expiredSignIns.Contains(provider) ? "Journey.Cloud.ReconnectHelp" : "Journey.Cloud.ConnectHelp", ProviderName(provider)), InfoBarSeverity.Informational);
        return false;
    }

    /// <summary>Opens Settings at the destination's Connect button, with a message that says why.</summary>
    // The service the user was sent to Settings for, and the page to go back to once it is connected.
    private CloudProvider? _cloudAskedFor;
    private string? _cloudReturnPage;

    private void SendToCloudSettings(CloudProvider provider, string title, string message, InfoBarSeverity severity)
    {
        _cloudAskedFor = provider;
        _cloudReturnPage = _page is "complete" or "cloud" ? _page : null;
        _returnFocusTo = (ConnectLabel(provider), 0);
        Navigate("settings");
        ShowBanner(title, message, severity);
    }

    /// <summary>Settings opened from a cloud page shows its cloud section, not the top of the page.</summary>
    private Task OpenCloudSettingsAsync()
    {
        _cloudAskedFor = null;
        _cloudReturnPage = _page is "complete" or "cloud" ? _page : null;
        _returnFocusTo = (L.T("Shell.UploadToAccessible"), 0);
        Navigate("settings");
        return Task.CompletedTask;
    }

    private async Task UploadSelectedAsync()
    {
        var selected = _exports.Where(e => _selectedExports.Contains(e.Path)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(L.T("Journey.Cloud.SelectionRequired"));
        var provider = _uploadProvider;
        if (!await EnsureCloudConnectedAsync(provider)) return;
        var clientId = ClientIdFor(provider);
        var name = ProviderName(provider);
        // A file sent twice is stored twice; nothing at the provider recognises it.
        var again = selected.Where(file => file.CloudCopies.Any(copy => copy.Provider == provider)).Select(file => Path.GetFileName(file.Path)).ToArray();
        if (again.Length > 0 && !await ConfirmAsync(L.T("Journey.Cloud.AgainTitle"), L.T("Journey.Cloud.AgainMessage", name, string.Join(", ", again)), L.T("Journey.Cloud.AgainUpload"), L.T("Journey.Cloud.AgainKeep"))) return;
        string? outcome = null;
        var signInEnded = false;
        await RunOperationAsync(async ct =>
        {
            var account = await _cloudAuth.GetSavedAccountAsync(provider, clientId, ct);
            if (account is null) { ForgetCloudAccount(provider); throw new InvalidOperationException(L.T("Journey.Cloud.AccountRequired", name)); }
            try
            {
                await CheckCloudSpaceAsync(account, selected, ct);
                var service = new CloudUploadService();
                var uploaded = 0;
                var failures = new List<string>();
                try
                {
                    for (var index = 0; index < selected.Length; index++)
                    {
                        var file = selected[index];
                        var number = index + 1;
                        var fileName = Path.GetFileName(file.Path);
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        // While the upload waits to try again the line says so: a still progress bar alone looks like a hang.
                        var progress = new Progress<CloudUploadProgress>(p => SetProgress(p.Fraction * 100, p.Retry > 0
                            ? L.T("Journey.Cloud.Retrying", number, selected.Length, fileName, p.Retry)
                            : L.T("Journey.Cloud.Progress", number, selected.Length, fileName, FormatBytes(p.BytesUploaded), FormatBytes(p.TotalBytes)) + PaceText(p.BytesUploaded, p.TotalBytes, clock.Elapsed)));
                        try
                        {
                            var result = await service.UploadAsync(new CloudUploadRequest(account, file.Path), progress, ct);
                            uploaded++;
                            _selectedExports.Remove(file.Path);
                            RecordCloudCopy(file.Path, provider, result);
                            // What was listed before no longer shows everything that is there.
                            if (_cloudListingProvider == provider) _cloudListing = null;
                        }
                        // A sign-in that ended fails every file, and so does the user's own Cancel; any other failure belongs to this file alone.
                        catch (Exception exception) when (exception is not CloudSignInExpiredException && !ct.IsCancellationRequested)
                        {
                            App.LogException("Uploading a file", exception);
                            var reason = Describe(exception);
                            failures.Add("• " + fileName + ": " + (reason.Length <= 260 ? reason : reason[..260].TrimEnd() + "…"));
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    ShowBanner(L.T("Journey.Cloud.StoppedTitle"), L.T("Journey.Cloud.StoppedHelp", uploaded, selected.Length, name), InfoBarSeverity.Informational);
                    throw;
                }
                if (failures.Count == 0)
                {
                    ShowBanner(L.T("Journey.Cloud.CompleteTitle"), L.Plural("Journey.Cloud.CompleteMessage", uploaded, name), InfoBarSeverity.Success);
                    return;
                }
                outcome = L.T("Journey.Cloud.PartlyTitle", uploaded, selected.Length, name);
                failures.Add(L.T("Journey.Cloud.FailedHelp"));
                ShowBanner(outcome, string.Join("\n", failures), uploaded > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
            catch (CloudSignInExpiredException expired)
            {
                App.LogException("Cloud sign-in ended", expired);
                _expiredSignIns.Add(provider);
                signInEnded = true;
                outcome = L.T("Journey.Cloud.SignInEnded", name);
                // Shown after the operation has ended, on the Settings page the user is taken to.
                _pendingCloudMessage = expired.Message;
            }
        }, L.T("Journey.Cloud.CompleteStatus"));
        if (signInEnded && !_closed) SendToCloudSettings(provider, L.T("Journey.Cloud.SignInEnded", name), _pendingCloudMessage ?? "", InfoBarSeverity.Warning);
        if (outcome is not null && !_closed) _status.Text = outcome;
    }

    private string? _pendingCloudMessage;

    private void RecordCloudCopy(string path, CloudProvider provider, CloudUploadResult result)
    {
        var index = _exports.FindIndex(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var link = Uri.TryCreate(result.WebUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
        _exports[index] = _exports[index] with { CloudCopies = [.. _exports[index].CloudCopies, new CloudCopy(provider, result.Name, link, DateTimeOffset.Now)] };
    }

    /// <summary>Stops before a long upload that cannot fit. A drive that does not say how much room it has is not an obstacle.</summary>
    private async Task CheckCloudSpaceAsync(CloudAccount account, IReadOnlyCollection<ExportedFile> files, CancellationToken ct)
    {
        long? free;
        try { free = await new CloudBrowseService().GetFreeSpaceAsync(account, ct); }
        catch (CloudSignInExpiredException) { throw; }
        catch (Exception ex) when (!ct.IsCancellationRequested) { App.LogException("Asking for free cloud space", ex); return; }
        var needed = files.Sum(FileLength);
        if (free is { } available && needed > available)
            throw new InvalidOperationException(L.T("Journey.Cloud.NotEnoughSpace", ProviderName(account.Provider), FormatBytes(available), FormatBytes(needed)));
    }

    private void BuildCloudPage(StackPanel page)
    {
        AddHeading(page, L.T("Journey.CloudView.Title"), L.T("Journey.CloudView.Subtitle"), L.T("Journey.CloudView.Stage"));
        var provider = _uploadProvider;
        var google = provider == CloudProvider.GoogleDrive;
        var connected = IsCloudConnected(provider);
        var listing = _cloudListingProvider == provider ? _cloudListing : null;
        var source = new StackPanel { Spacing = 14 };
        source.Children.Add(ProviderSelector());
        source.Children.Add(Muted(L.T("Journey.CloudView.SelectorNote"), 12));
        source.Children.Add(Body(CloudConnectionLabel(provider)));
        source.Children.Add(Muted(L.T(google ? "Journey.CloudView.GoogleScope" : "Journey.CloudView.OneDriveScope"), 12));
        source.Children.Add(ActionRow(
            ActionButton(L.T(listing is null ? "Journey.CloudView.List" : "Journey.CloudView.Refresh"), ListCloudAsync, true),
            ActionButton(L.T("Journey.Cloud.Manage"), OpenCloudSettingsAsync)));
        source.Children.Add(Muted(L.T("Journey.CloudView.Privacy"), 11));
        page.Children.Add(Card(source));
        // Nothing to show until a sign-in exists; the line above already says so.
        if (listing is null && !connected) return;

        var files = new StackPanel { Spacing = 12 };
        files.Children.Add(SectionTitle(ProviderName(provider), Symbol.Library));
        if (listing is null) files.Children.Add(Body(L.T("Journey.CloudView.NotListed")));
        else
        {
            files.Children.Add(Muted(L.Plural("Journey.CloudView.Summary", listing.Items.Count, _cloudListedAt.ToLocalTime().ToString("t")), 12));
            if (listing.Truncated) files.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = L.T("Journey.CloudView.Truncated", listing.Items.Count) });
            if (listing.Items.Count == 0) files.Children.Add(Body(L.T(google ? "Journey.CloudView.EmptyGoogle" : "Journey.CloudView.Empty")));
            else
            {
                var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 560 };
                AutomationProperties.SetName(list, L.T("Journey.CloudView.ListAccessible"));
                foreach (var item in listing.Items) list.Items.Add(CloudRow(item));
                files.Children.Add(list);
            }
        }
        page.Children.Add(Card(files));
    }

    private static ListViewItem CloudRow(CloudItem item)
    {
        var modified = item.Modified?.ToLocalTime().ToString("g");
        var kind = item.Kind switch
        {
            CloudItemKind.Folder => L.T("Journey.CloudView.Folder"),
            CloudItemKind.Other => L.T("Journey.CloudView.Other"),
            _ => item.Size is { } size ? FormatBytes(size) : L.T("Journey.CloudView.SizeUnknown")
        };
        var detail = modified is null ? kind : $"{kind}  ·  {L.T("Journey.CloudView.Changed", modified)}";
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(2, 8, 4, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new SymbolIcon(item.Kind == CloudItemKind.Folder ? Symbol.Folder : item.Kind == CloudItemKind.Other ? Symbol.Document : Symbol.Video) { Width = 20, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 3, Children =
        {
            new TextBlock { Text = item.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
            // Inherit the ListViewItem foreground so the text follows high-contrast colors.
            new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap }
        } };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (item.WebLink is { } link)
        {
            var open = new HyperlinkButton { Content = L.T("Journey.CloudView.Open"), NavigateUri = link, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(open, L.T("Journey.CloudView.OpenAccessible", item.Name));
            Grid.SetColumn(open, 2);
            row.Children.Add(open);
        }
        var entry = new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(entry, $"{item.Name}. {detail}.");
        return entry;
    }

    private async Task ListCloudAsync()
    {
        var provider = _uploadProvider;
        if (!await EnsureCloudConnectedAsync(provider)) return;
        var clientId = ClientIdFor(provider);
        var name = ProviderName(provider);
        var signInEnded = false;
        await RunOperationAsync(async ct =>
        {
            SetProgress(null, L.T("Journey.CloudView.Listing", name));
            var account = await _cloudAuth.GetSavedAccountAsync(provider, clientId, ct);
            if (account is null) { ForgetCloudAccount(provider); throw new InvalidOperationException(L.T("Journey.Cloud.AccountRequired", name)); }
            try
            {
                var listing = await new CloudBrowseService().ListAsync(account, ct);
                _cloudListing = listing;
                _cloudListingProvider = provider;
                _cloudListedAt = DateTimeOffset.Now;
            }
            catch (CloudSignInExpiredException expired)
            {
                App.LogException("Cloud sign-in ended", expired);
                _expiredSignIns.Add(provider);
                signInEnded = true;
                _pendingCloudMessage = expired.Message;
            }
        }, L.T("Journey.CloudView.Listed"));
        if (signInEnded && !_closed)
        {
            SendToCloudSettings(provider, L.T("Journey.Cloud.SignInEnded", name), _pendingCloudMessage ?? "", InfoBarSeverity.Warning);
            _status.Text = L.T("Journey.Cloud.SignInEnded", name);
        }
    }

    private async Task ConnectCloudAsync(CloudProvider provider, Func<CancellationToken, Task> saveSettings, string? typedSecret = null)
    {
        if (provider == CloudProvider.GoogleDrive) _googleSecretDraft = typedSecret?.Trim() ?? "";
        await RunOperationAsync(async ct =>
        {
            await saveSettings(ct);
            var client = ClientIdFor(provider);
            string? secret = null;
            if (provider == CloudProvider.GoogleDrive)
            {
                if (client.Length == 0) throw new InvalidOperationException(L.T("Shell.NeedGoogleClient"));
                // The secret is never written to the settings file; an empty field means the one kept with the saved sign-in.
                secret = _googleSecretDraft.Length != 0 ? _googleSecretDraft : (await _cloudAuth.GetSavedAccountAsync(provider, client, ct))?.ClientSecret;
                if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException(L.T("Shell.NeedGoogleSecret"));
            }
            SetProgress(null, L.T("Shell.WaitingSignIn"));
            // The status line is small; the instruction for the browser is also shown where the user is looking.
            ShowBanner(L.T("Shell.SignInTitle", ProviderName(provider)), L.T("Shell.SignInHelp", (int)_cloudAuth.SignInTimeout.TotalMinutes), InfoBarSeverity.Informational);
            CloudAccount account;
            try { account = await _cloudAuth.AuthenticateAsync(new OAuthClientOptions(provider, client, secret), ct); }
            // The instruction to finish in the browser must not stay on the screen once nothing waits for the browser any more.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ShowBanner(L.T("Shell.SignInCancelledTitle"), L.T("Shell.SignInCancelled"), InfoBarSeverity.Informational);
                throw;
            }
            // The browser is in front after the sign-in; bring the application back.
            Activate();
            ForgetCloudAccount(provider);
            _accountNames[provider] = account.DisplayName;
            if (provider == CloudProvider.GoogleDrive) { _googleSecretDraft = ""; _googleSecretStored = true; }
            // Someone who has just connected one service and has no usable sign-in for the chosen one means to use this one.
            var switched = provider != _uploadProvider && !IsCloudConnected(_uploadProvider);
            if (switched) await SetUploadProviderAsync(provider, ct);
            var destination = L.T(provider == _uploadProvider ? "Shell.AccountIsDestination" : "Shell.AccountNotDestination", ProviderName(provider), ProviderName(_uploadProvider));
            // Someone sent here from Archive or from the cloud view gets the way back with the good news.
            var back = _cloudReturnPage is { } origin ? ActionButton(L.T(origin == "cloud" ? "Shell.BackToCloud" : "Shell.BackToArchive"), () => { Navigate(origin); return Task.CompletedTask; }) : null;
            (_cloudAskedFor, _cloudReturnPage) = (null, null);
            // Connected, but the service refused the first request: for Google that is usually a step of the setup that was left
            // out, so the guide is one click away.
            if (account.Notice is { } notice) ShowBanner(L.T("Shell.AccountConnectedButTitle"), notice, InfoBarSeverity.Warning,
                provider == CloudProvider.GoogleDrive ? CloudGuideLink(provider, Links.Document("CLOUD-SETUP.md", "google-drive")) : null);
            else ShowBanner(L.T("Shell.AccountConnected"), L.T("Shell.AccountReady", account.DisplayName) + " " + destination, InfoBarSeverity.Success, back);
        }, L.T("Shell.AccountConnectedStatus"));
    }

    private async Task DisconnectCloudAsync(CloudProvider provider, Func<CancellationToken, Task> saveSettings)
    {
        // The Google client secret lives with the sign-in and Google does not show it a second time.
        if (provider == CloudProvider.GoogleDrive && !await ConfirmAsync(L.T("Shell.DisconnectGoogle.Title"), L.T("Shell.DisconnectGoogle.Message"), L.T("Shell.Disconnect"), L.T("Shell.DisconnectGoogle.Keep"))) return;
        await RunOperationAsync(async ct =>
        {
            var clientId = ClientIdFor(provider);
            // A work or school account keeps its permissions on another page than a personal one.
            var business = provider == CloudProvider.OneDrive && (await _cloudAuth.GetSavedAccountAsync(provider, clientId, ct))?.DriveType is { } drive && drive != "personal";
            // The sign-in goes first, whatever becomes of the page: it removes every sign-in saved for the provider.
            var ended = await _cloudAuth.DisconnectAsync(provider, clientId, ct);
            ForgetCloudAccount(provider);
            // The fields on the page are saved so that they are not lost; a page that cannot be saved does not bring the sign-in back.
            string? unsaved = null;
            try { await saveSettings(ct); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { unsaved = ex.Message; }
            var google = provider == CloudProvider.GoogleDrive;
            var message = L.T(!google ? "Shell.AccountDisconnectedHelp" : ended ? "Shell.AccountDisconnectedEnded" : "Shell.AccountDisconnectedNotEnded");
            if (unsaved is not null) message += " " + L.T("Shell.SettingsNotSaved", unsaved);
            // Where the permission itself is withdrawn; Google has nothing left to withdraw once it confirmed the end.
            var permissions = google && ended ? null : LinkButton(L.T("Shell.PermissionsLink", google ? "Google" : "Microsoft"), google ? Links.GooglePermissions : business ? Links.MicrosoftWorkPermissions : Links.MicrosoftPermissions);
            ShowBanner(L.T("Shell.AccountDisconnected"), message, unsaved is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning, permissions);
        }, L.T("Shell.AccountDisconnectedStatus"));
    }
}
