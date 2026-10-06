using System.Diagnostics;
using System.Globalization;
using Diga.Core.Media;

namespace Diga.Tests;

/// <summary>
/// What this assembly does when it is started as a program instead of being loaded by the test host. One command runs FFmpeg through
/// <see cref="ProcessRunner"/>, cancelled after half a second, in a process whose thread pool is limited to two threads; such a
/// limit cannot be set inside the test host, where the tests running alongside share the pool. Another starts a program through
/// <see cref="ProcessRunner"/> and waits to be ended without warning, as an application is by Task Manager. Two more stand in
/// for a tool whose output <see cref="ProcessRunner"/> reads: one writes more than is kept, one writes a name outside ASCII.
/// </summary>
internal static class ProcessRunnerProbe
{
    public const string Command = "process-runner-cancellation";
    public const string OrphanCommand = "process-runner-orphan";
    public const string LongOutputCommand = "process-runner-long-output";
    public const string Utf8Command = "process-runner-utf8";

    /// <summary>What <see cref="LongOutputCommand"/> writes: this many lines of warnings between a first line and the line that names the failure.</summary>
    public const int LongOutputLines = 60_000;
    public const string LongOutputFirstLine = "Input #0, mpegts, from 'recording.ts':";
    public const string LongOutputLastLine = "[in#0/mpegts @ 000001] Error during demuxing: I/O error";
    /// <summary>What <see cref="Utf8Command"/> writes, as the UTF-8 bytes FFmpeg writes: Polish letters, Japanese characters and one beyond the basic plane.</summary>
    public const string Utf8Line = "C:\\Users\\Łukasz\\Wideo\\Zażółć gęślą jaźń 録画 \U0001F3AC.ts: Invalid data found when processing input";

    /// <summary>The program and arguments that start this assembly with one of the commands above.</summary>
    public static (string Program, string[] Arguments) CommandLine(string command)
    {
        // testhost.exe runs the tests, so the dotnet host is taken from the runtime's own location.
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return (File.Exists(dotnet) ? dotnet : "dotnet", [typeof(ProcessRunnerProbe).Assembly.Location, command]);
    }

    private static int Main(string[] args)
    {
        if (args is [OrphanCommand]) return Orphan();
        if (args is [LongOutputCommand]) return LongOutput();
        if (args is [Utf8Command]) return Utf8();
        if (args is not [Command, var ffmpeg]) { Console.Error.WriteLine("This assembly holds tests. Run them with dotnet test."); return 2; }
        // The limit cannot be lower than the processor count, which the test sets to two with DOTNET_PROCESSOR_COUNT.
        ThreadPool.GetMaxThreads(out _, out var completionPortThreads);
        if (!ThreadPool.SetMaxThreads(2, completionPortThreads))
        {
            Console.Out.WriteLine($"refused a thread pool of two threads with {Environment.ProcessorCount} processors");
            return 3;
        }
        var elapsed = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        // Thirty seconds, so that an FFmpeg left behind by a killed test run ends by itself.
        var run = new ProcessRunner().RunAsync(ffmpeg,
            ["-hide_banner", "-re", "-f", "lavfi", "-i", "testsrc2=size=32x32:rate=25", "-t", "30", "-f", "null", "-"], cancellationToken: timeout.Token);
        string outcome;
        // Waited for on the main thread, which does not belong to the pool.
        try { outcome = run.Wait(TimeSpan.FromSeconds(10)) ? "exited" : "pending"; }
        catch (AggregateException ex) { outcome = ex.InnerException is OperationCanceledException ? "cancelled" : "failed"; }
        ThreadPool.GetMaxThreads(out var workerThreads, out _);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{outcome} {elapsed.ElapsedMilliseconds} ms; {Environment.ProcessorCount} processors, thread pool limit {workerThreads}, threads {ThreadPool.ThreadCount}"));
        Console.Out.Flush();
        // The test ends this process together with any FFmpeg it still has. A closed input means the test host is gone.
        Console.In.ReadLine();
        return 0;
    }

    /// <summary>
    /// Starts Windows PowerShell through <see cref="ProcessRunner"/>, passes on the process number it prints and waits. The test then
    /// ends this process alone. PowerShell sleeps for a minute and a half, so one that nothing ended goes by itself.
    /// </summary>
    private static int Orphan()
    {
        var run = new ProcessRunner().RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Write-Output $PID; Start-Sleep -Seconds 90"],
            line => { Console.Out.WriteLine(line); Console.Out.Flush(); });
        try { run.Wait(); } catch (AggregateException) { return 4; }
        return 0;
    }

    /// <summary>Six million characters of warnings on the error output, more than <see cref="ProcessRunner"/> keeps, and the reason for the failure last.</summary>
    private static int LongOutput()
    {
        using var error = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false), 1 << 16);
        error.WriteLine(LongOutputFirstLine);
        for (var line = 0; line < LongOutputLines; line++) error.WriteLine(LongOutputWarning(line));
        error.WriteLine(LongOutputLastLine);
        error.Flush();
        Console.Out.WriteLine("progress=end");
        return 1;
    }

    /// <summary>One hundred characters, so that the total is easy to tell.</summary>
    public static string LongOutputWarning(int line) => string.Create(CultureInfo.InvariantCulture, $"[mpegts @ 000001] warning {line:D6}: ").PadRight(100, 'x');

    private static int Utf8()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Utf8Line + "\n");
        using (var error = Console.OpenStandardError()) error.Write(bytes);
        using (var output = Console.OpenStandardOutput()) output.Write(bytes);
        return 1;
    }
}
