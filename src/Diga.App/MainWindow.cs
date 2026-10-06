using L = Diga.Core.Localization.AppText;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using Diga.Core.Storage;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Media.Playback;
using Microsoft.Windows.Storage.Pickers;

namespace Diga.App;

/// <summary>Native Windows shell for read-only disk extraction and DLNA downloads.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Grid _root = new();
    private readonly NavigationView _navigation = new();
    private readonly Grid _pageHost = new();
    private readonly ContentControl _pageContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly InfoBar _banner = new() { IsOpen = false, IsClosable = true };
    private readonly TextBlock _status = new() { Text = L.T("Shell.Ready"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 220, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = L.T("Shell.Cancel"), Visibility = Visibility.Collapsed };
    private readonly List<DiskSource> _disks = [];
    private readonly Dictionary<string, RecordingFile> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cached = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ownedCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stagedCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExportedFile> _exports = [];
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private MediaPlayer? _player;
    private PanasonicFileSystem? _fileSystem;
    private CancellationTokenSource? _operation;
    private string _page = "order";
    private string _imagePath = "";
    private string _search = "";
    private string? _focusedRecordingId;
    private string? _diskId;
    private string _sourceInfo = L.T("Shell.SourceInfo");
    private bool _busy;
    private bool _closed;
    private bool _closeRequested;
    private bool _showAllDisks;
    private bool _wizard = true;
    private bool _refreshingList;
    private TextBox? _sourceInfoBox;
    private TextBlock? _selectionText;
    private ListView? _recordingList;
    private MediaPlayerElement? _mediaElement;
    private ContentControl? _previewHost;
    private string? _previewUnavailable;

    public MainWindow()
    {
        _root.Language = AppLocalization.LanguageTag;
        Title = "AMG DIGA Archive";
        SystemBackdrop = new MicaBackdrop();
        SizeWindowForDisplay();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Diga.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        BuildShell();
        Content = _root;
        Activated += OnFirstActivated;
        AppWindow.Closing += (_, args) =>
        {
            if (!_busy) return;
            // Keep the message loop alive until readers, FFmpeg and uploads finish
            // their cancellation cleanup. Disposing the window sooner can orphan a child.
            args.Cancel = true;
            _closeRequested = true;
            _operation?.Cancel();
            _status.Text = L.T("Shell.Stopping");
            AnnounceStatus(true);
            _cancel.IsEnabled = false;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _operation?.Cancel();
            ReleasePreviewPlayer();
            _fileSystem?.Dispose();
            Application.Current.Exit();
        };
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        // Every launch starts maximized; the size set in the constructor is what Restore returns to.
        // Maximizing here, not in the constructor, keeps this handler the first to see the window activate
        // (Maximize itself shows and activates a window) and maximizes a window that already has its content.
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        await RunOperationAsync(async ct =>
        {
            await LoadSettingsAsync(ct);
            await RefreshDisksAsync(ct);
        }, L.T("Shell.OrderStart"));
    }

    private void BuildShell()
    {
        _root.Style = AppStyle("DigaCanvasStyle");
        _pageHost.Style = AppStyle("DigaCanvasStyle");
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _navigation.OpenPaneLength = 248;
        _navigation.ExpandedModeThresholdWidth = 1100;
        _navigation.CompactModeThresholdWidth = 720;
        _navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _navigation.IsSettingsVisible = false;
        _navigation.AlwaysShowHeader = false;
        var paneHeader = BrandHeader();
        _navigation.PaneHeader = paneHeader;
        AddNavigation("order", L.T("Shell.Order"), Symbol.Tag);
        AddNavigation("source", L.T("Shell.Connect"), Symbol.Save);
        AddNavigation("library", L.T("Shell.Discover"), Symbol.Video);
        AddNavigation("export", L.T("Shell.Preserve"), Symbol.SaveLocal);
        AddNavigation("complete", L.T("Shell.Archive"), Symbol.Library);
        _navigation.FooterMenuItems.Add(new NavigationViewItem { Content = L.T("Shell.Settings"), Tag = "settings", Icon = new SymbolIcon(Symbol.Setting) });
        var paneFooter = new StackPanel
        {
            Margin = new Thickness(20, 12, 16, 20), Spacing = 8,
            Children = { StageBadge(L.T("Shell.NoEncoding"), "Teal"), Muted(L.T("Shell.Footer"), 12), Muted(L.T("Shell.ReadOnly"), 11) }
        };
        _navigation.PaneFooter = paneFooter;
        void UpdatePaneChrome()
        {
            // Custom branding and prose need the expanded pane's width. Leave the
            // compact rail to NavigationView's native menu toggle and item icons.
            var visibility = _navigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            paneHeader.Visibility = visibility;
            paneFooter.Visibility = visibility;
            foreach (var item in _navigation.MenuItems.OfType<NavigationViewItem>())
                if (item.Content is FrameworkElement content) content.Visibility = visibility;
        }
        _navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePaneChrome());
        UpdatePaneChrome();
        _navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string page)
            {
                _focusPageAfterRender = true;
                _page = page;
                RenderPage();
            }
        };
        _pageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _banner.Margin = new Thickness(26, 14, 26, 0);
        _pageHost.Children.Add(_banner);
        Grid.SetRow(_pageContent, 1);
        _pageHost.Children.Add(_pageContent);
        _navigation.Content = _pageHost;
        _root.Children.Add(_navigation);

        _status.FontSize = 12;
        _status.Style = AppStyle("DigaMutedTextStyle");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, L.T("Shell.Progress"));
        var statusBar = new Grid { Padding = new Thickness(24, 10, 24, 10), ColumnSpacing = 14, Style = AppStyle("DigaStatusBarStyle") };
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_progress, 1);
        Grid.SetColumn(_cancel, 2);
        _cancel.Click += (_, _) => { _operation?.Cancel(); _cancel.IsEnabled = false; _status.Text = L.T("Shell.Cancelling"); AnnounceStatus(true); };
        statusBar.Children.Add(_status);
        statusBar.Children.Add(_progress);
        statusBar.Children.Add(_cancel);
        Grid.SetRow(statusBar, 1);
        _root.Children.Add(statusBar);
        Navigate("order");
    }

    private void AddNavigation(string page, string label, Symbol icon)
    {
        var state = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _navigationStates[page] = state;
        var item = new NavigationViewItem
        {
            Content = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 6), Children =
            {
                new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, state
            } }, Tag = page, Icon = new SymbolIcon(icon)
        };
        AutomationProperties.SetName(item, label);
        ToolTipService.SetToolTip(item, label);
        _navigation.MenuItems.Add(item);
    }

    private void Navigate(string page)
    {
        _focusPageAfterRender = true;
        _page = page;
        var item = _navigation.MenuItems.Concat(_navigation.FooterMenuItems).OfType<NavigationViewItem>().First(i => Equals(i.Tag, page));
        if (ReferenceEquals(_navigation.SelectedItem, item)) RenderPage();
        else _navigation.SelectedItem = item;
    }

    private void RenderPage()
    {
        if (_closed) return;
        _sourceInfoBox = null;
        _stageHeading = null;
        _pageFocusTarget = null;
        _recordingList = null;
        _selectionText = null;
        DetachPreviewElement();
        _previewHost = null;
        var stack = new StackPanel { Spacing = 20, Margin = new Thickness(26, 24, 26, 28), MaxWidth = 1300, HorizontalAlignment = HorizontalAlignment.Stretch };
        _journeyHost = null;
        if (_page != "settings")
        {
            _journeyHost = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            var journey = _journeyHost;
            journey.SizeChanged += (_, args) => ArrangeGrid(journey, JourneyColumns(args.NewSize.Width));
            stack.Children.Add(journey);
        }
        RefreshJourney();
        switch (_page)
        {
            case "order": BuildOrderPage(stack); break;
            case "library": BuildLibraryPage(stack); break;
            case "export": BuildExportPage(stack); break;
            case "complete": BuildCompletePage(stack); break;
            case "settings": BuildSettingsPage(stack); break;
            default: BuildSourcePage(stack); break;
        }
        _pageContent.Content = new ScrollViewer { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (_focusPageAfterRender && _stageHeading is { } heading)
        {
            _focusPageAfterRender = false;
            // A form page starts in its input; every other page starts at its heading.
            var input = _pageFocusTarget;
            stack.Loaded += (_, _) => { if (input is not null) input.Focus(FocusState.Programmatic); else heading.Focus(FocusState.Programmatic); };
        }
    }

    private void BuildSourcePage(StackPanel page)
    {
        AddHeading(page, L.T("Shell.ConnectTitle"), L.T("Shell.ConnectDescription"), L.T("Shell.ConnectStep"));
        page.Children.Add(BuildDlnaConnectCard());

        var disks = new StackPanel { Spacing = 14 };
        disks.Children.Add(StageBadge(L.T("Shell.FromRecorder"), "Teal"));
        disks.Children.Add(SectionTitle(L.T("Shell.UsbTitle"), Symbol.Save));
        disks.Children.Add(Muted(L.T("Shell.UsbDescription"), 13));
        var diskPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = _showAllDisks ? L.T("Shell.ChoosePhysical") : L.T("Shell.ChooseUsb"), MinHeight = 40 };
        AutomationProperties.SetName(diskPicker, L.T("Shell.RecorderDisk"));
        foreach (var disk in _disks.Where(d => _showAllDisks || d.IsUsb))
            diskPicker.Items.Add(new ComboBoxItem { Content = $"{disk.DisplayName}  ·  {FormatBytes(disk.SizeBytes)}  ·  {disk.Path}", Tag = disk.Id });
        diskPicker.SelectedItem = diskPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _diskId));
        _diskId = (diskPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        diskPicker.SelectionChanged += (_, _) => _diskId = (diskPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        disks.Children.Add(diskPicker);
        var openDisk = ActionButton(L.T("Shell.ConnectDisk"), async () =>
        {
            var selected = _disks.FirstOrDefault(d => d.Id == _diskId);
            if (selected is null) { ShowBanner(L.T("Shell.ChooseDisk"), L.T("Shell.ChooseDiskHelp"), InfoBarSeverity.Informational); return; }
            await OpenSourceAsync(selected.Path);
        }, true);
        disks.Children.Add(ActionRow(openDisk, ActionButton(L.T("Shell.RefreshDisks"), () => RunOperationAsync(RefreshDisksAsync, L.T("Shell.DisksUpdated")))));
        var visibleDiskCount = _disks.Count(d => _showAllDisks || d.IsUsb);
        disks.Children.Add(Muted(visibleDiskCount == 0 ? L.T("Shell.NoDisks") : L.T("Shell.DisksAvailable", visibleDiskCount), 12));
        var options = new CheckBox { Content = new TextBlock { Text = L.T("Shell.AllDisks"), TextWrapping = TextWrapping.Wrap }, IsChecked = _showAllDisks, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(options, L.T("Shell.AllDisks"));
        options.Click += (_, _) =>
        {
            _showAllDisks = options.IsChecked == true;
            _diskOptionsExpanded = true;
            _diskOptionsFocusAfterRender = options.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
            // A filter toggle stays at its checkbox instead of restarting page navigation.
            _focusPageAfterRender = false;
            RenderPage();
        };
        if (_diskOptionsFocusAfterRender is { } restoreFocus)
        {
            _diskOptionsFocusAfterRender = null;
            options.Loaded += (_, _) =>
            {
                if (!_closed && _page == "source" && options.IsLoaded) options.Focus(restoreFocus);
            };
        }
        var advanced = new StackPanel { Spacing = 10, Children = { options, Muted(IsAdministrator() ? L.T("Shell.AdminEnabled") : L.T("Shell.AdminHelp"), 12) } };
        if (!IsAdministrator()) advanced.Children.Add(ActionButton(L.T("Shell.RestartAdmin"), RestartElevatedAsync));
        var diskOptions = new Expander { Header = JourneyHeader(L.T("Shell.DiskOptions")), Content = advanced, IsExpanded = _diskOptionsExpanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        diskOptions.Expanding += (_, _) => _diskOptionsExpanded = true;
        diskOptions.Collapsed += (_, _) => _diskOptionsExpanded = false;
        disks.Children.Add(diskOptions);

        var image = new StackPanel { Spacing = 14 };
        image.Children.Add(StageBadge(L.T("Shell.FromCopy"), "Violet"));
        image.Children.Add(SectionTitle(L.T("Shell.ImageTitle"), Symbol.OpenFile));
        image.Children.Add(Muted(L.T("Shell.ImageHelp"), 13));
        var imageInput = new TextBox { Text = _imagePath, PlaceholderText = L.T("Shell.ImagePlaceholder"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(imageInput, L.T("Shell.ImagePath"));
        imageInput.TextChanged += (_, _) => _imagePath = imageInput.Text.Trim();
        image.Children.Add(imageInput);
        var browseImage = ActionButton(L.T("Shell.Browse"), async () => { var file = await PickFileAsync([".img", ".dd", ".raw", ".bin", ".iso", "*"]); if (file is not null) { _imagePath = file.Path; imageInput.Text = _imagePath; } });
        var openImage = ActionButton(L.T("Shell.OpenImage"), async () =>
        {
            if (string.IsNullOrWhiteSpace(_imagePath)) { ShowBanner(L.T("Shell.ChooseImage"), L.T("Shell.ChooseImageHelp"), InfoBarSeverity.Informational); return; }
            await OpenSourceAsync(_imagePath);
        });
        image.Children.Add(ActionRow(browseImage, openImage));
        image.Children.Add(Muted(L.T("Shell.ImageReadOnly"), 12));
        page.Children.Add(ResponsivePair(Card(disks), Card(image), 740));
        page.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Title = L.T("Shell.KeepDisk"),
            Message = L.T("Shell.KeepDiskHelp")
        });
        if (_fileSystem is not null)
        {
            var connected = new StackPanel { Spacing = 10, Children = { StageBadge(L.T("Shell.SourceConnected")), Body(L.T("Shell.FilesReady", _fileSystem.Recordings.Count)), Muted(_fileSystem.SourcePath, 12), ActionButton(L.T("Shell.ContinueDiscover"), () => { Navigate("library"); return Task.CompletedTask; }, true) } };
            page.Children.Add(TintCard(connected));
        }
        page.Children.Add(Muted(L.T("Shell.Compatibility"), 12));
    }

    private async Task RefreshDisksAsync(CancellationToken ct)
    {
        SetProgress(null, L.T("Shell.LookingDisks"));
        var disks = await DiskDiscovery.GetDisksAsync(ct);
        _disks.Clear();
        _disks.AddRange(disks);
        if (_diskId is null) _diskId = _disks.FirstOrDefault(d => d.IsUsb)?.Id;
    }

    private async Task OpenSourceAsync(string path)
    {
        await RunOperationAsync(async ct =>
        {
            SetProgress(null, L.T("Shell.ReadingFilesystem"));
            var report = new Progress<ScanProgress>(p => SetProgress(p.TotalBytes > 0 ? 100d * p.BytesScanned / p.TotalBytes : null, p.Message));
            var fileSystem = await PanasonicReader.OpenAsync(path, report, ct);
            if (_player is not null) _player.Source = null;
            ResetDlnaSource();
            _fileSystem?.Dispose();
            _fileSystem = fileSystem;
            _exportsAtSourceOpen = _exports.Count;
            _selected.Clear();
            _cached.Clear();
            _focusedRecordingId = null;
            _sourceInfo = L.T("Shell.SourceInfo");
            _search = "";
            if (fileSystem.Warnings.Count != 0) ShowBanner(L.T("Shell.SourceNotes"), string.Join("\n", fileSystem.Warnings), InfoBarSeverity.Warning);
            else ShowBanner(L.T("Shell.SourceConnected"), L.T("Shell.FilesFound", fileSystem.Recordings.Count), InfoBarSeverity.Success);
            if (_wizard) Navigate("library");
        }, L.T("Shell.SourceReady"));
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, string success)
    {
        if (_busy || _closed) return;
        _busy = true;
        _operation = new CancellationTokenSource();
        _navigation.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        _cancel.Visibility = Visibility.Visible;
        _cancel.IsEnabled = true;
        try
        {
            await operation(_operation.Token);
            if (!_closed) _status.Text = success;
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L.T("Shell.Cancelled"); }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _status.Text = L.T("Shell.OperationFailed");
                var detail = ex.Message;
                if (ex is UnauthorizedAccessException) detail += HasDlnaSource
                    ? L.T("Shell.NetworkAccessHelp")
                    : L.T("Shell.DiskAccessHelp");
                ShowBanner(L.T("Shell.ActionNeeded"), detail, InfoBarSeverity.Error);
            }
        }
        finally
        {
            _busy = false;
            _operation?.Dispose();
            _operation = null;
            if (!_closed)
            {
                AnnounceStatus(true);
                _navigation.IsEnabled = true;
                _progress.Visibility = Visibility.Collapsed;
                _cancel.Visibility = Visibility.Collapsed;
                if (_closeRequested) Close();
                else { _focusPageAfterRender = true; RenderPage(); }
            }
        }
    }

    private void SetProgress(double? percent, string message)
    {
        if (_closed) return;
        _progress.IsIndeterminate = !percent.HasValue;
        if (percent.HasValue) _progress.Value = Math.Clamp(percent.Value, 0, 100);
        _status.Text = message;
        AnnounceStatus();
    }

    private void ShowBanner(string title, string message, InfoBarSeverity severity)
    {
        if (_closed) return;
        _banner.Title = title;
        _banner.Message = message;
        _banner.Severity = severity;
        _banner.IsOpen = true;
        AutomationProperties.SetLiveSetting(_banner, severity == InfoBarSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_banner, $"{title}. {message}");
        (FrameworkElementAutomationPeer.FromElement(_banner) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_banner))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private async Task<PickFileResult?> PickFileAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return await picker.PickSingleFileAsync();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task RestartElevatedAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = L.T("Shell.AdminTitle"),
            Content = L.T("Shell.AdminDescription"),
            PrimaryButtonText = L.T("Shell.Restart"), CloseButtonText = L.T("Shell.Stay"), DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _status.Text = L.T("Shell.AdminCancelled"); }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Style AppStyle(string name) => (Style)Application.Current.Resources[name];
    private static TextBlock Body(string text) => new() { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Style = AppStyle("DigaTitleTextStyle") };
    private static TextBlock Muted(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaMutedTextStyle") };
    private static Border Card(UIElement content, bool featured = false)
    {
        if (featured) ApplyAccentText(content);
        return new Border
        {
            Child = content, Padding = new Thickness(22), CornerRadius = new CornerRadius(16),
            Style = AppStyle(featured ? "DigaFeaturedCardStyle" : "DigaCardStyle"), BorderThickness = new Thickness(1)
        };
    }
    private static void ApplyAccentText(UIElement element)
    {
        if (element is TextBlock text) text.Style = AppStyle("DigaAccentTextStyle");
        if (element is Panel panel) foreach (var child in panel.Children) ApplyAccentText(child);
        if (element is Border border && border.Child is not null) ApplyAccentText(border.Child);
    }
    private static Border Pill(string text) => new()
    {
        Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(5), Style = AppStyle("DigaPillStyle"),
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaTealTextStyle") }
    };
    private static Grid SectionTitle(string text, Symbol symbol)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new SymbolIcon(symbol) { Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Style = AppStyle("DigaTealIconStyle") });
        var label = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaTitleTextStyle") };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    }
    private void AddHeading(StackPanel page, string title, string subtitle, string eyebrow)
    {
        var heading = new StackPanel { Spacing = 8 };
        var tone = eyebrow.StartsWith("00") ? "Coral" : eyebrow.StartsWith("02") ? "Violet" : eyebrow.StartsWith("03") ? "Amber" : eyebrow.StartsWith("04") ? "Coral" : "Teal";
        heading.Children.Add(StageBadge(eyebrow, tone));
        var titleText = new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaTitleTextStyle"), IsTabStop = true };
        _stageHeading = titleText;
        AutomationProperties.SetHeadingLevel(titleText, AutomationHeadingLevel.Level1);
        heading.Children.Add(titleText);
        heading.Children.Add(Muted(subtitle, 14));
        page.Children.Add(heading);
    }
    private Button ActionButton(string label, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }, Padding = new Thickness(16, 10, 16, 10), MinHeight = 42, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(button, label);
        if (accent) button.Style = AppStyle("DigaAccentButtonStyle");
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowBanner(L.T("Shell.ActionNeeded"), ex.Message, InfoBarSeverity.Error); }
        };
        return button;
    }
    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.0} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{Math.Max(0, bytes) / 1024d:0.0} KB";
    }
    private bool HasRecordingSource => _fileSystem is not null || HasDlnaSource;

    private void PrepareForDlnaSource()
    {
        if (_player is not null) _player.Source = null;
        _fileSystem?.Dispose();
        _fileSystem = null;
        _selected.Clear();
        _cached.Clear();
        _focusedRecordingId = null;
        _sourceInfo = L.T("Shell.NetworkSourceInfo");
        _search = "";
        ResetDlnaSource();
        _exportsAtSourceOpen = _exports.Count;
        // Keep the exact cache ownership registry across sources for safe later cleanup.
    }

    private sealed record ExportedFile(string SourceName, string Path, string SourceInformation, string DestinationInformation,
        string VerificationSummary)
    {
        public ExportedFile(string sourceName, string path, string sourceInformation, string destinationInformation)
            : this(sourceName, path, sourceInformation, destinationInformation, L.T("Shell.ExportVerified")) { }
    }
}
