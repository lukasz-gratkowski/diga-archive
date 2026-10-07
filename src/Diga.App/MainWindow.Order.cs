using L = Diga.Core.Localization.AppText;
using Diga.Core.Naming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Diga.App;

public sealed partial class MainWindow
{
    // The order number names files saved in this session; it is deliberately not kept across launches.
    private string _orderNumber = "";
    private string _orderNumberText = "";
    // Recordings saved under each order number in this session. A second, different recording makes the order number a prefix.
    private readonly Dictionary<string, HashSet<string>> _savedUnderOrder = new(StringComparer.OrdinalIgnoreCase);

    private void BuildOrderPage(StackPanel page)
    {
        AddHeading(page, L.T("Journey.Order.Title"), L.T("Journey.Order.Subtitle"), L.T("Journey.Order.Stage"));
        var form = new StackPanel { Spacing = 14 };
        form.Children.Add(SectionTitle(L.T("Journey.Order.Number"), Symbol.Tag));
        var input = new TextBox
        {
            Text = _orderNumberText, PlaceholderText = L.T("Journey.Order.Placeholder"), FontSize = 20,
            MaxLength = SavedFileNames.MaximumOrderNumberLength, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false
        };
        input.SelectionStart = input.Text.Length;
        AutomationProperties.SetName(input, L.T("Journey.Order.Number"));
        form.Children.Add(input);
        var problem = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, Message = L.T("Journey.Order.Invalid") };
        form.Children.Add(problem);
        form.Children.Add(Muted(L.T("Journey.Order.Help"), 12));

        var naming = Body("");
        naming.VerticalAlignment = VerticalAlignment.Top;
        // The card must keep its height whatever is typed. Without a number the sentence is one line; with one it is several,
        // and it names the number three times. An invisible copy of the longer sentence, with a number as long as the card ever
        // shows one (a longer number is shortened there), holds the room for it from the start.
        var room = Body(L.T("Journey.Order.NamesByOrder", new string('0', ShownOrderNumberLength)));
        room.Opacity = 0;
        room.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(room, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var names = new StackPanel { Spacing = 12, Children = { StageBadge(L.T("Journey.Order.NamingBadge"), "Coral"), new Grid { Children = { room, naming } }, Muted(L.T("Journey.Order.NamingHelp"), 12) } };
        var next = ActionButton(L.T("Journey.Order.Continue"), () => { Navigate("source"); return Task.CompletedTask; }, true);

        // The number is taken with every character, and the button knows at once whether it can be used.
        bool Accept()
        {
            _orderNumberText = input.Text;
            var valid = SavedFileNames.TryNormalizeOrderNumber(input.Text, out _orderNumber);
            next.IsEnabled = valid;
            return valid;
        }
        // What the rest of the page says about the number: the note about one that cannot be used, and the card about file names.
        void Show()
        {
            problem.IsOpen = !Accept();
            naming.Text = _orderNumber.Length == 0 ? L.T("Journey.Order.NamesByTitle") : L.T("Journey.Order.NamesByOrder", ShownOrderNumber(_orderNumber));
            // A screen reader, and the tooltip, have the sentence with the whole number.
            var whole = _orderNumber.Length == 0 ? naming.Text : L.T("Journey.Order.NamesByOrder", _orderNumber);
            AutomationProperties.SetName(naming, whole);
            ToolTipService.SetToolTip(naming, whole == naming.Text ? null : whole);
        }
        Show();
        // While a number is being typed nothing else on the page changes. The strip of steps, the menu entry, the card about
        // file names and the note about an unusable number follow once, when the typing pauses: words that grew and wrapped
        // anew in three places with every character made the page restless.
        var typingPause = DispatcherQueue.CreateTimer();
        typingPause.Interval = TimeSpan.FromSeconds(1);
        typingPause.IsRepeating = false;
        typingPause.Tick += (_, _) => { Show(); RefreshJourney(); };
        input.TextChanged += (_, _) => { Accept(); typingPause.Stop(); typingPause.Start(); };
        // A pause that ends after the page has gone must not write to it.
        _leavingPage = typingPause.Stop;
        input.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || _busy) return;
            args.Handled = true;
            // Enter asks for the next step. A number that cannot be used gets its note now, not after the pause.
            if (!Accept()) { typingPause.Stop(); Show(); RefreshJourney(); return; }
            Navigate("source");
        };
        _pageFocusTarget = input;
        page.Children.Add(ResponsivePair(Card(form), TintCard(names, "Coral"), 820));
        page.Children.Add(next);
    }

    /// <summary>How many different recordings the order number covers: those about to be saved plus those already saved under it.</summary>
    private int OrderBatchCount(string orderNumber, IReadOnlyCollection<string> ids) =>
        ids.Count + (_savedUnderOrder.TryGetValue(orderNumber, out var earlier) ? earlier.Count(id => !ids.Contains(id)) : 0);

    // Ids repeat between network devices, so the device is part of a recording's identity for the order rule.
    private string OrderKey(DlnaPick pick) => _dlnaDevice?.Id + "\n" + pick.Id;

    private void RememberSavedUnderOrder(string orderNumber, string id)
    {
        if (orderNumber.Length == 0) return;
        if (!_savedUnderOrder.TryGetValue(orderNumber, out var ids)) _savedUnderOrder[orderNumber] = ids = new(StringComparer.Ordinal);
        ids.Add(id);
    }

    /// <summary>One line for the Preserve pages saying how the files about to be saved will be named.</summary>
    private string SavedNameSummary(int count) => _orderNumber.Length == 0 ? L.T("Journey.Order.SummaryNone")
        : count == 1 ? L.T("Journey.Order.SummarySingle", _orderNumber) : L.T("Journey.Order.SummarySeveral", _orderNumber);
}
