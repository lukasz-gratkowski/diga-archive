using L = Diga.Core.Localization.AppText;
using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Media.Playback;
using Microsoft.Windows.Storage.Pickers;

namespace Diga.App;

/// <summary>Native Windows shell for saving recordings from a network recorder and uploading the copies.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Grid _root = new();
    private readonly NavigationView _navigation = new();
    private readonly Grid _pageHost = new();
    private readonly ContentControl _pageContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly InfoBar _banner = new() { IsOpen = false, IsClosable = true };
    // A row under the page that does not scroll with it; the Settings page keeps its Save button there.
    private readonly ContentControl _pageFooter = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    private readonly TextBlock _status = new() { Text = L.T("Shell.Ready"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 220, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = L.T("Shell.Cancel"), Visibility = Visibility.Collapsed };
    private readonly HashSet<string> _ownedCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stagedCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExportedFile> _exports = [];
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private MediaPlayer? _player;
    private CancellationTokenSource? _operation;
    private string _page = "order";
    private bool _busy;
    private bool _closed;
    private bool _closeRequested;
    private bool _wizard = true;
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
        AppWindow.Closing += async (_, args) =>
        {
            if (!_busy) return;
            // Keep the message loop alive until readers, FFmpeg and uploads finish
            // their cancellation cleanup. Disposing the window sooner can orphan a child.
            args.Cancel = true;
            if (_closeRequested) return;
            // A download or an upload can run for hours and cannot be resumed, so one click on X must not end it.
            if (!await ConfirmAsync(L.T("Shell.Close.Title"), L.T("Shell.Close.Message", _status.Text), L.T("Shell.Close.Stop"), L.T("Shell.Close.Keep"))) return;
            if (!_busy) { Close(); return; }
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
            RemoveSessionCache();
            Application.Current.Exit();
        };
    }

    private bool _dialogOpen;

    /// <summary>Asks a yes/no question; the safe answer is the default button. False when another question is already open.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string confirm, string keep)
    {
        if (_dialogOpen || _closed || _root.XamlRoot is null) return false;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot, Language = AppLocalization.LanguageTag, RequestedTheme = _root.ActualTheme,
                Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirm, CloseButtonText = keep, DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        // Every launch starts maximized; the size set in the constructor is what Restore returns to.
        // Maximizing here, not in the constructor, keeps this handler the first to see the window activate
        // (Maximize itself shows and activates a window) and maximizes a window that already has its content.
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        await RunOperationAsync(LoadSettingsAsync, L.T("Shell.Ready"));
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
        // Not the pane's header: that shares a row with the menu button, which pushes the block to the right of it and out of
        // line with the menu. As custom content it gets the row below, from the pane's left edge.
        var paneHeader = BrandHeader();
        _navigation.PaneCustomContent = paneHeader;
        AddNavigation("order", L.T("Shell.Order"), new SymbolIcon(Symbol.Tag));
        // In the narrow window only the icons show, so each has to be recognisable on its own: a network link here, a cloud below.
        AddNavigation("source", L.T("Shell.Connect"), new FontIcon { Glyph = "\uE703" });
        AddNavigation("library", L.T("Shell.Discover"), new SymbolIcon(Symbol.Video));
        AddNavigation("export", L.T("Shell.Preserve"), new SymbolIcon(Symbol.SaveLocal));
        AddNavigation("complete", L.T("Shell.Archive"), new SymbolIcon(Symbol.Library));
        // The cloud view is not a stage of the journey: it is reachable at any time, like Settings.
        _navigation.FooterMenuItems.Add(new NavigationViewItem { Content = L.T("Shell.Cloud"), Tag = "cloud", Icon = new FontIcon { Glyph = "\uE753" } });
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
                LeaveMessagesBehind();
                RenderPage();
            }
        };
        _pageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _banner.Margin = new Thickness(26, 14, 26, 0);
        _pageHost.Children.Add(_banner);
        _pageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_pageContent, 1);
        _pageHost.Children.Add(_pageContent);
        Grid.SetRow(_pageFooter, 2);
        _pageHost.Children.Add(_pageFooter);
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
        // The status line is cut after two lines; its tooltip holds the whole of whatever it says now, not only progress texts.
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(_status, _status.Text));
        statusBar.Children.Add(_status);
        statusBar.Children.Add(_progress);
        statusBar.Children.Add(_cancel);
        Grid.SetRow(statusBar, 1);
        _root.Children.Add(statusBar);
        Navigate("order");
    }

    private void AddNavigation(string page, string label, IconElement icon)
    {
        // Two lines at most: a long order number ends in an ellipsis instead of pushing the entries below it down as it is typed.
        var state = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        _navigationStates[page] = state;
        var item = new NavigationViewItem
        {
            Content = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 6), Children =
            {
                new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, state
            } }, Tag = page, Icon = icon
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
        if (ReferenceEquals(_navigation.SelectedItem, item)) { LeaveMessagesBehind(); RenderPage(); }
        else _navigation.SelectedItem = item;
    }

    /// <summary>
    /// A message belongs to the page and the action that raised it. Moving to another page by hand closes it; a move made
    /// by a running operation keeps the message that operation is about to show or has just shown.
    /// </summary>
    private void LeaveMessagesBehind()
    {
        if (_busy) return;
        _banner.IsOpen = false;
        _status.Text = L.T("Shell.Ready");
    }

    private ScrollViewer? _pageScroller;
    private string? _renderedPage;
    // The control that gets the focus after the next rebuild: its accessible name, and which one of several with that name.
    private (string Name, int Ordinal)? _returnFocusTo;

    /// <summary>Rebuilds the current page where the user is: same scroll position, focus back on the control they used.</summary>
    private void RenderPageInPlace((string Name, int Ordinal)? focus = null)
    {
        _focusPageAfterRender = false;
        _returnFocusTo = focus;
        RenderPage(keepPlace: true);
    }

    private void RenderPageInPlace(string focusName) => RenderPageInPlace((focusName, 0));

    /// <summary>Identifies the focused control for the page that will replace this one. Two controls may share a name (a button repeated in each card).</summary>
    private (string Name, int Ordinal)? FocusedControlKey()
    {
        if (_root.XamlRoot is null || FocusManager.GetFocusedElement(_root.XamlRoot) is not Control focused) return null;
        var name = AutomationProperties.GetName(focused);
        if (string.IsNullOrEmpty(name) || _pageScroller is null) return null;
        var sameName = NamedOnPage(_pageScroller, name);
        var ordinal = sameName.IndexOf(focused);
        return ordinal < 0 ? null : (name, ordinal);
    }

    /// <summary>The controls of that name in the page and, after them, in the bar below it (Save preferences), which belongs to the page although it does not scroll with it.</summary>
    private List<Control> NamedOnPage(DependencyObject page, string name)
    {
        var found = new List<Control>();
        CollectByAutomationName(page, name, found, enabledOnly: false);
        if (_pageFooter.Content is DependencyObject bar)
        {
            if (bar is Control { IsTabStop: true } control && AutomationProperties.GetName(control) == name) found.Add(control);
            CollectByAutomationName(bar, name, found, enabledOnly: false);
        }
        return found;
    }

    private static void CollectByAutomationName(DependencyObject parent, string name, List<Control> found, bool enabledOnly)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Control { IsTabStop: true } control && (!enabledOnly || control.IsEnabled) && AutomationProperties.GetName(control) == name) found.Add(control);
            CollectByAutomationName(child, name, found, enabledOnly);
        }
    }

    private Control? FindByAutomationName(DependencyObject parent, (string Name, int Ordinal) key)
    {
        var sameName = NamedOnPage(parent, key.Name);
        var match = key.Ordinal < sameName.Count ? sameName[key.Ordinal] : sameName.FirstOrDefault();
        return match is { IsEnabled: true } ? match : null;
    }

    private void RenderPage(bool keepPlace = false)
    {
        if (_closed) return;
        var offset = keepPlace && _renderedPage == _page && _pageScroller is not null ? _pageScroller.VerticalOffset : 0;
        var returnFocusTo = _returnFocusTo;
        _returnFocusTo = null;
        // What was typed on the page being replaced is handed to the page that replaces it.
        _leavingPage?.Invoke();
        _leavingPage = null;
        _renderedPage = _page;
        _pageFooter.Content = null;
        // Being sent to Settings for one service lasts as long as the visit to Settings.
        if (_page != "settings") (_cloudAskedFor, _cloudReturnPage) = (null, null);
        _stageHeading = null;
        _pageFocusTarget = null;
        DetachPreviewElement();
        _previewHost = null;
        var stack = new StackPanel { Spacing = 20, Margin = new Thickness(26, 24, 26, 28), MaxWidth = 1300, HorizontalAlignment = HorizontalAlignment.Stretch };
        _journeyHost = null;
        if (_page is not ("settings" or "cloud"))
        {
            _journeyHost = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            var journey = _journeyHost;
            journey.SizeChanged += (_, args) => ArrangeGrid(journey, JourneyColumns(args.NewSize.Width));
            stack.Children.Add(journey);
        }
        RefreshJourney();
        try
        {
            switch (_page)
            {
                case "order": BuildOrderPage(stack); break;
                case "library": BuildLibraryPage(stack); break;
                case "export": BuildDlnaExportPage(stack); break;
                case "complete": BuildCompletePage(stack); break;
                case "cloud": BuildCloudPage(stack); break;
                case "settings": BuildSettingsPage(stack); break;
                default: BuildSourcePage(stack); break;
            }
        }
        // A fault while a page is being put together must not end the application, least of all after work that ran for hours:
        // what was saved is still listed on the other pages.
        catch (Exception exception)
        {
            App.LogException("Building the page " + _page, exception);
            stack.Children.Clear();
            _journeyHost = null;
            stack.Children.Add(Card(new StackPanel { Spacing = 14, Children =
            {
                Body(L.T("Shell.PageFailed", Describe(exception))),
                ActionButton(L.T("Shell.PageFailedBack"), () => { Navigate("order"); return Task.CompletedTask; })
            } }));
        }
        var scroller = new ScrollViewer { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _pageScroller = scroller;
        _pageContent.Content = scroller;
        var focusHeading = _focusPageAfterRender || keepPlace;
        _focusPageAfterRender = false;
        var heading = _stageHeading;
        // A form page starts in its input; every other page starts at its heading.
        var input = _pageFocusTarget;
        // Sound must not go on playing on a page that has no player to stop it.
        if (_page != "library") PausePreview();
        stack.Loaded += (_, _) =>
        {
            // A named control wins: the one the user worked with, or the one a message points to. Giving it the focus scrolls it into
            // view, so the earlier scroll position is put back only when no control was found (its button is gone, or was renamed).
            if (returnFocusTo is { } key && FindByAutomationName(stack, key) is { } target)
            {
                // First the page is put back where it was; a control that is then in view takes the focus without moving the page.
                if (offset > 0) scroller.ChangeView(null, offset, null, true);
                DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
                return;
            }
            if (focusHeading && input is not null && offset == 0) input.Focus(FocusState.Programmatic);
            else if (focusHeading) heading?.Focus(FocusState.Programmatic);
            if (offset > 0) DispatcherQueue.TryEnqueue(() => scroller.ChangeView(null, offset, null, true));
        };
    }

    /// <summary>Set by a page that holds typed values; called once, just before the page is replaced.</summary>
    private Action? _leavingPage;

    // Which sections the user opened or closed. A page is rebuilt after every action, and it must look as it was left:
    // the section with the button just pressed stays open, and the focus can return to that button.
    private readonly Dictionary<string, bool> _sections = new(StringComparer.Ordinal);

    private Expander Section(string key, string title, UIElement content, bool openByDefault = false)
    {
        var section = new Expander
        {
            Header = JourneyHeader(title), Content = content, IsExpanded = _sections.TryGetValue(key, out var open) ? open : openByDefault,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        section.Expanding += (_, _) => _sections[key] = true;
        section.Collapsed += (_, _) => _sections[key] = false;
        return section;
    }

    private void BuildSourcePage(StackPanel page)
    {
        AddHeading(page, L.T("Shell.ConnectTitle"), L.T("Shell.ConnectDescription"), L.T("Shell.ConnectStep"));
        page.Children.Add(BuildDlnaConnectCard());
        page.Children.Add(BuildRecorderChecklist());
    }

    /// <summary>
    /// Runs one action of the window. <paramref name="success"/> is what the status line says when it returns;
    /// <paramref name="outcome"/> can name something else for an action that returned without doing what it set out to do.
    /// It is asked before the status is read out, so that what is heard is what happened.
    /// </summary>
    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, string success, Func<string?>? outcome = null)
    {
        if (_busy || _closed) return;
        _busy = true;
        // The page is disabled while the work runs, which takes the focus away from the control that started it.
        var startedFrom = FocusedControlKey();
        PausePreview();
        var startedOn = _page;
        // A message from an earlier action must not be read as the outcome of this one.
        _banner.IsOpen = false;
        var operationSource = _operation = new CancellationTokenSource();
        _navigation.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        _cancel.Visibility = Visibility.Visible;
        _cancel.IsEnabled = true;
        // Downloads and uploads run for hours; Windows would otherwise put an untouched PC to sleep in the middle of one.
        SetThreadExecutionState(ExecutionContinuous | ExecutionSystemRequired);
        try
        {
            await operation(operationSource.Token);
            if (!_closed) _status.Text = outcome?.Invoke() ?? success;
        }
        // Only the user's own Cancel (or closing the window) is a cancellation; a time limit that ran out is a failure with a reason.
        catch (OperationCanceledException) when (operationSource.IsCancellationRequested) { if (!_closed) _status.Text = L.T("Shell.Cancelled"); }
        catch (Exception ex)
        {
            App.LogException("Operation", ex);
            if (!_closed)
            {
                _status.Text = L.T("Shell.OperationFailed");
                ShowBanner(L.T("Shell.ActionNeeded"), Describe(ex), InfoBarSeverity.Error);
            }
        }
        finally
        {
            SetThreadExecutionState(ExecutionContinuous);
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
                else if (_page == startedOn) RenderPageInPlace(startedFrom);
                else { _focusPageAfterRender = true; RenderPage(); }
            }
        }
    }

    private const uint ExecutionContinuous = 0x80000000, ExecutionSystemRequired = 0x00000001;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>What a failed action tells the user: the reason in the application's language, without a tool's whole output.</summary>
    private static string Describe(Exception exception)
    {
        var detail = exception switch
        {
            OperationCanceledException => L.T("Shell.TimedOut"),
            // Raised by Windows networking itself (no connection, no name resolution, a connection that broke in the middle of a
            // request), in the system's wording. A request that got an answer carries its status code and is not one of these.
            // A sentence the library wrote about a failed connection is shown as it is, further down; it is not wrapped in a second one.
            System.Net.Http.HttpRequestException { StatusCode: null } or System.Net.Sockets.SocketException
                when !L.IsCatalogText(exception.Message) => L.T("Shell.NetworkError", exception.Message),
            // Windows words these in the language of the .NET runtime, which is English whatever the language of this window.
            // Only what Windows raised names a path in quotation marks; the application's own messages pass through as they are.
            UnauthorizedAccessException when PathIn(exception) is { } path => L.T("Shell.Error.AccessDenied", path) + L.T("Shell.AccessHelp"),
            UnauthorizedAccessException when L.IsCatalogText(exception.Message) => exception.Message + L.T("Shell.AccessHelp"),
            DirectoryNotFoundException or DriveNotFoundException when PathIn(exception) is { } path => L.T("Shell.Error.FolderMissing", path),
            FileNotFoundException when PathIn(exception) is { } path => L.T("Shell.Error.FileMissing", path),
            IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } => L.T("Shell.Error.FileInUse", exception.Message),
            IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) } => L.T("Shell.Error.DiskFull", exception.Message),
            // What the application itself wrote is one of the catalogue's sentences and already in the chosen language. .NET adds the
            // name of a parameter to some of them, which says nothing to a user.
            ArgumentException { ParamName.Length: > 0 } when L.IsCatalogText(exception.Message) && exception.Message.LastIndexOf(" (Parameter '", StringComparison.Ordinal) is > 0 and var cut => exception.Message[..cut],
            _ when L.IsCatalogText(exception.Message) => exception.Message,
            // Everything else was written by the system or a library, in English whatever the language of this window: it is shown
            // as the technical detail of a sentence that is in that language.
            IOException => L.T("Shell.Error.Storage", exception.Message),
            _ => L.T("Shell.Error.Unexpected", exception.Message)
        };
        return detail.Length <= 900 ? detail : detail[..900].TrimEnd() + "…";
    }

    // The path Windows names inside its own message, between the quotation marks; null when the message names none.
    private static string? PathIn(Exception exception)
    {
        var message = exception.Message;
        var start = message.IndexOf('\'');
        var end = message.LastIndexOf('\'');
        if (start < 0 || end <= start + 1) return null;
        var path = message[(start + 1)..end];
        return Path.IsPathRooted(path) ? path : null;
    }

    /// <summary>Marks text that changes in answer to what the user does, so that a screen reader says the new text without moving focus.</summary>
    private static TextBlock Live(TextBlock text)
    {
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        return text;
    }

    private static void SayChanged(TextBlock text) =>
        (FrameworkElementAutomationPeer.FromElement(text) ?? FrameworkElementAutomationPeer.CreatePeerForElement(text))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    /// <summary>What a long transfer adds to its progress line: how fast it goes and about how long it still takes. Empty until there is something to go by.</summary>
    private static string PaceText(long done, long total, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 5 || done <= 0 || total <= done) return "";
        var perSecond = done / elapsed.TotalSeconds;
        var left = TimeSpan.FromSeconds((total - done) / perSecond);
        var time = left.TotalMinutes < 1.5 ? L.T("Journey.Progress.Minute")
            : left.TotalHours < 1 ? L.T("Journey.Progress.Minutes", (int)Math.Round(left.TotalMinutes))
            : L.T("Journey.Progress.Hours", (int)left.TotalHours, left.Minutes);
        return L.T("Journey.Progress.Pace", FormatBytes((long)perSecond), time);
    }

    private void SetProgress(double? percent, string message)
    {
        if (_closed) return;
        _progress.IsIndeterminate = !percent.HasValue;
        if (percent.HasValue) _progress.Value = Math.Clamp(percent.Value, 0, 100);
        _status.Text = message;
        AnnounceStatus();
    }

    private void ShowBanner(string title, string message, InfoBarSeverity severity, Microsoft.UI.Xaml.Controls.Primitives.ButtonBase? action = null)
    {
        if (_closed) return;
        _banner.Title = title;
        _banner.Message = message;
        _banner.Severity = severity;
        _banner.ActionButton = action;
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

    private static Style AppStyle(string name) => (Style)Application.Current.Resources[name];
    private static TextBlock Body(string text) => new() { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Style = AppStyle("DigaTitleTextStyle") };
    // 12 px is the smallest text Windows itself uses; explanatory notes must not be smaller.
    private static TextBlock Muted(string text, double size = 13) => new() { Text = text, FontSize = Math.Max(12, size), TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaMutedTextStyle") };
    private static Border Card(UIElement content)
    {
        return new Border
        {
            Child = content, Padding = new Thickness(22), CornerRadius = new CornerRadius(16),
            Style = AppStyle("DigaCardStyle"), BorderThickness = new Thickness(1)
        };
    }
    private static Grid SectionTitle(string text, Symbol symbol)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new SymbolIcon(symbol) { Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Style = AppStyle("DigaTealIconStyle") });
        var label = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaTitleTextStyle") };
        // Screen-reader users move between the sections of a long page by heading.
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
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
        AutomationProperties.SetName(button, SpokenLabel(label));
        if (accent) button.Style = AppStyle("DigaAccentButtonStyle");
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowBanner(L.T("Shell.ActionNeeded"), Describe(ex), InfoBarSeverity.Error); }
        };
        return button;
    }
    /// <summary>A label as a screen reader should say it: without the arrow that only decorates the visible text.</summary>
    private static string SpokenLabel(string label) => label.TrimEnd(' ', '\u2192', '\u2197');

    private void PausePreview()
    {
        try { if (_player?.PlaybackSession.CanPause == true) _player.Pause(); }
        catch (Exception ex) { App.LogException("Pausing the preview", ex); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.0} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{Math.Max(0, bytes) / 1024d:0.0} KB";
    }
    private void PrepareForDlnaSource()
    {
        if (_player is not null) _player.Source = null;
        ResetDlnaSource();
        _exportsAtSourceOpen = SavedCount;
        // Keep the exact cache ownership registry across sources for safe later cleanup.
    }

    /// <summary>The files saved from the recorder in this session; a file added by hand for an upload is listed with them but is not one of them.</summary>
    private int SavedCount => _exports.Count(export => !export.AddedByHand);

    private sealed record ExportedFile(string SourceName, string Path, string SourceInformation, string DestinationInformation,
        string VerificationSummary)
    {
        /// <summary>Chosen from this PC for an upload, not saved from the recorder in this session.</summary>
        public bool AddedByHand { get; init; }
        /// <summary>The uploads of this file made in this session, oldest first.</summary>
        public IReadOnlyList<CloudCopy> CloudCopies { get; init; } = [];
    }

    /// <summary>
    /// One upload of a saved file. <paramref name="Link"/> opens it in a browser: the file itself, or, for an upload into the
    /// shared folder set in Settings, that folder by its sharing link, whose name is then in <paramref name="Folder"/>.
    /// </summary>
    private sealed record CloudCopy(Diga.Core.Cloud.CloudProvider Provider, string Name, Uri? Link, DateTimeOffset At, string? Folder = null);
}
