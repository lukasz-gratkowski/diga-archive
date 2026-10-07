using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using L = Diga.Core.Localization.AppText;

namespace Diga.App;

public sealed partial class MainWindow
{
    private Grid? _journeyHost;
    private TextBlock? _stageHeading;
    private Control? _pageFocusTarget;
    private bool _focusPageAfterRender;
    private int _exportsAtSourceOpen;
    private DateTimeOffset _lastStatusAnnouncement;
    private string _lastStatusAnnounced = "";
    private readonly Dictionary<string, TextBlock> _navigationStates = [];

    private void SizeWindowForDisplay()
    {
        // AppWindow uses physical pixels; all XAML breakpoints use effective pixels.
        var scale = Math.Max(1, WindowDpi.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)Math.Round(24 * scale);
        var width = Math.Min((int)Math.Round(1280 * scale), Math.Max(600, work.Width - margin * 2));
        var height = Math.Min((int)Math.Round(880 * scale), Math.Max(450, work.Height - margin * 2));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(width, (int)Math.Round(900 * scale));
            presenter.PreferredMinimumHeight = Math.Min(height, (int)Math.Round(620 * scale));
        }
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2));
    }

    private static class WindowDpi
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);
    }

    private static string ValidTone(string tone) => tone is "Teal" or "Amber" or "Violet" or "Coral" ? tone : "Teal";

    private static Border TintCard(UIElement content, string tone = "Teal") => new()
    {
        Child = content, Padding = new Thickness(22), CornerRadius = new CornerRadius(16),
        BorderThickness = new Thickness(1), Style = AppStyle($"Diga{ValidTone(tone)}CardStyle")
    };

    private static Border StageBadge(string text, string tone = "Teal") => new()
    {
        Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(6),
        HorizontalAlignment = HorizontalAlignment.Left,
        Style = AppStyle($"Diga{ValidTone(tone)}CardStyle"),
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Style = AppStyle($"Diga{ValidTone(tone)}TextStyle") }
    };

    private static Grid ResponsivePair(UIElement left, UIElement right, double breakpoint = 760)
    {
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Children.Add(left);
        grid.Children.Add(right);
        ArrangeGrid(grid, 2);
        grid.SizeChanged += (_, args) => ArrangeGrid(grid, args.NewSize.Width >= breakpoint ? 2 : 1);
        return grid;
    }

    private static Grid ActionRow(params UIElement[] controls)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var control in controls) grid.Children.Add(control);
        ArrangeGrid(grid, Math.Max(1, controls.Length));
        grid.SizeChanged += (_, args) => ArrangeGrid(grid, args.NewSize.Width >= Math.Max(360, controls.Length * 165) ? Math.Max(1, controls.Length) : 1);
        return grid;
    }

    private static void ArrangeGrid(Grid grid, int columns)
    {
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count != 0) return;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Math.Max(1, (grid.Children.Count + columns - 1) / columns); i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < grid.Children.Count; i++)
            if (grid.Children[i] is FrameworkElement child)
            {
                Grid.SetColumn(child, i % columns);
                Grid.SetRow(child, i / columns);
            }
    }

    private FrameworkElement BrandHeader()
    {
        // The block is laid out like an entry of the menu below it: the mark fills the column the icons are centred in, from
        // 4 to 44 pixels, which is where the highlight of the chosen entry begins, and the name starts where the labels start,
        // at 48 pixels. It stands on a row of its own under the menu button, which is centred on the same column.
        var row = new Grid { ColumnSpacing = 4, Margin = new Thickness(4, 2, 8, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "BrandMark.png");
        if (File.Exists(logoPath))
        {
            var logo = new Image { Source = new BitmapImage(new Uri(logoPath)), Width = 40, Height = 40 };
            // The name beside it says what this is; the picture itself adds nothing for a screen reader.
            AutomationProperties.SetAccessibilityView(logo, AccessibilityView.Raw);
            row.Children.Add(logo);
        }
        else row.Children.Add(new SymbolIcon(Symbol.Play) { Width = 40, Style = AppStyle("DigaTealIconStyle") });
        var name = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "AMG", FontSize = 13, FontWeight = FontWeights.SemiBold, Style = AppStyle("DigaTealTextStyle") },
            new TextBlock { Text = "DIGA Archive", FontSize = 20, FontWeight = FontWeights.SemiBold, Style = AppStyle("DigaTitleTextStyle") }
        } };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        AutomationProperties.SetName(row, "AMG DIGA Archive");
        return row;
    }

    private void RefreshJourney()
    {
        var sourceReady = HasDlnaSource;
        var sourceLabel = L.T("Design.SourceConnected");
        var selected = DlnaSelectionCount;
        var preserved = SavedCount > _exportsAtSourceOpen;
        var ordered = _orderNumber.Length > 0;
        var steps = new[]
        {
            (Page: "order", Title: L.T("Design.Stage.Order"), Tone: "Coral", Done: ordered, State: ordered ? L.T("Design.OrderSet", _orderNumber) : L.T("Design.OrderMissing")),
            (Page: "source", Title: L.T("Design.Stage.Connect"), Tone: "Teal", Done: sourceReady, State: sourceReady ? sourceLabel : L.T("Design.ChooseSource")),
            (Page: "library", Title: L.T("Design.Stage.Discover"), Tone: "Violet", Done: selected > 0, State: selected > 0 ? L.Plural("Design.Selected", selected) : sourceReady ? L.T("Design.ExploreVideos") : L.T("Design.OpenSourceFirst")),
            (Page: "export", Title: L.T("Design.Stage.Preserve"), Tone: "Amber", Done: preserved, State: preserved ? L.T("Design.CopiesCreated") : selected > 0 ? L.T("Design.ReadyToExport") : L.T("Design.ChooseVideosFirst")),
            (Page: "complete", Title: L.T("Design.Stage.Archive"), Tone: "Coral", Done: SavedCount > 0, State: SavedCount > 0 ? L.Plural("Design.SavedThisSession", SavedCount) : L.T("Design.CopiesAppearHere"))
        };
        foreach (var step in steps)
            if (_navigationStates.TryGetValue(step.Page, out var state)) state.Text = step.State;
        if (_journeyHost is null) return;
        // While a page is shown only the words change: the order number as it is typed, the count of ticked recordings. The
        // buttons stay and get their new words. Making them anew for every keystroke made the whole strip flicker and move.
        var buttons = _journeyHost.Children.OfType<Button>().ToArray();
        var built = buttons.Length == steps.Length && _journeyHost.Children.Count == steps.Length
            && buttons.Select(button => (button.Tag as JourneyParts)?.Page).SequenceEqual(steps.Select(step => step.Page));
        if (!built)
        {
            _journeyHost.Children.Clear();
            _journeyHost.ColumnDefinitions.Clear();
            _journeyHost.RowDefinitions.Clear();
            foreach (var step in steps)
            {
                var heading = new Grid { ColumnSpacing = 8 };
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                // The number of a stage and the tick of a finished one take the same room, so the title beside them stays put.
                var badge = StageBadge("", step.Tone);
                badge.MinWidth = 36;
                var mark = (TextBlock)badge.Child;
                mark.HorizontalAlignment = HorizontalAlignment.Center;
                heading.Children.Add(badge);
                var title = new TextBlock { Text = step.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(title, 1);
                heading.Children.Add(title);
                // Two lines at most: a long order number ends in an ellipsis instead of making the strip taller with every character.
                var detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                var button = new Button
                {
                    Content = new StackPanel { Spacing = 6, Children = { heading, detail } },
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = new JourneyParts(step.Page, mark, detail)
                };
                detail.IsTextTrimmedChanged += (sender, _) => ToolTipService.SetToolTip(button, sender.IsTextTrimmed ? sender.Text : null);
                var page = step.Page;
                button.Click += (_, _) => Navigate(page);
                _journeyHost.Children.Add(button);
            }
            buttons = _journeyHost.Children.OfType<Button>().ToArray();
        }
        for (var i = 0; i < steps.Length; i++)
        {
            var (step, button, parts) = (steps[i], buttons[i], (JourneyParts)buttons[i].Tag);
            var current = _page == step.Page;
            // The order number is stage 00; Connect to Archive keep their numbers 01 to 04.
            var mark = step.Done ? "✓" : $"{i:00}";
            if (parts.Mark.Text != mark) parts.Mark.Text = mark;
            var detail = current ? L.T("Design.CurrentState", step.State) : step.State;
            if (parts.Detail.Text != detail) parts.Detail.Text = detail;
            var style = AppStyle(current ? $"Diga{step.Tone}ButtonStyle" : "DigaJourneyButtonStyle");
            if (!ReferenceEquals(button.Style, style)) button.Style = style;
            var accessibilityState = string.Join(" ", new[]
            {
                current ? L.T("Design.CurrentStepAccessible") : null,
                step.Done ? L.T("Design.CompletedAccessible") : null,
                step.State + "."
            }.Where(text => text is not null));
            AutomationProperties.SetName(button, L.T("Design.StepAccessible", i, step.Title, accessibilityState));
        }
        if (!built) ArrangeGrid(_journeyHost, JourneyColumns(_journeyHost.ActualWidth));
    }

    /// <summary>What changes in the button of one stage while a page is shown.</summary>
    private sealed record JourneyParts(string Page, TextBlock Mark, TextBlock Detail);

    // Five stages: one row whenever each can still show its two short lines (a maximised window on a laptop included), otherwise
    // three or two columns. An unmeasured host counts as wide.
    private static int JourneyColumns(double width) => width is > 0 and < 500 ? 2 : width is > 0 and < 760 ? 3 : 5;

    private void AnnounceStatus(bool force = false)
    {
        // Progress lines change many times a second. A screen reader hears a forced announcement (start, end, cancel) at once and
        // running progress at most twice a minute, and never the same sentence twice in a row.
        if (!force && (DateTimeOffset.UtcNow - _lastStatusAnnouncement < TimeSpan.FromSeconds(30) || _status.Text == _lastStatusAnnounced)) return;
        _lastStatusAnnouncement = DateTimeOffset.UtcNow;
        _lastStatusAnnounced = _status.Text;
        var peer = FrameworkElementAutomationPeer.FromElement(_status) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
