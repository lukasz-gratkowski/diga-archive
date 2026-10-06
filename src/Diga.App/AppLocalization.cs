using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Diga.Core.Configuration;
using Diga.Core.Localization;

namespace Diga.App;

internal static class AppLocalization
{
    internal static IReadOnlyList<string> SystemLanguages { get; private set; } = [];
    internal static string LanguageTag => AppText.LanguageCode == "pl" ? "pl-PL" : "en-US";

    internal static void Initialize()
    {
        try { SystemLanguages = Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray(); }
        catch (Exception ex)
        {
            App.LogException("Windows language preferences", ex);
            SystemLanguages = [CultureInfo.CurrentUICulture.Name];
        }
        AppText.Configure("system", SystemLanguages);
        try
        {
            // Settings I/O does not capture a synchronization context. Resolve before
            // loading XAML resources or constructing controls to avoid mixed languages.
            var settings = new JsonSettingsStore().LoadAsync().GetAwaiter().GetResult();
            AppText.Configure(settings.Language, SystemLanguages);
        }
        catch (Exception ex) { App.LogException("Startup language preference", ex); }
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = LanguageTag;
        ApplyNativeThreadLanguage();
    }

    private static void ApplyNativeThreadLanguage()
    {
        // MediaTransportControls loads Win32 MUI satellite strings instead of MRT
        // resources. Apply this on the UI thread before XAML initialization so its
        // native play/pause and mute/unmute labels follow the app preference too.
        // This changes neither Windows language settings nor regional formats.
        const uint muiLanguageName = 0x8;
        try
        {
            if (!SetThreadPreferredUILanguages(muiLanguageName, LanguageTag + "\0\0", out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count == 0) throw new InvalidOperationException("No native UI resource language was applied.");
        }
        catch (Exception ex) { App.LogException("Native UI resource language", ex); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPreferredUILanguages(uint flags, string languages, out uint count);
}
