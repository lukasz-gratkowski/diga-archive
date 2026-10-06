using System.Diagnostics;
using System.Runtime.InteropServices;
using Diga.Core.Localization;
using Diga.Core.Media;

namespace Diga.Tests;

/// <summary>
/// The time limits of <see cref="ProcessRunner"/> and the Windows job that ends its programs with the application. Windows
/// PowerShell stands in for a program that hangs, repeats itself or writes a file, so no FFmpeg is needed.
/// </summary>
public sealed class ProcessRunnerLimitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaProcessTests-" + Guid.NewGuid().ToString("N"));
    public ProcessRunnerLimitTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private static string[] PowerShell(string script) => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script];

    [Fact]
    public async Task AProgramThatRunsLongerThanItsLimitIsEnded()
    {
        if (!OperatingSystem.IsWindows()) return;
        int? id = null;
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ProcessTimeoutException>(() => new ProcessRunner().RunAsync("powershell.exe", PowerShell("Write-Output $PID; Start-Sleep -Seconds 90"),
            new ProcessLimits(Overall: TimeSpan.FromSeconds(3)), line => { if (int.TryParse(line, out var number)) id = number; }));
        Assert.False(error.Inactivity);
        Assert.Equal(TimeSpan.FromSeconds(3), error.Limit);
        Assert.Equal("powershell", error.Program);
        Assert.Equal(AppText.T("Core.Media.Process.TimedOut", "powershell", 3L), error.Message);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(30));
        // The run does not return before the program is gone.
        if (id is { } started) AssertEnded(started);
    }

    [Fact]
    public async Task AProgramThatGoesSilentOrOnlyRepeatsItselfIsEnded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var silent = await Assert.ThrowsAsync<ProcessTimeoutException>(() => new ProcessRunner().RunAsync("powershell.exe", PowerShell("Write-Output 'ready'; Start-Sleep -Seconds 90"),
            new ProcessLimits(Inactivity: TimeSpan.FromSeconds(2))));
        Assert.True(silent.Inactivity);
        Assert.Equal(AppText.T("Core.Media.Process.Stalled", "powershell", 2L), silent.Message);
        // FFmpeg writes its progress twice a second whether or not anything moves, and may write warnings for ever: neither the same
        // position again nor a line on the error output is progress.
        var lines = 0;
        // On a busy PC Windows PowerShell can need longer than the limit to start, and is then ended before it wrote a line. That
        // shows nothing about lines that repeat, so the run is made again with twice the time.
        for (var seconds = 2; lines == 0 && seconds <= 16; seconds *= 2)
        {
            var repeating = await Assert.ThrowsAsync<ProcessTimeoutException>(() => new ProcessRunner().RunAsync("powershell.exe",
                PowerShell("while ($true) { Write-Output 'total_size=10747904'; Write-Output 'out_time_us=19776000'; Write-Output 'progress=continue'; [Console]::Error.WriteLine('warning: nothing moves'); Start-Sleep -Milliseconds 100 }"),
                new ProcessLimits(Inactivity: TimeSpan.FromSeconds(seconds), Progress: RemuxService.ProgressMoved()), _ => Interlocked.Increment(ref lines)));
            Assert.True(repeating.Inactivity);
        }
        Assert.True(lines >= 6, $"Only {lines} lines arrived, so the program was not ended while it was writing.");
    }

    [Fact]
    public async Task AProgramThatMovesOnOrWritesItsFileIsNotEnded()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The time without progress counts from the start of the program, and on a busy PC Windows PowerShell can need longer
        // than the limit to start. A program that was ended before it was properly under way, with at most one line or one block
        // written, shows nothing about progress. Both are then run again with twice the limit, each time for one and a half
        // times as long as the limit. With the longest limit, 48 seconds, the failure stands whenever it comes.
        static async Task<ProcessResult?> UnlessEndedAtTheStart(Task<ProcessResult> run, bool lastTry, Func<bool> underWay)
        {
            try { return await run; }
            catch (ProcessTimeoutException) when (!lastTry && !underWay()) { return null; }
        }
        for (var seconds = 6; ; seconds *= 2)
        {
            var limits = new ProcessLimits(Inactivity: TimeSpan.FromSeconds(seconds), Progress: RemuxService.ProgressMoved());
            // A new position twice a second: FFmpeg while it copies.
            var (positions, lines) = (seconds * 3, 0);
            var moving = UnlessEndedAtTheStart(new ProcessRunner().RunAsync("powershell.exe", PowerShell($"1..{positions} | ForEach-Object {{ Write-Output \"out_time_us=$_\"; Start-Sleep -Milliseconds 500 }}"), limits,
                _ => Interlocked.Increment(ref lines)), seconds == 48, () => Volatile.Read(ref lines) > 1);
            // Not one line, and a megabyte a second: FFmpeg while it rewrites an MP4 at the end to put its index first.
            var file = Path.Combine(_directory, $"rewritten-{seconds}.bin");
            const int block = 262144;
            var blocks = seconds * 6;
            var writing = UnlessEndedAtTheStart(new ProcessRunner().RunAsync("powershell.exe", PowerShell(
                $"$file = [IO.File]::Create('{file.Replace("'", "''")}'); $block = New-Object byte[] {block}; 1..{blocks} | ForEach-Object {{ $file.Write($block, 0, $block.Length); $file.Flush(); Start-Sleep -Milliseconds 250 }}; $file.Close()"), limits),
                seconds == 48, () => File.Exists(file) && new FileInfo(file).Length > block);
            var results = await Task.WhenAll(moving, writing);
            if (results.Contains(null)) continue;
            Assert.All(results, result => Assert.True(result!.ExitCode == 0, result.StandardError));
            Assert.Equal(positions, results[0]!.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal((long)blocks * block, new FileInfo(file).Length);
            return;
        }
    }

    [Fact]
    public async Task LimitsMustBePositiveAndARunWithoutLimitsIsNotWatched()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ProcessRunner().RunAsync("does-not-exist.exe", [], new ProcessLimits(Overall: TimeSpan.Zero)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ProcessRunner().RunAsync("does-not-exist.exe", [], new ProcessLimits(Inactivity: TimeSpan.FromSeconds(-1))));
        if (!OperatingSystem.IsWindows()) return;
        var result = await new ProcessRunner().RunAsync("powershell.exe", PowerShell("Write-Output 'done'"), new ProcessLimits(Overall: TimeSpan.FromMinutes(1), Inactivity: TimeSpan.FromMinutes(1)));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.StandardOutput.Trim());
        // The caller's own cancellation stays a cancellation, with or without limits.
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync("powershell.exe", PowerShell("Start-Sleep -Seconds 90"),
            new ProcessLimits(Overall: TimeSpan.FromMinutes(1)), cancellationToken: cancel.Token));
    }

    /// <summary>
    /// A program started by <see cref="ProcessRunner"/> ends when the process that started it is ended without warning, the way
    /// Task Manager ends an application. Without the job object it would go on, as FFmpeg went on writing its file.
    /// </summary>
    [Fact]
    public async Task AProgramDoesNotOutliveTheProcessThatStartedIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        // testhost.exe runs the tests, so the dotnet host is taken from the runtime's own location.
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(File.Exists(dotnet) ? dotnet : "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(ProcessRunnerProbe).Assembly.Location, ProcessRunnerProbe.OrphanCommand }) start.ArgumentList.Add(argument);
        using var probe = Process.Start(start) ?? throw new IOException("Could not start the probe process.");
        var stderr = Task.Factory.StartNew(probe.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Process? program = null;
        try
        {
            var line = await Task.Factory.StartNew(probe.StandardOutput.ReadLine, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(int.TryParse(line, out var id), $"The probe did not name its program: '{line}'. {(probe.HasExited ? await stderr : "")}");
            program = Process.GetProcessById(id);
            Assert.Equal("powershell", program.ProcessName, ignoreCase: true);
            Assert.False(program.HasExited);
            // The probe alone, not its process tree: what Windows does with the rest is the thing tested.
            probe.Kill();
            await probe.WaitForExitAsync();
            Assert.True(program.WaitForExit(15000), "The program went on running after the process that started it was ended.");
        }
        finally
        {
            try { if (!probe.HasExited) probe.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { if (program is { HasExited: false }) program.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            program?.Dispose();
        }
    }

    private static void AssertEnded(int id)
    {
        Process program;
        try { program = Process.GetProcessById(id); }
        catch (ArgumentException) { return; }
        using (program) Assert.True(program.HasExited, $"Process {id} is still running.");
    }
}
