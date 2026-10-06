using L = Diga.Core.Localization.AppText;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Diga.Core.Media;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The end of the error output, where a tool names what stopped it. The whole output can run to megabytes of warnings and is nothing to show a person.</summary>
    public string ErrorSummary
    {
        get
        {
            const int limit = 400;
            var tail = StandardError.Length > 4096 ? StandardError[^4096..] : StandardError;
            var lines = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kept = new List<string>();
            var length = 0;
            // A cut tail starts in the middle of a line; that first piece is left out unless it is all there is.
            var first = tail.Length < StandardError.Length && lines.Length > 1 ? 1 : 0;
            for (var index = lines.Length - 1; index >= first && kept.Count < 4; index--)
            {
                if (length + lines[index].Length > limit) { if (kept.Count == 0) kept.Add(lines[index][..limit] + "…"); break; }
                kept.Insert(0, lines[index]);
                length += lines[index].Length + 1;
            }
            return string.Join(" ", kept);
        }
    }

    /// <summary>The error output for a log: its end, which is where the cause is.</summary>
    public string ErrorLog => StandardError.Length <= 32 * 1024 ? StandardError : StandardError[^(32 * 1024)..];
}

/// <summary>Time limits for one run of a program. When one runs out the program is ended and the run fails with <see cref="ProcessTimeoutException"/>.</summary>
/// <param name="Overall">The longest the program may run.</param>
/// <param name="Inactivity">
/// The longest the program may go without progress. Progress is a line of standard output that <paramref name="Progress"/> accepts,
/// or data written to a file. Lines on the error output are not progress: a program that spins may write warnings for ever.
/// </param>
/// <param name="Progress">Says whether a line of standard output shows that the work moved on. Without it every such line does.</param>
public sealed record ProcessLimits(TimeSpan? Overall = null, TimeSpan? Inactivity = null, Func<string, bool>? Progress = null);

