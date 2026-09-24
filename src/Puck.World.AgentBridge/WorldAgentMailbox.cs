using System.Threading.Channels;

namespace Puck.World.Agents;

/// <summary>A bounded agent-to-host mailbox the host drains at its closed simulation boundaries.</summary>
/// <remarks>
/// Model inference and Harness orchestration remain on their worker threads. Only the small delegates queued through
/// <see cref="InvokeAsync{TResult}"/> run in <see cref="Drain"/>, on the one simulation thread that owns loopback
/// submission and query ordering. Capacity and per-drain limits bound both retained work and host-thread
/// cost. A full mailbox refuses new work instead of silently dropping or indefinitely buffering an agent action.
/// </remarks>
public sealed class WorldAgentMailbox : IWorldAgentDispatcher, IDisposable {
    private readonly Channel<WorkItem> m_channel;
    private readonly object m_drainGate = new();
    private readonly int m_maximumOperationsPerDrain;

    private int m_disposed;

    /// <summary>Initializes a bounded mailbox.</summary>
    /// <param name="capacity">Maximum queued operations.</param>
    /// <param name="maximumOperationsPerDrain">Maximum operations executed by one <see cref="Drain"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public WorldAgentMailbox(int capacity = 256, int maximumOperationsPerDrain = 32) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: maximumOperationsPerDrain);

        m_channel = Channel.CreateBounded<WorkItem>(options: new BoundedChannelOptions(capacity: capacity) {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        m_maximumOperationsPerDrain = maximumOperationsPerDrain;
    }

    /// <summary>Gets the approximate number of operations waiting for a drain.</summary>
    public int PendingCount => m_channel.Reader.Count;

    private void RefuseRemainingOnShutdown() {
        var exception = new ObjectDisposedException(objectName: nameof(WorldAgentMailbox));

        while (m_channel.Reader.TryRead(item: out var item)) {
            item.Refuse(exception: exception);
        }
    }

    /// <summary>Executes up to the configured per-drain limit on the calling simulation thread.</summary>
    /// <exception cref="InvalidOperationException">More than one thread attempts to drain the single-reader
    /// mailbox concurrently.</exception>
    public void Drain() {
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
                ((Volatile.Read(location: ref m_disposed) == 0) &&
                    (operationIndex < m_maximumOperationsPerDrain) &&
                    m_channel.Reader.TryRead(item: out var item));
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
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) {
            return;
        }

        _ = m_channel.Writer.TryComplete();
        lock (m_drainGate) {
            RefuseRemainingOnShutdown();
        }
    }
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

        var item = new WorkItem<TResult>(
            cancellationToken: cancellationToken,
            operation: operation
        );

        if (!m_channel.Writer.TryWrite(item: item)) {
            item.Refuse(exception: ((Volatile.Read(location: ref m_disposed) != 0)
                ? new ObjectDisposedException(objectName: nameof(WorldAgentMailbox))
                : new InvalidOperationException(message: "The world-agent mailbox is full; retry after the host drains pending operations.")));
        }

        return new ValueTask<TResult>(task: item.Task);
    }

    private abstract class WorkItem {
        public abstract void Execute();
        public abstract void Refuse(Exception exception);
    }
    private sealed class WorkItem<TResult> : WorkItem {
        private readonly CancellationToken m_cancellationToken;
        private readonly TaskCompletionSource<TResult> m_completion = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<TResult> m_operation;
        private readonly CancellationTokenRegistration m_registration;

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

        private void CancelQueued() {
            if (Interlocked.CompareExchange(
                comparand: 0,
                location1: ref m_state,
                value: 2
            ) == 0) {
                m_completion.TrySetCanceled(cancellationToken: m_cancellationToken);
            }
        }

        public override void Execute() {
            if (Interlocked.CompareExchange(
                comparand: 0,
                location1: ref m_state,
                value: 1
            ) != 0) {
                m_registration.Dispose();
                return;
            }

            try {
                m_completion.SetResult(result: m_operation());
            } catch (Exception exception) {
                m_completion.SetException(exception: exception);
            } finally {
                Volatile.Write(
                    location: ref m_state,
                    value: 2
                );
                m_registration.Dispose();
            }
        }
        public override void Refuse(Exception exception) {
            if (Interlocked.CompareExchange(
                comparand: 0,
                location1: ref m_state,
                value: 2
            ) == 0) {
                m_completion.SetException(exception: exception);
            }

            m_registration.Dispose();
        }
    }
}
