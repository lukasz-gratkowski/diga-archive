using L = Diga.Core.Localization.AppText;
using Diga.Core.Cloud;
using Diga.Core.Media;
using Diga.Core.Naming;
using Diga.Core.Storage;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Diga.App;

public sealed partial class MainWindow
{
    private OutputFormat _format = OutputFormat.Matroska;
    private string _outputDirectory = "";
    private readonly HashSet<string> _selectedExports = new(StringComparer.OrdinalIgnoreCase);
    private TextBlock? _uploadSelectionText;
    private Button? _uploadSelectedButton;

    private void BuildExportPage(StackPanel page)
    {
        if (HasDlnaSource) { BuildDlnaExportPage(page); return; }
        AddHeading(page, L.T("Journey.Export.Title"), L.T("Journey.Export.Subtitle"), L.T("Journey.Stage.Preserve"));
        if (_fileSystem is null || _selected.Count == 0)
        {
            page.Children.Add(TintCard(new StackPanel { Spacing = 18, Children = {
                StageBadge(L.T("Journey.Export.NextStep"), "Amber"),
                SectionTitle(L.T("Journey.Export.EmptyTitle"), Symbol.Video),
                Body(L.T("Journey.Export.EmptyHelp")),
                ActionButton(L.T("Journey.Export.Discover"), () => { Navigate(_fileSystem is null ? "source" : "library"); return Task.CompletedTask; }, true)
            } }, "Amber"));
            return;
        }
        var bytes = _selected.Values.Sum(f => f.SizeBytes);
        page.Children.Add(ResponsivePair(
            MetricTile(_selected.Count.ToString(), L.Plural("Journey.Export.ReadyMetric", _selected.Count), "Amber"),
            MetricTile(FormatBytes(bytes), L.T("Journey.Export.SizeMetric"), "Teal"), 500));

        var format = new StackPanel { Spacing = 17 };
        format.Children.Add(SectionTitle(L.T("Journey.Export.FormatTitle"), Symbol.Video));
        format.Children.Add(Muted(L.T("Journey.Export.FormatHelp"), 12));
        var formatSummary = Body(FormatSummary(_format));
        RadioButton FormatChoice(OutputFormat value, string title, string detail)
        {
            var choice = new RadioButton
            {
                GroupName = "PreservationFormat", IsChecked = _format == value,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 6, Children = {
                    new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    Muted(detail, 12)
                } }
            };
            AutomationProperties.SetName(choice, title + ". " + detail);
            choice.Checked += (_, _) => { _format = value; formatSummary.Text = FormatSummary(value); };
            return choice;
        }
        format.Children.Add(TintCard(new StackPanel { Spacing = 12, Children = {
            StageBadge(L.T("Journey.Export.Recommended"), "Teal"),
            FormatChoice(OutputFormat.Matroska, L.T("Journey.Format.Mkv"), L.T("Journey.Export.MkvHelp"))
        } }));
        var alternatives = new StackPanel { Spacing = 20, Children = {
            FormatChoice(OutputFormat.Mpeg2, L.T("Journey.Format.Mpeg"), L.T("Journey.Export.MpegHelp")),
            FormatChoice(OutputFormat.Mp4, L.T("Journey.Format.Mp4"), L.T("Journey.Export.Mp4Help"))
        } };
        format.Children.Add(new Expander { Header = JourneyHeader(L.T("Journey.Export.OtherFormats")), IsExpanded = _format != OutputFormat.Matroska,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = alternatives });
        format.Children.Add(Muted(L.T("Journey.Export.CompatibilityHelp"), 11));

