using L = Diga.Core.Localization.AppText;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Diga.Core.Media;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaximumCapturedCharacters = 4 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new FileNotFoundException(L.T("Core.Media.Process.StartFailed", Path.GetFileName(executable)), executable, ex); }
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readOut = ReadOnOwnThread(process.StandardOutput, stdout, outputLine);
        var readErr = ReadOnOwnThread(process.StandardError, stderr, null);
        try
        {
            var exited = process.WaitForExitAsync(cancellationToken);
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
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(readOut, readErr).ConfigureAwait(false); } catch { /* Preserve the first failure/cancellation. */ }
            throw;
        }
    }

    // An asynchronous read of a redirected pipe occupies a thread-pool thread until the child writes or exits. Two of them per
    // running program leave a PC with two processors no thread for timers and continuations, so cancellation came seconds late.
    private static Task ReadOnOwnThread(StreamReader reader, StringBuilder capture, Action<string>? lineCallback) =>
        Task.Factory.StartNew(() => Read(reader, capture, lineCallback), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Read(StreamReader reader, StringBuilder capture, Action<string>? lineCallback)
    {
        while (reader.ReadLine() is { } line)
        {
            // Keep the start and the end of very long output: FFmpeg reports an input read error on one of its last lines.
            var kept = line.Length > MaximumCapturedCharacters / 2 ? line[..(MaximumCapturedCharacters / 2)] : line;
            if (capture.Length + kept.Length > MaximumCapturedCharacters) capture.Remove(MaximumCapturedCharacters / 4, capture.Length / 2);
            capture.AppendLine(kept);
            lineCallback?.Invoke(line);
        }
    }
}
