using L = Diga.Core.Localization.AppText;
using Diga.Core.Dlna;
using Diga.Core.Files;
using Diga.Core.Media;
using Diga.Core.Naming;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Storage;

namespace Diga.App;

public sealed partial class MainWindow
{
    private readonly List<DlnaDevice> _dlnaDevices = [];
    private readonly Dictionary<string, DlnaPick> _dlnaSelected = new(StringComparer.Ordinal);
    private readonly Stack<(string Id, string Title)> _dlnaParents = [];
    private DlnaDevice? _dlnaDevice;
    private IReadOnlyList<DlnaObject> _dlnaObjects = [];
    private string? _dlnaDeviceId;
    private string _dlnaContainerId = "0";
    private string _dlnaFolderTitle = L.T("Journey.Dlna.Recordings");
    private string _dlnaSearch = "";
    private string? _dlnaFocusedId;
    private bool _dlnaKeepDelivered = true;
    private bool _dlnaRefreshing;
    private bool _dlnaSearchedEmpty;
    private string _dlnaAddress = "";
    // Recordings saved in this session (see OrderKey); the list marks them so that saving one again is a deliberate choice.
    private readonly HashSet<string> _dlnaSavedKeys = new(StringComparer.Ordinal);
    private ListView? _dlnaList;
    private StackPanel? _dlnaFolders;
    private TextBlock? _dlnaSelectionText;
    private bool _dlnaNothingShown;
    private StackPanel? _dlnaUnavailable;
    private TextBlock? _dlnaTargetText;
    private Button? _dlnaPreserveButton;
    private DlnaPick? _dlnaCachedPick;
    private DlnaDownloadResult? _dlnaCachedDownload;
    private string? _dlnaPreviewName;
    private string? _dlnaInspectedName;
    private string _dlnaMediaInfo = L.T("Journey.Dlna.SelectInspect");

    private bool HasDlnaSource => _dlnaDevice is not null;
    private int DlnaSelectionCount => _dlnaSelected.Count;

    private sealed record DlnaPick(string Id, string Title, Uri Uri, DlnaResource Resource, string? FolderId = null)
    {
        public long? SizeBytes => Resource.SizeBytes;
    }

    private void ResetDlnaSource()
    {
        _dlnaDevice = null;
        _dlnaObjects = [];
        _dlnaSelected.Clear();
        _dlnaParents.Clear();
        _dlnaContainerId = "0";
        _dlnaFolderTitle = L.T("Journey.Dlna.Recordings");
        _dlnaFocusedId = null;
        _dlnaSearch = "";
        _dlnaKeepDelivered = !_settings.SaveAsContainer;
        _dlnaList = null;
        _dlnaFolders = null;
        _dlnaSelectionText = null;
        _dlnaUnavailable = null;
        _dlnaTargetText = null;
        _dlnaPreserveButton = null;
        ClearDlnaPreviewState();
    }

    private void ClearDlnaPreviewState()
    {
        // Actual cache files stay in the shared exact-owned registry until cleanup.
        _dlnaCachedPick = null;
        _dlnaCachedDownload = null;
        _dlnaPreviewName = null;
        _dlnaInspectedName = null;
        _dlnaMediaInfo = L.T("Journey.Dlna.SelectInspect");
    }

    private FrameworkElement BuildDlnaConnectCard()
    {
        var network = new StackPanel { Spacing = 14 };
        network.Children.Add(StageBadge(L.T("Journey.Dlna.ConnectBadge"), "Violet"));
        network.Children.Add(SectionTitle(L.T("Journey.Dlna.ConnectTitle"), Symbol.World));
        network.Children.Add(Muted(L.T("Journey.Dlna.ConnectHelp"), 12));
        var devices = new ComboBox { PlaceholderText = L.T("Journey.Dlna.DevicePlaceholder"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(devices, L.T("Journey.Dlna.DeviceAccessible"));
        foreach (var device in _dlnaDevices)
            // Two devices can carry the same name; the address is what tells the recorder from anything that merely calls itself one.
            devices.Items.Add(new ComboBoxItem { Content = L.T("Journey.Dlna.DeviceEntry", device.FriendlyName, device.ModelName, device.Address), Tag = device.DescriptionUri.AbsoluteUri });
        devices.SelectedItem = devices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _dlnaDeviceId));
        network.Children.Add(devices);
        // The highlighted button is always the one that can work now: searching until a recorder is listed, connecting after that.
        var found = _dlnaDevices.Count > 0;
        var connect = ActionButton(L.T("Journey.Dlna.Connect"), async () =>
        {
            var device = _dlnaDevices.FirstOrDefault(item => item.DescriptionUri.AbsoluteUri == _dlnaDeviceId);
            if (device is null) throw new InvalidOperationException(L.T("Journey.Dlna.DeviceRequired"));
            await OpenDlnaDeviceAsync(device);
        }, found && _dlnaDevice is null);
        connect.IsEnabled = _dlnaDeviceId is not null;
        devices.SelectionChanged += (_, _) => { _dlnaDeviceId = (devices.SelectedItem as ComboBoxItem)?.Tag as string; connect.IsEnabled = _dlnaDeviceId is not null; };
        network.Children.Add(ActionRow(ActionButton(L.T(found ? "Journey.Dlna.FindAgain" : "Journey.Dlna.Find"), DiscoverDlnaAsync, !found), connect));
        if (_dlnaDevice is not null)
        {
            network.Children.Add(Muted(L.T("Journey.Dlna.Connected", L.T("Journey.Dlna.DeviceEntry", _dlnaDevice.FriendlyName, _dlnaDevice.ModelName, _dlnaDevice.Address)), 12));
            network.Children.Add(ActionButton(L.T("Journey.Dlna.ContinueDiscover"), () => { Navigate("library"); return Task.CompletedTask; }, true));
        }
        return Card(network);
    }

    /// <summary>What to check when the recorder is not found; opened by itself after a search that found nothing.</summary>
    private FrameworkElement BuildRecorderChecklist()
    {
        var help = new StackPanel { Spacing = 10 };
        foreach (var item in new[] { "On", "Server", "Network", "Others" }) help.Children.Add(Body("•  " + L.T("Journey.Dlna.Checklist." + item)));
        help.Children.Add(LinkButton(L.T("Journey.Dlna.Checklist.Guide"), Links.Document("RECORDER-SETUP.md")));
        // A home network does not always pass the search between Wi-Fi and cable. The recorder can then be asked by its address.
        var address = LabeledInput(help, L.T("Journey.Dlna.Address"), _dlnaAddress);
        address.PlaceholderText = L.T("Journey.Dlna.AddressPlaceholder");
        address.TextChanged += (_, _) => _dlnaAddress = address.Text;
        help.Children.Add(Muted(L.T("Journey.Dlna.AddressHelp"), 12));
        help.Children.Add(ActionButton(L.T("Journey.Dlna.AskAddress"), () => DiscoverDlnaAtAsync(_dlnaAddress)));
        return Section("source:checklist", L.T("Journey.Dlna.Checklist.Title"), help, _dlnaSearchedEmpty);
    }

