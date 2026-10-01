using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // One activation's crossing log: each record joins the row's publication queue behind every checkpoint and
    // mutation already queued, is appended under the activation's fence, and is answered only once the store holds
    // it. The store refuses an append from an activation its fence has closed, so that activation can never claim a
    // crossing step.
    private sealed class RowCrossingLog(WorldSiloHost host, WorldAuthorityIdentity identity, RowBookkeeping bookkeeping, WorldServer server) : IWorldCrossingLog {
        public bool TryAppend(in WorldCrossingEntry entry, out string reason) {
            // The caller holds the row's authority gate, so the engine clock it reads here is the step boundary the
            // record describes.
            var journal = new WorldAuthorityJournalEntry(
                Encoded: WorldAuthorityCheckpointCodec.EncodeCrossingEntry(entry: in entry),
                EngineTick: server.CompletedEngineTicks,
                Kind: WorldAuthorityJournalEntryKind.Crossing,
                Tick: entry.Tick
            );
            Task<WorldAuthorityStoreOutcome> append;

            lock (bookkeeping.TailGate) {
                var previous = bookkeeping.JournalTail;

                append = Task.Run(function: () => host.AppendCrossingAfterAsync(
                    bookkeeping: bookkeeping,
                    entry: journal,
                    identity: identity,
                    previous: previous
                ));
                bookkeeping.JournalTail = append;
            }

            WorldAuthorityStoreOutcome outcome;

            try {
                outcome = append.GetAwaiter().GetResult();
            } catch (Exception exception) when ((exception is IOException or InvalidDataException or OperationCanceledException or TimeoutException)) {
                reason = $"the authority store did not take the record ({exception.Message})";
                return false;
            }
            if (!outcome.Ok) {
                reason = $"the authority store refused the record ({outcome.Kind}: {outcome.Detail})";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    private async Task<WorldAuthorityStoreOutcome> AppendCrossingAfterAsync(Task previous, RowBookkeeping bookkeeping, WorldAuthorityIdentity identity, WorldAuthorityJournalEntry entry) {
        await ObservePersistenceAsync(
            ct: CancellationToken.None,
            operation: previous
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (bookkeeping.PersistenceBlocked) { return WorldAuthorityStoreOutcome.RecoveryRequired(detail: "This activation must recover before publishing again."); }

        var outcome = await m_store.AppendJournalAsync(
            cancellationToken: CancellationToken.None,
            entry: entry,
            fence: bookkeeping.Fence,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        ObservePublication(
            bookkeeping: bookkeeping,
            outcome: outcome
        );
        return outcome;
    }
    // A recovered journal interleaves committed mutations with crossing records. Mutations replay onto the restored
    // server before the row is admitted; crossing records need the admitted row and its host slice, so they are kept
    // apart and redone after the slice is restored.
    private static bool TryDecodeCrossings(IReadOnlyList<WorldAuthorityJournalEntry> entries, WorldPlayerDefaults defaults, out List<WorldCrossingEntry> crossings, out string reason) {
        crossings = [];
        foreach (var entry in entries) {
            if (entry.Kind != WorldAuthorityJournalEntryKind.Crossing) {
                continue;
            }
            if (!WorldAuthorityCheckpointCodec.TryDecodeCrossingEntry(
                bytes: entry.Encoded.Span,
                defaults: defaults,
                entry: out var crossing,
                reason: out reason
            )) {
                return false;
            }
            crossings.Add(item: crossing);
        }
        reason = string.Empty;
        return true;
    }
}
