using System.Diagnostics;
using System.Text;
using Puck.Networking;

namespace Puck.Hosting;

/// <summary>
/// <para>Starts child processes from an argument vector, never a shell expression, and owns their pipe lifecycle.</para>
/// <para><see cref="RunAsync"/> runs a tool to exit. It reads both captured streams while the child runs, because
/// waiting for a child before draining its streams deadlocks once the child fills a pipe buffer. It also waits for both
/// reads to finish before returning, because returning early loses the tail that often names a crash. Its bound is a
/// timeout on a caller-supplied clock, or cancellation; either kills the whole process tree.</para>
/// <para>A read ends at end of stream, which comes only once every holder of the pipe's write end has closed it. A
/// process the child started with inherited handles holds that end for its own lifetime, and a build node or compiler
/// server outlives the child by many minutes. So once the child has exited, <see cref="DrainAfterExitAsync{T}"/> gives
/// the reads <see cref="ExitDrainGrace"/> to finish, and every run ends through it.</para>
/// <para><see cref="StartRedirected"/> starts a companion that the caller drives over its own lifetime. The caller
/// then owns every redirected stream and must drain each one it does not close.</para>
/// </summary>
public static class ChildProcess {
    /// <summary>How long to wait for EOF after exit. At expiry, readers consume the bytes still buffered in the pipe
    /// before ending, so scheduling delays cannot truncate the child's output.</summary>
    public static readonly TimeSpan ExitDrainGrace = TimeSpan.FromSeconds(seconds: 5);

    /// <summary>Runs one tool to exit and returns its exit code with both streams read raw. A child that exits before
    /// reading all of its input ends the write, not the run.</summary>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="arguments">The argument vector, passed through <see cref="ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="workingDirectory">The child's working directory, or the caller's own when it is <see langword="null"/>.</param>
    /// <param name="capture">The capture switch: <see langword="true"/> reads both output streams as UTF-8, and
    /// <see langword="false"/> lets the child inherit the console's output streams and leaves both texts empty.</param>
    /// <param name="input">The text written to the child's standard input, which is then closed. When it is
    /// <see langword="null"/>, standard input is inherited.</param>
    /// <param name="timeout">The bound on the run, measured on <paramref name="clock"/>. <see langword="null"/> or
    /// <see cref="Timeout.InfiniteTimeSpan"/> leaves <paramref name="cancellationToken"/> as the only bound.</param>
    /// <param name="clock">The clock the timeout runs on; system time when it is <see langword="null"/>.</param>
    /// <param name="cancellationToken">The token that cancels the run.</param>
    /// <returns>The exit code and both captured texts. The texts hold everything the child wrote. A process that
    /// inherited its streams contributes through the final buffer snapshot after <see cref="ExitDrainGrace"/> on
    /// <paramref name="clock"/>, and never holds the run open. A timeout kills the whole tree, drains both streams, and
    /// answers <see cref="ChildProcessResult.TimedOut"/> rather than throwing.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. A token already
    /// cancelled starts nothing; one cancelled during the run first kills the whole tree and drains both streams.</exception>
    /// <exception cref="InvalidOperationException">The child could not be started.</exception>
    public static async Task<ChildProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, bool capture = true, string? input = null, TimeSpan? timeout = null, TimeProvider? clock = null, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        // A child that does not capture shares the caller's console. Given a hidden console of its own instead, a tool
        // that probes its console input with stdin inherited (dotnet project convert) waits on that console forever.
        var info = new ProcessStartInfo(fileName: fileName) {
            CreateNoWindow = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = (input is not null),
            RedirectStandardOutput = capture,
            UseShellExecute = false,
            WorkingDirectory = (workingDirectory ?? string.Empty),
        };

