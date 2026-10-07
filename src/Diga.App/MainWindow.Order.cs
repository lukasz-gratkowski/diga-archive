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
        // Typing must not move anything. Without a number the sentence is one line; with one it is several, and it names the
        // number three times. An invisible copy of the longer sentence holds the room for it from the start, for a number of
        // usual length or the longest one typed on this page, so the card keeps its height while the words in it change.
        var room = Body("");
        room.Opacity = 0;
        room.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(room, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var usual = new string('0', 12);
        var longest = usual;
        var names = new StackPanel { Spacing = 12, Children = { StageBadge(L.T("Journey.Order.NamingBadge"), "Coral"), new Grid { Children = { room, naming } }, Muted(L.T("Journey.Order.NamingHelp"), 12) } };
        var next = ActionButton(L.T("Journey.Order.Continue"), () => { Navigate("source"); return Task.CompletedTask; }, true);

        void Apply()
        {
            _orderNumberText = input.Text;
            var valid = SavedFileNames.TryNormalizeOrderNumber(input.Text, out _orderNumber);
            problem.IsOpen = !valid;
            next.IsEnabled = valid;
            naming.Text = _orderNumber.Length == 0 ? L.T("Journey.Order.NamesByTitle") : L.T("Journey.Order.NamesByOrder", _orderNumber);
            AutomationProperties.SetName(naming, naming.Text);
            // An emptied field starts over: the room goes back to that of a usual number.
            if (input.Text.Length == 0) longest = usual;
            else if (_orderNumber.Length > longest.Length) longest = _orderNumber;
            var reserved = L.T("Journey.Order.NamesByOrder", longest);
            if (room.Text != reserved) room.Text = reserved;
        }
        Apply();
        input.TextChanged += (_, _) => { Apply(); RefreshJourney(); };
        input.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || !next.IsEnabled || _busy) return;
            args.Handled = true;
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
