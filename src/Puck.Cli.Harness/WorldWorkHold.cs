namespace Puck.Cli;

/// <summary>
/// This process's execution hold on a machine resource for the Worlds it starts. The first World of a
/// resource, holder and lane takes the admitted lease; the others started while it is held share it (a canary's
/// parallel GPU legs, test workers), and the last to release it releases the lease. A holder or lane of
/// its own queues for the lease like any other process, so lanes sharing one MCP adapter still exclude one another.
/// Acquisition nests under an ancestor that holds the lease (<see cref="ProcessInspection.IsAncestor"/>).
/// </summary>
internal static class WorldWorkHold {
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Root, string Name, string Holder, string Lane), Entry> Held = [];

    private sealed class Entry : IDisposable {
        public readonly CancellationTokenSource Abandon = new();
        public readonly Task<CliLease> Lease;
        public int Count;

        public Entry(Func<CancellationToken, Task<CliLease>> acquire) => Lease = acquire(Abandon.Token);

        // The last share ends the hold: a wait no caller still shares stops and leaves the queue, and a granted lease is released.
        public void Dispose() {
            Abandon.Cancel();
            _ = Lease.ContinueWith(continuationAction: acquired => {
                if (acquired.IsCompletedSuccessfully) { acquired.Result.Dispose(); }
                Abandon.Dispose();
            }, cancellationToken: CancellationToken.None, continuationOptions: TaskContinuationOptions.ExecuteSynchronously, scheduler: TaskScheduler.Default);
        }
    }

    /// <summary>Takes or shares an admitted execution lease for a resource, holder and lane.</summary>
    /// <param name="checkout">The checkout whose shared lease directory holds the lease.</param>
    /// <param name="holder">The holder's name.</param>
    /// <param name="name">The execution resource.</param>
    /// <param name="acquire">The ticket-preserving admitted acquisition for the first start.</param>
    /// <param name="admitShared">A nonwaiting reservation for each further start under live work's hold.</param>
    /// <param name="lane">The lane the holder works for.</param>
    /// <param name="cancellationToken">Stops this caller's wait; a wait another caller shares continues for it.</param>
    /// <returns>The hold, which releases this caller's share when disposed.</returns>
    /// <exception cref="TimeoutException">The lease stayed held past <see cref="CliLease.DefaultWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The checkout names no shared lease directory.</exception>
    public static async Task<IDisposable> AcquireAsync(string checkout, string name, string holder, string lane, Func<string, CancellationToken, Task<CliLease>> acquire, Func<CancellationToken, Task<bool>> admitShared, CancellationToken cancellationToken) {
        var root = await CliLease.RootAsync(cancellationToken: cancellationToken, checkout: checkout).ConfigureAwait(continueOnCapturedContext: false);
        var key = (root, name, holder, lane);
        Entry entry;
        var joined = true;

        lock (Gate) {
            if (!Held.TryGetValue(key: key, value: out entry!) || entry.Lease.IsFaulted || entry.Lease.IsCanceled) {
                entry = new Entry(token => acquire(root, token));
                Held[key] = entry;
                joined = false;
            }
            entry.Count++;
        }

        var share = new Share(entry: entry, key: key);

        try {
            _ = await entry.Lease.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            if (joined && !await admitShared(cancellationToken).ConfigureAwait(false)) { throw new HostAdmissionRefusedException(holder); }

            return share;
        } catch {
            share.Dispose();

            throw;
        }
    }

    private sealed class Share((string Root, string Name, string Holder, string Lane) key, Entry entry) : IDisposable {
        private int m_disposed;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_disposed, value: 1) != 0) { return; }

            lock (Gate) {
                if (--entry.Count != 0) { return; }
                if (Held.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) { _ = Held.Remove(key: key); }
            }

            entry.Dispose();
        }
    }
}
