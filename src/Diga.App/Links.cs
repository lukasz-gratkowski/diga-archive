using L = Diga.Core.Localization.AppText;

namespace Diga.App;

/// <summary>The addresses the application opens in the browser, in one place so that a renamed repository needs one change.</summary>
internal static class Links
{
    public const string Project = "https://github.com/lukasz-gratkowski/diga-archive";
    public const string Releases = Project + "/releases";
    public const string MediaInfo = "https://mediaarea.net/en/MediaInfo";
    // Where a person withdraws the permission given to an application: the provider's own page, not something this application can do.
    public const string MicrosoftPermissions = "https://account.microsoft.com/privacy/app-access";
    public const string MicrosoftWorkPermissions = "https://myapps.microsoft.com";
    public const string GooglePermissions = "https://myaccount.google.com/connections";

    /// <summary>
    /// A guide in the repository's docs folder, as it was when this version was released: the address names the version's tag,
    /// not the newest state of the repository, so that a guide renamed or rewritten later does not break the links of an
    /// installed copy. In Polish where a Polish version exists and the application runs in Polish.
    /// </summary>
    public static string Document(string name, string anchor = "")
    {
        var localized = L.LanguageCode == "pl" && Translated.Contains(name) ? Path.ChangeExtension(name, ".pl.md") : name;
        return $"{Project}/blob/v{Version}/docs/{localized}{(anchor.Length == 0 ? "" : "#" + anchor)}";
    }

    // A release is tagged "v" plus this version; the packaging refuses any other tag.
    private static string Version => typeof(Links).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly HashSet<string> Translated = new(StringComparer.OrdinalIgnoreCase) { "CLOUD-SETUP.md", "USER-GUIDE.md", "RECORDER-SETUP.md" };
}
