using Microsoft.Extensions.Hosting;
using Puck.Commands;

namespace Puck.Launcher;

/// <summary>
/// Reads command lines from standard input and feeds them to a <see cref="TextCommandSource"/>, so a piped
/// script or an interactive terminal drives the command system like any other source.
/// </summary>
/// <remarks>
/// Console reads are blocking and not reliably cancellable, so the loop runs on a dedicated background
/// thread and <see cref="ExecuteAsync"/> returns immediately — host shutdown never waits on a pending
/// read, and the thread dies with the process. The loop ends on its own at end-of-input (a closed pipe
/// or no attached console) or when shutdown is signalled. Redirected input also reports its
/// <see cref="StandardInputBacklog"/>, released at end of input, on a read failure, or when the writer had written
/// nothing by the reader's first read.
/// </remarks>
public sealed partial class StandardInputReaderService : BackgroundService {
    private const int ReadBufferSize = 4096;

    private readonly StandardInputBacklog m_backlog;
    private readonly TextCommandSource m_source;
    private readonly string m_threadName;

    /// <summary>Initializes a new instance of the <see cref="StandardInputReaderService"/> class and claims
    /// <paramref name="backlog"/>.</summary>
    /// <param name="source">The text source each read command line is enqueued onto.</param>
    /// <param name="backlog">The backlog this reader reports on.</param>
    /// <param name="threadName">The name of the dedicated reader thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="backlog"/> is
    /// <see langword="null"/>.</exception>
    public StandardInputReaderService(TextCommandSource source, StandardInputBacklog backlog, string threadName = "Puck Stdin Reader") {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(backlog);

        m_backlog = backlog;
        m_source = source;
        m_threadName = threadName;

        // Claimed here, not on the reader thread: every hosted service is constructed before any starts, so the tick
        // host cannot take a step between its own start and this reader's.
        m_backlog.Claim();
    }

    // Console.In over redirected input is exactly this StreamReader (the console's input encoding, no byte-order-mark
    // detection, a 4 KiB buffer); owning it lets the stream beneath report end of input and a silent writer. The
    // StreamReader reads its stream only once every complete line it already decoded has been returned, and this
    // loop queues each returned line before asking for the next, so every release follows the lines read before it.
    private TextReader OpenInput() {
        if (!Console.IsInputRedirected) {
            m_backlog.Release();

            return Console.In;
        }

        return new StreamReader(
            bufferSize: ReadBufferSize,
            detectEncodingFromByteOrderMarks: false,
            encoding: Console.InputEncoding,
            stream: new BacklogReportingStream(
                backlog: m_backlog,
                inner: Console.OpenStandardInput()
            )
        );
    }
    private void ReadLoop(CancellationToken stoppingToken) {
        try {
            var input = OpenInput();

            while (!stoppingToken.IsCancellationRequested) {
                var line = input.ReadLine();

                if (line is null) {
                    break;
                }

                m_source.Enqueue(line: line);
            }
        } catch (IOException) {
            // No readable console (for example, a windowed launch with no attached terminal): there is
            // simply nothing to drive from, so the reader stops.
        } finally {
            m_backlog.Release();
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        _ = LauncherHostLoop.StartBackgroundThread(
            action: () => ReadLoop(stoppingToken: stoppingToken),
            name: m_threadName
        );

        return Task.CompletedTask;
    }

    // Releases the backlog when a read returns end of input, or when the first read would wait for a writer that has
    // written nothing (an idle pipe, such as one an IDE or a supervising process holds open). Once any byte has
    // arrived, a read that would wait releases nothing: the writer may only be pausing between chunks.
    private sealed class BacklogReportingStream(Stream inner, StandardInputBacklog backlog) : Stream {
        private bool m_receivedInput;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private int Report(int read) {
            if (read == 0) {
                backlog.Release();
            } else {
                m_receivedInput = true;
            }

            return read;
        }
        private void BeforeRead() {
            if (
                !m_receivedInput &&
                backlog.IsHolding &&
                ReadWouldWait()
            ) {
                backlog.Release();
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                inner.Dispose();
            }

            base.Dispose(disposing: disposing);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) {
            BeforeRead();

            return Report(read: inner.Read(
                buffer: buffer,
                count: count,
                offset: offset
            ));
        }
        public override int Read(Span<byte> buffer) {
            BeforeRead();

            return Report(read: inner.Read(buffer: buffer));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
