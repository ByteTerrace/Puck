using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    /// <summary>Waits for the latest checkpoint upload queued for each active row at the next mailbox boundary.
    /// This includes cadence uploads and does not capture another image. Later captures belong to a later wait.</summary>
    /// <param name="ct">Cancels the wait without cancelling the uploads.</param>
    /// <returns>Whether every selected upload succeeded. A later successful checkpoint supersedes earlier failures.</returns>
    public async Task<bool> WaitForCheckpointUploadsAsync(CancellationToken ct) {
        var selected = new TaskCompletionSource<Task<WorldAuthorityStoreOutcome>[]>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => selected.TrySetResult(result: [.. m_rows.Values
            .Select(selector: static row => row.CheckpointUpload)
            .OfType<Task<WorldAuthorityStoreOutcome>>()]));
        var uploads = await selected.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        await ObservePersistenceAsync(operation: Task.WhenAll(tasks: uploads), ct: ct).ConfigureAwait(continueOnCapturedContext: false);
        return uploads.All(predicate: static upload => (upload.IsCompletedSuccessfully && upload.Result.Ok));
    }

    // Capture and queue together: a socket worker cannot publish a crossing between the image and its upload.
    // The worker receives only captured data and never needs the authority gate held by a waiting crossing.
    private bool TryQueueCheckpoint(WorldInstance row, WorldAuthorityIdentity identity, CancellationToken cancellationToken,
        out Task<WorldAuthorityStoreOutcome>? upload, out string outcome) {
        lock (row.Server.AuthorityGate) {
            if (!TryCaptureRow(
                capturedJournalSequence: out var coverage,
                encoded: out var encoded,
                outcome: out outcome,
                row: row,
                tick: out var tick
            )) {
                upload = null;
                return false;
            }
            upload = QueueCheckpoint(
                bookkeeping: m_rows[row.Name],
                cancellationToken: cancellationToken,
                capturedJournalSequence: coverage,
                encoded: encoded,
                identity: identity,
                tick: tick,
                worldId: row.Name
            );
            return true;
        }
    }
    // The journal boundary joins the row's publication queue inside the capture's authority critical section, so the
    // checkpoint covers exactly the publications its image reflects. Upload callers retain the gate through queueing;
    // raw fixture captures retain this boundary without uploading another checkpoint.
    private bool TryCaptureRow(WorldInstance row, out byte[] encoded, out string outcome, out ulong tick, out Task<long> capturedJournalSequence) {
        WorldAuthorityCheckpoint? checkpoint;

        lock (row.Server.AuthorityGate) {
            if (!row.Server.TryCaptureCheckpoint(
                checkpoint: out checkpoint,
                hostRow: Instances.CaptureRow(row: row),
                reason: out var reason
            )) {
                encoded = [];
                outcome = reason;
                tick = 0;
                capturedJournalSequence = Task.FromResult(result: -1L);
                return false;
            }
            var bookkeeping = m_rows[row.Name];

            lock (bookkeeping.TailGate) {
                capturedJournalSequence = CaptureJournalCoverageAsync(bookkeeping: bookkeeping, previous: bookkeeping.JournalTail);
                bookkeeping.JournalTail = capturedJournalSequence;
            }
        }

        encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);
        outcome = "ok";
        tick = row.CompletedTicks;

        return true;
    }
    private static async Task<long> CaptureJournalCoverageAsync(Task previous, RowBookkeeping bookkeeping) {
        await ObservePersistenceAsync(operation: previous, ct: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
        return bookkeeping.PublishedJournalSequence;
    }
    private void RecordCheckpointFailure(string worldId, RowBookkeeping bookkeeping, Exception error) {
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
                bookkeeping.LastCheckpointOutcome = $"failed ({error.Message})";
            }
        });
    }
    private Task<WorldAuthorityStoreOutcome> QueueCheckpoint(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick,
        RowBookkeeping bookkeeping, CancellationToken cancellationToken, Task<long> capturedJournalSequence) {
        lock (bookkeeping.TailGate) {
            var previous = bookkeeping.JournalTail;
            var queued = Task.Run(function: () => UploadCheckpointAsync(
                bookkeeping: bookkeeping,
                cancellationToken: cancellationToken,
                capturedJournalSequence: capturedJournalSequence,
                encoded: encoded,
                identity: identity,
                previous: previous,
                tick: tick,
                worldId: worldId
            ));

            bookkeeping.JournalTail = queued;
            bookkeeping.CheckpointUpload = queued;
            return queued;
        }
    }
    private async Task<WorldAuthorityStoreOutcome> UploadCheckpointAsync(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick,
        RowBookkeeping bookkeeping, Task previous, CancellationToken cancellationToken, Task<long> capturedJournalSequence) {
        try {
            await ObservePersistenceAsync(
                ct: cancellationToken,
                operation: previous
            );
            if (bookkeeping.PersistenceBlocked) { return WorldAuthorityStoreOutcome.RecoveryRequired(detail: "This activation must recover before publishing again."); }
            var outcome = await m_store.WriteCheckpointAsync(
                cancellationToken: cancellationToken,
                encoded: encoded,
                identity: identity,
                tick: tick,
                fence: bookkeeping.Fence,
                capturedJournalSequence: await capturedJournalSequence.ConfigureAwait(continueOnCapturedContext: false)
            );

            ObservePublication(
                bookkeeping: bookkeeping,
                outcome: outcome
            );

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
                    bookkeeping.LastCheckpointOutcome = (outcome.Ok
                        ? "ok"
                        : $"failed ({outcome.Detail})"
                    );

                    if (outcome.PublishedRoot is { } published) {
                        bookkeeping.CheckpointTimestamp = m_clock.GetTimestamp();
                        bookkeeping.LastCheckpointOrdinal = published.Root.CheckpointOrdinal;
                        bookkeeping.LastCheckpointTick = published.Root.CheckpointTick;
                    }
                }
            });
            return outcome;
        } catch (Exception error) {
            RecordCheckpointFailure(
                bookkeeping: bookkeeping,
                error: error,
                worldId: worldId
            );
            throw;
        }
    }

}
