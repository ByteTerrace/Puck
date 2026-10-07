using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // One activation's crossing log: each record joins the row's publication queue behind every checkpoint and
    // mutation already queued, is appended under the activation's fence, and is answered only once the store says
    // what became of it. The store refuses an append from an activation its fence has closed, so that activation can
    // never claim a crossing step. A root compare-and-swap whose outcome the store could not reconcile is Uncertain:
    // the record may be durable, and the activation is blocked until a recovery reads the log back.
    private sealed class RowCrossingLog(WorldSiloHost host, WorldAuthorityIdentity identity, RowBookkeeping bookkeeping, WorldServer server) : IWorldCrossingLog {
        public WorldCrossingDurability Append(in WorldCrossingEntry entry, out string reason) {
            // The caller holds the row's authority gate, so the engine clock it reads here is the step boundary the
            // record describes.
            var journal = new WorldAuthorityJournalEntry(
                Encoded: WorldAuthorityCheckpointCodec.EncodeCrossingEntry(entry: in entry),
                EngineTick: server.CompletedEngineTicks,
                Kind: WorldAuthorityJournalEntryKind.Crossing,
                Tick: entry.Tick
            );
            Task<(WorldCrossingDurability Durability, string Reason)> append;

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

            (var durability, reason) = append.GetAwaiter().GetResult();
            return durability;
        }
    }

    private async Task<(WorldCrossingDurability Durability, string Reason)> AppendCrossingAfterAsync(Task previous, RowBookkeeping bookkeeping, WorldAuthorityIdentity identity, WorldAuthorityJournalEntry entry) {
        await ObservePersistenceAsync(
            ct: CancellationToken.None,
            operation: previous
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (bookkeeping.PersistenceBlocked) { return (WorldCrossingDurability.Refused, "this activation must recover before publishing again"); }

        WorldAuthorityStoreOutcome outcome;

        try {
            outcome = await m_store.AppendJournalAsync(
                cancellationToken: CancellationToken.None,
                entry: entry,
                fence: bookkeeping.Fence,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) {
            // The store reconciles its own commit point, so a throw escapes only before the record could land.
            return (WorldCrossingDurability.Refused, $"the authority store did not take the record ({exception.Message})");
        }
        try {
            ObservePublication(
                bookkeeping: bookkeeping,
                outcome: outcome
            );
        } catch (InvalidDataException exception) {
            return (WorldCrossingDurability.Uncertain, exception.Message);
        }
        return (outcome.Kind switch {
            WorldAuthorityStoreOutcomeKind.Ok => (WorldCrossingDurability.Durable, string.Empty),
            WorldAuthorityStoreOutcomeKind.RecoveryRequired => (WorldCrossingDurability.Uncertain, $"the authority store could not say whether it took the record ({outcome.Detail})"),
            _ => (WorldCrossingDurability.Refused, $"the authority store refused the record ({outcome.Kind}: {outcome.Detail})"),
        });
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
