using L = Diga.Core.Localization.AppText;
using Diga.Core.Dlna;
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
    private ListView? _dlnaList;
    private StackPanel? _dlnaFolders;
    private TextBlock? _dlnaSelectionText;
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
        _dlnaKeepDelivered = true;
        _dlnaList = null;
        _dlnaFolders = null;
        _dlnaSelectionText = null;
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
            devices.Items.Add(new ComboBoxItem { Content = $"{device.FriendlyName} · {device.ModelName}", Tag = device.DescriptionUri.AbsoluteUri });
        devices.SelectedItem = devices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _dlnaDeviceId));
        devices.SelectionChanged += (_, _) => _dlnaDeviceId = (devices.SelectedItem as ComboBoxItem)?.Tag as string;
        network.Children.Add(devices);
        network.Children.Add(ActionRow(
            ActionButton(L.T("Journey.Dlna.Find"), DiscoverDlnaAsync),
            ActionButton(L.T("Journey.Dlna.Connect"), async () =>
            {
                var device = _dlnaDevices.FirstOrDefault(item => item.DescriptionUri.AbsoluteUri == _dlnaDeviceId);
                if (device is null) throw new InvalidOperationException(L.T("Journey.Dlna.DeviceRequired"));
                await OpenDlnaDeviceAsync(device);
            }, true)));
        if (_dlnaDevice is not null)
        {
            network.Children.Add(Muted(L.T("Journey.Dlna.Connected", _dlnaDevice.FriendlyName), 12));
            network.Children.Add(ActionButton(L.T("Journey.Dlna.ContinueDiscover"), () => { Navigate("library"); return Task.CompletedTask; }, true));
        }
        return Card(network);
    }

    private async Task DiscoverDlnaAsync() => await RunOperationAsync(async ct =>
    {
        SetProgress(null, L.T("Journey.Dlna.Finding"));
        using var discovery = new DlnaDiscoveryService();
        var devices = await discovery.DiscoverAsync(cancellationToken: ct);
        _dlnaDevices.Clear();
        _dlnaDevices.AddRange(devices);
        _dlnaDeviceId = DlnaDevicePreference.Preferred(_dlnaDevices)?.DescriptionUri.AbsoluteUri;
        ShowBanner(devices.Count == 0 ? L.T("Journey.Dlna.NoneFound") : L.T("Journey.Dlna.FoundTitle"),
            devices.Count == 0 ? L.T("Journey.Dlna.NoneFoundHelp") : L.Plural("Journey.Dlna.Found", devices.Count),
            devices.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }, L.T("Journey.Dlna.DiscoveryFinished"));

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
        AddHeading(page, L.T("Journey.Dlna.LibraryTitle"), L.T("Journey.Dlna.LibrarySubtitle"), L.T("Journey.Dlna.DiscoverStage"));
        page.Children.Add(StageBadge(_dlnaDevice?.FriendlyName ?? "", "Violet"));
        page.Children.Add(Muted(L.T("Journey.Dlna.OriginalNotice"), 12));
        var library = new StackPanel { Spacing = 12 };
        library.Children.Add(SectionTitle(_dlnaFolderTitle, Symbol.Folder));
        var back = ActionButton(L.T("Journey.Dlna.Up"), GoBackDlnaAsync);
        back.IsEnabled = _dlnaParents.Count > 0;
        library.Children.Add(ActionRow(back, ActionButton(L.T("Journey.Dlna.Refresh"), () => BrowseDlnaFolderAsync(_dlnaContainerId, _dlnaFolderTitle, false))));
        var search = new TextBox { Text = _dlnaSearch, PlaceholderText = L.T("Journey.Dlna.FilterPlaceholder"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(search, L.T("Journey.Dlna.FilterAccessible"));
        search.TextChanged += (_, _) => { _dlnaSearch = search.Text; RefreshDlnaList(); };
        library.Children.Add(search);
        _dlnaFolders = new StackPanel { Spacing = 8 };
        library.Children.Add(_dlnaFolders);
        _dlnaList = new ListView { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 90, MaxHeight = 360 };
        AutomationProperties.SetName(_dlnaList, L.T("Journey.Dlna.ListAccessible"));
        _dlnaList.SelectionChanged += (_, args) =>
        {
            if (_dlnaRefreshing) return;
            foreach (var item in args.RemovedItems.OfType<ListViewItem>()) if (item.Tag is DlnaPick pick) _dlnaSelected.Remove(pick.Id);
            foreach (var item in args.AddedItems.OfType<ListViewItem>()) if (item.Tag is DlnaPick pick) { _dlnaSelected[pick.Id] = pick; _dlnaFocusedId = pick.Id; }
            UpdateDlnaSelection();
        };
        library.Children.Add(_dlnaList);
        library.Children.Add(ActionRow(ActionButton(L.T("Journey.Dlna.SelectAvailable"), () => { foreach (var pick in VisibleDlnaPicks()) _dlnaSelected[pick.Id] = pick; RefreshDlnaList(); return Task.CompletedTask; }),
            ActionButton(L.T("Journey.Dlna.ClearAll"), () => { _dlnaSelected.Clear(); RefreshDlnaList(); return Task.CompletedTask; })));
        _dlnaSelectionText = Muted("", 12);
        library.Children.Add(_dlnaSelectionText);
        _dlnaPreserveButton = ActionButton(L.T("Journey.Dlna.ContinuePreserve"), () => { Navigate("export"); return Task.CompletedTask; }, true);
        library.Children.Add(_dlnaPreserveButton);
        var preview = new StackPanel { Spacing = 12 };
        var currentPreviewName = _player?.Source is not null ? _dlnaPreviewName : null;
        preview.Children.Add(SectionTitle(currentPreviewName is null ? L.T("Journey.Dlna.InspectTitle") : L.T("Journey.Dlna.PreviewNamed", currentPreviewName), Symbol.Play));
        _previewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (_dlnaPreviewName is not null && _player?.Source is not null) AttachPreviewElement();
        else _previewHost.Content = Body(_previewUnavailable ?? L.T("Journey.Dlna.PreviewHelp"));
        preview.Children.Add(_previewHost);
        _dlnaTargetText = Muted("", 12);
        preview.Children.Add(_dlnaTargetText);
        preview.Children.Add(ActionRow(ActionButton(L.T("Journey.Dlna.Preview"), () => InspectDlnaSelectedAsync(true)), ActionButton(L.T("Journey.Dlna.Inspect"), () => InspectDlnaSelectedAsync(false))));
        preview.Children.Add(Muted(L.T("Journey.Dlna.DownloadNotice"), 11));
        var informationTitle = _dlnaInspectedName is null ? L.T("Journey.Dlna.DeliveredInfo") : L.T("Journey.Dlna.DeliveredNamed", _dlnaInspectedName);
        preview.Children.Add(new Expander { Header = JourneyHeader(informationTitle), Content = InformationBox(_dlnaMediaInfo, informationTitle), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
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
            var items = _dlnaObjects.Where(item => !item.IsContainer && MatchesDlna(item.Title))
                .Select(item => (item.Id, item.Title, Status: DlnaStatusText(item.TransferStatus), Size: item.PreferredResource?.SizeBytes)).ToArray();
            foreach (var entry in items)
            {
                picks.TryGetValue(entry.Id, out var pick);
                var row = new StackPanel { Spacing = 5, Margin = new Thickness(3, 8, 3, 8), Children =
                {
                    new TextBlock { Text = entry.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = entry.Status, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = entry.Size.HasValue ? FormatBytes(entry.Size.Value) : L.T("Journey.Dlna.SizeUnknown"), FontSize = 11 }
                } };
                var item = new ListViewItem { Content = row, Tag = pick, IsEnabled = pick is not null, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, $"{entry.Title}. {entry.Status}. {(entry.Size.HasValue ? FormatBytes(entry.Size.Value) : L.T("Journey.Dlna.SizeUnknown"))}.");
                _dlnaList.Items.Add(item);
                if (pick is not null && _dlnaSelected.ContainsKey(pick.Id)) _dlnaList.SelectedItems.Add(item);
            }
            if (_dlnaList.Items.Count == 0 && _dlnaFolders.Children.Count == 0)
                _dlnaFolders.Children.Add(Muted(string.IsNullOrWhiteSpace(_dlnaSearch) ? L.T("Journey.Dlna.EmptyFolder") : L.T("Journey.Dlna.NoMatches"), 12));
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
        if (_dlnaSelectionText is not null) _dlnaSelectionText.Text = L.Plural("Journey.Dlna.Selected", _dlnaSelected.Count) + (hidden > 0 ? L.Plural("Journey.Dlna.HiddenSelected", hidden) : "");
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
        AddHeading(page, L.T("Journey.Dlna.ExportTitle"), L.T("Journey.Dlna.ExportSubtitle"), L.T("Journey.Dlna.PreserveStage"));
        if (_dlnaSelected.Count == 0)
        {
            page.Children.Add(Card(new StackPanel { Spacing = 12, Children = { Body(L.T("Journey.Dlna.ExportSelectHelp")), ActionButton(L.T("Journey.Dlna.ChooseRecordings"), () => { Navigate("library"); return Task.CompletedTask; }, true) } }));
            return;
        }
        var options = new StackPanel { Spacing = 14 };
        options.Children.Add(StageBadge(L.T("Journey.Dlna.DefaultMode"), "Teal"));
        var raw = new RadioButton { GroupName = "DlnaPreservation", Content = new TextBlock { Text = L.T("Journey.Dlna.KeepUnchanged"), TextWrapping = TextWrapping.Wrap }, IsChecked = _dlnaKeepDelivered, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var remux = new RadioButton { GroupName = "DlnaPreservation", Content = new TextBlock { Text = L.T("Journey.Dlna.KeepAndRemux"), TextWrapping = TextWrapping.Wrap }, IsChecked = !_dlnaKeepDelivered, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(raw, L.T("Journey.Dlna.KeepUnchanged"));
        AutomationProperties.SetName(remux, L.T("Journey.Dlna.KeepAndRemux"));
        var formats = new ComboBox { Header = JourneyHeader(L.T("Journey.Dlna.AdditionalContainer")), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !_dlnaKeepDelivered };
        AutomationProperties.SetName(formats, L.T("Journey.Dlna.AdditionalContainer"));
        foreach (var format in Enum.GetValues<OutputFormat>()) formats.Items.Add(new ComboBoxItem { Content = format switch { OutputFormat.Matroska => L.T("Journey.Format.Mkv"), OutputFormat.Mpeg2 => L.T("Journey.Format.Mpeg"), _ => L.T("Journey.Dlna.Mp4Compatible") }, Tag = format });
        formats.SelectedItem = formats.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, _format));
        formats.SelectionChanged += (_, _) => { if (formats.SelectedItem is ComboBoxItem item && item.Tag is OutputFormat format) _format = format; };
        raw.Checked += (_, _) => { _dlnaKeepDelivered = true; formats.IsEnabled = false; };
        remux.Checked += (_, _) => { _dlnaKeepDelivered = false; formats.IsEnabled = true; };
        options.Children.Add(raw);
        options.Children.Add(remux);
        options.Children.Add(formats);
        options.Children.Add(Muted(L.T("Journey.Dlna.UnchangedNotice"), 12));
        var destination = new StackPanel { Spacing = 14 };
        var folder = LabeledInput(destination, L.T("Journey.Dlna.SaveTo"), _outputDirectory);
        folder.TextChanged += (_, _) => _outputDirectory = folder.Text.Trim();
        destination.Children.Add(ActionButton(L.T("Journey.Common.ChooseFolder"), async () => { var selected = await PickFolderAsync(); if (selected is not null) folder.Text = selected; }));
        var known = DlnaPlannedBytes(_dlnaSelected.Values);
        var unknown = _dlnaSelected.Values.Count(item => !item.SizeBytes.HasValue);
        destination.Children.Add(Muted(L.Plural("Journey.Dlna.SelectionSummary", _dlnaSelected.Count) + (known is null ? L.T("Journey.Dlna.SizeTotalUnsupported") : known > 0 ? L.T("Journey.Dlna.AdvertisedTotal", FormatBytes(known.Value)) : "") + (unknown > 0 ? L.Plural("Journey.Dlna.UnknownSizes", unknown) : "") + L.T("Journey.Dlna.AdditionalSpace"), 12));
        destination.Children.Add(Body(SavedNameSummary(OrderBatchCount(_orderNumber, _dlnaSelected.Values.Select(OrderKey).ToArray()))));
        page.Children.Add(ResponsivePair(Card(options), Card(destination), 850));
        page.Children.Add(ActionRow(ActionButton(L.Plural("Journey.Dlna.PreserveCount", _dlnaSelected.Count), ExportDlnaSelectedAsync, true), ActionButton(L.T("Journey.Dlna.Back"), () => { Navigate("library"); return Task.CompletedTask; })));
    }

    private async Task ExportDlnaSelectedAsync()
    {
        if (_dlnaSelected.Count == 0) throw new InvalidOperationException(L.T("Journey.Dlna.SelectionRequired"));
        if (string.IsNullOrWhiteSpace(_outputDirectory)) throw new InvalidOperationException(L.T("Journey.Dlna.OutputRequired"));
        var picks = _dlnaSelected.Values.ToArray();
        var knownBytes = DlnaPlannedBytes(picks) ?? throw new InvalidOperationException(L.T("Journey.Dlna.TotalUnsupported"));
        var directory = Path.GetFullPath(_outputDirectory);
        var keepDeliveredOnly = _dlnaKeepDelivered;
        var format = _format;
        var orderNumber = _orderNumber;
        var batchCount = OrderBatchCount(orderNumber, picks.Select(OrderKey).ToArray());
        await RunOperationAsync(async ct =>
        {
            await Task.Run(() => { Directory.CreateDirectory(directory); CheckAvailableSpace(directory, knownBytes); }, ct);
            using var downloader = new DlnaDownloadService();
            var bothKept = false;
            for (var index = 0; index < picks.Length; index++)
            {
                var pick = picks[index];
                var stem = SavedFileNames.Stem(pick.Title, "Network recording", orderNumber, batchCount, titleIsFileName: false);
                var extension = DlnaDownloadService.GuessSafeExtension(pick.Uri, pick.Resource.MimeType);
                var current = index;
                var progress = new Progress<DlnaDownloadProgress>(value => SetProgress(value.Fraction * 100, value.TotalBytes.HasValue ? L.T("Journey.Dlna.DownloadProgressKnown", current + 1, picks.Length, pick.Title, FormatBytes(value.BytesReceived), FormatBytes(value.TotalBytes.Value)) : L.T("Journey.Dlna.DownloadProgressUnknown", current + 1, picks.Length, pick.Title, FormatBytes(value.BytesReceived))));
                if (keepDeliveredOnly)
                {
                    var result = await downloader.DownloadAsync(new DlnaDownloadRequest(pick.Uri, SavedFileNames.NextAvailablePath(directory, stem, extension), pick.Resource), progress, ct);
                    RememberSavedUnderOrder(orderNumber, OrderKey(pick));
                    await RecordDeliveredDlnaAsync(pick, result.DestinationPath, DlnaDownloadVerification(result), ct);
                    continue;
                }
                // Container mode keeps one file. The delivered stream waits under a working name until the
                // container is verified; if that does not happen, it is kept under its own name, or under the working name when the rename fails.
                var staging = Path.Combine(directory, $".diga-{Guid.NewGuid():N}.download{extension}");
                var delivered = await downloader.DownloadAsync(new DlnaDownloadRequest(pick.Uri, staging, pick.Resource), progress, ct);
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
                    var remuxed = await new RemuxService(_settings.FfmpegPath, _settings.FfprobePath).RemuxAsync(new RemuxRequest(staging, remuxPath, format), remuxProgress, ct);
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
                    if (keepReason is null && !await Task.Run(() => TryDeleteDlnaStaging(staging)))
                    {
                        keepReason = L.T("Journey.Dlna.DeliveredKeptLocked");
                        bothKept = true;
                        if (remuxIndex >= 0) _exports[remuxIndex] = _exports[remuxIndex] with { VerificationSummary = L.T("Journey.Dlna.RemuxVerificationKept") };
                    }
                    if (keepReason is not null && File.Exists(staging))
                    {
                        bothKept = true;
                        var kept = SavedFileNames.NextAvailablePath(directory, keptStem, extension);
                        try { File.Move(staging, kept); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { kept = staging; }
                        // The token may already be cancelled here; the kept file is recorded without waiting for MediaInfo.
                        _exports.Add(new ExportedFile(pick.Title, kept, DlnaSourceInformation(pick), verification, verification + "\n" + keepReason));
                        _selectedExports.Add(kept);
                        RefreshJourney();
                    }
                }
            }
            ShowBanner(L.T("Journey.Dlna.SavedTitle"), L.T(keepDeliveredOnly ? "Journey.Dlna.SavedHelp" : bothKept ? "Journey.Dlna.SavedContainerKeptHelp" : "Journey.Dlna.SavedContainerHelp"),
                bothKept ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            if (_wizard) Navigate("complete");
        }, L.T("Journey.Dlna.PreservationComplete"));
    }

    private async Task RecordDeliveredDlnaAsync(DlnaPick pick, string path, string verification, CancellationToken ct)
    {
        var archiveIndex = _exports.Count;
        _exports.Add(new ExportedFile(pick.Title, path, DlnaSourceInformation(pick), L.T("Journey.Dlna.RawInspectionPending"), verification));
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

    private static bool TryDeleteDlnaStaging(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task InspectDlnaSelectedAsync(bool preview)
    {
        var pick = FocusedDlnaPick() ?? throw new InvalidOperationException(L.T("Journey.Dlna.PreviewSelectionRequired"));
        await RunOperationAsync(async ct =>
        {
            var download = await EnsureDlnaCachedAsync(pick, ct);
            SetProgress(null, L.T("Journey.Dlna.Inspecting"));
            _dlnaMediaInfo = DlnaSourceInformation(pick) + "\n\n" + DlnaDownloadVerification(download) + "\n\n" + await InspectDlnaDownloadAsync(download.DestinationPath, ct);
            _dlnaInspectedName = pick.Title;
            if (!preview || !EnsurePreviewPlayer()) return;
            SetProgress(null, L.T("Journey.Dlna.PreparingPreview"));
            var prepared = await new PreviewService(_settings.FfmpegPath, _settings.FfprobePath).PrepareAsync(download.DestinationPath, SessionCacheDirectory, ct);
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
            Directory.CreateDirectory(directory);
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
