using System.Text;

using Puck.Abstractions.Waiting;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// A bounded asynchronous stdout/stderr writer for the command pump's result echoes. Writes enqueue onto a single FIFO
/// ring buffer drained by one background writer thread (started lazily on the first write), so a slow pipe reader
/// (such as the MCP front) never stalls the frame thread on a synchronous <see cref="StreamWriter.Flush"/> syscall.
/// <para>
/// Strict submission order across stdout and stderr is preserved structurally: both accepted results
/// (<see cref="WriteLine"/>) and refused results (<see cref="WriteErrorLine"/>) ride the same FIFO queue, and the
/// single writer thread batches consecutive stdout records into one flush before any stderr write and at the end of
/// each drained burst. Calling <see cref="Flush"/> when nothing was written since the last flush is a lock-free no-op;
/// otherwise it wakes the writer thread without blocking. <see cref="Drain"/> and <see cref="Dispose"/> wait (bounded)
/// for all queued records to reach the underlying streams, leaving both streams open.
/// </para>
/// </summary>
public sealed class BufferedConsoleOutput : IDisposable {
    /// <summary>The default bounded queue capacity (in console records).</summary>
    public const int DefaultQueueCapacity = 4096;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(seconds: 5);
    private static readonly WaitReason QueueSpaceReason = new(Subsystem: "console.output", WaitsFor: "bounded console queue space");
    private static readonly WaitReason IdleReason = new(Subsystem: "console.output", WaitsFor: "queued console output");
    private static readonly WaitReason DrainReason = new(Subsystem: "console.output", WaitsFor: "drain queued console output");
    private static readonly WaitReason JoinReason = new(Subsystem: "console.output", WaitsFor: "join console output writer");

    private enum EntryKind : byte {
        Output,
        Error,
    }

    private readonly record struct Entry(EntryKind Kind, string Text);

    private readonly StreamWriter m_errorWriter;
    private readonly StreamWriter m_writer;
    private readonly HostLog? m_log;
    private readonly object m_gate = new();
    private readonly Entry[] m_ring;

    private int m_head;
    private int m_tail;
    private int m_count;
    private bool m_completed;
    private bool m_disposed;
    private bool m_writerFailed;
    private long m_enqueued;
    private long m_flushed;
    private Thread? m_thread;

