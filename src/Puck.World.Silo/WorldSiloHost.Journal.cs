using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private async Task AppendJournalEntryAsync(WorldAuthorityIdentity identity, string worldId, ulong tick, ulong engineTick, byte[] encoded, RowBookkeeping bookkeeping) {
        try {
            var outcome = (bookkeeping.PersistenceBlocked
                ? WorldAuthorityStoreOutcome.RecoveryRequired(detail: "This activation must recover before publishing again.")
                : await m_store.AppendJournalAsync(
                    cancellationToken: CancellationToken.None,
                    entry: new WorldAuthorityJournalEntry(
                        Encoded: encoded,
                        EngineTick: engineTick,
                        Tick: tick
                    ),
                    identity: identity,
                    fence: bookkeeping.Fence
                )
            );

            ObservePublication(
                bookkeeping: bookkeeping,
                outcome: outcome
            );
            // This mutation is already installed in the live document. A later crossing cannot be recovered
            // against that document unless the mutation landed too; stop the publication queue on any failure.
            if (!outcome.Ok) { bookkeeping.PersistenceBlocked = true; }

            Post(action: () => {
                if (
                    m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) &&
                    ReferenceEquals(
                    objA: current,
                    objB: bookkeeping
                )
                ) {
                    bookkeeping.PendingJournalAppends--;
                    bookkeeping.JournalTimestamp = m_clock.GetTimestamp();
                    bookkeeping.JournalFailed |= !outcome.Ok;
                    if (!outcome.Ok) { bookkeeping.JournalFailureTick = tick; }
                    bookkeeping.LastJournalOutcome = (outcome.Ok
                        ? "ok"
                        : $"failed ({outcome.Detail})"
                    );
                }
            });
        } catch (Exception error) {
            bookkeeping.PersistenceBlocked = true;
            Post(action: () => {
                if (
                    m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) &&
                    ReferenceEquals(
                    objA: current,
                    objB: bookkeeping
                )
                ) {
                    bookkeeping.PendingJournalAppends--;
                    bookkeeping.JournalFailed = true;
                    bookkeeping.JournalFailureTick = tick;
                    bookkeeping.LastJournalOutcome = $"failed ({error.Message})";
                }
            });
            throw;
        }
    }
    // Called from WorldServer.MutationJournalTap, always on the tick thread — the one writer of JournalTail.
    private void ScheduleJournalAppend(string worldId, WorldAuthorityIdentity identity, ulong tick, ulong engineTick, WorldMutation mutation, WorldServer source) {
        if (!m_rows.TryGetValue(
            key: worldId,
            value: out var bookkeeping
        )) {
            return;
        }

        if (
            !Instances.TryGet(
            instance: out var active,
            name: worldId
        ) ||
            (active is null) ||
            !ReferenceEquals(
            objA: active.Server,
            objB: source
        )
        ) { return; }

        if (!WorldSubmissionCodec.TryEncodeCommittedMutation(
            bytes: out var encoded,
            failure: out var failure,
            mutation: mutation
        )) {
            bookkeeping.PersistenceBlocked = true;
            bookkeeping.JournalFailed = true;
            bookkeeping.JournalFailureTick = tick;
            bookkeeping.LastJournalOutcome = $"failed (encoding: {failure})";
            Console.Error.WriteLine(value: $"[silo.journal: '{RowKey(identity: identity)}' a mutation would not re-encode for the durable journal ({failure}) — this tick's mutation is unrecoverable after a restart with no later checkpoint]");

            return;
        }

        if (bookkeeping.PendingJournalAppends == 0) { bookkeeping.JournalTimestamp = m_clock.GetTimestamp(); }
        bookkeeping.PendingJournalAppends++;
        lock (bookkeeping.TailGate) {
            bookkeeping.JournalTail = bookkeeping.JournalTail.ContinueWith(
                continuationFunction: _ => AppendJournalEntryAsync(
                    bookkeeping: bookkeeping,
                    encoded: encoded,
                    engineTick: engineTick,
                    identity: identity,
                    tick: tick,
                    worldId: worldId
                ),
                scheduler: TaskScheduler.Default
            ).Unwrap();
        }
    }
}