    private async Task DiscoverDlnaAsync() => await RunOperationAsync(async ct =>
    {
        SetProgress(null, L.T("Journey.Dlna.Finding"));
        using var discovery = new DlnaDiscoveryService();
        var found = await discovery.DiscoverWithRespondersAsync(cancellationToken: ct);
        var devices = found.Devices;
        _dlnaDevices.Clear();
        _dlnaDevices.AddRange(devices);
        _dlnaSearchedEmpty = devices.Count == 0;
        // The outcome of a search decides anew whether the checklist is open.
        _sections.Remove("source:checklist");
        var preferred = DlnaDevicePreference.Preferred(_dlnaDevices);
        _dlnaDeviceId = preferred?.DescriptionUri.AbsoluteUri;
        // One device, it calls itself a DIGA, and nothing else on the network answered the search: there is nothing to choose,
        // so the guided journey connects straight away. With a second answer the user looks at the list and its addresses first.
        // Not while a recorder is connected: connecting starts the catalogue anew and would drop every tick made so far, so a
        // search from there only refreshes the list.
        if (_wizard && _dlnaDevice is null && devices.Count == 1 && found.ResponderCount == 1 && preferred is not null && (preferred.FriendlyName + preferred.ModelName).Contains("DIGA", StringComparison.OrdinalIgnoreCase))
        {
            await LoadDlnaRootAsync(preferred, ct);
            return;
        }
        ShowBanner(devices.Count == 0 ? L.T("Journey.Dlna.NoneFound") : L.T("Journey.Dlna.FoundTitle"),
            devices.Count == 0 ? L.T("Journey.Dlna.NoneFoundHelp") : L.Plural("Journey.Dlna.Found", devices.Count),
            devices.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }, L.T("Journey.Dlna.DiscoveryFinished"));

    /// <summary>Asks one address directly whether a recorder is there, for networks on which the search does not arrive.</summary>
    private async Task DiscoverDlnaAtAsync(string text)
    {
        // Said before anything is sent: only an address of the home network, written as four numbers, is asked.
        if (!DlnaDiscoveryService.TryParseAddress(text, out var address)) throw new InvalidOperationException(L.T("Core.Dlna.Discovery.AddressInvalid"));
        await RunOperationAsync(async ct =>
        {
            SetProgress(null, L.T("Journey.Dlna.AskingAddress", address));
            using var discovery = new DlnaDiscoveryService();
            var devices = await discovery.DiscoverAtAsync(address, cancellationToken: ct);
            if (devices.Count == 0)
            {
                ShowBanner(L.T("Journey.Dlna.NoneAtAddress"), L.T("Journey.Dlna.NoneAtAddressHelp", address), InfoBarSeverity.Warning);
                return;
            }
            // Added to what the search found, and chosen; connecting stays the user's own step.
            foreach (var device in devices)
                if (!_dlnaDevices.Any(known => known.DescriptionUri == device.DescriptionUri)) _dlnaDevices.Add(device);
            _dlnaDeviceId = devices[0].DescriptionUri.AbsoluteUri;
            _dlnaSearchedEmpty = false;
            _sections.Remove("source:checklist");
            ShowBanner(L.T("Journey.Dlna.FoundTitle"), L.Plural("Journey.Dlna.Found", devices.Count), InfoBarSeverity.Success);
        }, L.T("Journey.Dlna.DiscoveryFinished"));
    }

    private async Task OpenDlnaDeviceAsync(DlnaDevice device) => await RunOperationAsync(ct => LoadDlnaRootAsync(device, ct), L.T("Journey.Dlna.CatalogueReady"));

    private async Task LoadDlnaRootAsync(DlnaDevice device, CancellationToken ct)
    {
        SetProgress(null, L.T("Journey.Dlna.Opening", device.FriendlyName));
        using var browser = new DlnaContentDirectoryClient();
        var objects = await browser.BrowseAsync(device, "0", ct);
        ct.ThrowIfCancellationRequested();
        PrepareForDlnaSource();
        _dlnaDevice = device;
        _dlnaObjects = objects;
        ShowBanner(L.T("Journey.Dlna.CatalogueOpened"), L.T("Journey.Dlna.CatalogueHelp"), InfoBarSeverity.Success);
        if (_wizard) Navigate("library");
    }

    private void BuildDlnaLibraryPage(StackPanel page)
    {
        AddHeading(page, L.T("Journey.Dlna.LibraryTitle"), L.T("Journey.Dlna.LibrarySubtitle"), L.T("Journey.Stage.Discover"));
        var library = new StackPanel { Spacing = 12 };
        library.Children.Add(SectionTitle(_dlnaFolderTitle, Symbol.Folder));
        var back = ActionButton(L.T("Journey.Dlna.Up"), GoBackDlnaAsync);
        back.IsEnabled = _dlnaParents.Count > 0;
        library.Children.Add(ActionRow(back, ActionButton(L.T("Journey.Dlna.Refresh"), () => BrowseDlnaFolderAsync(_dlnaContainerId, _dlnaFolderTitle, false))));
        var search = new TextBox { Text = _dlnaSearch, PlaceholderText = L.T("Journey.Dlna.FilterPlaceholder"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(search, L.T("Journey.Dlna.FilterAccessible"));
        // The list is rebuilt when the typing pauses, not once for every letter: a folder can hold thousands of recordings.
        var typingPause = DispatcherQueue.CreateTimer();
        typingPause.Interval = TimeSpan.FromMilliseconds(200);
        typingPause.IsRepeating = false;
        typingPause.Tick += (_, _) => RefreshDlnaList();
        search.TextChanged += (_, _) => { _dlnaSearch = search.Text; typingPause.Stop(); typingPause.Start(); };
        library.Children.Add(search);
        _dlnaFolders = new StackPanel { Spacing = 8 };
        library.Children.Add(_dlnaFolders);
        _dlnaList = new ListView { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 90, MaxHeight = 440 };
        AutomationProperties.SetName(_dlnaList, L.T("Journey.Dlna.ListAccessible"));
        _dlnaList.SelectionChanged += (_, args) =>
        {
            if (_dlnaRefreshing) return;
            foreach (var item in args.RemovedItems.OfType<ListViewItem>()) if (item.Tag is DlnaPick pick) _dlnaSelected.Remove(pick.Id);
            foreach (var item in args.AddedItems.OfType<ListViewItem>()) if (item.Tag is DlnaPick pick) { _dlnaSelected[pick.Id] = pick; _dlnaFocusedId = pick.Id; }
            UpdateDlnaSelection();
        };
        library.Children.Add(_dlnaList);
        _dlnaUnavailable = new StackPanel { Spacing = 6 };
        library.Children.Add(_dlnaUnavailable);
        library.Children.Add(ActionRow(// Recordings already saved in this session are left out; ticking one by hand saves it again.
            ActionButton(L.T("Journey.Dlna.SelectAvailable"), () => { foreach (var pick in VisibleDlnaPicks().Where(pick => !_dlnaSavedKeys.Contains(OrderKey(pick)))) _dlnaSelected[pick.Id] = pick; RefreshDlnaList(); return Task.CompletedTask; }),
            ActionButton(L.T("Journey.Dlna.ClearAll"), () => { _dlnaSelected.Clear(); RefreshDlnaList(); return Task.CompletedTask; })));
        _dlnaSelectionText = Live(Muted("", 12));
        library.Children.Add(_dlnaSelectionText);
        _dlnaPreserveButton = ActionButton(L.T("Journey.Dlna.ContinuePreserve"), () => { Navigate("export"); return Task.CompletedTask; }, true);
        library.Children.Add(_dlnaPreserveButton);
        // Below the list, so that the recordings themselves are what a small screen shows first.
        library.Children.Add(StageBadge(_dlnaDevice?.FriendlyName ?? "", "Violet"));
        library.Children.Add(Muted(L.T("Journey.Dlna.OriginalNotice"), 12));
        var preview = new StackPanel { Spacing = 12 };
        var currentPreviewName = _player?.Source is not null ? _dlnaPreviewName : null;
        preview.Children.Add(SectionTitle(currentPreviewName is null ? L.T("Journey.Dlna.InspectTitle") : L.T("Journey.Dlna.PreviewNamed", currentPreviewName), Symbol.Play));
        _previewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (_dlnaPreviewName is not null && _player?.Source is not null) AttachPreviewElement();
        else _previewHost.Content = PreviewPlaceholder();
        preview.Children.Add(_previewHost);
        _dlnaTargetText = Muted("", 12);
        preview.Children.Add(_dlnaTargetText);
        if (FfmpegNotice() is { } previewNotice) preview.Children.Add(previewNotice);
        preview.Children.Add(ActionRow(ActionButton(L.T("Journey.Dlna.Preview"), () => InspectDlnaSelectedAsync(true)), ActionButton(L.T("Journey.Dlna.Inspect"), () => InspectDlnaSelectedAsync(false))));
        preview.Children.Add(Muted(L.T("Journey.Dlna.DownloadNotice"), 11));
        var informationTitle = _dlnaInspectedName is null ? L.T("Journey.Dlna.DeliveredInfo") : L.T("Journey.Dlna.DeliveredNamed", _dlnaInspectedName);
        // Opened by itself once a recording was inspected: the result is what the user downloaded the whole recording for.
        preview.Children.Add(Section("library:details:" + _dlnaInspectedName, informationTitle, InformationBox(_dlnaMediaInfo, informationTitle), _dlnaInspectedName is not null));
        page.Children.Add(ResponsivePair(Card(library), Card(preview), 920));
        RefreshDlnaList();
    }

    private IEnumerable<DlnaPick> VisibleDlnaPicks() =>
        _dlnaObjects.Where(item => !item.IsContainer && item.PreferredResource is not null && MatchesDlna(item.Title))
            .Select(item => new DlnaPick(item.Id, item.Title, item.PreferredResource!.Uri, item.PreferredResource, _dlnaContainerId));

    private bool MatchesDlna(string text) => string.IsNullOrWhiteSpace(_dlnaSearch) || text.Contains(_dlnaSearch, StringComparison.OrdinalIgnoreCase);

    private void RefreshDlnaList()
    {
        if (_dlnaList is null || _dlnaFolders is null) return;
        _dlnaRefreshing = true;
        try
        {
            _dlnaList.Items.Clear();
            _dlnaFolders.Children.Clear();
            foreach (var folder in _dlnaObjects.Where(item => item.IsContainer && MatchesDlna(item.Title)))
                _dlnaFolders.Children.Add(ActionButton(L.T("Journey.Dlna.OpenFolder", folder.Title), () => BrowseDlnaFolderAsync(folder.Id, folder.Title, true)));
            var picks = VisibleDlnaPicks().ToDictionary(item => item.Id, StringComparer.Ordinal);
            var unavailable = new List<(string Title, string Reason)>();
            foreach (var entry in _dlnaObjects.Where(item => !item.IsContainer && MatchesDlna(item.Title)))
            {
                // A recording that cannot be saved is not a row nobody can reach: it is listed under the list with its reason.
                if (!picks.TryGetValue(entry.Id, out var pick)) { unavailable.Add((entry.Title, DlnaStatusText(entry.TransferStatus))); continue; }
                var resource = entry.PreferredResource ?? entry.Resources.FirstOrDefault();
                // What a person chooses a recording by: when it was recorded, how long it is, how big it is.
                var facts = string.Join("  ·  ", new[]
                {
                    entry.RecordedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                    resource?.Duration is { } duration && duration > TimeSpan.Zero ? duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture) : null,
                    resource?.SizeBytes is { } size ? FormatBytes(size) : L.T("Journey.Dlna.SizeUnknown")
                }.Where(text => !string.IsNullOrEmpty(text)));
                var status = _dlnaSavedKeys.Contains(OrderKey(pick)) ? L.T("Journey.Dlna.StatusSaved") : DlnaStatusText(entry.TransferStatus);
                var row = new StackPanel { Spacing = 5, Margin = new Thickness(3, 8, 3, 8), Children =
                {
                    new TextBlock { Text = entry.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = facts, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = status, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                } };
                var item = new ListViewItem { Content = row, Tag = pick, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, $"{entry.Title}. {facts}. {status}.");
                _dlnaList.Items.Add(item);
                if (_dlnaSelected.ContainsKey(pick.Id)) _dlnaList.SelectedItems.Add(item);
            }
            _dlnaList.Visibility = _dlnaList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            // The list has its own scrolling, and a rebuild starts it at the top: go back to the recording last worked with.
            if (_dlnaFocusedId is { } focusedId && _dlnaList.Items.OfType<ListViewItem>().FirstOrDefault(row => row.Tag is DlnaPick rowPick && rowPick.Id == focusedId) is { } focusedRow)
            {
                var list = _dlnaList;
                DispatcherQueue.TryEnqueue(() => list.ScrollIntoView(focusedRow));
            }
            if (_dlnaList.Items.Count == 0 && _dlnaFolders.Children.Count == 0 && unavailable.Count == 0)
            {
                var nothing = Live(Muted(string.IsNullOrWhiteSpace(_dlnaSearch) ? L.T("Journey.Dlna.EmptyFolder") : L.T("Journey.Dlna.NoMatches"), 12));
                _dlnaFolders.Children.Add(nothing);
                // A filter that matches nothing is said once, not again with every further letter.
                if (!_dlnaNothingShown) nothing.Loaded += (_, _) => SayChanged(nothing);
                _dlnaNothingShown = true;
            }
            else _dlnaNothingShown = false;
            if (_dlnaUnavailable is not null)
            {
                _dlnaUnavailable.Children.Clear();
                _dlnaUnavailable.Visibility = unavailable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (unavailable.Count > 0) _dlnaUnavailable.Children.Add(new TextBlock { Text = L.Plural("Journey.Dlna.UnavailableCount", unavailable.Count), FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                foreach (var (title, reason) in unavailable)
                    _dlnaUnavailable.Children.Add(new TextBlock { Text = title + "  ·  " + reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = AppStyle("DigaMutedTextStyle") });
            }
        }
        finally { _dlnaRefreshing = false; }
        UpdateDlnaSelection();
    }

    private static string DlnaStatusText(DlnaTransferStatus status) => status switch
    {
        DlnaTransferStatus.OriginalAvailable => L.T("Journey.Dlna.StatusOriginal"),
        DlnaTransferStatus.Unverified => L.T("Journey.Dlna.StatusUnknown"),
        DlnaTransferStatus.Protected => L.T("Journey.Dlna.StatusProtected"),
        DlnaTransferStatus.ConvertedOnly => L.T("Journey.Dlna.StatusConverted"),
        _ => L.T("Journey.Dlna.StatusUnavailable")
    };

    private void UpdateDlnaSelection()
    {
        var visible = VisibleDlnaPicks().Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var hidden = _dlnaSelected.Keys.Count(id => !visible.Contains(id));
        if (_dlnaSelectionText is not null)
        {
            var selection = L.Plural("Journey.Dlna.Selected", _dlnaSelected.Count) + (hidden > 0 ? L.Plural("Journey.Dlna.HiddenSelected", hidden) : "");
            // Said aloud only when the count really changed: this also runs while a filter is typed.
            if (_dlnaSelectionText.Text != selection) { var first = _dlnaSelectionText.Text.Length == 0; _dlnaSelectionText.Text = selection; if (!first) SayChanged(_dlnaSelectionText); }
        }
        if (_dlnaPreserveButton is not null) _dlnaPreserveButton.IsEnabled = _dlnaSelected.Count > 0;
        if (_dlnaTargetText is not null) _dlnaTargetText.Text = FocusedDlnaPick() is { } pick ? L.T("Journey.Preview.Target", pick.Title) : L.T("Journey.Dlna.SelectDownloadable");
        RefreshJourney();
    }

    private DlnaPick? FocusedDlnaPick() => _dlnaFocusedId is not null && _dlnaSelected.TryGetValue(_dlnaFocusedId, out var pick) ? pick : _dlnaSelected.Values.FirstOrDefault();

    private async Task BrowseDlnaFolderAsync(string id, string title, bool pushParent)
    {
        if (_dlnaDevice is null) return;
        await RunOperationAsync(async ct =>
        {
            SetProgress(null, L.T("Journey.Dlna.ReadingFolder"));
            using var browser = new DlnaContentDirectoryClient();
            var items = await browser.BrowseAsync(_dlnaDevice, id, ct);
            ct.ThrowIfCancellationRequested();
            ReconcileDlnaSelection(items, id);
            if (pushParent) _dlnaParents.Push((_dlnaContainerId, _dlnaFolderTitle));
            _dlnaContainerId = id;
            _dlnaFolderTitle = title;
            _dlnaObjects = items;
            _dlnaSearch = "";
        }, L.T("Journey.Dlna.FolderReady"));
    }

    private async Task GoBackDlnaAsync()
    {
        if (_dlnaParents.Count == 0 || _dlnaDevice is null) return;
        var parent = _dlnaParents.Peek();
        await RunOperationAsync(async ct =>
        {
            using var browser = new DlnaContentDirectoryClient();
            var items = await browser.BrowseAsync(_dlnaDevice, parent.Id, ct);
            ct.ThrowIfCancellationRequested();
            ReconcileDlnaSelection(items, parent.Id);
            _dlnaParents.Pop();
            _dlnaContainerId = parent.Id;
            _dlnaFolderTitle = parent.Title;
            _dlnaObjects = items;
            _dlnaSearch = "";
        }, L.T("Journey.Dlna.FolderReady"));
    }

    private void ReconcileDlnaSelection(IReadOnlyList<DlnaObject> items, string folderId)
    {
        // A fresh catalogue response can describe a changed recording even if its ID is stable.
        if (_dlnaCachedPick is { } cached && (cached.FolderId == folderId || items.Any(item => item.Id == cached.Id)))
        {
            _dlnaCachedPick = null;
            _dlnaCachedDownload = null;
        }
        var currentIds = items.Where(item => !item.IsContainer).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var old in _dlnaSelected.Values.Where(item => item.FolderId == folderId && !currentIds.Contains(item.Id)).ToArray())
            _dlnaSelected.Remove(old.Id);
        foreach (var item in items)
        {
            if (!_dlnaSelected.ContainsKey(item.Id)) continue;
            if (item.PreferredResource is { } resource)
                _dlnaSelected[item.Id] = new DlnaPick(item.Id, item.Title, resource.Uri, resource, folderId);
            else _dlnaSelected.Remove(item.Id);
        }
    }

    private void BuildDlnaExportPage(StackPanel page)
    {
        AddHeading(page, L.T("Journey.Dlna.ExportTitle"), L.T("Journey.Dlna.ExportSubtitle"), L.T("Journey.Stage.Preserve"));
        if (_dlnaSelected.Count == 0)
        {
            page.Children.Add(Card(new StackPanel { Spacing = 12, Children = { Body(L.T("Journey.Dlna.ExportSelectHelp")), ActionButton(L.T("Journey.Dlna.ChooseRecordings"), () => { Navigate(HasDlnaSource ? "library" : "source"); return Task.CompletedTask; }, true) } }));
            return;
        }
        var options = new StackPanel { Spacing = 14 };
        options.Children.Add(StageBadge(L.T("Journey.Dlna.DefaultMode"), "Teal"));
        var fileTypes = Live(Body(""));
        // The file types the chosen way of saving produces, so nobody has to guess what ends up in the folder.
        void ShowFileTypes()
        {
            IEnumerable<string> types = _dlnaKeepDelivered
                ? _dlnaSelected.Values.Select(pick => DlnaDownloadService.GuessSafeExtension(pick.Uri, pick.Resource.MimeType)).Select(type => type == UnknownExtension ? L.T("Journey.Dlna.TypeAfterDownload") : type).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                : [OutputFormats.Extension(_format)];
            var text = L.T("Journey.Dlna.FileTypes", string.Join(", ", types));
            if (fileTypes.Text == text) return;
            var first = fileTypes.Text.Length == 0;
            fileTypes.Text = text;
            if (!first) SayChanged(fileTypes);
        }
        var raw = new RadioButton { GroupName = "DlnaPreservation", Content = new TextBlock { Text = L.T("Journey.Dlna.KeepUnchanged"), TextWrapping = TextWrapping.Wrap }, IsChecked = _dlnaKeepDelivered, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var remux = new RadioButton { GroupName = "DlnaPreservation", Content = new TextBlock { Text = L.T("Journey.Dlna.KeepAndRemux"), TextWrapping = TextWrapping.Wrap }, IsChecked = !_dlnaKeepDelivered, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(raw, L.T("Journey.Dlna.KeepUnchanged"));
        AutomationProperties.SetName(remux, L.T("Journey.Dlna.KeepAndRemux"));
        var formats = new ComboBox { Header = JourneyHeader(L.T("Journey.Dlna.AdditionalContainer")), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_dlnaKeepDelivered };
        AutomationProperties.SetName(formats, L.T("Journey.Dlna.AdditionalContainer"));
        foreach (var format in Enum.GetValues<OutputFormat>()) formats.Items.Add(new ComboBoxItem { Content = format switch { OutputFormat.Matroska => L.T("Journey.Format.Mkv"), OutputFormat.Mpeg2 => L.T("Journey.Format.Mpeg"), _ => L.T("Journey.Dlna.Mp4Compatible") }, Tag = format });
        formats.SelectedItem = formats.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, _format));
        formats.SelectionChanged += (_, _) => { if (formats.SelectedItem is ComboBoxItem item && item.Tag is OutputFormat format) { _format = format; ShowFileTypes(); } };
        raw.Checked += (_, _) => { _dlnaKeepDelivered = true; formats.IsEnabled = false; ShowFileTypes(); };
        remux.Checked += (_, _) => { _dlnaKeepDelivered = false; formats.IsEnabled = true; ShowFileTypes(); };
        ShowFileTypes();
        options.Children.Add(raw);
        options.Children.Add(remux);
        options.Children.Add(formats);
        if (FfmpegNotice() is { } containerNotice) options.Children.Add(containerNotice);
        options.Children.Add(Muted(L.T("Journey.Dlna.UnchangedNotice"), 12));
        var destination = new StackPanel { Spacing = 14 };
        var folder = LabeledInput(destination, L.T("Journey.Dlna.SaveTo"), _outputDirectory);
        folder.TextChanged += (_, _) => _outputDirectory = folder.Text.Trim();
        destination.Children.Add(ActionButton(L.T("Journey.Common.ChooseFolder"), async () => { var selected = await PickFolderAsync(); if (selected is not null) folder.Text = selected; }));
        var known = DlnaPlannedBytes(_dlnaSelected.Values);
        var unknown = _dlnaSelected.Values.Count(item => !item.SizeBytes.HasValue);
        destination.Children.Add(Muted(L.Plural("Journey.Dlna.SelectionSummary", _dlnaSelected.Count) + (known is null ? L.T("Journey.Dlna.SizeTotalUnsupported") : known > 0 ? L.T("Journey.Dlna.AdvertisedTotal", FormatBytes(known.Value)) : "") + (unknown > 0 ? L.Plural("Journey.Dlna.UnknownSizes", unknown) : "") + L.T("Journey.Dlna.AdditionalSpace"), 12));
        destination.Children.Add(Body(SavedNameSummary(OrderBatchCount(_orderNumber, _dlnaSelected.Values.Select(OrderKey).ToArray()))));
        destination.Children.Add(fileTypes);
        page.Children.Add(ResponsivePair(Card(options), Card(destination), 850));
        page.Children.Add(ActionRow(ActionButton(L.Plural("Journey.Dlna.PreserveCount", _dlnaSelected.Count), ExportDlnaSelectedAsync, true), ActionButton(L.T("Journey.Dlna.Back"), () => { Navigate("library"); return Task.CompletedTask; })));
    }

    private async Task ExportDlnaSelectedAsync()
    {
        if (_dlnaSelected.Count == 0) throw new InvalidOperationException(L.T("Journey.Dlna.SelectionRequired"));
        if (string.IsNullOrWhiteSpace(_outputDirectory)) throw new InvalidOperationException(L.T("Journey.Dlna.OutputRequired"));
        // A name without a drive would be taken to mean a folder beside the program, wherever that happens to be.
        if (!Path.IsPathFullyQualified(_outputDirectory)) throw new InvalidOperationException(L.T("Journey.Dlna.OutputAbsolute"));
        var picks = _dlnaSelected.Values.ToArray();
        var knownBytes = DlnaPlannedBytes(picks) ?? throw new InvalidOperationException(L.T("Journey.Dlna.TotalUnsupported"));
        var directory = Path.GetFullPath(_outputDirectory);
        var keepDeliveredOnly = _dlnaKeepDelivered;
        var format = _format;
        var orderNumber = _orderNumber;
        var batchCount = OrderBatchCount(orderNumber, picks.Select(OrderKey).ToArray());
        var (ffmpeg, ffprobe) = (FfmpegExecutable, FfprobeExecutable);
        // Asked before anything is downloaded from the recorder: without FFmpeg only the unchanged stream can be saved.
        if (!keepDeliveredOnly && (ffmpeg is null || ffprobe is null)) throw new InvalidOperationException(L.T("Shell.Ffmpeg.RequiredForContainer"));
        string? outcome = null;
        await RunOperationAsync(async ct =>
        {
            // An easy-to-play file exists beside its download until it has been checked, so the largest recording is needed twice.
            var neededBytes = knownBytes + (keepDeliveredOnly ? 0 : picks.Max(pick => pick.SizeBytes ?? 0));
            await Task.Run(() => { Directory.CreateDirectory(directory); CheckAvailableSpace(directory, neededBytes); }, ct);
            // A crash or a power cut during an earlier save leaves working files in this folder that nothing else would ever find.
            var known = _exports.Select(export => export.Path).ToArray();
            var leftovers = await Task.Run(() => WorkingFiles.Sweep(directory, known), ct);
            var leftoverNotes = new List<string>();
            if (leftovers.PartialFilesRemoved > 0) leftoverNotes.Add(L.T("Journey.Dlna.LeftoverPartials", leftovers.PartialFilesRemoved, FormatBytes(leftovers.PartialBytesRemoved)));
            if (leftovers.DeliveredFiles.Count > 0) leftoverNotes.Add(L.T("Journey.Dlna.LeftoverDownloads", leftovers.DeliveredFiles.Count, FormatBytes(leftovers.DeliveredBytes)));
            await RememberSaveModeAsync(!keepDeliveredOnly, ct);
            using var downloader = new DlnaDownloadService();
            var bothKept = false;
            // Saved as asked for; or kept as an exact copy, because the easy-to-play file could not be made or the work was stopped.
            var (saved, kept, attempted, failedInARow) = (0, 0, 0, 0);
            var failures = new List<(string Title, string Reason, bool Kept)>();
            try
            {
                foreach (var pick in picks)
                {
                    var exportsBefore = _exports.Count;
                    var completed = false;
                    attempted++;
                    try
                    {
                        bothKept |= await SaveDlnaPickAsync(downloader, pick, attempted, picks.Length, directory, keepDeliveredOnly, format, orderNumber, batchCount, ffmpeg, ffprobe, ct);
                        completed = true;
                        saved++;
                        failedInARow = 0;
                    }
                    // One recording that fails must not cost the others. Only the user's own Cancel ends the whole batch.
                    catch (Exception exception) when (!ct.IsCancellationRequested)
                    {
                        App.LogException("Saving a recording", exception);
                        var arrived = _exports.Count > exportsBefore;
                        failures.Add((pick.Title, Describe(exception), arrived));
                        // A recording that arrived and only could not be given the chosen file type says nothing about the recorder or
                        // the network. Two in a row of which nothing arrived mean that one of them is gone, and waiting out every
                        // further time limit helps nobody.
                        if (arrived) { kept++; failedInARow = 0; }
                        else if (++failedInARow >= 2) break;
                    }
                    finally
                    {
                        // However this recording ended (saved, kept as an exact copy, stopped during its last step), a file of it that
                        // is listed in Archive must not be fetched a second time.
                        if (_exports.Count > exportsBefore)
                        {
                            MarkDlnaSaved(pick);
                            if (!completed && ct.IsCancellationRequested) kept++;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (saved + kept > 0) ShowBanner(L.T("Journey.Dlna.StoppedTitle"), string.Join("\n", leftoverNotes.Prepend(L.T("Journey.Dlna.StoppedHelp", saved + kept, picks.Length))), InfoBarSeverity.Informational);
                throw;
            }
            if (failures.Count == 0)
            {
                ShowBanner(L.T("Journey.Dlna.SavedTitle"), string.Join("\n", leftoverNotes.Prepend(L.T(keepDeliveredOnly ? "Journey.Dlna.SavedHelp" : bothKept ? "Journey.Dlna.SavedContainerKeptHelp" : "Journey.Dlna.SavedContainerHelp"))),
                    bothKept ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
                if (_wizard) Navigate("complete");
                return;
            }
            var lines = failures.Take(4).Select(failure => "• " + failure.Title + ": " + (failure.Reason.Length <= 600 ? failure.Reason : failure.Reason[..600].TrimEnd() + "…")
                + (failure.Kept ? " " + L.T("Journey.Dlna.KeptAsExactCopy") : "")).ToList();
            if (failures.Count > 4) lines.Add(L.T("Journey.Dlna.FailedMore", failures.Count - 4));
            if (picks.Length > attempted) lines.Add(L.T("Journey.Dlna.NotAttempted", picks.Length - attempted));
            // Only what did not arrive is still ticked; what was kept as an exact copy is in Archive.
            if (failures.Any(failure => !failure.Kept) || picks.Length > attempted) lines.Add(L.T("Journey.Dlna.FailedHelp"));
            lines.AddRange(leftoverNotes);
            outcome = L.T("Journey.Dlna.PartlySavedTitle", saved + kept, picks.Length);
            ShowBanner(outcome, string.Join("\n", lines), saved + kept > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }, L.T("Journey.Dlna.PreservationComplete"));
        if (outcome is not null && !_closed) _status.Text = outcome;
    }

    // What the library names a download whose media type the recorder did not say, or said in words it does not know.
    private const string UnknownExtension = ".bin";

    /// <summary>A saved recording leaves the selection, so the next save cannot fetch it a second time; the list marks it as saved.</summary>
    private void MarkDlnaSaved(DlnaPick pick)
    {
        _dlnaSelected.Remove(pick.Id);
        _dlnaSavedKeys.Add(OrderKey(pick));
        if (_dlnaFocusedId == pick.Id) _dlnaFocusedId = null;
    }

    /// <summary>The way of saving chosen last is what the next session starts with.</summary>
    private async Task RememberSaveModeAsync(bool asContainer, CancellationToken ct)
    {
        if (_settings.SaveAsContainer == asContainer) return;
        try
        {
            // Only this one choice is written, into the file as it is now; see JsonSettingsStore.UpdateAsync.
            await _settingsStore.UpdateAsync(stored => stored with { SaveAsContainer = asContainer }, ct);
            _settings = _settings with { SaveAsContainer = asContainer };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { App.LogException("Remembering the way of saving", exception); }
    }

    /// <summary>Saves one recording. True when the delivered stream was kept beside its container.</summary>
    private async Task<bool> SaveDlnaPickAsync(DlnaDownloadService downloader, DlnaPick pick, int number, int count, string directory, bool keepDeliveredOnly,
        OutputFormat format, string orderNumber, int batchCount, string? ffmpeg, string? ffprobe, CancellationToken ct)
    {
        var stem = SavedFileNames.Stem(pick.Title, L.T("Journey.Dlna.FallbackName"), orderNumber, batchCount);
        var extension = DlnaDownloadService.GuessSafeExtension(pick.Uri, pick.Resource.MimeType);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<DlnaDownloadProgress>(value => SetProgress(value.Fraction * 100, value.TotalBytes.HasValue
            ? L.T("Journey.Dlna.DownloadProgressKnown", number, count, pick.Title, FormatBytes(value.BytesReceived), FormatBytes(value.TotalBytes.Value)) + PaceText(value.BytesReceived, value.TotalBytes.Value, clock.Elapsed)
            : L.T("Journey.Dlna.DownloadProgressUnknown", number, count, pick.Title, FormatBytes(value.BytesReceived))));
        if (keepDeliveredOnly)
        {
            var result = await FetchDeliveredAsync(downloader, pick, SavedFileNames.NextAvailablePath(directory, stem, extension), progress, ct);
            // A recorder that names no known media type gives a file ending in .bin, which nothing opens; what the file begins
            // with says what it is. If the rename fails the file keeps the name it has.
            if (extension == UnknownExtension && result.ContentExtension is { } known)
            {
                var named = SavedFileNames.NextAvailablePath(directory, stem, known);
                try { File.Move(result.DestinationPath, named, overwrite: false); result = result with { DestinationPath = named }; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { App.LogException("Naming a recording by its content", exception); }
            }
            RememberSavedUnderOrder(orderNumber, OrderKey(pick));
            await RecordDeliveredDlnaAsync(pick, result.DestinationPath, DlnaDeliveredSummary(result), DlnaDownloadVerification(result), ct);
            return false;
        }
        // Container mode keeps one file. The delivered stream waits under a working name until the
        // container is verified; if that does not happen, it is kept under its own name, or under the working name when the rename fails.
        var bothKept = false;
        var staging = Path.Combine(directory, WorkingFiles.DeliveredName(extension));
        var delivered = await FetchDeliveredAsync(downloader, pick, staging, progress, ct);
        // If this download has to be kept beside or instead of its easy-to-play file, it is kept under the extension its content shows.
        if (extension == UnknownExtension && delivered.ContentExtension is { } byContent) extension = byContent;
        RememberSavedUnderOrder(orderNumber, OrderKey(pick));
        var verification = DlnaDownloadVerification(delivered);
        string? keepReason = L.T("Journey.Dlna.DeliveredKeptFailed");
        var keptStem = stem;
        var remuxIndex = -1;
        try
        {
            var information = await InspectDlnaDownloadAsync(staging, ct);
            var remuxPath = SavedFileNames.NextAvailablePath(directory, stem, OutputFormats.Extension(format));
            var remuxProgress = new Progress<RemuxProgress>(value => SetProgress(value.Fraction * 100, L.T("Journey.Dlna.RemuxProgress", pick.Title, value.Message)));
            var remuxed = await new RemuxService(ffmpeg!, ffprobe!).RemuxAsync(new RemuxRequest(staging, remuxPath, format), remuxProgress, ct);
            keepReason = remuxed.DurationsAgree ? null : L.T("Journey.Dlna.DeliveredKeptDuration");
            // A delivered stream kept beside its container carries the container's name, including any " (2)".
            keptStem = Path.GetFileNameWithoutExtension(remuxed.DestinationPath);
            remuxIndex = _exports.Count;
            _exports.Add(new ExportedFile(pick.Title, remuxed.DestinationPath,
                DlnaSourceInformation(pick) + L.T("Journey.Dlna.DeliveredHeading") + information + "\n\n" + DlnaDownloadVerification(delivered, "Journey.Dlna.DeliveredVerification"),
                L.T("Journey.Dlna.RemuxInspectionPending"), L.T(keepReason is null ? "Journey.Dlna.RemuxVerification" : "Journey.Dlna.RemuxVerificationKept")));
            _selectedExports.Add(remuxed.DestinationPath);
            RefreshJourney();
            string remuxInfo;
            try { remuxInfo = await InspectDlnaDownloadAsync(remuxed.DestinationPath, ct); }
            catch (OperationCanceledException)
            {
                _exports[remuxIndex] = _exports[remuxIndex] with { DestinationInformation = L.T("Journey.Dlna.RemuxInspectionCancelled") };
                throw;
            }
            _exports[remuxIndex] = _exports[remuxIndex] with { DestinationInformation = remuxInfo };
        }
        finally
        {
            // The download is deleted only when its easy-to-play file was made and checked; in every other case it is kept under a
            // proper name. WorkingFiles.SettleDelivered holds that rule and its tests.
            var keep = keepReason is not null;
            var settled = await Task.Run(() => WorkingFiles.SettleDelivered(staging, keep, directory, keptStem, extension));
            if (settled.DeleteFailed)
            {
                keepReason = L.T("Journey.Dlna.DeliveredKeptLocked");
                if (remuxIndex >= 0) _exports[remuxIndex] = _exports[remuxIndex] with { VerificationSummary = L.T("Journey.Dlna.RemuxVerificationKept") };
            }
            if (settled.KeptPath is { } kept)
            {
                bothKept = true;
                // The token may already be cancelled here; the kept file is recorded without waiting for MediaInfo.
                _exports.Add(new ExportedFile(pick.Title, kept, DlnaSourceInformation(pick), verification, DlnaDeliveredSummary(delivered) + "\n" + keepReason));
                _selectedExports.Add(kept);
                RefreshJourney();
            }
        }
        return bothKept;
    }

    /// <summary>
    /// Brings the delivered stream to <paramref name="destination"/>: from the recorder, or, when this recording was just
    /// downloaded for a preview, from that copy on this PC, checked against the fingerprint taken when it arrived.
    /// </summary>
    private async Task<DlnaDownloadResult> FetchDeliveredAsync(DlnaDownloadService downloader, DlnaPick pick, string destination, IProgress<DlnaDownloadProgress> progress, CancellationToken ct)
    {
        if (_dlnaCachedPick == pick && _dlnaCachedDownload is { } cached && File.Exists(cached.DestinationPath))
        {
            try
            {
                var copied = new Progress<long>(bytes => progress.Report(new(bytes, cached.BytesWritten)));
                // Written under a working name and renamed only when its fingerprint is the one taken at the download.
                if (await WorkingFiles.CopyVerifiedAsync(cached.DestinationPath, destination, cached.Sha256, copied, ct)) return cached with { DestinationPath = destination };
            }
            // The preview copy is a convenience; if it cannot be used, the recorder is asked as usual.
            catch (IOException) { }
        }
        return await downloader.DownloadAsync(new DlnaDownloadRequest(pick.Uri, destination, pick.Resource), progress, ct);
    }

    private async Task RecordDeliveredDlnaAsync(DlnaPick pick, string path, string summary, string verification, CancellationToken ct)
    {
        var archiveIndex = _exports.Count;
        _exports.Add(new ExportedFile(pick.Title, path, DlnaSourceInformation(pick), L.T("Journey.Dlna.RawInspectionPending"), summary));
        _selectedExports.Add(path);
        RefreshJourney();
        string information;
        try { information = await InspectDlnaDownloadAsync(path, ct); }
        catch (OperationCanceledException)
        {
            _exports[archiveIndex] = _exports[archiveIndex] with { DestinationInformation = L.T("Journey.Dlna.RawInspectionCancelled", verification) };
            throw;
        }
        _exports[archiveIndex] = _exports[archiveIndex] with { SourceInformation = DlnaSourceInformation(pick) + L.T("Journey.Dlna.DeliveredHeading") + information, DestinationInformation = information + "\n\n" + verification };
    }

    private async Task InspectDlnaSelectedAsync(bool preview)
    {
        var pick = FocusedDlnaPick() ?? throw new InvalidOperationException(L.T("Journey.Dlna.PreviewSelectionRequired"));
        var (ffmpeg, ffprobe) = (FfmpegExecutable, FfprobeExecutable);
        if (preview && (ffmpeg is null || ffprobe is null)) throw new InvalidOperationException(L.T("Shell.Ffmpeg.RequiredForPreview"));
        await RunOperationAsync(async ct =>
        {
            var download = await EnsureDlnaCachedAsync(pick, ct);
            SetProgress(null, L.T("Journey.Dlna.Inspecting"));
            _dlnaMediaInfo = DlnaSourceInformation(pick) + "\n\n" + DlnaDownloadVerification(download) + "\n\n" + await InspectDlnaDownloadAsync(download.DestinationPath, ct);
            _dlnaInspectedName = pick.Title;
            if (!preview || !EnsurePreviewPlayer()) return;
            SetProgress(null, L.T("Journey.Dlna.PreparingPreview"));
            var prepared = await new PreviewService(ffmpeg!, ffprobe!).PrepareAsync(download.DestinationPath, SessionCacheDirectory, ct);
            _ownedCacheFiles.Add(prepared.Path);
            ct.ThrowIfCancellationRequested();
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(prepared.Path);
                ct.ThrowIfCancellationRequested();
                _player!.Source = MediaSource.CreateFromStorageFile(file);
                _player.Play();
                _dlnaPreviewName = pick.Title;
                ShowBanner(L.T("Journey.Dlna.PreviewReady"), L.T("Journey.Dlna.PreviewReadyHelp", prepared.Notice), InfoBarSeverity.Informational);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { ShowPreviewUnavailable(exception); }
        }, L.T("Journey.Dlna.Inspected"));
    }

    private async Task<DlnaDownloadResult> EnsureDlnaCachedAsync(DlnaPick pick, CancellationToken ct)
    {
        if (_dlnaCachedPick == pick && _dlnaCachedDownload is { } cached && File.Exists(cached.DestinationPath)) return cached;
        var directory = SessionCacheDirectory;
        var knownBytes = DlnaPlannedBytes([pick]) ?? throw new InvalidOperationException(L.T("Journey.Dlna.RecordingSizeUnsupported"));
        await Task.Run(() =>
        {
            foreach (var old in _stagedCacheFiles.ToArray()) ReleaseStagedPath(old);
            PrepareSessionCache();
            CheckAvailableSpace(directory, knownBytes);
        }, ct);
        _dlnaCachedPick = null;
        _dlnaCachedDownload = null;
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + DlnaDownloadService.GuessSafeExtension(pick.Uri, pick.Resource.MimeType));
        using var downloader = new DlnaDownloadService();
        var progress = new Progress<DlnaDownloadProgress>(value => SetProgress(value.Fraction * 100, value.TotalBytes.HasValue ? L.T("Journey.Dlna.PreviewProgressKnown", pick.Title, FormatBytes(value.BytesReceived), FormatBytes(value.TotalBytes.Value)) : L.T("Journey.Dlna.PreviewProgressUnknown", pick.Title, FormatBytes(value.BytesReceived))));
        var result = await downloader.DownloadAsync(new DlnaDownloadRequest(pick.Uri, destination, pick.Resource), progress, ct);
        _ownedCacheFiles.Add(result.DestinationPath);
        _stagedCacheFiles.Add(result.DestinationPath);
        _dlnaCachedPick = pick;
        _dlnaCachedDownload = result;
        return result;
    }

    private async Task<string> InspectDlnaDownloadAsync(string path, CancellationToken ct)
    {
        try { return await InspectMediaAsync(path, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return L.T("Journey.Dlna.InspectionUnavailable", exception.Message); }
    }

    private static string DlnaSourceInformation(DlnaPick pick) => L.T("Journey.Dlna.SourceInformation", pick.Title, pick.Uri,
        L.T("Journey.Dlna.CatalogueMetadata",
            pick.Resource.MimeType ?? L.T("Journey.Media.Unknown"),
            pick.Resource.IsConverted.HasValue ? (pick.Resource.IsConverted.Value ? L.T("Journey.Dlna.Converted") : L.T("Journey.Dlna.NonConverted")) : L.T("Journey.Dlna.NotAdvertised"),
            pick.SizeBytes.HasValue ? FormatBytes(pick.SizeBytes.Value) : L.T("Journey.Media.Unknown")));

    /// <summary>What the card of an exact copy says: what was saved and how it was checked. The fingerprint itself is in the technical details.</summary>
    private static string DlnaDeliveredSummary(DlnaDownloadResult result) => L.T("Journey.Dlna.DeliveredSummary",
        FormatBytes(result.BytesWritten), result.LengthVerified ? L.T("Journey.Dlna.LengthVerified") : L.T("Journey.Dlna.LengthUnverified"));

    private static string DlnaDownloadVerification(DlnaDownloadResult result, string key = "Journey.Dlna.DownloadVerification") => L.T(key,
        FormatBytes(result.BytesWritten),
        result.LengthVerified ? L.T("Journey.Dlna.LengthVerified") : L.T("Journey.Dlna.LengthUnverified"),
        result.MetadataUnknown ? L.T("Journey.Dlna.ConversionUnknown") : L.T("Journey.Dlna.ServerOriginalClaim"),
        result.Sha256);

    private static long? DlnaPlannedBytes(IEnumerable<DlnaPick> picks)
    {
        // Leave room for CheckAvailableSpace's reserve without trusting server-supplied totals.
        const long maximum = long.MaxValue - 64L * 1024 * 1024;
        long total = 0;
        foreach (var pick in picks)
        {
            var size = pick.SizeBytes ?? 0;
            if (size < 0 || size > maximum - total) return null;
            total += size;
        }
        return total;
    }

}
