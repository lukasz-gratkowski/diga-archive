namespace Diga.Core.Configuration;

/// <summary>
/// Where the application keeps things for the current Windows user. Everything is below one folder, so that removing that
/// folder removes all the application ever stored about its user, apart from the recordings saved where the user chose.
/// </summary>
public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diga");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    /// <summary>Saved cloud sign-ins, encrypted for the Windows account.</summary>
    public static string Accounts => Path.Combine(Root, "Accounts");
    /// <summary>The default folder for temporary files; the settings can name another.</summary>
    public static string Cache => Path.Combine(Root, "Cache");
    /// <summary>FFmpeg downloaded by the application itself.</summary>
    public static string Tools => Path.Combine(Root, "tools");
    public static string Logs => Path.Combine(Root, "logs");
}
