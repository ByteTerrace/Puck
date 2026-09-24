using System.Diagnostics;
using System.Text;
using Puck.Hosting;
using Puck.Networking;

namespace Puck.Cli;

// The CLI's child-process boundary, over Puck.Hosting's ChildProcess. The tool runner (RunAsync, and RunCheckedAsync
// over it) adds the one CLI concern ChildProcess lacks, resolving the az and npm cmd.exe launchers to their
// interpreters, and otherwise is ChildProcess.RunAsync. The proof runner (RunCaptured) differs because a proof judges
// the transcript itself: it records every line with its sequence and arrival time, can hold stdin open for a
// continuation handshake, and reports a timeout as data for the proof to judge. A synchronous caller waits on
// RunAsync. Both runners own the pipe lifecycle, because waiting for a child before draining both streams can
// deadlock, and returning before the pumps finish loses the tail that often names a crash.
internal static class CliProcess {
    // At most three UTF-8 bytes a character, so the head stays inside the smallest default pipe buffer (4 KiB).
    private const int InputHeadCharacters = 1024;

    // Runs one tool to exit and requires exit code zero: a nonzero exit throws InvalidOperationException naming the
    // diagnostics stream (captured standard output can be a token, so it is never repeated), and a timeout throws
    // TimeoutException. A credential may travel on stdin (docker login --password-stdin), never in an argument.
    internal static async Task<string> RunCheckedAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, bool capture = false, string? input = null, TimeSpan? timeout = null, TimeProvider? clock = null, CancellationToken cancellationToken = default) {
        var run = await RunAsync(
            arguments: arguments,
            cancellationToken: cancellationToken,
            capture: capture,
            clock: clock,
            fileName: fileName,
            input: input,
            timeout: timeout,
            workingDirectory: workingDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (run.TimedOut) {
            throw new TimeoutException(message: $"{Path.GetFileName(path: fileName)} did not exit within {timeout}. {run.Stderr}".TrimEnd());
        }
        if (run.ExitCode != 0) {
            throw new InvalidOperationException(message: $"{Path.GetFileName(path: fileName)} exited with code {run.ExitCode}. {run.Stderr}".TrimEnd());
        }
        if (
            capture &&
            !string.IsNullOrWhiteSpace(value: run.Stderr)
        ) { Console.Error.WriteLine(value: run.Stderr); }
        return run.Stdout;
    }
    // Runs one tool to exit through ChildProcess.RunAsync, which owns the timeout, tree kill and stream draining, after
    // resolving the az and npm launchers on Windows.
    internal static async Task<ChildProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, bool capture = true, string? input = null, TimeSpan? timeout = null, TimeProvider? clock = null, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var command = arguments.ToList();

        // The cmd.exe launchers behind az and npm re-parse their arguments; their interpreters take a clean vector.
        if (
            OperatingSystem.IsWindows() &&
            (fileName is "az" or "npm")
        ) {
            var home = Path.GetDirectoryName(path: FindOnPath(name: (fileName + ".cmd")))!;

            if (fileName == "az") {
                fileName = Path.GetFullPath(path: Path.Combine(
                    path1: home,
                    path2: "../python.exe"
                ));
                command.InsertRange(
                    collection: ["-I", "-B", "-X", "utf8", "-m", "azure.cli"],
                    index: 0
                );
            } else {
                fileName = Path.Combine(
                    path1: home,
                    path2: "node.exe"
                );
                command.Insert(
                    index: 0,
                    item: Path.Combine(
                        path1: home,
                        path2: "node_modules/npm/bin/npm-cli.js"
                    )
                );
            }
        }
        return await ChildProcess.RunAsync(
            arguments: command,
            cancellationToken: cancellationToken,
            capture: capture,
            clock: clock,
            fileName: fileName,
            input: input,
            timeout: timeout,
            workingDirectory: workingDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static string FindOnPath(string name) {
        foreach (var directory in (Environment.GetEnvironmentVariable(variable: "PATH") ?? "").Split(Path.PathSeparator)) {
            var path = Path.Combine(
                path1: directory.Trim(trimChar: '"'),
                path2: name
            );

            if (File.Exists(path: path)) { return path; }
        }
        throw new FileNotFoundException(message: $"Cannot find {name} on PATH.");
    }
    private static async Task<string> PumpAsync(
        StreamReader reader,
        CliProcessOutputStream stream,
        List<CliProcessOutputLine> events,
        object eventGate,
        Func<long> nextSequence,
        long startedAt,
        Action<CliProcessOutputLine>? onOutput
    ) {
        var text = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false) is { } line) {
            text.AppendLine(value: line);

            lock (eventGate) {
                var observed = new CliProcessOutputLine(
                    ElapsedMilliseconds: Stopwatch.GetElapsedTime(startingTimestamp: startedAt).TotalMilliseconds,
                    Line: line,
                    Sequence: nextSequence(),
                    Stream: stream
                );

                events.Add(item: observed);
                onOutput?.Invoke(observed);
            }
        }

        return text.ToString();
    }
    private static async Task<CliProcessResult> RunCapturedAsync(string fileName, IReadOnlyList<string> arguments, string input, TimeSpan timeout,
        Func<CliProcessOutputLine, bool>? continueWhen, string continuationInput, Task? continuationGate, TimeProvider clock, CancellationToken cancellationToken, string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment) {
        cancellationToken.ThrowIfCancellationRequested();

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
            WorkingDirectory = (workingDirectory ?? string.Empty),
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }
        foreach (var (name, value) in (environment ?? new Dictionary<string, string?>())) {
            if (value is null) {
                _ = startInfo.Environment.Remove(key: name);
            } else {
                startInfo.Environment[name] = value;
            }
        }

        var startedAt = Stopwatch.GetTimestamp();
        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));
        // A World treats a pipe still empty at its first read as idle and starts stepping, so the input's first bytes are
        // written here, before any await can yield to a busy thread pool. The head stays under a pipe buffer, so this
        // write never blocks on a child that has not started reading.
        var head = Math.Min(
            val1: input.Length,
            val2: InputHeadCharacters
        );

        if (head != 0) {
            try {
                process.StandardInput.Write(buffer: input.AsSpan(
                    length: head,
                    start: 0
                ));
                process.StandardInput.Flush();
            } catch (IOException) {
                // An early-exiting child closes its pipe; the writer below meets the same pipe and settles it.
            }

            input = input[head..];
        }

        // A caller's cancellation takes the timeout's path: the whole tree is killed and both streams drained.
        using var cancellation = new OperationDeadline(
            caller: cancellationToken,
            timeProvider: clock,
            timeout: timeout
        );
        var events = new List<CliProcessOutputLine>();
        var eventGate = new object();
        var sequence = 0L;
        var continuation = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = process.WaitForExitAsync(cancellationToken: cancellation.Token);
        // Both pumps invoke this under eventGate, preserving observation order for stateful predicates.
        void Observe(CliProcessOutputLine line) {
            if (continueWhen?.Invoke(line) == true) { continuation.TrySetResult(); }
        }
        var stdout = PumpAsync(
            reader: process.StandardOutput,
            stream: CliProcessOutputStream.Stdout,
            events: events,
            eventGate: eventGate,
            nextSequence: () => Interlocked.Increment(location: ref sequence),
            startedAt: startedAt,
            onOutput: ((continueWhen is null) ? null : Observe)
        );
        var stderr = PumpAsync(
            reader: process.StandardError,
            stream: CliProcessOutputStream.Stderr,
            events: events,
            eventGate: eventGate,
            nextSequence: () => Interlocked.Increment(location: ref sequence),
            startedAt: startedAt,
            onOutput: ((continueWhen is null) ? null : Observe)
        );
        var inputPump = WriteInputAsync(
            writer: process.StandardInput,
            input: input,
            continueAfter: ((continueWhen is null)
                ? null
                : Task.WhenAny(
                    task1: ((continuationGate is null)
                        ? continuation.Task
                        : Task.WhenAll(
                            continuation.Task,
                            continuationGate
                        )
                    ),
                    task2: exited
                )
            ),
            continuationInput: continuationInput,
            cancellationToken: cancellation.Token
        );
        var timedOut = false;

        try {
            await Task.WhenAll(
                exited,
                inputPump
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested) {
            timedOut = true;

            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) {
                // The child won the race with the timeout. Waiting below still drains both streams completely.
            }

            await process.WaitForExitAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
            try { process.StandardInput.Close(); } catch (IOException) { /* The killed child's pipe is already gone. */ }
        }

        var streams = await Task.WhenAll(
            stdout,
            stderr
        ).ConfigureAwait(continueOnCapturedContext: false);

        cancellationToken.ThrowIfCancellationRequested();

        return new CliProcessResult(
            ExitCode: process.ExitCode,
            OutputLines: events.OrderBy(keySelector: static line => line.Sequence).ToArray(),
            Stderr: streams[1],
            Stdout: streams[0],
            TimedOut: timedOut
        );
    }
    private static async Task WriteInputAsync(StreamWriter writer, string input, CancellationToken cancellationToken,
        Task? continueAfter, string continuationInput) {
        try {
            if (input.Length != 0) {
                await writer.WriteAsync(
                    buffer: input.AsMemory(),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (continueAfter is not null) {
                await writer.FlushAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                await continueAfter.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                await writer.WriteAsync(buffer: continuationInput.AsMemory(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (IOException) {
            // An early-exiting child closes its pipe. The missing runner-owned terminal response makes the proof fail;
            // the writer does not replace that decision with an infrastructure exception.
        }
        // EOF is the one-shot contract's final input, sent only when the input completed. A timeout leaves stdin open
        // so the kill, not an EOF the child might treat as its cue to proceed, is what ends the child.
        writer.Close();
    }

    /// <summary>Gets what remains of a suite-wide time budget after a running clock's elapsed time. The result is
    /// zero or negative once the budget is spent.</summary>
    /// <param name="clock">The running suite clock.</param>
    /// <param name="budget">The suite-wide time budget.</param>
    /// <remarks>A caller must refuse its work outright once this is too small to hold it, and never clamp a child's
    /// timeout down to the remainder: <see cref="RunCaptured"/> kills a child whose timeout elapses, and on Windows a
    /// killed child reports exit code -1 with both streams empty — indistinguishable from a failure to launch.</remarks>
    public static TimeSpan RemainingBudget(Stopwatch clock, TimeSpan budget) => (budget - clock.Elapsed);
    // An optional output predicate releases a final stdin chunk. Keep stdin open while waiting, flush the initial
    // chunk first, and stop waiting if the child exits or times out. A continuation gate also holds that chunk until
    // the gate completes, so several children can be released together. Ordinary one-shot callers are unchanged. The
    // timeout runs on clock, the system clock unless a caller supplies one; Timeout.InfiniteTimeSpan sets no deadline,
    // leaving cancellationToken as the run's only bound. Cancelling cancellationToken kills the
    // child's whole tree like a timeout, waits for it to exit, and then throws OperationCanceledException; a token
    // already cancelled starts nothing. The child starts in workingDirectory, or the caller's own when it is null, and
    // inherits this process's environment with each environment entry applied: a value sets the variable, null removes it.
    public static CliProcessResult RunCaptured(string fileName, IReadOnlyList<string> arguments, string input, TimeSpan timeout,
        Func<CliProcessOutputLine, bool>? continueWhen = null, string continuationInput = "", TimeProvider? clock = null, Task? continuationGate = null,
        CancellationToken cancellationToken = default, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null) =>
        RunCapturedAsync(
            arguments: arguments,
            cancellationToken: cancellationToken,
            clock: (clock ?? TimeProvider.System),
            continuationGate: continuationGate,
            continuationInput: continuationInput,
            continueWhen: continueWhen,
            environment: environment,
            fileName: fileName,
            input: input,
            timeout: timeout,
            workingDirectory: workingDirectory
        ).GetAwaiter().GetResult();
}
internal enum CliProcessOutputStream {
    Stdout,
    Stderr,
}
// Observation time includes process launch and pipe delivery; it is an upper bound on when the child wrote the line.
internal sealed record CliProcessOutputLine(string Line, long Sequence, CliProcessOutputStream Stream, double ElapsedMilliseconds);
internal sealed record CliProcessResult(
    int ExitCode,
    IReadOnlyList<CliProcessOutputLine> OutputLines,
    string Stderr,
    string Stdout,
    bool TimedOut
);
