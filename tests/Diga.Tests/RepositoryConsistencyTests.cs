using System.Text.RegularExpressions;
using Xunit;

namespace Diga.Tests;

/// <summary>What the application and the documents say about each other, checked against the files of the repository itself.</summary>
public sealed partial class RepositoryConsistencyTests
{
    private static readonly string Root = LocalizationTests.RepositoryRoot();

    [GeneratedRegex(@"Links\.Document\(""(?<name>[^""]+)""(?:,\s*""(?<anchor>[^""]+)"")?\)")]
    private static partial Regex GuideLink();

    [GeneratedRegex(@"!?\[[^\]]*\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<a\s+(?:name|id)=""(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex NamedAnchor();

    [Fact]
    public void EveryGuideTheApplicationOpensExistsWithItsAnchorsInEachLanguageItIsOfferedIn()
    {
        var application = Path.Combine(Root, "src", "Diga.App");
        var links = Directory.EnumerateFiles(application, "*.cs")
            .SelectMany(file => GuideLink().Matches(File.ReadAllText(file))).Select(match => (Name: match.Groups["name"].Value, Anchor: match.Groups["anchor"].Value))
            .Distinct().ToArray();
        Assert.NotEmpty(links);
        // The guides that have a Polish version are named once, in Links.cs.
        var translated = Regex.Match(File.ReadAllText(Path.Combine(application, "Links.cs")), @"Translated\s*=\s*new\([^)]*\)\s*\{(?<names>[^}]*)\}").Groups["names"].Value;
        Assert.Contains("CLOUD-SETUP.md", translated, StringComparison.Ordinal);
        foreach (var (name, anchor) in links)
        {
            var versions = translated.Contains("\"" + name + "\"", StringComparison.OrdinalIgnoreCase) ? new[] { name, Path.ChangeExtension(name, ".pl.md") } : [name];
            foreach (var version in versions)
            {
                var path = Path.Combine(Root, "docs", version);
                Assert.True(File.Exists(path), $"The application opens docs/{version}, which does not exist.");
                if (anchor.Length != 0) Assert.True(Anchors(File.ReadAllText(path)).Contains(anchor), $"docs/{version} has no heading or anchor named {anchor}.");
            }
        }
    }

    [Fact]
    public void EveryLinkBetweenTheDocumentsLeadsToAFileAndAHeadingThatExist()
    {
        var documents = Directory.EnumerateFiles(Root, "*.md").Concat(Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, ".github"), "*.md", SearchOption.AllDirectories)).ToArray();
        Assert.True(documents.Length >= 10, "The documents were not found.");
        var broken = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var text = File.ReadAllText(document);
            var name = Path.GetRelativePath(Root, document).Replace('\\', '/');
            foreach (Match link in MarkdownLink().Matches(WithoutCode(text)))
            {
                var target = link.Groups["target"].Value.Trim('<', '>');
                // Addresses on the web are not this test's business; they change without the repository changing.
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = target.Split('#', 2);
                var file = parts[0].Length == 0 ? document : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, Uri.UnescapeDataString(parts[0])));
                if (!File.Exists(file) && !Directory.Exists(file)) { broken.Add($"{name}: {target} (no such file)"); continue; }
                if (parts.Length == 2 && parts[1].Length != 0 && file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Anchors(File.ReadAllText(file)).Contains(Uri.UnescapeDataString(parts[1])))
                    broken.Add($"{name}: {target} (no such heading)");
            }
        }
        Assert.True(broken.Count == 0, "Broken links:\n" + string.Join("\n", broken));
    }

    [Theory]
    [InlineData("## OneDrive with your own registration", "onedrive-with-your-own-registration")]
    [InlineData("# What it does, and what it doesn't", "what-it-does-and-what-it-doesnt")]
    [InlineData("### Zapisywanie – krok 3: „Zapisz”", "zapisywanie--krok-3-zapisz")]
    [InlineData("## `settings.json` and the log", "settingsjson-and-the-log")]
    [InlineData("<a name=\"google-drive\"></a>\n## Dysk Google", "google-drive")]
    public void HeadingsAreNamedAsGitHubNamesThem(string markdown, string anchor) => Assert.Contains(anchor, Anchors(markdown));

    /// <summary>The names by which a place in a document can be linked to: GitHub's name for each heading, and explicit anchors.</summary>
    private static HashSet<string> Anchors(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match heading in Heading().Matches(WithoutCodeBlocks(markdown)))
        {
            // GitHub: lower case; letters, digits, spaces, hyphens and underscores stay; spaces become hyphens; a repeated name gets a number.
            // The words of a link or of a code span in the heading count; the link's address does not.
            var title = Regex.Replace(heading.Groups["title"].Value, @"\[([^\]]*)\]\([^)]*\)", "$1");
            var name = new string(title.ToLowerInvariant().Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_').ToArray()).Replace(' ', '-');
            var unique = name;
            for (var number = 1; !anchors.Add(unique); number++) unique = name + "-" + number;
        }
        foreach (Match anchor in NamedAnchor().Matches(markdown)) anchors.Add(anchor.Groups["name"].Value);
        return anchors;
    }

    // A line that starts with # inside a code block is a comment, not a heading.
    private static string WithoutCodeBlocks(string markdown) => Regex.Replace(markdown, @"(?ms)^```.*?^```\s*$", "");

    // A link written inside code is an example, not a link.
    private static string WithoutCode(string markdown) => Regex.Replace(WithoutCodeBlocks(markdown), "`[^`\n]*`", "");
}
