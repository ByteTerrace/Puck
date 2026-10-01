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
    // Replay the publication order after admission and host-slice restore. A mutation can change the spawn,
    // capacity or admission policy an arrival used, so moving it ahead of that arrival changes the traveler. A record
    // that does not decode or redo refuses the activation and leaves its crossing watermark where it stopped.
    private bool TryRecoverJournal(WorldInstance row, IReadOnlyList<WorldAuthorityJournalEntry> entries, out string reason) {
        foreach (var entry in entries) {
            switch (entry.Kind) {
                case WorldAuthorityJournalEntryKind.Crossing:
                    if (!WorldAuthorityCheckpointCodec.TryDecodeCrossingEntry(
                        bytes: entry.Encoded.Span,
                        defaults: row.Server.Definition.PlayerDefaults,
                        entry: out var crossing,
                        reason: out var decodeReason
                    )) {
                        reason = $"crossing decode: {decodeReason}";
                        return false;
                    }
                    try {
                        Instances.RecoverCrossings(
                            entries: [crossing],
                            row: row
                        );
                    } catch (Exception redo) when ((redo is ArgumentException or InvalidOperationException)) {
                        reason = redo.Message;
                        return false;
                    }
                    break;
                case WorldAuthorityJournalEntryKind.Mutation:
                    if (
                        !WorldSubmissionCodec.TryDecodeCommittedMutation(
                        bytes: entry.Encoded.Span,
                        failure: out var failure,
                        mutation: out var mutation
                    ) ||
                        (mutation is null)
                    ) {
                        reason = $"mutation decode: {failure}";
                        return false;
                    }
                    if (!row.Server.TryApplyJournalTailMutation(
                        engineTick: entry.EngineTick,
                        mutation: mutation,
                        tick: entry.Tick
                    )) {
                        reason = "replay rejected a recorded mutation";
                        return false;
                    }
                    break;
                default:
                    reason = $"undeclared entry kind '{entry.Kind}'";
                    return false;
            }
        }
        reason = string.Empty;
        return true;
    }
    // Runs on the mailbox: an activation that admitted its row but cannot finish leaves no row and no bookkeeping.
    private void DiscardActivation(WorldInstance row) {
        row.Server.FreezeForRetirement();
        if (!Instances.TryStop(
            name: row.Name,
            reason: out _
        )) {
            row.Dispose();
        }
        _ = m_rows.Remove(key: row.Name);
    }
}
