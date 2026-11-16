using System.Diagnostics;
using System.Text;

namespace Puck.Cli;

/// <summary>One line a World wrote.</summary>
/// <param name="Index">The line's position in the World's output, counting both streams in arrival order.</param>
/// <param name="Stream">The stream the line came on.</param>
/// <param name="Text">The line.</param>
public sealed record WorldOutputLine(int Index, CliProcessOutputStream Stream, string Text);
/// <summary>
/// A real <c>Puck.World</c> process driven line by line over its standard input, with every output line recorded in
/// arrival order, so a run can send a line, read what the World answered, and decide the next line from it. Both
/// streams are also written to log files in the run's directory. Only an admitted launch starts one
/// (<see cref="WorldLaunch.Start"/>); it holds that launch's GPU lease until the World has exited and it is stopped or
/// disposed.
/// </summary>
public sealed class WorldProcess : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object m_gate = new();
    private readonly List<WorldOutputLine> m_lines = [];
    private readonly CancellationTokenSource m_release = new();

    private readonly Process m_process;
    private readonly StreamWriter m_stderr;
    private readonly StreamWriter m_stdout;

    private readonly WorldReadiness m_readiness;

    private IDisposable? m_gpu;
    private Task[] m_pumps = [];
    private Task? m_drained;

    private WorldProcess(Process process, StreamWriter stdout, StreamWriter stderr, IDisposable? gpu, WorldReadiness readiness) {
        m_gpu = gpu;
        m_readiness = readiness;
        m_process = process;
        m_stderr = stderr;
        m_stdout = stdout;
    }

    /// <summary>Gets the World's process id, for stopping exactly this process.</summary>
    public int ProcessId => m_process.Id;
    /// <summary>Gets a value indicating whether the World has exited.</summary>
    public bool HasExited => m_process.HasExited;
    /// <summary>Gets how many lines the World has written.</summary>
    public int LineCount {
        get {
            lock (m_gate) {
                return m_lines.Count;
            }
        }
    }

    internal static WorldProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment, string workingDirectory, string logDirectory, string logPrefix, IDisposable? gpu, Func<Process, WorldReadiness> readiness) {
        _ = Directory.CreateDirectory(path: logDirectory);

        // Acquire fallible log sinks before spawning a child. A failure here starts no World.
        var stdout = new StreamWriter(path: Path.Combine(path1: logDirectory, path2: $"{logPrefix}stdout.log"), append: false, encoding: Utf8);
        StreamWriter? stderr = null;
        Process? process = null;
        WorldProcess world;

        try {
            stderr = new StreamWriter(path: Path.Combine(path1: logDirectory, path2: $"{logPrefix}stderr.log"), append: false, encoding: Utf8);
            process = WorldLaunch.Contain(process: Puck.Abstractions.ChildProcess.StartRedirected(arguments: arguments, encoding: Utf8, environment: environment, fileName: fileName, workingDirectory: workingDirectory));
            world = new WorldProcess(gpu: gpu, process: process, readiness: readiness(arg: process), stderr: stderr, stdout: stdout);
            Console.Error.WriteLine(value: $"[world.launch: started process {process.Id}]");
        } catch {
            stdout.Dispose();
            stderr?.Dispose();
            if (process is not null) {
                KillTree(process: process);
                process.Dispose();
            }
            throw;
        }

        // Both readers open before either pump starts, so each is the sole reader of its pipe from the first byte.
        var output = Puck.Abstractions.ChildProcess.OpenOutputReader(reader: process.StandardOutput, release: world.m_release.Token);
        var errors = Puck.Abstractions.ChildProcess.OpenOutputReader(reader: process.StandardError, release: world.m_release.Token);

        world.m_pumps = [world.PumpAsync(reader: output, stream: CliProcessOutputStream.Stdout), world.PumpAsync(reader: errors, stream: CliProcessOutputStream.Stderr)];
        return world;
    }
    /// <summary>Kills a process's whole tree and waits for it to exit; a process that already exited is left as it is.</summary>
    /// <param name="process">The process.</param>
    public static void KillTree(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit();
        } catch (InvalidOperationException) {
            // The process exited between the check and the kill, or never started.
        }
    }
    /// <summary>Sends one console line.</summary>
    /// <param name="line">The line, without its terminator.</param>
    /// <returns><see langword="false"/> when the World has closed its input.</returns>
    public bool Send(string line) {
        try {
            m_process.StandardInput.WriteLine(value: line);
            m_process.StandardInput.Flush();

            return true;
        } catch (IOException) {
            return false;
        }
    }
    /// <summary>Waits for a line a predicate accepts, looking at the lines from a position on.</summary>
    /// <param name="from">The first line index to look at.</param>
    /// <param name="accept">Whether a line is the one waited for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellation">Ends the wait promptly when cancelled.</param>
    /// <returns>The line, or <see langword="null"/> when cancelled, timed out or the World exited.</returns>
    public WorldOutputLine? WaitFor(int from, Func<WorldOutputLine, bool> accept, TimeSpan timeout, CancellationToken cancellation = default) {
        var deadline = (Stopwatch.GetTimestamp() + ((long)(timeout.TotalSeconds * Stopwatch.Frequency)));
        var cursor = from;
        using var registration = cancellation.Register(callback: () => {
            lock (m_gate) {
                Monitor.PulseAll(obj: m_gate);
            }
        });

        while (true) {
            lock (m_gate) {
                for (; (cursor < m_lines.Count); cursor++) {
                    if (accept(arg: m_lines[cursor])) {
                        return m_lines[cursor];
                    }
                }

                var remaining = TimeSpan.FromSeconds(value: ((deadline - Stopwatch.GetTimestamp()) / ((double)Stopwatch.Frequency)));

                if (cancellation.IsCancellationRequested) {
                    return null;
                }
                if ((remaining <= TimeSpan.Zero) || m_process.HasExited) {
                    // A line that arrived between the last scan and the exit is still in the list: scan once more.
                    if (m_process.HasExited && (cursor < m_lines.Count)) {
                        continue;
                    }

                    return null;
                }

                _ = Monitor.Wait(obj: m_gate, timeout: TimeSpan.FromMilliseconds(value: Math.Min(val1: 500d, val2: remaining.TotalMilliseconds)));
            }
        }
    }
    /// <summary>Returns the lines from a position on.</summary>
    /// <param name="from">The first line index.</param>
    /// <returns>A copy of the lines.</returns>
    public IReadOnlyList<WorldOutputLine> LinesFrom(int from) {
        lock (m_gate) {
            return [.. m_lines.Skip(count: from)];
        }
    }
    /// <summary>Waits for the World to exit, killing exactly this process tree when it does not within the timeout.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The exit code, or <see langword="null"/> when the World was killed.</returns>
    public int? WaitForExit(TimeSpan timeout) {
        if (m_process.WaitForExit(timeout: timeout)) {
            try {
                m_process.WaitForExit();
                JoinPumps();
            } finally {
                ReleaseGpu();
            }

            return m_process.ExitCode;
        }

        Kill();

        return null;
    }
    /// <summary>Stops this process tree, settles both output pumps and releases its GPU lease. The pumps settle on every
    /// path, a failed termination included (the drain's release ends their reads whether or not the World still runs),
    /// so when this returns or throws no pump touches a log or reader again. The GPU lease is released only once the
    /// World has exited: a World that could not be stopped may still be using the GPU, so its lease stays held until
    /// this process ends.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The process tree could not be terminated.</exception>
    /// <exception cref="AggregateException">Termination failed and a pump also faulted.</exception>
    public void Kill() {
        Exception? termination = null;

        try {
            KillTree(process: m_process);
        } catch (Exception exception) {
            termination = exception;
        }
        try {
            JoinPumps();
        } catch (Exception exception) when (termination is not null) {
            throw new AggregateException(termination, exception);
        } finally {
            if (Exited()) { ReleaseGpu(); }
        }
        if (termination is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source: termination); }
    }
    /// <inheritdoc/>
    public void Dispose() {
        try {
            Kill();
        } finally {
            // Kill settles every pump whether it returns or throws, so no read races the disposal of its reader, log or
            // process.
            m_process.Dispose();
            m_stderr.Dispose();
            m_stdout.Dispose();
            m_release.Dispose();
        }
    }

    // Whether the World is known to have exited; a process that cannot be inspected is not.
    private bool Exited() {
        try {
            return m_process.HasExited;
        } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            return false;
        }
    }

    private void ReleaseGpu() => Interlocked.Exchange(location1: ref m_gpu, value: null)?.Dispose();
    private async Task PumpAsync(StreamReader reader, CliProcessOutputStream stream) {
        using var source = reader;

        try {
            while (await source.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false) is { } text) {
                lock (m_gate) {
                    m_lines.Add(item: new WorldOutputLine(Index: m_lines.Count, Stream: stream, Text: text));

                    var log = ((stream == CliProcessOutputStream.Stdout) ? m_stdout : m_stderr);

                    log.WriteLine(value: text);
                    m_readiness.Observe(line: text, stdout: (stream == CliProcessOutputStream.Stdout));
                    log.Flush();
                    Monitor.PulseAll(obj: m_gate);
                }
            }
        } finally {
            lock (m_gate) {
                Monitor.PulseAll(obj: m_gate);
            }
        }
    }
    // Every caller shares one drain, which ends both pumps within ChildProcess.ExitDrainGrace even when a process the
    // World started still holds a pipe, or the World itself could not be stopped.
    private void JoinPumps() {
        Task drained;

        lock (m_gate) {
            drained = (m_drained ??= Puck.Abstractions.ChildProcess.DrainAfterExitAsync(clock: TimeProvider.System, pumps: m_pumps, release: m_release));
        }
        drained.GetAwaiter().GetResult();
    }
}
