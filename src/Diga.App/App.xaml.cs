using Microsoft.UI.Xaml;

namespace Diga.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        AppLocalization.Initialize();
        UnhandledException += (_, args) => LogException("XAML unhandled exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) LogException("Runtime unhandled exception", exception);
        };
        try { InitializeComponent(); }
        catch (Exception ex) { LogException("Application resources initialization", ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { LogException("Main window startup", ex); throw; }
    }

    /// <summary>Beside the settings and the saved sign-ins, so that one folder holds everything the application keeps on this PC.</summary>
    internal static string LogDirectory => Diga.Core.Configuration.AppPaths.Logs;

    internal static void DeleteLog()
    {
        foreach (var name in new[] { "errors.log", "errors.previous.log" }) File.Delete(Path.Combine(LogDirectory, name));
    }

    internal static void LogException(string context, Exception exception)
    {
        // Last-resort logging preserves diagnostic context without hiding fatal faults.
        try
        {
            var directory = LogDirectory;
            Directory.CreateDirectory(directory);
            var log = Path.Combine(directory, "errors.log");
            // The log stays on this PC and is kept small: at most the current file and the one before it.
            if (File.Exists(log) && new FileInfo(log).Length > 512 * 1024) File.Move(log, Path.Combine(directory, "errors.previous.log"), overwrite: true);
            // What a tool wrote before it failed travels with the exception; the message shown on screen holds only its last lines.
            var output = exception.Data["Output"] is string text && text.Length > 0 ? "\n--- tool output ---\n" + text.TrimEnd() : "";
            var entry = $"{DateTimeOffset.Now:u} [{context}] {exception}{output}";
            // One entry cannot outgrow the file's own limit: its beginning names the fault, its end holds the tool's last lines.
            const int limit = 48 * 1024;
            if (entry.Length > limit) entry = entry[..(limit / 2)] + "\n[...]\n" + entry[^(limit / 2)..];
            File.AppendAllText(log, entry + "\n");
        }
        catch { }
    }
}
