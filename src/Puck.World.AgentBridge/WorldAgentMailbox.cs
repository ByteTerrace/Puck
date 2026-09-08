using System.Threading.Channels;
using Puck.Commands;

namespace Puck.World.Agents;

/// <summary>A bounded agent-to-host mailbox drained through Puck's existing per-frame input-capture seam.</summary>
/// <remarks>
/// Model inference and Harness orchestration remain on their worker threads. Only the small delegates queued through
/// <see cref="InvokeAsync{TResult}"/> run in <see cref="CaptureFrame"/>, on the same launcher pump thread that owns
/// loopback submission and query ordering. Capacity and per-frame limits bound both retained work and host-thread
/// cost. A full mailbox refuses new work instead of silently dropping or indefinitely buffering an agent action.
/// </remarks>
public sealed class WorldAgentMailbox : IWorldAgentDispatcher, ISnapshotInputCapture, IDisposable {
    private readonly Channel<WorkItem> m_channel;
    private readonly object m_drainGate = new();
    private readonly int m_maximumOperationsPerFrame;

    private int m_disposed;

    /// <summary>Initializes a bounded mailbox.</summary>
    /// <param name="capacity">Maximum queued operations.</param>
    /// <param name="maximumOperationsPerFrame">Maximum operations executed by one host-frame capture.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public WorldAgentMailbox(int capacity = 256, int maximumOperationsPerFrame = 32) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: maximumOperationsPerFrame);

        m_channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity: capacity) {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        m_maximumOperationsPerFrame = maximumOperationsPerFrame;
    }

    /// <summary>Gets the approximate number of operations waiting for a host-frame drain.</summary>
    public int PendingCount => m_channel.Reader.Count;

    /// <inheritdoc/>
    public ValueTask<TResult> InvokeAsync<TResult>(
        Func<TResult> operation,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(argument: operation);
        if (cancellationToken.IsCancellationRequested) {
            return ValueTask.FromCanceled<TResult>(cancellationToken: cancellationToken);
        }
        if (Volatile.Read(location: ref m_disposed) != 0) {
            return ValueTask.FromException<TResult>(exception: new ObjectDisposedException(objectName: nameof(WorldAgentMailbox)));
        }

        var item = new WorkItem<TResult>(operation: operation, cancellationToken: cancellationToken);
        if (!m_channel.Writer.TryWrite(item: item)) {
            item.Refuse(exception: ((Volatile.Read(location: ref m_disposed) != 0)
                ? new ObjectDisposedException(objectName: nameof(WorldAgentMailbox))
                : new InvalidOperationException(message: "The world-agent mailbox is full; retry after the host drains pending operations.")
            ));
        }

        return new ValueTask<TResult>(item.Task);
    }

    /// <summary>Executes up to the configured per-frame limit on the calling launcher thread.</summary>
    /// <param name="frameKey">The monotonically increasing host-frame key; ordering is supplied by the mailbox, so
    /// the value is not otherwise needed.</param>
    /// <exception cref="InvalidOperationException">More than one thread attempts to drain the single-reader
    /// mailbox concurrently.</exception>
    public void CaptureFrame(ulong frameKey) {
        _ = frameKey;
        if (Volatile.Read(location: ref m_disposed) != 0) {
            return;
        }
        if (!Monitor.TryEnter(obj: m_drainGate)) {
            if (Volatile.Read(location: ref m_disposed) != 0) {
                return;
            }

            throw new InvalidOperationException(message: "The world-agent mailbox may only be drained by one host thread at a time.");
        }

        try {
            for (
                var operationIndex = 0;
                (Volatile.Read(location: ref m_disposed) == 0) &&
                    (operationIndex < m_maximumOperationsPerFrame) &&
                    m_channel.Reader.TryRead(item: out var item);
                operationIndex++
            ) {
                item.Execute();
            }
        } finally {
            Monitor.Exit(obj: m_drainGate);
        }
    }

    /// <summary>Refuses every operation still queued during host shutdown and closes the mailbox to new work.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(location1: ref m_disposed, value: 1) != 0) {
            return;
        }

        _ = m_channel.Writer.TryComplete();
        lock (m_drainGate) {
            RefuseRemainingOnShutdown();
        }
    }

    private void RefuseRemainingOnShutdown() {
        var exception = new ObjectDisposedException(objectName: nameof(WorldAgentMailbox));

        while (m_channel.Reader.TryRead(item: out var item)) {
            item.Refuse(exception: exception);
        }
    }

    private abstract class WorkItem {
        public abstract void Execute();
        public abstract void Refuse(Exception exception);
    }

    private sealed class WorkItem<TResult> : WorkItem {
        private readonly CancellationToken m_cancellationToken;
        private readonly CancellationTokenRegistration m_registration;
        private readonly Func<TResult> m_operation;
        private readonly TaskCompletionSource<TResult> m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 0 = queued, 1 = executing, 2 = terminal. Cancellation may win only while queued.
        private int m_state;

        public WorkItem(Func<TResult> operation, CancellationToken cancellationToken) {
            m_cancellationToken = cancellationToken;
            m_operation = operation;
            m_registration = cancellationToken.Register(
                callback: static state => ((WorkItem<TResult>)state!).CancelQueued(),
                state: this
            );
        }

        public Task<TResult> Task => m_completion.Task;

        public override void Execute() {
            if (Interlocked.CompareExchange(location1: ref m_state, value: 1, comparand: 0) != 0) {
                m_registration.Dispose();
                return;
            }

            try {
                m_completion.SetResult(result: m_operation());
            } catch (Exception exception) {
                m_completion.SetException(exception: exception);
            } finally {
                Volatile.Write(location: ref m_state, value: 2);
                m_registration.Dispose();
            }
        }

        public override void Refuse(Exception exception) {
            if (Interlocked.CompareExchange(location1: ref m_state, value: 2, comparand: 0) == 0) {
                m_completion.SetException(exception: exception);
            }

            m_registration.Dispose();
        }

        private void CancelQueued() {
            if (Interlocked.CompareExchange(location1: ref m_state, value: 2, comparand: 0) == 0) {
                m_completion.TrySetCanceled(cancellationToken: m_cancellationToken);
            }
        }
    }
}
