using System.Text;

namespace Puck.Launcher;

/// <summary>
/// A once-per-frame BUFFERED stdout for the command pump's result echoes. The default <see cref="Console.Out"/> is a
/// <see cref="StreamWriter"/> with <c>AutoFlush=true</c>, so every <c>WriteLine</c> is its own write syscall — a burst
/// of piped console lines draining in one frame's <c>Collect</c> pays one flush per line (measured ~0.9 ms for a
/// ~372-line burst, a direct contributor to the stdin proof's worst-frame dips). This owns ONE
/// <see cref="StreamWriter"/> over the raw standard-output stream with <c>AutoFlush=false</c>; the pump writes every
/// echo into it during <c>Collect</c> and calls <see cref="Flush"/> exactly once afterward, collapsing the burst to a
/// single write. Strict FIFO is preserved (one writer, appended in submission order).
/// <para>
/// SINGLE-THREADED by contract: the <c>TextCommandSource</c> result callback runs on the window-pump thread during
/// <c>Collect</c>, and the pump flushes on that same thread, so no lock guards the writer. The underlying stream is left
/// OPEN on dispose (the process still owns standard output) — dispose only flushes any buffered tail.
/// </para>
/// </summary>
public sealed class BufferedConsoleOutput : IDisposable {
    private readonly StreamWriter m_errorWriter;
    private readonly StreamWriter m_writer;

    /// <summary>Initializes a new instance of the <see cref="BufferedConsoleOutput"/> class over the process's raw
    /// standard-output and standard-error streams, both UTF-8 without a byte-order mark (stdout buffered, stderr
    /// auto-flushed).</summary>
    public BufferedConsoleOutput() {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        m_writer = new StreamWriter(
            stream: Console.OpenStandardOutput(),
            encoding: encoding,
            bufferSize: 4096,
            leaveOpen: true
        ) {
            AutoFlush = false,
        };
        // Stderr gets the SAME explicit UTF-8 writer: bare Console.Error encodes through the console's default
        // codepage, which mangles every non-ASCII char (the em dash every refusal's pointer ends with) — so a driver
        // matching the source literal would miss on the refusal stream and hit on the success stream.
        m_errorWriter = new StreamWriter(
            stream: Console.OpenStandardError(),
            encoding: encoding,
            bufferSize: 4096,
            leaveOpen: true
        ) {
            AutoFlush = true,
        };
    }

    /// <summary>Appends one echoed line to the buffer (not flushed until <see cref="Flush"/>).</summary>
    /// <param name="value">The line to write.</param>
    public void WriteLine(string value) {
        m_writer.WriteLine(value: value);
    }

    /// <summary>Writes one REFUSED line to standard error, so a rejection is distinguishable from ordinary transcript
    /// output by stream and not only by wording. The buffered stdout is flushed first, so a reader merging the two
    /// streams still sees strict submission order across them.</summary>
    /// <param name="value">The line to write.</param>
    public void WriteErrorLine(string value) {
        m_writer.Flush();
        m_errorWriter.WriteLine(value: value);
    }

    /// <summary>Flushes the buffered lines to standard output in one write. Called once per frame after the command
    /// pump's <c>Collect</c> drain, and on shutdown, so a burst of echoes costs one syscall and no tail is ever lost.</summary>
    public void Flush() {
        m_writer.Flush();
    }

    /// <summary>Flushes any buffered tail; the underlying standard streams are left open for the process.</summary>
    public void Dispose() {
        m_writer.Flush();
        m_writer.Dispose();
        m_errorWriter.Dispose();
    }
}
