using System.Threading.Channels;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private readonly Channel<Action> m_pending = Channel.CreateUnbounded<Action>();

    // Marshals one unit of work onto the tick thread via DrainPending and awaits its result — the one hand-off point
    // between a connection's background read loop and the single-threaded server.
    internal Task<T> RunOnTickThreadAsync<T>(Func<T> work, CancellationToken ct = default) {
        var tcs = new TaskCompletionSource<T>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        if (!m_pending.Writer.TryWrite(item: () => {
            // A canceled queue item must never execute later: the caller has already released its socket/handshake
            // slot, so committing admission here would create a population body with no connection to own it.
            if (ct.IsCancellationRequested) {
                tcs.TrySetCanceled(cancellationToken: ct);

                return;
            }

            try {
                tcs.TrySetResult(result: ExecuteAuthorityOperation(work));
            } catch (Exception ex) {
                tcs.TrySetException(exception: ex);
            }
        })) { tcs.TrySetException(new ObjectDisposedException(nameof(WorldServer))); }

        // WaitAsync releases the caller promptly even when the tick queue is not draining. The guard above is the
        // other half of the contract: it prevents that still-enqueued work from taking effect if draining resumes.
        return (ct.CanBeCanceled
            ? tcs.Task.WaitAsync(cancellationToken: ct)
            : tcs.Task
        );
    }
    internal void DrainMailbox() {
        // A crossing record the log has answered settles here, at the tick boundary, before any reply that may wait on it;
        // a retiring activation drains here too, so the settle takes the gate itself.
        lock (m_authorityGate) {
            SettleCrossingIfAnswered();
        }
        // Replies arriving during this drain belong to the next input batch.
        var count = m_pending.Reader.Count;
        for (var i = 0; i < count && m_pending.Reader.TryRead(out var action); i++) { action(); }
    }

    internal async ValueTask WaitForPendingWorkAsync(CancellationToken ct) {
        await m_pending.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
    }
}
