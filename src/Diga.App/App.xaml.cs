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

    internal static void LogException(string context, Exception exception)
    {
        // Last-resort logging preserves diagnostic context without hiding fatal faults.
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigaArchive", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "errors.log"), $"{DateTimeOffset.Now:u} [{context}] {exception}\n");
        }
        catch { }
    }
}
