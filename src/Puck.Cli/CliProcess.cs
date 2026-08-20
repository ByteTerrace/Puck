using System.Diagnostics;
using System.Text;

namespace Puck.Cli;

// The one child-process boundary for CLI verbs: ordinary tools can inherit the console, while proof runners capture
// both streams without merging them. The captured shape owns the pipe lifecycle because waiting for a child before
// draining both streams can deadlock, and returning before the pumps finish loses the tail that often names a crash.
internal static class CliProcess {
    public static int RunStreamed(string fileName, params string[] arguments) {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));

        process.WaitForExit();

        return process.ExitCode;
    }
    public static CliProcessResult RunCaptured(string fileName, IReadOnlyList<string> arguments, string input, TimeSpan timeout) =>
        RunCapturedAsync(arguments: arguments, fileName: fileName, input: input, timeout: timeout).GetAwaiter().GetResult();

    /// <summary>Gets what remains of a suite-wide time budget after a running clock's elapsed time, floored at one
    /// millisecond so a caller never passes a zero or negative timeout to <see cref="RunCaptured"/>.</summary>
    /// <param name="clock">The running suite clock.</param>
    /// <param name="budget">The suite-wide time budget.</param>
    public static TimeSpan RemainingBudget(Stopwatch clock, TimeSpan budget) {
        var remaining = (budget - clock.Elapsed);

        return ((remaining > TimeSpan.Zero) ? remaining : TimeSpan.FromMilliseconds(value: 1));
    }

    private static async Task<CliProcessResult> RunCapturedAsync(string fileName, IReadOnlyList<string> arguments, string input, TimeSpan timeout) {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            StandardOutputEncoding = utf8NoBom,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));
        using var cancellation = new CancellationTokenSource(delay: timeout);
        var events = new List<CliProcessOutputLine>();
        var eventGate = new object();
        var sequence = 0L;
        var stdout = PumpAsync(reader: process.StandardOutput, stream: CliProcessOutputStream.Stdout, events: events, eventGate: eventGate, nextSequence: () => Interlocked.Increment(location: ref sequence));
        var stderr = PumpAsync(reader: process.StandardError, stream: CliProcessOutputStream.Stderr, events: events, eventGate: eventGate, nextSequence: () => Interlocked.Increment(location: ref sequence));
        var inputPump = WriteInputAsync(writer: process.StandardInput, input: input, cancellationToken: cancellation.Token);
        var timedOut = false;

        try {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken: cancellation.Token), inputPump).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            timedOut = true;

            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) {
                // The child won the race with the timeout. Waiting below still drains both streams completely.
            }

            await process.WaitForExitAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
        }

        var streams = await Task.WhenAll(stdout, stderr).ConfigureAwait(continueOnCapturedContext: false);

        return new CliProcessResult(
            ExitCode: process.ExitCode,
            OutputLines: events.OrderBy(keySelector: static line => line.Sequence).ToArray(),
            Stderr: streams[1],
            Stdout: streams[0],
            TimedOut: timedOut
        );
    }
    private static async Task<string> PumpAsync(
        StreamReader reader,
        CliProcessOutputStream stream,
        List<CliProcessOutputLine> events,
        object eventGate,
        Func<long> nextSequence
    ) {
        var text = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false) is { } line) {
            text.AppendLine(value: line);

            lock (eventGate) {
                events.Add(item: new CliProcessOutputLine(Line: line, Sequence: nextSequence(), Stream: stream));
            }
        }

        return text.ToString();
    }
    private static async Task WriteInputAsync(StreamWriter writer, string input, CancellationToken cancellationToken) {
        try {
            if (input.Length != 0) {
                await writer.WriteAsync(buffer: input.AsMemory(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (IOException) {
            // An early-exiting child closes its pipe. The missing runner-owned terminal response makes the proof fail;
            // the writer does not replace that decision with an infrastructure exception.
        } finally {
            writer.Close();
        }
    }
}
internal enum CliProcessOutputStream {
    Stdout,
    Stderr,
}
internal sealed record CliProcessOutputLine(string Line, long Sequence, CliProcessOutputStream Stream);
internal sealed record CliProcessResult(
    int ExitCode,
    IReadOnlyList<CliProcessOutputLine> OutputLines,
    string Stderr,
    string Stdout,
    bool TimedOut
);