/// <summary>A program was ended because it ran, or made no progress, for longer than it was allowed.</summary>
public sealed class ProcessTimeoutException(string program, TimeSpan limit, bool inactivity)
    : TimeoutException(L.T(inactivity ? "Core.Media.Process.Stalled" : "Core.Media.Process.TimedOut", program, (long)Math.Ceiling(limit.TotalSeconds)))
{
    public string Program { get; } = program;
    public TimeSpan Limit { get; } = limit;
    /// <summary>True when the program made no progress for <see cref="Limit"/>, false when its whole run took longer than that.</summary>
    public bool Inactivity { get; } = inactivity;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default);

    /// <summary>The same run under time limits. A runner that has no way to enforce them, such as a test double, runs without them.</summary>
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessLimits limits, Action<string>? outputLine = null, CancellationToken cancellationToken = default)
        => RunAsync(executable, arguments, outputLine, cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaximumCapturedCharacters = 4 * 1024 * 1024;
    // More than the progress lines of one check interval can add up to, far less than a file being written adds.
    private const long FileProgressBytes = 64 * 1024;

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default)
        => RunAsync(executable, arguments, new ProcessLimits(), outputLine, cancellationToken);

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessLimits limits, Action<string>? outputLine = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Overall <= TimeSpan.Zero || limits.Inactivity <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limits));
        cancellationToken.ThrowIfCancellationRequested();
        // FFmpeg and FFprobe write UTF-8. Left to itself, .NET reads their output in the code page of the console, or of Windows
        // when there is no console: a path such as C:\Users\Łukasz came out garbled in messages and in the log, and on a system
        // with a multi-byte code page a garbled character could take the quotation mark after it out of FFprobe's JSON.
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new FileNotFoundException(L.T("Core.Media.Process.StartFailed", Path.GetFileName(executable)), executable, ex); }
        ChildProcessJob.Add(process);
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var activity = new Activity();
        var readOut = ReadOnOwnThread(process.StandardOutput, stdout, outputLine, activity, limits.Progress ?? (_ => true));
        var readErr = ReadOnOwnThread(process.StandardError, stderr, null, activity, null);
        // Cancelled by the caller or by a limit that ran out; the second token only lets the watch go when the run is over.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var over = new CancellationTokenSource();
        var watch = WatchAsync(process, Path.GetFileNameWithoutExtension(executable), limits, activity, stop, over.Token);
        try
        {
            var exited = process.WaitForExitAsync(stop.Token);
            var first = await Task.WhenAny(exited, readOut, readErr).ConfigureAwait(false);
            // A failing progress callback must not leave a child blocked on a full stdout pipe.
            if (first.IsFaulted) await first.ConfigureAwait(false);
            if (first == readOut && !readErr.IsCompleted)
            {
                var second = await Task.WhenAny(exited, readErr).ConfigureAwait(false);
                if (second.IsFaulted) await second.ConfigureAwait(false);
            }
            else if (first == readErr && !readOut.IsCompleted)
            {
                var second = await Task.WhenAny(exited, readOut).ConfigureAwait(false);
                if (second.IsFaulted) await second.ConfigureAwait(false);
            }
            await exited.ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(readOut, readErr).ConfigureAwait(false); } catch { /* Preserve the first failure/cancellation. */ }
            over.Cancel();
            // A limit that ran out is a failure with a reason. Only the caller's own cancellation is a cancellation.
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested && await watch.ConfigureAwait(false) is { } expired) throw expired;
            throw;
        }
        finally { over.Cancel(); await watch.ConfigureAwait(false); }
    }

    /// <summary>
    /// Ends the run, through <paramref name="stop"/>, when a limit runs out, and says which. Time is counted check by check and one
    /// check never counts for more than two intervals, so a PC that slept for an hour does not find its programs an hour late.
    /// </summary>
    private static async Task<ProcessTimeoutException?> WatchAsync(Process process, string program, ProcessLimits limits, Activity activity, CancellationTokenSource stop, CancellationToken over)
    {
        if ((limits.Overall ?? limits.Inactivity) is not { } shortest) return null;
        if (limits.Inactivity < shortest) shortest = limits.Inactivity.Value;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(shortest.TotalMilliseconds / 10, 20, 2000));
        TimeSpan elapsed = TimeSpan.Zero, idle = TimeSpan.Zero;
        var marks = activity.Marks;
        var written = FileBytesWritten(process, activity) ?? 0;
        var last = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(over).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var step = Stopwatch.GetElapsedTime(last, now);
                if (step > interval * 2) step = interval * 2;
                last = now;
                elapsed += step;
                var (currentMarks, currentWritten) = (activity.Marks, FileBytesWritten(process, activity) ?? written);
                // FFmpeg writes the end of an MP4 (the whole file once more, to put its index first) without one progress line.
                idle = currentMarks != marks || currentWritten - written >= FileProgressBytes ? TimeSpan.Zero : idle + step;
                (marks, written) = (currentMarks, currentWritten);
                ProcessTimeoutException? expired = limits.Overall is { } overall && elapsed >= overall ? new(program, overall, inactivity: false)
                    : limits.Inactivity is { } inactivity && idle >= inactivity ? new(program, inactivity, inactivity: true) : null;
                if (expired is null) continue;
                stop.Cancel();
                return expired;
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    /// <summary>
    /// Bytes the program wrote to files. Windows counts what went to the output pipes as well, so the most that can be is taken off:
    /// warnings repeated for ever are not a file growing. Null when Windows does not say.
    /// </summary>
    private static long? FileBytesWritten(Process process, Activity activity)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return GetProcessIoCounters(process.SafeHandle, out var counters) ? (long)Math.Min(counters.WriteTransferCount, long.MaxValue) - activity.PipedBytes : null; }
        catch (InvalidOperationException) { return null; }
    }

    // An asynchronous read of a redirected pipe occupies a thread-pool thread until the child writes or exits. Two of them per
    // running program leave a PC with two processors no thread for timers and continuations, so cancellation came seconds late.
    private static Task ReadOnOwnThread(StreamReader reader, StringBuilder capture, Action<string>? lineCallback, Activity activity, Func<string, bool>? progress) =>
        Task.Factory.StartNew(() => Read(reader, capture, lineCallback, activity, progress), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Read(StreamReader reader, StringBuilder capture, Action<string>? lineCallback, Activity activity, Func<string, bool>? progress)
    {
        while (reader.ReadLine() is { } line)
        {
            activity.Piped(line.Length);
            // Keep the start and the end of very long output: FFmpeg reports an input read error on one of its last lines.
            var kept = line.Length > MaximumCapturedCharacters / 2 ? line[..(MaximumCapturedCharacters / 2)] : line;
            if (capture.Length + kept.Length > MaximumCapturedCharacters) capture.Remove(MaximumCapturedCharacters / 4, capture.Length / 2);
            capture.AppendLine(kept);
            if (progress?.Invoke(line) == true) activity.Mark();
            lineCallback?.Invoke(line);
        }
    }

    /// <summary>What the two reading threads tell the watch.</summary>
    private sealed class Activity
    {
        private long _marks, _pipedBytes;
        public long Marks => Interlocked.Read(ref _marks);
        public long PipedBytes => Interlocked.Read(ref _pipedBytes);
        public void Mark() => Interlocked.Increment(ref _marks);
        // No character takes more than three bytes in UTF-8, and a line ends with at most two.
        public void Piped(int characters) => Interlocked.Add(ref _pipedBytes, 3L * characters + 2);
    }

    /// <summary>
    /// One Windows job object for every program started here, set to end its programs when it is closed. Windows closes it when this
    /// process goes, in whatever way it goes, so FFmpeg does not keep writing after the application crashed or was ended in Task Manager.
    /// </summary>
    private static class ChildProcessJob
    {
        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;
        // Never closed and not inheritable: a program that inherited it would keep the job, and so itself, alive.
        private static readonly nint Job = Create();

        public static void Add(Process process)
        {
            if (Job == 0) return;
            // Refused for a program that has already ended, or by a Windows policy; the run goes on without this safeguard.
            try { AssignProcessToJobObject(Job, process.SafeHandle); }
            catch (InvalidOperationException) { }
        }

        private static nint Create()
        {
            if (!OperatingSystem.IsWindows()) return 0;
            var job = CreateJobObjectW(0, null);
            if (job == 0) return 0;
            var information = default(ExtendedLimits);
            information.Basic.LimitFlags = KillOnJobClose;
            if (SetInformationJobObject(job, ExtendedLimitInformation, ref information, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
            CloseHandle(job);
            return 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimits information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);

        // JOBOBJECT_BASIC_LIMIT_INFORMATION and JOBOBJECT_EXTENDED_LIMIT_INFORMATION of winnt.h.
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    // IO_COUNTERS of winnt.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
}