        if (capture) { info.StandardErrorEncoding = Encoding.UTF8; info.StandardOutputEncoding = Encoding.UTF8; }
        foreach (var argument in arguments) { info.ArgumentList.Add(item: argument); }
        var runClock = (clock ?? TimeProvider.System);
        using var process = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: $"Cannot start {fileName}."));
        // Process.Dispose leaves streams that callers accessed open. Close stdin's handle even when a cancelled
        // write bypasses StreamWriter.Close; disposing its buffer would otherwise try to flush into a dead child.
        using var inputStream = (input is not null) ? process.StandardInput.BaseStream : null;
        using var release = new CancellationTokenSource();
        using var output = capture ? OpenOutputReader(reader: process.StandardOutput, release: release.Token) : null;
        using var errors = capture ? OpenOutputReader(reader: process.StandardError, release: release.Token) : null;
        using var deadline = new OperationDeadline(
            caller: cancellationToken,
            timeProvider: runClock,
            timeout: (timeout ?? Timeout.InfiniteTimeSpan)
        );
        Task<string>[] pumps = (capture
            ? [output!.ReadToEndAsync(), errors!.ReadToEndAsync()]
            : [Task.FromResult(result: ""), Task.FromResult(result: "")]
        );
        var timedOut = false;

        try {
            if (input is not null) {
                try {
                    await process.StandardInput.WriteAsync(
                        buffer: input.AsMemory(),
                        cancellationToken: deadline.Token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    process.StandardInput.Close();
                } catch (IOException) {
                    // A child that exits early closes its pipe; its exit code, not the write, is the run's answer.
                }
            }
            await process.WaitForExitAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested) {
            timedOut = true;
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
        }

        var drained = await DrainAfterExitAsync(
            cancellationToken: cancellationToken,
            clock: runClock,
            pumps: pumps,
            release: release
        ).ConfigureAwait(continueOnCapturedContext: false);

        cancellationToken.ThrowIfCancellationRequested();
        return new ChildProcessResult(
            ExitCode: process.ExitCode,
            Stderr: drained[1],
            Stdout: drained[0],
            TimedOut: timedOut
        );
    }
    /// <summary>Finishes reading the streams of a child that has exited. Waits for every pump to reach end of stream
    /// for at most <see cref="ExitDrainGrace"/> on <paramref name="clock"/>, then cancels <paramref name="release"/>,
    /// so a pump using <see cref="OpenOutputReader"/> consumes the remaining buffered bytes and ends.</summary>
    /// <typeparam name="T">The result each pump returns.</typeparam>
    /// <param name="pumps">The reads of the child's output streams, using <see cref="OpenOutputReader"/> with
    /// <paramref name="release"/>'s token.</param>
    /// <param name="release">The source of the token every pump reads with.</param>
    /// <param name="clock">The clock the grace runs on.</param>
    /// <param name="cancellationToken">Ends the grace early; pumps still settle before this method returns.</param>
    /// <returns>Each pump's result, in the order of <paramref name="pumps"/>.</returns>
    public static async Task<T[]> DrainAfterExitAsync<T>(Task<T>[] pumps, CancellationTokenSource release, TimeProvider clock, CancellationToken cancellationToken = default) {
        var drained = Task.WhenAll(tasks: pumps);
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        var expired = Task.Delay(delay: ExitDrainGrace, timeProvider: clock, cancellationToken: grace.Token);

        try {
            if (await Task.WhenAny(drained, expired).ConfigureAwait(continueOnCapturedContext: false) != drained) {
                try {
                    await release.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
                } finally {
                    // Even a failing cancellation callback must not leave reads racing disposal of their streams.
                    await drained.ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            return await drained.ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            await grace.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    /// <summary>Opens the sole reader of a freshly redirected output pipe. Releasing it after child exit cancels
    /// a pending byte read, drains the bytes still in the pipe, and presents EOF to the decoder. Partial characters
    /// and lines therefore survive release. The reader must finish before it or the process is disposed.</summary>
    /// <param name="reader">The child's standard output or error, which must not have been read yet.</param>
    /// <param name="release">Ends waiting for more bytes once the child has exited.</param>
    /// <returns>A reader owned by the caller, which also disposes the supplied reader and its pipe.</returns>
    public static StreamReader OpenOutputReader(StreamReader reader, CancellationToken release) => new(
        stream: new ExitDrainStream(reader: reader, release: release),
        encoding: reader.CurrentEncoding,
        detectEncodingFromByteOrderMarks: true
    );
    /// <summary>Starts a companion the caller drives itself: reading its streams as they arrive, writing to it, and
    /// killing or waiting on it at its own pace.</summary>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="arguments">The argument vector, passed through <see cref="ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="workingDirectory">The child's working directory, or the caller's own when it is <see langword="null"/>.</param>
    /// <returns>The started process. Standard input, output and error are all redirected and the child inherits no
    /// console; the caller must drain each output stream, or the child blocks once a pipe buffer fills.</returns>
    /// <exception cref="InvalidOperationException">The child could not be started.</exception>
    public static Process StartRedirected(string fileName, IEnumerable<string> arguments, string? workingDirectory = null) {
        var info = new ProcessStartInfo(fileName: fileName) {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = (workingDirectory ?? string.Empty),
        };

        foreach (var argument in arguments) { info.ArgumentList.Add(item: argument); }

        return (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: $"Cannot start {fileName}."));
    }
}
/// <summary>The outcome of one <see cref="ChildProcess.RunAsync"/> run.</summary>
/// <param name="ExitCode">The child's exit code. After a timeout it is the killed child's and means nothing.</param>
/// <param name="Stderr">The captured standard error, or empty without capture.</param>
/// <param name="Stdout">The captured standard output, or empty without capture.</param>
/// <param name="TimedOut">Whether the run's timeout expired and the tree was killed.</param>
public readonly record struct ChildProcessResult(int ExitCode, string Stderr, string Stdout, bool TimedOut = false);
