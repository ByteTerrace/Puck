using System.Diagnostics;
using System.Text;

namespace Puck.Cli;

// The one child-process boundary for CLI verbs: ordinary tools can inherit the console, while proof runners capture
// both streams without merging them. The captured shape owns the pipe lifecycle because waiting for a child before
// draining both streams can deadlock, and returning before the pumps finish loses the tail that often names a crash.
internal static class CliProcess {
    // A credential may travel on stdin (docker login --password-stdin), never in an argument or a shell expression.
    internal static async Task<string> RunCheckedAsync(string root, string executable, IEnumerable<string> arguments, bool capture = false, string? input = null, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var command = arguments.ToList();

        // The cmd.exe launchers behind az and npm re-parse their arguments; their interpreters take a clean vector.
        if (
            OperatingSystem.IsWindows() &&
            (executable is "az" or "npm")
        ) {
            var home = Path.GetDirectoryName(path: FindOnPath(name: (executable + ".cmd")))!;

            if (executable == "az") {
                executable = Path.GetFullPath(path: Path.Combine(
                    path1: home,
                    path2: "../python.exe"
                ));
                command.InsertRange(
                    collection: ["-I", "-B", "-X", "utf8", "-m", "azure.cli"],
                    index: 0
                );
            } else {
                executable = Path.Combine(
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
        var info = new ProcessStartInfo(fileName: executable) {
            CreateNoWindow = true,
            RedirectStandardError = capture,
            RedirectStandardInput = (input is not null),
            RedirectStandardOutput = capture,
            UseShellExecute = false,
            WorkingDirectory = root,
        };

        if (capture) { info.StandardErrorEncoding = Encoding.UTF8; info.StandardOutputEncoding = Encoding.UTF8; }
        foreach (var argument in command) { info.ArgumentList.Add(item: argument); }
        using var process = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: $"Cannot start {executable}."));
        var output = (capture
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(result: "")
        );
        var errors = (capture
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(result: "")
        );

        try {
            if (input is not null) {
                await process.StandardInput.WriteAsync(
                    input.AsMemory(),
                    cancellationToken
                ); process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken: cancellationToken);
        } catch (OperationCanceledException) {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None);
            await Task.WhenAll(
                output,
                errors
            );
            throw;
        }
        var text = await output;
        var errorText = await errors;

        // Captured standard output can be a token (`az account get-access-token`), so a failure repeats only the diagnostics stream.
        if (process.ExitCode != 0) {
            throw new InvalidOperationException(message: $"{Path.GetFileName(path: executable)} exited with code {process.ExitCode}. {errorText}".TrimEnd());
        }
        if (
            capture &&
            !string.IsNullOrWhiteSpace(value: errorText)
        ) { Console.Error.WriteLine(value: errorText); }
        return text;
    }
    internal static int RunStreamedInDirectory(string fileName, string workingDirectory, params string[] arguments) {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, WorkingDirectory = workingDirectory };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));

        process.WaitForExit();

        return process.ExitCode;
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
        Func<CliProcessOutputLine, bool>? continueWhen, string continuationInput) {
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

        var startedAt = Stopwatch.GetTimestamp();
        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));
        using var cancellation = new CancellationTokenSource(delay: timeout);
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
            continueAfter: ((continueWhen is null) ? null : Task.WhenAny(task1: continuation.Task, task2: exited)),
            continuationInput: continuationInput,
            cancellationToken: cancellation.Token
        );
        var timedOut = false;

        try {
            await Task.WhenAll(
                exited,
                inputPump
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            timedOut = true;

            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) {
                // The child won the race with the timeout. Waiting below still drains both streams completely.
            }

            await process.WaitForExitAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
        }

        var streams = await Task.WhenAll(
            stdout,
            stderr
        ).ConfigureAwait(continueOnCapturedContext: false);

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
                await writer.WriteAsync(continuationInput.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (IOException) {
            // An early-exiting child closes its pipe. The missing runner-owned terminal response makes the proof fail;
            // the writer does not replace that decision with an infrastructure exception.
        } finally {
            writer.Close();
        }
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
    // chunk first, and stop waiting if the child exits or times out. Ordinary one-shot callers are unchanged.
    public static CliProcessResult RunCaptured(string fileName, IReadOnlyList<string> arguments, string input, TimeSpan timeout,
        Func<CliProcessOutputLine, bool>? continueWhen = null, string continuationInput = "") =>
        RunCapturedAsync(
            arguments: arguments,
            continuationInput: continuationInput,
            continueWhen: continueWhen,
            fileName: fileName,
            input: input,
            timeout: timeout
        ).GetAwaiter().GetResult();
    /// <summary>Spawns <paramref name="fileName"/>, drains both streams to their end exactly as read (no line
    /// splitting or re-joining, so byte content — including line endings — passes through unchanged), waits for
    /// exit, and returns the raw text alongside the exit code. Unlike <see cref="RunCaptured"/> this leaves the
    /// child's standard input inherited from the caller rather than redirected, and never times out or kills the
    /// child — the shape a short, non-interactive, synchronous invocation (a local <c>git</c> query) needs.</summary>
    public static CliRawProcessResult RunCapturedRaw(string fileName, IReadOnlyList<string> arguments) {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = utf8NoBom,
            StandardOutputEncoding = utf8NoBom,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));
        // Both pipes drain concurrently: a child that fills one pipe before closing the other would deadlock a
        // sequential ReadToEnd pair.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();

        process.WaitForExit();

        return new CliRawProcessResult(
            ExitCode: process.ExitCode,
            Stderr: stderr,
            Stdout: stdout
        );
    }
    public static int RunStreamed(string fileName, params string[] arguments) {
        return RunStreamedInDirectory(
            fileName: fileName,
            workingDirectory: Environment.CurrentDirectory,
            arguments: arguments
        );
    }
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
internal readonly record struct CliRawProcessResult(int ExitCode, string Stderr, string Stdout);