    /// <summary>Initializes a new instance of the <see cref="BufferedConsoleOutput"/> class over the process's raw
    /// standard-output and standard-error streams, both UTF-8 without a byte-order mark (stdout buffered, stderr
    /// auto-flushed).</summary>
    public BufferedConsoleOutput() : this(output: Console.OpenStandardOutput(), error: Console.OpenStandardError()) { }
    /// <summary>Initializes writers over the process's raw standard streams whose refused results are also held in a
    /// host log, since they bypass <see cref="Console.Error"/>.</summary>
    /// <param name="log">The log every refused result is appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    public BufferedConsoleOutput(HostLog log) : this(output: Console.OpenStandardOutput(), error: Console.OpenStandardError(), log: log) {
        ArgumentNullException.ThrowIfNull(argument: log);
    }
    /// <summary>Initializes UTF-8 result writers over borrowed streams: stdout is buffered and stderr auto-flushed.
    /// Disposing this output drains and releases the writers while leaving both streams open.</summary>
    /// <param name="output">The writable destination for accepted command results.</param>
    /// <param name="error">The writable destination for refused command results.</param>
    /// <param name="log">The host log every refused result is also appended to, or <see langword="null"/> for
    /// none.</param>
    /// <param name="queueCapacity">The bounded FIFO queue capacity in records.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> or <paramref name="error"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="queueCapacity"/> is not positive.</exception>
    public BufferedConsoleOutput(Stream output, Stream error, HostLog? log = null, int queueCapacity = DefaultQueueCapacity) {
        ArgumentNullException.ThrowIfNull(argument: output);
        ArgumentNullException.ThrowIfNull(argument: error);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: queueCapacity);

        m_log = log;
        m_ring = new Entry[queueCapacity];

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        m_writer = new StreamWriter(
            bufferSize: 4096,
            encoding: encoding,
            leaveOpen: true,
            stream: output
        ) {
            AutoFlush = false,
        };
        // Stderr gets the SAME explicit UTF-8 writer: bare Console.Error encodes through the console's default
        // codepage, which mangles every non-ASCII char (the em dash every refusal's pointer ends with) — so a driver
        // matching the source literal would miss on the refusal stream and hit on the success stream.
        m_errorWriter = new StreamWriter(
            bufferSize: 4096,
            encoding: encoding,
            leaveOpen: true,
            stream: error
        ) {
            AutoFlush = true,
        };
    }

    /// <summary>Gets the bounded queue capacity, in records.</summary>
    public int QueueCapacity => m_ring.Length;
    /// <summary>Gets the number of records currently queued for the background writer.</summary>
    public int PendingCount {
        get {
            lock (m_gate) {
                return m_count;
            }
        }
    }

    /// <summary>Drains all queued records, stops the background writer thread, and releases the writers while leaving
    /// the underlying standard streams open for the process.</summary>
    public void Dispose() {
        Thread? thread;

        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            m_completed = true;
            thread = m_thread;
            Monitor.PulseAll(obj: m_gate);
        }

        var joined = true;

        if (thread is not null) {
            joined = (HostWaits.Join(
                deadline: WaitDeadline.Synchronous(budget: DrainTimeout),
                reason: in JoinReason,
                thread: thread
            ) == WaitOutcome.Completed);
        }

        if ((thread is null) || (joined && !thread.IsAlive)) {
            try {
                m_writer.Flush();
            } catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
            }
            m_writer.Dispose();
            m_errorWriter.Dispose();
        }
    }
    /// <summary>Non-blocking flush signal for the per-frame host loop. Returns immediately without taking any lock
    /// when no records have been enqueued since the writer last flushed; otherwise wakes the background writer.</summary>
    public void Flush() {
        if (Volatile.Read(location: ref m_enqueued) == Volatile.Read(location: ref m_flushed)) {
            return;
        }

        lock (m_gate) {
            if (!m_completed && (m_count > 0)) {
                Monitor.Pulse(obj: m_gate);
            }
        }
    }
    /// <summary>Waits (bounded) for the background writer thread to write and flush every record enqueued up to this
    /// call. Used at host teardown so no buffered tail is lost before process exit.</summary>
    public void Drain() {
        var target = Volatile.Read(location: ref m_enqueued);

        if (Volatile.Read(location: ref m_flushed) >= target) {
            return;
        }

        var deadline = WaitDeadline.Synchronous(budget: DrainTimeout);

        lock (m_gate) {
            if ((m_thread is null) || m_writerFailed) {
                return;
            }

            Monitor.PulseAll(obj: m_gate);
            while ((m_flushed < target) && !m_writerFailed) {
                if (HostWaits.OnCondition(
                    deadline: deadline,
                    monitor: m_gate,
                    reason: in DrainReason
                ) == WaitOutcome.DeadlinePassed) {
                    break;
                }
            }
        }
    }
    /// <summary>Enqueues one REFUSED result for standard error, preserving strict stdout-before-stderr ordering on the
    /// background writer thread. Also records the refusal synchronously in the host log.</summary>
    /// <param name="value">The result's text.</param>
    public void WriteErrorLine(string value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        m_log?.Append(stream: HostLogStream.Error, text: value);
        Enqueue(entry: new Entry(Kind: EntryKind.Error, Text: value));
    }
    /// <summary>Enqueues one accepted result for standard output; the background writer batches consecutive lines into
    /// one flush.</summary>
    /// <param name="value">The result's text.</param>
    public void WriteLine(string value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        Enqueue(entry: new Entry(Kind: EntryKind.Output, Text: value));
    }

    private void Enqueue(Entry entry) {
        lock (m_gate) {
            if (m_completed || m_writerFailed) {
                return;
            }

            EnsureThreadLocked();

            while ((m_count == m_ring.Length) && !m_completed && !m_writerFailed) {
                if (HostWaits.OnCondition(
                    deadline: WaitDeadline.Synchronous(budget: DrainTimeout),
                    monitor: m_gate,
                    reason: in QueueSpaceReason
                ) == WaitOutcome.DeadlinePassed) {
                    // A permanently stalled reader must not block the frame thread indefinitely.
                    return;
                }
            }

            if (m_completed || m_writerFailed) {
                return;
            }

            m_ring[m_tail] = entry;
            m_tail = ((m_tail + 1) % m_ring.Length);
            m_count++;
            Volatile.Write(location: ref m_enqueued, value: (m_enqueued + 1));

            if (m_count == 1) {
                Monitor.Pulse(obj: m_gate);
            }
        }
    }
    private void EnsureThreadLocked() {
        if (m_thread is not null) {
            return;
        }

        var thread = new Thread(start: WriteLoop) {
            IsBackground = true,
            Name = "Puck.Launcher Console Output",
        };

        m_thread = thread;
        thread.Start();
    }
    private void WriteLoop() {
        var consumer = HostWaits.ClaimConsumer(reason: in IdleReason);
        var pendingStdout = false;
        var batchFlushed = 0L;

        try {
            while (true) {
                Entry entry;
                var wasFull = false;

                lock (m_gate) {
                    if ((batchFlushed > 0L) && (m_count == 0)) {
                        Volatile.Write(location: ref m_flushed, value: (m_flushed + batchFlushed));
                        batchFlushed = 0L;
                        Monitor.PulseAll(obj: m_gate);
                    }

                    while ((m_count == 0) && !m_completed) {
                        HostWaits.IdleOnCondition(consumer: consumer, monitor: m_gate);
                    }

                    if ((m_count == 0) && m_completed) {
                        break;
                    }

                    wasFull = (m_count == m_ring.Length);
                    entry = m_ring[m_head];
                    m_ring[m_head] = default;
                    m_head = ((m_head + 1) % m_ring.Length);
                    m_count--;

                    if (wasFull) {
                        Monitor.PulseAll(obj: m_gate);
                    }
                }

                if (entry.Kind == EntryKind.Output) {
                    ConsoleRecord.Write(
                        record: entry.Text,
                        writer: m_writer
                    );
                    pendingStdout = true;
                    batchFlushed++;

                    // Keep draining consecutive queued items into m_writer's buffer before issuing a single flush.
                    var moreQueued = false;

                    lock (m_gate) {
                        moreQueued = (m_count > 0);
                    }

                    if (!moreQueued && pendingStdout) {
                        m_writer.Flush();
                        pendingStdout = false;
                    }
                } else {
                    if (pendingStdout) {
                        m_writer.Flush();
                        pendingStdout = false;
                    }

                    ConsoleRecord.Write(
                        record: entry.Text,
                        writer: m_errorWriter
                    );
                    batchFlushed++;
                }
            }

            if (pendingStdout) {
                m_writer.Flush();
                pendingStdout = false;
            }
        } catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
            lock (m_gate) {
                m_writerFailed = true;
                Monitor.PulseAll(obj: m_gate);
            }
        } finally {
            lock (m_gate) {
                if (batchFlushed > 0L) {
                    Volatile.Write(location: ref m_flushed, value: (m_flushed + batchFlushed));
                }

                Monitor.PulseAll(obj: m_gate);
            }
        }
    }
}
