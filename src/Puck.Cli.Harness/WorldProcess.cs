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
/// (<see cref="WorldLaunch.Start"/>); it holds that launch's GPU lease until it is disposed.
/// </summary>
public sealed class WorldProcess : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object m_gate = new();
    private readonly List<WorldOutputLine> m_lines = [];
    private readonly List<Thread> m_pumps = [];

    private readonly Process m_process;
    private readonly StreamWriter m_stderr;
    private readonly StreamWriter m_stdout;

    private IDisposable? m_gpu;

    private WorldProcess(Process process, StreamWriter stdout, StreamWriter stderr, IDisposable? gpu) {
        m_gpu = gpu;
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

    internal static WorldProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment, string workingDirectory, string logDirectory, string logPrefix, IDisposable? gpu) {
        _ = Directory.CreateDirectory(path: logDirectory);

        // Acquire fallible log sinks before spawning a child. A failure here starts no World.
        var stdout = new StreamWriter(path: Path.Combine(path1: logDirectory, path2: $"{logPrefix}stdout.log"), append: false, encoding: Utf8);
        StreamWriter? stderr = null;
        Process? process = null;
        WorldProcess world;

        try {
            stderr = new StreamWriter(path: Path.Combine(path1: logDirectory, path2: $"{logPrefix}stderr.log"), append: false, encoding: Utf8);
            process = WorldLaunch.Contain(process: Puck.Abstractions.ChildProcess.StartRedirected(arguments: arguments, encoding: Utf8, environment: environment, fileName: fileName, workingDirectory: workingDirectory));
            world = new WorldProcess(gpu: gpu, process: process, stderr: stderr, stdout: stdout);
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

        world.Pump(reader: process.StandardOutput, stream: CliProcessOutputStream.Stdout);
        world.Pump(reader: process.StandardError, stream: CliProcessOutputStream.Stderr);

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
            m_process.WaitForExit();
            JoinPumps();
            ReleaseGpu();

            return m_process.ExitCode;
        }

        Kill();

        return null;
    }
    /// <summary>Stops this process tree and releases its GPU lease.</summary>
    public void Kill() {
        KillTree(process: m_process);
        JoinPumps();
        ReleaseGpu();
    }
    /// <inheritdoc/>
    public void Dispose() {
        Kill();
        m_process.Dispose();
        m_stderr.Dispose();
        m_stdout.Dispose();
    }

    private void ReleaseGpu() => Interlocked.Exchange(location1: ref m_gpu, value: null)?.Dispose();
    private void Pump(StreamReader reader, CliProcessOutputStream stream) {
        var thread = new Thread(start: () => {
            while (reader.ReadLine() is { } text) {
                lock (m_gate) {
                    m_lines.Add(item: new WorldOutputLine(Index: m_lines.Count, Stream: stream, Text: text));

                    var log = ((stream == CliProcessOutputStream.Stdout) ? m_stdout : m_stderr);

                    log.WriteLine(value: text);
                    log.Flush();
                    Monitor.PulseAll(obj: m_gate);
                }
            }

            lock (m_gate) {
                Monitor.PulseAll(obj: m_gate);
            }
        }) { IsBackground = true };

        m_pumps.Add(item: thread);
        thread.Start();
    }
    private void JoinPumps() {
        foreach (var pump in m_pumps) {
            _ = pump.Join(timeout: TimeSpan.FromSeconds(value: 5));
        }
    }
}
