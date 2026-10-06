using L = Diga.Core.Localization.AppText;
using System.Text;
using System.Text.Json;
using Diga.Core.Media;
using Diga.Core.Storage;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Diga.App;

public sealed partial class MainWindow
{
    private TextBlock? _libraryEmptyMessage;
    private TextBlock? _libraryResultText;
    private TextBlock? _previewRecordingTitle;
    private TextBlock? _previewRecordingDetail;
    private TextBlock? _previewTargetText;
    private TextBlock? _sourceInfoTitle;
    private Button? _preserveSelectionButton;
    private RecordingFile? _previewRecording;
    private RecordingFile? _inspectedRecording;
    private PanasonicFileSystem? _previewIdentitySource;
    private PanasonicFileSystem? _inspectedIdentitySource;

    private void BuildLibraryPage(StackPanel page)
    {
        if (HasDlnaSource) { BuildDlnaLibraryPage(page); return; }
        AddHeading(page, L.T("Journey.Library.Title"), L.T("Journey.Library.Subtitle"), L.T("Journey.Stage.Discover"));
        if (_fileSystem is null)
        {
            page.Children.Add(TintCard(new StackPanel { Spacing = 18, Children = {
                StageBadge(L.T("Journey.Library.Start"), "Amber"),
                SectionTitle(L.T("Journey.Library.Find"), Symbol.Video),
                Body(L.T("Journey.Library.ConnectHelp")),
                ActionButton(L.T("Journey.Library.Connect"), () => { Navigate("source"); return Task.CompletedTask; }, true)
            } }, "Amber"));
            return;
        }
        var source = new StackPanel { Spacing = 9 };
        source.Children.Add(Muted(L.Plural("Journey.Library.SourceSummary", _fileSystem.Recordings.Count, Path.GetFileName(_fileSystem.SourcePath), FormatBytes(_fileSystem.Recordings.Sum(f => f.SizeBytes))), 12));
        source.Children.Add(new Expander { Header = JourneyHeader(L.T("Journey.Library.SourceDetails")), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = Muted($"{_fileSystem.FileSystemName}\n{_fileSystem.SourcePath}", 12) });
        page.Children.Add(source);

        var library = new StackPanel { Spacing = 16 };
        library.Children.Add(SectionTitle(L.T("Journey.Library.Choose"), Symbol.Video));
        library.Children.Add(Muted(L.T("Journey.Library.SelectHelp"), 12));
        var search = new TextBox { Text = _search, PlaceholderText = L.T("Journey.Library.SearchPlaceholder"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(search, L.T("Journey.Library.SearchAccessible"));
        search.TextChanged += (_, _) => { _search = search.Text; RefreshRecordingList(); };
        library.Children.Add(search);
        library.Children.Add(ActionRow(
            ActionButton(L.T("Journey.Library.SelectVisible"), () => { foreach (var item in VisibleRecordings()) _selected[item.Id] = item; RefreshRecordingList(); return Task.CompletedTask; }),
            ActionButton(L.T("Journey.Library.ClearSelection"), () => { _selected.Clear(); RefreshRecordingList(); return Task.CompletedTask; }),
            ActionButton(L.T("Journey.Library.ClearSearch"), () => { search.Text = ""; return Task.CompletedTask; })));
        _libraryResultText = Muted("", 12);
        library.Children.Add(_libraryResultText);
        _recordingList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true,
            MinHeight = 100, MaxHeight = 430, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(_recordingList, L.T("Journey.Library.ListAccessible"));
        _recordingList.SelectionChanged += (_, args) =>
        {
            if (_refreshingList) return;
            foreach (var item in args.RemovedItems.OfType<ListViewItem>()) if (item.Tag is RecordingFile file) _selected.Remove(file.Id);
            foreach (var item in args.AddedItems.OfType<ListViewItem>())
                if (item.Tag is RecordingFile file) { _selected[file.Id] = file; _focusedRecordingId = file.Id; }
            UpdateSelectionText();
        };
        _recordingList.DoubleTapped += async (_, _) => await InspectSelectedAsync(true);
        library.Children.Add(_recordingList);
        _libraryEmptyMessage = Muted("", 13);
        library.Children.Add(_libraryEmptyMessage);
        _selectionText = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_selectionText, AutomationLiveSetting.Polite);
        library.Children.Add(_selectionText);
        _preserveSelectionButton = ActionButton(L.T("Journey.Library.ChooseContinue"), () =>
        {
            if (_selected.Count == 0) ShowBanner(L.T("Journey.Library.Choose"), L.T("Journey.Library.SelectPreserveHelp"), InfoBarSeverity.Informational);
            else Navigate("export");
            return Task.CompletedTask;
        }, true);
        library.Children.Add(_preserveSelectionButton);
        library.Children.Add(Muted(L.T("Journey.Library.ProgrammesNotice"), 11));

        var preview = new StackPanel { Spacing = 16 };
        preview.Children.Add(StageBadge(L.T("Journey.Preview.Badge"), "Violet"));
        var currentPreview = ReferenceEquals(_previewIdentitySource, _fileSystem) && _player?.Source is not null ? _previewRecording : null;
        _previewRecordingTitle = new TextBlock { Text = currentPreview?.Name ?? L.T("Journey.Preview.Title"), FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _previewRecordingDetail = Muted(currentPreview is not null ? RecordingDetail(currentPreview) : L.T("Journey.Preview.SelectHelp"), 12);
        preview.Children.Add(_previewRecordingTitle);
        preview.Children.Add(_previewRecordingDetail);
        _previewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        preview.Children.Add(_previewHost);
        if (_player?.Source is not null) AttachPreviewElement();
        else _previewHost.Content = PreviewPlaceholder();
        _previewTargetText = Muted("", 12);
        preview.Children.Add(_previewTargetText);
        preview.Children.Add(ActionRow(
            ActionButton(L.T("Journey.Preview.Play"), () => InspectSelectedAsync(true)),
            ActionButton(L.T("Journey.Preview.Inspect"), () => InspectSelectedAsync(false))));
        preview.Children.Add(Muted(L.T("Journey.Preview.CacheNotice"), 11));
        var informationTitle = ReferenceEquals(_inspectedIdentitySource, _fileSystem) && _inspectedRecording is not null
            ? L.T("Journey.Preview.OriginalNamed", _inspectedRecording.Name) : L.T("Journey.Preview.OriginalDetails");
        _sourceInfoBox = InformationBox(_sourceInfo, informationTitle);
        _sourceInfoTitle = new TextBlock { Text = informationTitle, TextWrapping = TextWrapping.Wrap };
        preview.Children.Add(new Expander { Header = _sourceInfoTitle, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = _sourceInfoBox });
        page.Children.Add(ResponsivePair(Card(library), Card(preview), 900));
        RefreshRecordingList();
    }

    private IEnumerable<RecordingFile> VisibleRecordings() => _fileSystem?.Recordings.Where(f => string.IsNullOrWhiteSpace(_search) || f.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) || f.FullPath.Contains(_search, StringComparison.OrdinalIgnoreCase)) ?? [];

    private void RefreshRecordingList()
    {
        if (_recordingList is null) return;
        _refreshingList = true;
        try
        {
            _recordingList.Items.Clear();
            var visible = VisibleRecordings().ToArray();
            if (_libraryResultText is not null) _libraryResultText.Text = L.T("Journey.Library.ResultCount", visible.Length, _fileSystem?.Recordings.Count ?? 0);
            foreach (var file in visible)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(2, 12, 4, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new Border { Width = 42, Height = 48, CornerRadius = new CornerRadius(8), Child = new SymbolIcon(Symbol.Video) { Width = 20, Height = 20 } });
                var title = new StackPanel { Spacing = 5 };
                title.Children.Add(new TextBlock { Text = file.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var date = file.RecordedAt?.ToLocalTime().ToString("g") ?? L.T("Journey.Library.DateUnavailable");
                // Inherit the ListViewItem foreground so selected text follows high-contrast colors.
                title.Children.Add(new TextBlock { Text = $"{FormatBytes(file.SizeBytes)}  ·  {date}", FontSize = 12, TextWrapping = TextWrapping.Wrap });
                title.Children.Add(new TextBlock { Text = file.FullPath, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(title, 1);
                row.Children.Add(title);
                var item = new ListViewItem { Content = row, Tag = file, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, $"{file.Name}, {FormatBytes(file.SizeBytes)}, {date}");
                _recordingList.Items.Add(item);
                if (_selected.ContainsKey(file.Id)) _recordingList.SelectedItems.Add(item);
            }
            if (_libraryEmptyMessage is not null)
            {
                _libraryEmptyMessage.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _libraryEmptyMessage.Text = _fileSystem?.Recordings.Count == 0
                    ? L.T("Journey.Library.NoVideos")
                    : L.T("Journey.Library.NoMatches");
            }
        }
        finally { _refreshingList = false; }
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        var visibleIds = VisibleRecordings().Select(recording => recording.Id).ToHashSet(StringComparer.Ordinal);
        var hidden = _selected.Keys.Count(id => !visibleIds.Contains(id));
        if (_selectionText is not null) _selectionText.Text = _selected.Count == 0 ? L.T("Journey.Library.SelectionEmpty")
            : L.Plural("Journey.Library.Selected", _selected.Count, FormatBytes(_selected.Values.Sum(f => f.SizeBytes))) + (hidden > 0 ? L.Plural("Journey.Library.HiddenSelection", hidden) : "");
        if (_preserveSelectionButton is not null)
        {
            _preserveSelectionButton.IsEnabled = _selected.Count > 0;
            var label = _selected.Count == 0 ? L.T("Journey.Library.ChooseContinue") : L.Plural("Journey.Library.PreserveSelected", _selected.Count);
            _preserveSelectionButton.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold };
            AutomationProperties.SetName(_preserveSelectionButton, label);
        }
        if (_previewTargetText is not null) _previewTargetText.Text = FocusedRecording() is { } focused
            ? L.T("Journey.Preview.Target", focused.Name) : L.T("Journey.Preview.SelectTarget");
        RefreshJourney();
    }

    private static string RecordingDetail(RecordingFile recording) =>
        $"{FormatBytes(recording.SizeBytes)}  ·  {recording.RecordedAt?.ToLocalTime().ToString("g") ?? L.T("Journey.Library.RecordingDateUnavailable")}";

    private RecordingFile? FocusedRecording() =>
        (_focusedRecordingId is not null && _selected.TryGetValue(_focusedRecordingId, out var selected)) ? selected : _selected.Values.FirstOrDefault();

    private async Task InspectSelectedAsync(bool preview)
    {
        var recording = FocusedRecording();
        if (recording is null) { ShowBanner(L.T("Journey.Preview.SelectSource"), L.T("Journey.Preview.SelectSourceHelp"), InfoBarSeverity.Informational); return; }
        await RunOperationAsync(async ct =>
        {
            var cached = await EnsureCachedAsync(recording, ct);
            SetProgress(null, L.T("Journey.Preview.Inspecting", recording.Name));
            _sourceInfo = L.T("Journey.Media.SourcePath", recording.FullPath) + await InspectMediaAsync(cached, ct);
            _inspectedRecording = recording;
            _inspectedIdentitySource = _fileSystem;
            var informationTitle = L.T("Journey.Preview.OriginalNamed", recording.Name);
            if (_sourceInfoTitle is not null) _sourceInfoTitle.Text = informationTitle;
            if (_sourceInfoBox is not null)
            {
                _sourceInfoBox.Text = _sourceInfo;
                AutomationProperties.SetName(_sourceInfoBox, informationTitle);
            }
            if (preview && EnsurePreviewPlayer())
            {
                SetProgress(null, L.T("Journey.Preview.Preparing"));
                DiskDiscovery.EnsureSafeDestination(_fileSystem!.SourcePath, Path.Combine(SessionCacheDirectory, "diga-preview-check.tmp"));
                var prepared = await new PreviewService(_settings.FfmpegPath, _settings.FfprobePath).PrepareAsync(cached, SessionCacheDirectory, ct);
                _ownedCacheFiles.Add(prepared.Path);
                ct.ThrowIfCancellationRequested();
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(prepared.Path);
                    ct.ThrowIfCancellationRequested();
                    _player!.Source = MediaSource.CreateFromStorageFile(file);
                    _previewRecording = recording;
                    _previewIdentitySource = _fileSystem;
                    if (_previewRecordingTitle is not null) _previewRecordingTitle.Text = recording.Name;
                    if (_previewRecordingDetail is not null) _previewRecordingDetail.Text = RecordingDetail(recording);
                    _player.Play();
                    ShowBanner(L.T("Journey.Preview.Ready"), $"{recording.Name}. {prepared.Notice}", InfoBarSeverity.Informational);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ShowPreviewUnavailable(ex);
                }
            }
        }, L.T("Journey.Preview.Inspected", recording.Name));
    }

    private bool EnsurePreviewPlayer()
    {
        // Windows N and Server installations may not provide Media Foundation.
        // Playback is optional: activate it only after an explicit preview request.
        try
        {
            if (_player is null)
            {
                var player = new MediaPlayer();
                _player = player;
                player.AutoPlay = false;
                player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && ReferenceEquals(_player, player))
                        ShowBanner(L.T("Journey.Preview.PlaybackError"), L.T("Journey.Preview.PlaybackErrorHelp", args.ErrorMessage), InfoBarSeverity.Warning);
                });
            }
            _previewUnavailable = null;
            return AttachPreviewElement();
        }
        catch (Exception ex)
        {
            ShowPreviewUnavailable(ex);
            return false;
        }
    }

    private bool AttachPreviewElement()
    {
        if (_previewHost is null) return _player is not null;
        if (_player is null)
        {
            _previewHost.Content = PreviewPlaceholder();
            return false;
        }
        try
        {
            if (_mediaElement is null)
            {
                _mediaElement = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true, AutoPlay = false, Height = 300,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                _mediaElement.SetMediaPlayer(_player);
                AutomationProperties.SetName(_mediaElement, L.T("Journey.Preview.Accessible"));
            }
            _previewHost.Content = _mediaElement;
            return true;
        }
        catch (Exception ex)
        {
            ShowPreviewUnavailable(ex);
            return false;
        }
    }

    private Border PreviewPlaceholder()
    {
        var content = new StackPanel { Spacing = 15, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new SymbolIcon(Symbol.Play) { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = L.T("Journey.Preview.PlaceholderTitle"), FontSize = 18, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var detail = Muted(_previewUnavailable ?? L.T("Journey.Preview.PlaceholderHelp"), 12);
        detail.TextAlignment = TextAlignment.Center;
        content.Children.Add(detail);
        return new Border { Child = content, MinHeight = 240, Padding = new Thickness(24), CornerRadius = new CornerRadius(12), Style = AppStyle("DigaPillStyle") };
    }

    private void ShowPreviewUnavailable(Exception exception)
    {
        App.LogException("Preview initialization or playback", exception);
        ReleasePreviewPlayer();
        _previewUnavailable = L.T("Journey.Preview.UnavailableHelp", exception.Message);
        if (_previewHost is not null) _previewHost.Content = Body(_previewUnavailable);
        if (!_closed) ShowBanner(L.T("Journey.Preview.Unavailable"), _previewUnavailable, InfoBarSeverity.Warning);
    }

    private void DetachPreviewElement()
    {
        var element = _mediaElement;
        _mediaElement = null;
        try { element?.SetMediaPlayer(null); }
        catch (Exception ex) { App.LogException("Detaching preview", ex); }
    }

    private void ReleasePreviewPlayer()
    {
        DetachPreviewElement();
        var player = _player;
        _player = null;
        if (player is null) return;
        try { player.Source = null; }
        catch (Exception ex) { App.LogException("Releasing preview source", ex); }
        try { player.Dispose(); }
        catch (Exception ex) { App.LogException("Disposing preview player", ex); }
    }

    private string SessionCacheDirectory => Path.Combine(Path.GetFullPath(_settings.CacheDirectory), "session-" + _sessionId);

    private async Task<string> EnsureCachedAsync(RecordingFile recording, CancellationToken ct)
    {
        if (_fileSystem is null) throw new InvalidOperationException(L.T("Journey.Library.SourceRequired"));
        if (_cached.TryGetValue(recording.Id, out var existing) && File.Exists(existing)) return existing;
        var directory = SessionCacheDirectory;
        await Task.Run(() =>
        {
            DiskDiscovery.EnsureSafeDestination(_fileSystem.SourcePath, Path.Combine(directory, "diga-cache-check.tmp"));
            // Preview playback reads a separate short clip, so prior full containers can be released.
            foreach (var previous in _stagedCacheFiles.ToArray()) ReleaseStagedPath(previous);
            Directory.CreateDirectory(directory);
            CheckAvailableSpace(directory, recording.SizeBytes);
        }, ct);
        var extension = Path.GetExtension(recording.Name);
        if (extension.Length > 10 || extension.Any(c => !char.IsLetterOrDigit(c) && c != '.')) extension = ".vro";
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
        var progress = new Progress<double>(fraction => SetProgress(fraction * 100, L.T("Journey.Cache.Reading", recording.Name, fraction)));
        await _fileSystem.ExtractAsync(recording, destination, progress, ct);
        _ownedCacheFiles.Add(destination);
        _stagedCacheFiles.Add(destination);
        _cached[recording.Id] = destination;
        return destination;
    }

    private void ReleaseStagedSource(string id)
    {
        if (!_cached.TryGetValue(id, out var path)) return;
        ReleaseStagedPath(path);
    }

    private void ReleaseStagedPath(string path)
    {
        var parent = Path.GetFullPath(Path.GetDirectoryName(path)!);
        if (!Path.GetFileName(parent).Equals("session-" + _sessionId, StringComparison.Ordinal))
            throw new IOException(L.T("Journey.Cache.UnsafeDelete"));
        if (_fileSystem is not null) DiskDiscovery.EnsureSafeDestination(_fileSystem.SourcePath, path);
        File.Delete(path);
        _ownedCacheFiles.Remove(path);
        _stagedCacheFiles.Remove(path);
        foreach (var id in _cached.Where(pair => pair.Value.Equals(path, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray()) _cached.Remove(id);
    }

    private async Task<string> InspectMediaAsync(string path, CancellationToken ct)
    {
        var media = await new MediaInfoService(_settings.MediaInfoPath).InspectAsync(path, ct);
        if (media.Available) return ReadableMediaInfo(media.Json);
        // Keep the missing native library visible; ffprobe is a useful secondary diagnostic.
        var probe = await new MediaProbeService(_settings.FfprobePath).ProbeAsync(path, ct);
        var result = new StringBuilder(L.T("Journey.Media.FfprobeDetails", media.Error, Path.GetFileName(path), FormatBytes(probe.SizeBytes), probe.Duration?.ToString() ?? L.T("Journey.Media.Unknown")));
        foreach (var stream in probe.Streams)
            result.AppendLine($"{MediaTrackLabel(stream.Type)} #{stream.Index}: {stream.Codec}  {stream.Width}×{stream.Height}  {stream.Language}");
        return result.ToString();
    }

    private static string ReadableMediaInfo(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            if (!document.RootElement.TryGetProperty("media", out var media) || !media.TryGetProperty("track", out var tracks)) return json;
            string[] keys = ["Format", "Format_Profile", "CodecID", "Duration", "FileSize", "OverallBitRate", "BitRate", "Width", "Height", "FrameRate", "ScanType", "DisplayAspectRatio", "Channels", "ChannelLayout", "SamplingRate", "Language", "Title"];
            foreach (var track in tracks.EnumerateArray())
            {
                builder.AppendLine(MediaTrackLabel(track.TryGetProperty("@type", out var kind) ? kind.GetString() : null));
                foreach (var key in keys)
                    if (track.TryGetProperty(key, out var value)) builder.AppendLine($"{L.T("Journey.Media.Field." + key)}: {value}");
                builder.AppendLine();
            }
            builder.Append(L.T("Journey.Media.Units"));
            return builder.ToString();
        }
        catch (JsonException) { return json; }
    }

    private static string MediaTrackLabel(string? type) => type?.ToLowerInvariant() switch
    {
        "general" => L.T("Journey.Media.Track.General"),
        "video" => L.T("Journey.Media.Track.Video"),
        "audio" => L.T("Journey.Media.Track.Audio"),
        "text" or "subtitle" => L.T("Journey.Media.Track.Text"),
        "menu" => L.T("Journey.Media.Track.Menu"),
        "image" => L.T("Journey.Media.Track.Image"),
        "other" or "data" or "attachment" => L.T("Journey.Media.Track.Other"),
        null or "" => L.T("Journey.Media.Track.Track"),
        _ => type ?? L.T("Journey.Media.Track.Track")
    };

    private static TextBlock JourneyHeader(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBox InformationBox(string text, string? accessibleLabel = null)
    {
        var box = new TextBox
        {
            AcceptsReturn = true, Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 130, MaxHeight = 400, FontSize = 12, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (accessibleLabel is not null) AutomationProperties.SetName(box, accessibleLabel);
        return box;
    }

    private static void CheckAvailableSpace(string directory, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes + 64L * 1024 * 1024)
            throw new IOException(L.T("Journey.Cache.SpaceRequired", directory, FormatBytes(requiredBytes)));
    }
}
