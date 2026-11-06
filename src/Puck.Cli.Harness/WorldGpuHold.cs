namespace Puck.Cli;

/// <summary>
/// This process's hold on the machine's <c>gpu</c> lease for the device Worlds it starts. The first device World of a
/// holder and lane takes the lease; the others of that holder and lane started while it is held share it (a canary's
/// parallel GPU legs, a federation's authorities), and the last to release it releases the lease. A holder or lane of
/// its own queues for the lease like any other process, so lanes sharing one MCP adapter still exclude one another.
/// Acquisition nests under an ancestor that holds the lease (<see cref="ProcessInspection.IsAncestor"/>).
/// </summary>
internal static class WorldGpuHold {
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Root, string Holder, string Lane), Entry> Held = [];

    private sealed class Entry : IDisposable {
        public readonly CancellationTokenSource Abandon = new();
        public readonly Task<CliLease> Lease;
        public int Count;

        public Entry(string root, string holder, string lane) =>
            Lease = CliLease.AcquireAsync(ancestor: ProcessInspection.IsAncestor, cancellationToken: Abandon.Token, clock: TimeProvider.System, holder: holder, lane: lane, name: "gpu", root: root, wait: CliLease.DefaultWait);

        // The last share ends the hold: a wait no caller still shares stops and leaves the queue, and a granted lease is released.
        public void Dispose() {
            Abandon.Cancel();
            _ = Lease.ContinueWith(continuationAction: acquired => {
                if (acquired.IsCompletedSuccessfully) { acquired.Result.Dispose(); }
                Abandon.Dispose();
            }, cancellationToken: CancellationToken.None, continuationOptions: TaskContinuationOptions.ExecuteSynchronously, scheduler: TaskScheduler.Default);
        }
    }

    /// <summary>Takes or shares the GPU lease for a holder and lane.</summary>
    /// <param name="checkout">The checkout whose shared lease directory holds the lease.</param>
    /// <param name="holder">The holder's name.</param>
    /// <param name="lane">The lane the holder works for.</param>
    /// <param name="cancellationToken">Stops this caller's wait; a wait another caller shares continues for it.</param>
    /// <returns>The hold, which releases this caller's share when disposed.</returns>
    /// <exception cref="TimeoutException">The lease stayed held past <see cref="CliLease.DefaultWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The checkout names no shared lease directory.</exception>
    public static async Task<IDisposable> AcquireAsync(string checkout, string holder, string lane, CancellationToken cancellationToken) {
        var root = await CliLease.RootAsync(cancellationToken: cancellationToken, checkout: checkout).ConfigureAwait(continueOnCapturedContext: false);
        var key = (root, holder, lane);
        Entry entry;

        lock (Gate) {
            if (!Held.TryGetValue(key: key, value: out entry!)) {
                entry = new Entry(holder: holder, lane: lane, root: root);
                Held[key] = entry;
            }
            entry.Count++;
        }

        var share = new Share(entry: entry, key: key);

        try {
            _ = await entry.Lease.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return share;
        } catch {
            share.Dispose();

            throw;
        }
    }

    private sealed class Share((string Root, string Holder, string Lane) key, Entry entry) : IDisposable {
        private int m_disposed;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_disposed, value: 1) != 0) { return; }

            lock (Gate) {
                if (--entry.Count != 0) { return; }
                _ = Held.Remove(key: key);
            }

            entry.Dispose();
        }
    }
}