        var destination = new StackPanel { Spacing = 17 };
        destination.Children.Add(SectionTitle(L.T("Journey.Export.DestinationTitle"), Symbol.Folder));
        destination.Children.Add(Muted(L.T("Journey.Export.DestinationHelp"), 12));
        var destinationInput = new TextBox { Header = JourneyHeader(L.T("Journey.Export.SaveTo")), Text = _outputDirectory, PlaceholderText = L.T("Journey.Export.FolderPlaceholder"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(destinationInput, L.T("Journey.Export.FolderAccessible"));
        destinationInput.TextChanged += (_, _) => _outputDirectory = destinationInput.Text.Trim();
        destination.Children.Add(destinationInput);
        destination.Children.Add(ActionButton(L.T("Journey.Common.ChooseFolder"), async () => { var folder = await PickFolderAsync(); if (folder is not null) { _outputDirectory = folder; destinationInput.Text = folder; } }));
        destination.Children.Add(StageBadge(L.T("Journey.Export.SpaceTitle"), "Amber"));
        destination.Children.Add(Body(L.T("Journey.Export.SpaceEstimate", FormatBytes(bytes), FormatBytes(_selected.Values.Max(f => f.SizeBytes)))));
        destination.Children.Add(Muted(L.T("Journey.Export.SpaceHelp"), 11));
        destination.Children.Add(Muted(L.T("Journey.Export.ExistingFilesHelp"), 11));
        page.Children.Add(ResponsivePair(Card(format), Card(destination), 930));

        var review = new StackPanel { Spacing = 15 };
        review.Children.Add(StageBadge(L.T("Journey.Export.Ready"), "Amber"));
        review.Children.Add(formatSummary);
        review.Children.Add(Body(SavedNameSummary(OrderBatchCount(_orderNumber, _selected.Values.Select(OrderKey).ToArray()))));
        var names = new StackPanel { Spacing = 7 };
        foreach (var recording in _selected.Values.Take(20)) names.Children.Add(Muted($"{recording.Name} · {FormatBytes(recording.SizeBytes)}", 12));
        if (_selected.Count > 20) names.Children.Add(Muted(L.Plural("Journey.Export.More", _selected.Count - 20), 12));
        review.Children.Add(new Expander { Header = JourneyHeader(L.Plural("Journey.Export.Review", _selected.Count)), Content = names,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        review.Children.Add(ActionRow(
            ActionButton(L.Plural("Journey.Export.Preserve", _selected.Count), ExportSelectedAsync, true),
            ActionButton(L.T("Journey.Export.Back"), () => { Navigate("library"); return Task.CompletedTask; })));
        review.Children.Add(Muted(L.T("Journey.Export.VerificationHelp"), 12));
        page.Children.Add(TintCard(review, "Amber"));
    }

    private static string FormatSummary(OutputFormat format) => format switch
    {
        OutputFormat.Matroska => L.T("Journey.Export.MkvSummary"),
        OutputFormat.Mpeg2 => L.T("Journey.Export.MpegSummary"),
        _ => L.T("Journey.Export.Mp4Summary")
    };

    private async Task ExportSelectedAsync()
    {
        if (_fileSystem is null || _selected.Count == 0) throw new InvalidOperationException(L.T("Journey.Export.SelectionRequired"));
        if (string.IsNullOrWhiteSpace(_outputDirectory)) throw new InvalidOperationException(L.T("Journey.Export.FolderRequired"));
        var recordings = _selected.Values.ToArray();
        var outputDirectory = Path.GetFullPath(_outputDirectory);
        var orderNumber = _orderNumber;
        var batchCount = OrderBatchCount(orderNumber, recordings.Select(OrderKey).ToArray());
        await RunOperationAsync(async ct =>
        {
            SetProgress(null, L.T("Journey.Export.CheckingDestination"));
            await Task.Run(() =>
            {
                DiskDiscovery.EnsureSafeDestination(_fileSystem.SourcePath, Path.Combine(outputDirectory, "diga-destination-check.tmp"));
                Directory.CreateDirectory(outputDirectory);
                CheckAvailableSpace(outputDirectory, recordings.Sum(f => f.SizeBytes));
            }, ct);
            _player?.Pause();
            var remux = new RemuxService(_settings.FfmpegPath, _settings.FfprobePath);
            for (var index = 0; index < recordings.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var recording = recordings[index];
                var cached = await EnsureCachedAsync(recording, ct);
                SetProgress(null, L.T("Journey.Export.InspectingOriginal", index + 1, recordings.Length));
                var sourceInfo = await InspectMediaAsync(cached, ct);
                var destination = SavedFileNames.NextAvailablePath(outputDirectory,
                    SavedFileNames.Stem(recording.Name, "Panasonic recording", orderNumber, batchCount), OutputFormats.Extension(_format));
                DiskDiscovery.EnsureSafeDestination(_fileSystem.SourcePath, destination);
                var currentIndex = index;
                var progress = new Progress<RemuxProgress>(p => SetProgress(p.Fraction * 100, L.T("Journey.Export.Progress", currentIndex + 1, recordings.Length, recording.Name, p.Message)));
                var result = await remux.RemuxAsync(new RemuxRequest(cached, destination, _format), progress, ct);
                RememberSavedUnderOrder(orderNumber, OrderKey(recording));
                SetProgress(null, L.T("Journey.Export.InspectingSaved", recording.Name));
                // The verified export remains visible even if optional native metadata inspection fails.
                string destinationInfo;
                try { destinationInfo = await InspectMediaAsync(result.DestinationPath, ct); }
                catch (OperationCanceledException)
                {
                    _exports.Add(new ExportedFile(recording.Name, result.DestinationPath, sourceInfo, L.T("Journey.Export.InspectionCancelled")));
                    _selectedExports.Add(result.DestinationPath);
                    throw;
                }
                catch (Exception ex) { destinationInfo = L.T("Journey.Export.AdditionalInspection", ex.Message); }
                _exports.Add(new ExportedFile(recording.Name, result.DestinationPath, sourceInfo, destinationInfo));
                _selectedExports.Add(result.DestinationPath);
                await Task.Run(() => ReleaseStagedSource(recording.Id), ct);
            }
            ShowBanner(L.T("Journey.Export.ReadyTitle"), L.Plural("Journey.Export.CompleteMessage", recordings.Length), InfoBarSeverity.Success);
            if (_wizard) Navigate("complete");
        }, L.Plural("Journey.Export.CompleteStatus", recordings.Length));
    }

    private void BuildCompletePage(StackPanel page)
    {
        AddHeading(page, L.T("Journey.Archive.Title"), L.T("Journey.Archive.Subtitle"), L.T("Journey.Stage.Archive"));
        if (_exports.Count == 0)
        {
            page.Children.Add(TintCard(new StackPanel { Spacing = 18, Children = {
                StageBadge(L.T("Journey.Archive.EmptyBadge"), "Coral"),
                SectionTitle(L.T("Journey.Archive.EmptyTitle"), Symbol.Library),
                Body(L.T("Journey.Archive.EmptyHelp")),
                ActionButton(L.T("Journey.Archive.Find"), () => { Navigate(HasRecordingSource ? "library" : "source"); return Task.CompletedTask; }, true)
            } }, "Coral"));
            return;
        }
        var completion = new StackPanel { Spacing = 15 };
        completion.Children.Add(StageBadge(L.T("Journey.Archive.SavedBadge"), "Teal"));
        completion.Children.Add(new TextBlock { Text = L.Plural("Journey.Archive.Saved", _exports.Count), FontSize = 29, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        completion.Children.Add(Body(L.T("Journey.Archive.VerificationHelp")));
        completion.Children.Add(ActionRow(
            ActionButton(L.T("Journey.Archive.OpenLatest"), () => { OpenFolder(Path.GetDirectoryName(_exports[^1].Path)!); return Task.CompletedTask; }, true),
            ActionButton(L.T("Journey.Archive.DiscoverMore"), () => { Navigate("library"); return Task.CompletedTask; })));
        page.Children.Add(TintCard(completion));
        page.Children.Add(SectionTitle(L.T("Journey.Archive.CopiesTitle"), Symbol.Library));
        page.Children.Add(Muted(L.T("Journey.Archive.SelectHelp"), 12));
        foreach (var exported in _exports)
        {
            var entry = new StackPanel { Spacing = 12 };
            var check = new CheckBox { Content = new TextBlock { Text = Path.GetFileName(exported.Path), FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                IsChecked = _selectedExports.Contains(exported.Path), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(check, L.T("Journey.Archive.SelectAccessible", Path.GetFileName(exported.Path)));
            check.Click += (_, _) => { if (check.IsChecked == true) _selectedExports.Add(exported.Path); else _selectedExports.Remove(exported.Path); UpdateUploadSelection(); };
            entry.Children.Add(check);
            entry.Children.Add(Muted(exported.Path, 12));
            entry.Children.Add(Muted(exported.VerificationSummary, 12));
            var sourceLabel = L.T("Journey.Archive.Source", exported.SourceName);
            var savedLabel = L.T("Journey.Archive.SavedFile", Path.GetFileName(exported.Path));
            var before = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = sourceLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, InformationBox(exported.SourceInformation, sourceLabel) } };
            var after = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = savedLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, InformationBox(exported.DestinationInformation, savedLabel) } };
            entry.Children.Add(new Expander { Header = JourneyHeader(L.T("Journey.Archive.Compare")), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = ResponsivePair(before, after, 760) });
            entry.Children.Add(ActionButton(L.T("Journey.Archive.OpenFolder"), () => { OpenFolder(Path.GetDirectoryName(exported.Path)!); return Task.CompletedTask; }));
            page.Children.Add(Card(entry));
        }
        var cloud = new StackPanel { Spacing = 16 };
        cloud.Children.Add(StageBadge(L.T("Journey.Cloud.Optional"), "Violet"));
        cloud.Children.Add(SectionTitle(L.T("Journey.Cloud.Title"), Symbol.Upload));
        cloud.Children.Add(Body(L.T("Journey.Cloud.Help")));
        cloud.Children.Add(Muted(_oneDriveAccount is { } account ? L.T("Journey.Cloud.Connected", account) : L.T("Journey.Cloud.Disconnected"), 12));
        _uploadSelectionText = Muted("", 12);
        cloud.Children.Add(_uploadSelectionText);
        _uploadSelectedButton = ActionButton(L.T("Journey.Cloud.Upload"), UploadSelectedAsync);
        cloud.Children.Add(ActionRow(_uploadSelectedButton, ActionButton(L.T("Journey.Cloud.Manage"), () => { Navigate("settings"); return Task.CompletedTask; })));
        cloud.Children.Add(Muted(L.T("Journey.Cloud.SetupHelp"), 11));
        page.Children.Add(Card(cloud));
        UpdateUploadSelection();
    }

    private void UpdateUploadSelection()
    {
        var count = _exports.Count(e => _selectedExports.Contains(e.Path));
        if (_uploadSelectionText is not null) _uploadSelectionText.Text = count == 0 ? L.T("Journey.Cloud.SelectHelp") : L.Plural("Journey.Cloud.Selected", count);
        if (_uploadSelectedButton is not null) _uploadSelectedButton.IsEnabled = count > 0;
    }

    private async Task UploadSelectedAsync()
    {
        var selected = _exports.Where(e => _selectedExports.Contains(e.Path)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(L.T("Journey.Cloud.SelectionRequired"));
        var clientId = _settings.EffectiveOneDriveClientId;
        // The label is filled at startup; if that failed, a sign-in may still be saved, so the store is asked before sending the user to sign in.
        _oneDriveAccount ??= (await _cloudAuth.GetSavedAccountAsync(CloudProvider.OneDrive, clientId))?.DisplayName;
        if (_oneDriveAccount is null) { Navigate("settings"); ShowBanner(L.T("Journey.Cloud.ConnectTitle"), L.T("Journey.Cloud.ConnectHelp"), InfoBarSeverity.Informational); return; }
        await RunOperationAsync(async ct =>
        {
            var account = await _cloudAuth.GetSavedAccountAsync(CloudProvider.OneDrive, clientId, ct);
            if (account is null) throw new InvalidOperationException(L.T("Journey.Cloud.AccountRequired"));
            var service = new CloudUploadService();
            for (var index = 0; index < selected.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var file = selected[index];
                var current = index;
                var progress = new Progress<CloudUploadProgress>(p => SetProgress(p.Fraction * 100, L.T("Journey.Cloud.Progress", current + 1, selected.Length, Path.GetFileName(file.Path), FormatBytes(p.BytesUploaded), FormatBytes(p.TotalBytes))));
                await service.UploadAsync(new CloudUploadRequest(account, file.Path), progress, ct);
                _selectedExports.Remove(file.Path);
            }
            ShowBanner(L.T("Journey.Cloud.CompleteTitle"), L.Plural("Journey.Cloud.CompleteMessage", selected.Length, "OneDrive"), InfoBarSeverity.Success);
        }, L.T("Journey.Cloud.CompleteStatus"));
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(L.T("Journey.Archive.FolderUnavailable"));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
