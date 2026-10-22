using System.Diagnostics;
using System.Text;

namespace Puck.Cli.Motion;

/// <summary>One line a World wrote.</summary>
/// <param name="Index">The line's position in the World's output, counting both streams in arrival order.</param>
/// <param name="Stream">The stream the line came on.</param>
/// <param name="Text">The line.</param>
public sealed record MotionOutputLine(int Index, CliProcessOutputStream Stream, string Text);
/// <summary>
/// A real <c>Puck.World</c> process driven line by line over its standard input, with every output line recorded in
/// arrival order, so a run can send a line, read what the World answered, and decide the next line from it. Both
/// streams are also written to <c>stdout.log</c> and <c>stderr.log</c> in the run's directory.
/// </summary>
public sealed class MotionWorldProcess : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object m_gate = new();
    private readonly List<MotionOutputLine> m_lines = [];

    private readonly Process m_process;

    private readonly List<Thread> m_pumps = [];

    private readonly StreamWriter m_stderr;
    private readonly StreamWriter m_stdout;

    private MotionWorldProcess(Process process, StreamWriter stdout, StreamWriter stderr) {
        m_process = process;
        m_stderr = stderr;
        m_stdout = stdout;
    }

    /// <summary>Gets the World's process id, for the lease record and for stopping exactly this process.</summary>
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

    /// <summary>Starts a World.</summary>
    /// <param name="artifact">The World build's entry assembly.</param>
    /// <param name="arguments">The World's arguments.</param>
    /// <param name="workingDirectory">The directory the World runs in; relative paths in <paramref name="arguments"/> resolve against it.</param>
    /// <param name="logDirectory">The directory that receives <c>stdout.log</c> and <c>stderr.log</c>.</param>
    /// <returns>The running World.</returns>
    public static MotionWorldProcess Start(string artifact, IReadOnlyList<string> arguments, string workingDirectory, string logDirectory) {
        _ = Directory.CreateDirectory(path: logDirectory);

        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        startInfo.ArgumentList.Add(item: artifact);

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "Failed to start dotnet."));
        var world = new MotionWorldProcess(
            process: process,
            stderr: new StreamWriter(path: Path.Combine(path1: logDirectory, path2: "stderr.log"), append: false, encoding: Utf8),
            stdout: new StreamWriter(path: Path.Combine(path1: logDirectory, path2: "stdout.log"), append: false, encoding: Utf8)
        );

        world.Pump(reader: process.StandardOutput, stream: CliProcessOutputStream.Stdout);
        world.Pump(reader: process.StandardError, stream: CliProcessOutputStream.Stderr);

        return world;
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
    /// <returns>The line, or <see langword="null"/> when the timeout passed or the World exited without it.</returns>
    public MotionOutputLine? WaitFor(int from, Func<MotionOutputLine, bool> accept, TimeSpan timeout) {
        var deadline = (Stopwatch.GetTimestamp() + ((long)(timeout.TotalSeconds * Stopwatch.Frequency)));
        var cursor = from;

        while (true) {
            lock (m_gate) {
                for (; (cursor < m_lines.Count); cursor++) {
                    if (accept(arg: m_lines[cursor])) {
                        return m_lines[cursor];
                    }
                }

                var remaining = TimeSpan.FromSeconds(value: ((deadline - Stopwatch.GetTimestamp()) / ((double)Stopwatch.Frequency)));

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
    public IReadOnlyList<MotionOutputLine> LinesFrom(int from) {
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

            return m_process.ExitCode;
        }

        Kill();

        return null;
    }
    /// <summary>Stops this process tree.</summary>
    public void Kill() {
        try {
            if (!m_process.HasExited) {
                m_process.Kill(entireProcessTree: true);
            }

            m_process.WaitForExit();
        } catch (InvalidOperationException) {
            // The World exited between the check and the kill.
        }

        JoinPumps();
    }
    /// <inheritdoc/>
    public void Dispose() {
        Kill();
        m_process.Dispose();
        m_stderr.Dispose();
        m_stdout.Dispose();
    }

    private void Pump(StreamReader reader, CliProcessOutputStream stream) {
        var thread = new Thread(start: () => {
            while (reader.ReadLine() is { } text) {
                lock (m_gate) {
                    m_lines.Add(item: new MotionOutputLine(Index: m_lines.Count, Stream: stream, Text: text));

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
