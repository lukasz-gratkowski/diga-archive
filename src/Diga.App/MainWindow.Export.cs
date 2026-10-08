using L = Diga.Core.Localization.AppText;
using Diga.Core.Cloud;
using Diga.Core.Media;
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

    private void BuildCompletePage(StackPanel page)
    {
        AddHeading(page, L.T("Journey.Archive.Title"), L.T("Journey.Archive.Subtitle"), L.T("Journey.Stage.Archive"));
        if (_exports.Count == 0)
        {
            page.Children.Add(TintCard(new StackPanel { Spacing = 18, Children = {
                StageBadge(L.T("Journey.Archive.EmptyBadge"), "Coral"),
                SectionTitle(L.T("Journey.Archive.EmptyTitle"), Symbol.Library),
                Body(L.T("Journey.Archive.EmptyHelp")),
                ActionButton(L.T("Journey.Archive.Find"), () => { Navigate(HasDlnaSource ? "library" : "source"); return Task.CompletedTask; }, true)
            } }, "Coral"));
            // The cloud card stays available without a saved copy: it also leads to the view of what is already stored there.
            page.Children.Add(BuildCloudUploadCard());
            return;
        }
        var completion = new StackPanel { Spacing = 15 };
        completion.Children.Add(StageBadge(L.T("Journey.Archive.SavedBadge"), "Teal"));
        completion.Children.Add(new TextBlock { Text = SavedCount > 0 ? L.Plural("Journey.Archive.Saved", SavedCount) : L.T("Journey.Archive.AddedOnly"), FontSize = 29, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
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
            foreach (var copy in exported.CloudCopies)
            {
                // The provider may store the file under another name when that name is taken; the stored name is what to look for there.
                var stored = string.Equals(copy.Name, Path.GetFileName(exported.Path), StringComparison.Ordinal)
                    ? L.T("Journey.Archive.InCloud", ProviderName(copy.Provider), copy.At.ToLocalTime().ToString("t"))
                    : L.T("Journey.Archive.InCloudAs", ProviderName(copy.Provider), copy.At.ToLocalTime().ToString("t"), copy.Name);
                entry.Children.Add(new TextBlock { Text = "✓  " + stored, FontSize = 13, TextWrapping = TextWrapping.Wrap, Style = AppStyle("DigaTealTextStyle") });
                // A file in a shared folder is reached through the folder's sharing link: the file's own address opens only for an
                // account that can already reach the file, and the browser may be signed in to another one. Who the sharing link
                // opens for was decided when the folder was shared.
                if (copy.Link is not null) entry.Children.Add(LinkButton(copy.Folder is { } folder ? L.T("Journey.Archive.OpenSharedFolder", folder)
                    : L.T("Journey.Archive.OpenInCloud", ProviderName(copy.Provider)), copy.Link.AbsoluteUri));
            }
            var sourceLabel = L.T("Journey.Archive.Source", exported.SourceName);
            var savedLabel = L.T("Journey.Archive.SavedFile", Path.GetFileName(exported.Path));
            var before = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = sourceLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, InformationBox(exported.SourceInformation, sourceLabel) } };
            var after = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = savedLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, InformationBox(exported.DestinationInformation, savedLabel) } };
            // Every file has these two controls; their spoken names carry the file name so that a list of buttons tells them apart.
            var details = Section("archive:" + exported.Path, L.T("Journey.Archive.Compare"), ResponsivePair(before, after, 760));
            AutomationProperties.SetName(details, L.T("Journey.Archive.Compare") + ": " + Path.GetFileName(exported.Path));
            entry.Children.Add(details);
            var show = ActionButton(L.T("Journey.Archive.OpenFolder"), () => { OpenFolder(Path.GetDirectoryName(exported.Path)!); return Task.CompletedTask; });
            AutomationProperties.SetName(show, L.T("Journey.Archive.OpenFolder") + ": " + Path.GetFileName(exported.Path));
            entry.Children.Add(show);
            page.Children.Add(Card(entry));
        }
        page.Children.Add(BuildCloudUploadCard());
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(L.T("Journey.Archive.FolderUnavailable"));
        // With the separator at its end the name can only be a folder; without it Windows may prefer a program of the same name beside it.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, UseShellExecute = true });
    }
}
