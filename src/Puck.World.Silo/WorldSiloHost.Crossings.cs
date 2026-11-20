using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // One activation's crossing log: each record joins the row's publication queue behind every checkpoint and
    // mutation already queued, is appended under the activation's fence, and is answered once the store says what
    // became of it. The step thread never waits for that answer: the row's authority parks the step and settles it at
    // a later tick boundary. The store refuses an append from an activation its fence has closed, so that activation
    // can never claim a crossing step. A root compare-and-swap whose outcome the store could not reconcile is
    // Uncertain: the record may be durable, and the activation is blocked until a recovery reads the log back.
    private sealed class RowCrossingLog(WorldSiloHost host, WorldAuthorityIdentity identity, RowBookkeeping bookkeeping, WorldServer server) : IWorldCrossingLog {
        public ValueTask<WorldCrossingAppend> Append(in WorldCrossingEntry entry) {
            // The caller holds the row's authority gate, so the engine clock it reads here is the step boundary the
            // record describes.
            var journal = new WorldAuthorityJournalEntry(
                Encoded: WorldAuthorityCheckpointCodec.EncodeCrossingEntry(entry: in entry),
                EngineTick: server.CompletedEngineTicks,
                Kind: WorldAuthorityJournalEntryKind.Crossing,
                Tick: entry.Tick
            );
            host.FlushJournal(bookkeeping);
            Task<WorldCrossingAppend> append;

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
            return new ValueTask<WorldCrossingAppend>(task: append);
        }
    }

    private async Task<WorldCrossingAppend> AppendCrossingAfterAsync(Task previous, RowBookkeeping bookkeeping, WorldAuthorityIdentity identity, WorldAuthorityJournalEntry entry) {
        var answer = await AppendCrossingCoreAsync(
            bookkeeping: bookkeeping,
            entry: entry,
            identity: identity,
            previous: previous
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (answer.Durability == WorldCrossingDurability.Durable) {
            Interlocked.Increment(location: ref bookkeeping.JournalAdmitted);
            Interlocked.Add(location1: ref bookkeeping.JournalAdmittedBytes, value: entry.Encoded.Length);
        }
        return answer;
    }
    private async Task<WorldCrossingAppend> AppendCrossingCoreAsync(Task previous, RowBookkeeping bookkeeping, WorldAuthorityIdentity identity, WorldAuthorityJournalEntry entry) {
        await ObservePersistenceAsync(
            ct: CancellationToken.None,
            operation: previous
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (bookkeeping.PersistenceBlocked) { return new(Durability: WorldCrossingDurability.Refused, Reason: "this activation must recover before publishing again"); }

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
            return new(Durability: WorldCrossingDurability.Refused, Reason: $"the authority store did not take the record ({exception.Message})");
        }
        try {
            ObservePublication(
                bookkeeping: bookkeeping,
                outcome: outcome
            );
        } catch (InvalidDataException exception) {
            return new(Durability: WorldCrossingDurability.Uncertain, Reason: exception.Message);
        }
        return (outcome.Kind switch {
            WorldAuthorityStoreOutcomeKind.Ok => WorldCrossingAppend.Durable,
            WorldAuthorityStoreOutcomeKind.RecoveryRequired => new(Durability: WorldCrossingDurability.Uncertain, Reason: $"the authority store could not say whether it took the record ({outcome.Detail})"),
            _ => new(Durability: WorldCrossingDurability.Refused, Reason: $"the authority store refused the record ({outcome.Kind}: {outcome.Detail})"),
        });
    }
    // Replay the publication order after admission and host-slice restore. A mutation can change the spawn,
    // capacity or admission policy an arrival used, so moving it ahead of that arrival changes the traveler. A record
    // that does not decode or redo refuses the activation and leaves its crossing watermark where it stopped.
    private bool TryRecoverJournal(WorldInstance row, IReadOnlyList<WorldAuthorityJournalEntry> entries, WorldPrivateDrawKeys drawKeys, out string reason) {
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
                case WorldAuthorityJournalEntryKind.PrivateMutation:
                    if (!WorldAuthorityCheckpointCodec.TryDecodeJournalMutation(entry, out var mutation, out reason)) {
                        return false;
                    }
                    if (!row.Server.TryRecoverJournalMutation(mutation, drawKeys)) {
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
    // Suspends ingress to each named row and freezes it on the pump, then waits for the log to answer any crossing
    // record a frozen row still has in flight: the final capture that follows settles an answered record before it
    // images the row, and a frozen row sends no further record. Draining marks the silo draining in the same pump action.
    private async Task FreezeRowsAsync(string[] worldIds, CancellationToken ct, bool draining = false) {
        var frozen = new TaskCompletionSource<Task[]>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                ct.ThrowIfCancellationRequested();
                if (draining) {
                    Volatile.Write(
                        location: ref m_draining,
                        value: 1
                    );
                }
                var answers = new List<Task>(capacity: worldIds.Length);

                foreach (var worldId in worldIds) {
                    if (
                        !Instances.TryGet(instance: out var row, name: worldId) ||
                        (row is null) ||
                        (draining && m_rows.TryGetValue(key: worldId, value: out var bookkeeping) && bookkeeping.Released)
                    ) {
                        continue;
                    }
                    row.Door?.SuspendIngress();
                    row.Server.FreezeForRetirement();
                    answers.Add(item: row.Server.CrossingAnswered);
                }
                frozen.TrySetResult(result: [.. answers]);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                frozen.TrySetCanceled(cancellationToken: ct);
            } catch (Exception error) {
                frozen.TrySetException(exception: error);
            }
        });

        var answered = await frozen.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        // A failed answer still counts as an answer: the capture settles it as uncertain.
        foreach (var answer in answered) {
            try {
                await answer.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception) when (!ct.IsCancellationRequested) {
            }
        }
    }
    // Runs on the mailbox: an activation that admitted its row but cannot finish leaves no row and no bookkeeping.
    private void DiscardActivation(WorldInstance row) {
        row.Server.FreezeForRetirement();
        if (!Instances.TryRetire(
            name: row.Name,
            reason: out _
        )) {
            row.Dispose();
        }
        _ = m_rows.Remove(key: row.Name);
    }
}
