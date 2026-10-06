using Diga.Core.Media;

namespace Diga.Tests;

/// <summary>
/// What <see cref="ProcessRunner"/> keeps of the output of a tool. This assembly, started as a program
/// (<see cref="ProcessRunnerProbe"/>), stands in for the tool, so no FFmpeg is needed.
/// </summary>
public sealed class ProcessRunnerOutputTests
{
    /// <summary>
    /// FFmpeg names an input error on one of its last lines, after any number of warnings, and can still exit with 0.
    /// <see cref="RemuxService"/> looks for that line in what was kept, so cutting the output must never cut its end.
    /// </summary>
    [Fact]
    public async Task TheLineThatNamesTheFailureSurvivesMoreOutputThanIsKept()
    {
        const int kept = 4 * 1024 * 1024;
        var written = ProcessRunnerProbe.LongOutputLines * 100L;
        Assert.True(written > kept, "The probe has to write more than is kept.");
        var (program, arguments) = ProcessRunnerProbe.CommandLine(ProcessRunnerProbe.LongOutputCommand);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new ProcessRunner().RunAsync(program, arguments, cancellationToken: timeout.Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("progress=end", result.StandardOutput.Trim());
        // Bounded, and not by throwing everything away.
        Assert.InRange(result.StandardError.Length, kept / 4, kept);
        var lines = result.StandardError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ProcessRunnerProbe.LongOutputFirstLine, lines[0]);
        Assert.Equal(ProcessRunnerProbe.LongOutputWarning(0), lines[1]);
        Assert.Equal(ProcessRunnerProbe.LongOutputWarning(ProcessRunnerProbe.LongOutputLines - 1), lines[^2]);
        Assert.Equal(ProcessRunnerProbe.LongOutputLastLine, lines[^1]);
        // What was left out came from the middle: the warnings of the first and of the last ten thousand lines are all there.
        Assert.InRange(lines.Length, 20_000, ProcessRunnerProbe.LongOutputLines);
        Assert.Equal(Enumerable.Range(0, 10_000).Select(ProcessRunnerProbe.LongOutputWarning), lines[1..10_001]);
        Assert.Equal(Enumerable.Range(ProcessRunnerProbe.LongOutputLines - 10_000, 10_000).Select(ProcessRunnerProbe.LongOutputWarning), lines[^10_001..^1]);
        // The two things the application does with the output still work on what is left.
        Assert.Contains("Error during demuxing", result.StandardError, StringComparison.Ordinal);
        Assert.EndsWith(ProcessRunnerProbe.LongOutputLastLine, result.ErrorSummary, StringComparison.Ordinal);
        Assert.EndsWith(ProcessRunnerProbe.LongOutputLastLine, result.ErrorLog.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// FFmpeg and FFprobe write UTF-8 whatever the code page of Windows is. Read in that code page, the name of a file in the
    /// folder of a user called Łukasz came out as other characters in the message and in the log.
    /// </summary>
    [Fact]
    public async Task AFileNameOutsideAsciiArrivesUnchangedInWhatAToolWrote()
    {
        var (program, arguments) = ProcessRunnerProbe.CommandLine(ProcessRunnerProbe.Utf8Command);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var lines = new List<string>();
        var result = await new ProcessRunner().RunAsync(program, arguments, lines.Add, timeout.Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(ProcessRunnerProbe.Utf8Line, result.StandardError.TrimEnd());
        Assert.Equal(ProcessRunnerProbe.Utf8Line, result.StandardOutput.TrimEnd());
        Assert.Equal([ProcessRunnerProbe.Utf8Line], lines);
        Assert.Equal(ProcessRunnerProbe.Utf8Line, result.ErrorSummary);
        Assert.Contains("Łukasz", result.ErrorLog, StringComparison.Ordinal);
    }
}
