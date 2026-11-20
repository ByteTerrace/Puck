using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    /// <summary>What an operation queued behind an activation's publications reads of them
    /// (<see cref="RowPublicationQueue.After"/>).</summary>
    /// <param name="Fence">The activation's fence.</param>
    /// <param name="JournalSequence">The journal sequence the publications before the operation reached.</param>
    /// <param name="Blocked">Whether the activation must recover before it publishes again.</param>
    private readonly record struct RowPublished(WorldAuthorityFence Fence, long JournalSequence, bool Blocked);

    /// <summary>One activation's publications to the authority store, in order: every checkpoint, journal batch,
    /// crossing record and definition it writes, and the reads that must see exactly what was written before
    /// them.</summary>
    /// <remarks>An activation's writes reach the store strictly in the order they were enqueued, each under the
    /// activation's fence, and none once the activation is blocked. The tail and the blocked flag are private, and
    /// <see cref="Enqueue"/> is the one member that sends a write, so no caller orders or gates a write its own way.
    /// <para>Every operation starts on the thread pool, so nothing it does before its first wait (encoding a document)
    /// runs on the thread that enqueued it, which may be the tick thread holding the authority gate. An operation
    /// waits for the one before it to settle and takes nothing from it: an earlier failure belongs to its own caller.
    /// A blocked activation sends nothing and answers a failed outcome naming the recovery it needs. A write blocks
    /// the activation when the store calls its fence stale or its commit uncertain, or reports success with another
    /// activation's root; a write of edits that are already live blocks it on any failure, since a later publication
    /// must not land past the hole. A write that throws did not land (the store reconciles its own commit point) and
    /// answers a failed outcome; only the caller's own cancellation leaves a write's task cancelled.</para></remarks>
    /// <param name="fence">The activation's fence.</param>
    /// <param name="journalSequence">The journal sequence the activation recovered at.</param>
    /// <param name="journalSegments">The journal segment count the activation recovered at.</param>
    private sealed class RowPublicationQueue(WorldAuthorityFence fence, long journalSequence, int journalSegments) {
        private const string BlockedDetail = "This activation must recover before publishing again.";

        private readonly Lock m_gate = new();
        private volatile bool m_blocked;
        private volatile int m_journalSegments = journalSegments;
        private long m_journalSequence = journalSequence;
        private Task m_tail = Task.CompletedTask;

        /// <summary>Gets whether the activation must recover before it publishes again.</summary>
        public bool Blocked => m_blocked;
        /// <summary>Gets the journal segment count of the last publication that landed.</summary>
        public int JournalSegments => m_journalSegments;

        /// <summary>Queues a write behind every operation enqueued before it.</summary>
        /// <param name="write">Sends the write under the fence it is handed.</param>
        /// <param name="live">Whether the write records edits that are already live, so any failure leaves a hole
        /// that blocks the activation.</param>
        /// <param name="cancellationToken">The token the write itself observes; its cancellation is the one failure
        /// that leaves the returned task cancelled.</param>
        /// <returns>The store's outcome, or a failed outcome for a write a blocked activation did not send or that
        /// threw before it could land.</returns>
        public Task<WorldAuthorityStoreOutcome> Enqueue(Func<WorldAuthorityFence, Task<WorldAuthorityStoreOutcome>> write, bool live = false, CancellationToken cancellationToken = default) =>
            Append(start: previous => Task.Run(function: () => PublishAsync(cancellationToken: cancellationToken, live: live, previous: previous, write: write)));
        /// <summary>Queues an operation that publishes nothing the queue observes behind every write enqueued before
        /// it: a read of what those writes left. Its failure is its caller's and does not block the activation.</summary>
        /// <typeparam name="T">The operation's result.</typeparam>
        /// <param name="operation">Reads what the earlier publications left.</param>
        /// <returns>The operation's result.</returns>
        public Task<T> After<T>(Func<RowPublished, Task<T>> operation) =>
            Append(start: previous => Task.Run(function: async () => {
                await previous.ConfigureAwait(options: ConfigureAwaitOptions.SuppressThrowing);
                return await operation(arg: new RowPublished(Blocked: m_blocked, Fence: fence, JournalSequence: Volatile.Read(location: ref m_journalSequence))).ConfigureAwait(continueOnCapturedContext: false);
            }));

        private Task<T> Append<T>(Func<Task, Task<T>> start) {
            lock (m_gate) {
                var queued = start(arg: m_tail);

                m_tail = queued;
                return queued;
            }
        }
        private async Task<WorldAuthorityStoreOutcome> PublishAsync(Task previous, Func<WorldAuthorityFence, Task<WorldAuthorityStoreOutcome>> write, bool live, CancellationToken cancellationToken) {
            await previous.ConfigureAwait(options: ConfigureAwaitOptions.SuppressThrowing);
            if (m_blocked) { return WorldAuthorityStoreOutcome.Failed(detail: BlockedDetail); }

            WorldAuthorityStoreOutcome outcome;

            try {
                outcome = await write(arg: fence).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception error) when (live || !((error is OperationCanceledException) && cancellationToken.IsCancellationRequested)) {
                Console.Error.WriteLine(value: $"[silo.persistence: {error.Message}]");
                outcome = WorldAuthorityStoreOutcome.Failed(detail: error.Message);
            }
            if (outcome.Ok) {
                if (
                    (outcome.PublishedRoot is not { } publication) ||
                    (publication.Root.Epoch != fence.Epoch) ||
                    (publication.Root.FenceToken != fence.Token)
                ) {
                    // A stale or uncertain writer is never healed by adopting another activation's root.
                    outcome = WorldAuthorityStoreOutcome.RecoveryRequired(detail: "A successful authority publication did not return this activation's root.");
                } else {
                    Volatile.Write(location: ref m_journalSequence, value: publication.Root.JournalSequence);
                    m_journalSegments = publication.Root.JournalSegmentCount;
                }
            }
            if ((outcome.Kind is WorldAuthorityStoreOutcomeKind.StaleFence or WorldAuthorityStoreOutcomeKind.RecoveryRequired) || (live && !outcome.Ok)) {
                m_blocked = true;
            }
            return outcome;
        }
    }

    private async Task<WorldAuthorityStoreOutcome> PublishDefinitionCoreAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        var admission = new TaskCompletionSource<Task<WorldAuthorityStoreOutcome>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            if (ct.IsCancellationRequested) { admission.TrySetCanceled(cancellationToken: ct); return; }
            if (FindWorldRow(identity: identity) is null) {
                admission.TrySetResult(result: Task.FromResult(result: WorldAuthorityStoreOutcome.Failed(detail: "The world is not declared in this silo.")));
                return;
            }
            if (m_rows.TryGetValue(
                key: identity.World.Value,
                value: out var bookkeeping
            )) {
                if (
                    bookkeeping.Released ||
                    !Instances.TryGet(
                    identity.World.Value,
                    out var row
                ) ||
                    (row is null) ||
                    row.Server.IsRetiring
                ) {
                    admission.TrySetResult(result: Task.FromResult(result: WorldAuthorityStoreOutcome.Failed(detail: "The activation is retiring.")));
                    return;
                }
                lock (row.Server.AuthorityGate) {
                    FlushJournal(bookkeeping);
                    admission.TrySetResult(result: bookkeeping.Publications.Enqueue(cancellationToken: ct, write: fence => m_store.PublishDefinitionAsync(identity, composed, ct, fence)));
                }
            } else {
                // No activation holds the world, so nothing orders this write but the store's own root.
                admission.TrySetResult(result: Task.Run(function: () => m_store.PublishDefinitionAsync(
                    identity,
                    composed,
                    ct
                )));
            }
        });
        return await (await admission.Task.WaitAsync(cancellationToken: ct)).WaitAsync(cancellationToken: ct);
    }
}
