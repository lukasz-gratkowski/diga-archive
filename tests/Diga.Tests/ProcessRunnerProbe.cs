using System.Diagnostics;
using System.Globalization;
using Diga.Core.Media;

namespace Diga.Tests;

/// <summary>
/// What this assembly does when it is started as a program instead of being loaded by the test host: one FFmpeg run through
/// <see cref="ProcessRunner"/>, cancelled after half a second, in a process whose thread pool is limited to two threads.
/// Such a limit cannot be set inside the test host, where the tests running alongside share the pool.
/// </summary>
internal static class ProcessRunnerProbe
{
    public const string Command = "process-runner-cancellation";

    private static int Main(string[] args)
    {
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
}
