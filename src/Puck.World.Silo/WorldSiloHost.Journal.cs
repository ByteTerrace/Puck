using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // Bound both the number of retained entries and their payload bytes. Admission runs while the
    // prepared edit can still be unwound, under the same authority gate as its commit callback.
    private const long JournalBacklogBytes = 32L * 1024 * 1024;

    private (Action? Commit, string? Refusal) PrepareJournalMutation(RowBookkeeping row, WorldJournalEntry entry) {
        if (row.PersistenceBlocked || row.Initializing || row.Released) {
            return (null, "the authority journal is unavailable");
        }
        var limit = m_definition.Lifecycle?.JournalBacklogLimit ?? WorldSiloLifecycle.DefaultJournalBacklogLimit;
        if (row.PendingJournalAppends >= limit) {
            return (null, "the authority journal backlog is full; retry after it drains");
        }
        if (!WorldAuthorityCheckpointCodec.TryEncodeJournalMutation(entry, out var encoded, out var failure)) {
            return (null, $"the mutation cannot be encoded for authority recovery ({failure})");
        }
        var bytes = encoded.Encoded.Length;
        if (bytes > WorldAuthorityStoreWireCodec.MaxEntryBytes) {
            return (null, "the mutation exceeds the authority journal entry byte limit");
        }
        if (row.PendingJournalBytes + bytes > JournalBacklogBytes) {
            return (null, "the authority journal byte backlog is full; retry after it drains");
        }

        return (() => {
            if (row.PendingJournalAppends == 0) { row.JournalTimestamp = m_clock.GetTimestamp(); }
            row.PendingJournalAppends++;
            row.PendingJournalBytes += bytes;
            row.JournalBatch.Add(encoded);
            row.JournalBatchBytes += bytes;
        }, null);
    }

    // Called under the row's authority gate, at a tick boundary or before another ordered publication.
    private void FlushJournal(RowBookkeeping row) {
        if (row.JournalBatch.Count == 0) { return; }
        var entries = row.JournalBatch;
        var bytes = row.JournalBatchBytes;
        row.JournalBatch = [];
        row.JournalBatchBytes = 0;

        lock (row.TailGate) {
            var previous = row.JournalTail;
            row.JournalTail = Task.Run(() => AppendJournalBatchAsync(row, row.Identity, entries, bytes, previous));
        }
    }

    private async Task AppendJournalBatchAsync(RowBookkeeping row, WorldAuthorityIdentity identity,
        List<WorldAuthorityJournalEntry> entries, long bytes, Task previous) {
        WorldAuthorityStoreOutcome outcome;
        try {
            await ObservePersistenceAsync(previous, CancellationToken.None).ConfigureAwait(false);
            outcome = row.PersistenceBlocked
                ? WorldAuthorityStoreOutcome.RecoveryRequired("This activation must recover before publishing again.")
                : await m_store.AppendJournalBatchAsync(identity, entries, CancellationToken.None, row.Fence).ConfigureAwait(false);
            ObservePublication(row, outcome);
        } catch (Exception error) {
            outcome = WorldAuthorityStoreOutcome.Failed(error.Message);
        }
        // These edits are already live. A later crossing or checkpoint must not be published past a hole.
        if (!outcome.Ok) { row.PersistenceBlocked = true; }
        Post(() => {
            if (!m_rows.TryGetValue(identity.World.Value, out var current) || !ReferenceEquals(current, row)) { return; }
            row.PendingJournalAppends -= entries.Count;
            row.PendingJournalBytes -= bytes;
            row.JournalTimestamp = m_clock.GetTimestamp();
            row.LastJournalOutcome = outcome.Ok ? "ok" : $"failed ({outcome.Detail})";
            if (outcome.Ok) {
                row.JournalBytesWritten += outcome.JournalBytesWritten;
                row.JournalMutationsWritten += entries.Count;
            }
        });
    }
}
