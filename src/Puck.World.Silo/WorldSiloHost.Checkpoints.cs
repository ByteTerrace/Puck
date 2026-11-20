using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // The longest a failed cadence checkpoint waits before its next attempt, in engine ticks.
    private const ulong CadenceBackoffCeilingTicks = (64UL * Puck.Maths.FixedTickConversion.TicksPerSecond);
    // How much journal a row may hold above its landed checkpoint before the cadence checkpoints it early. Recovery
    // replays that tail in one mailbox action, so these bound the replay as well as the store's tail.
    private const long JournalTailEntryLimit = 4096;
    private const long JournalTailByteLimit = JournalBacklogBytes;
    // Landed checkpoints in a row whose retention failed before persistence health names the failure.
    private const int RetentionFailureLimit = 3;

    // One captured image and what it covers: the row tick it reflects, the journal entries (and their bytes) this
    // activation had admitted when it was taken, and the journal sequence its publication covers once the
    // preceding queue settles.
    private sealed record RowCapture(byte[] Encoded, ulong Tick, long Admitted, long AdmittedBytes, Task<long> Coverage);

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

    // Whether the landed checkpoint covers everything this row holds: it has not stepped past the checkpoint's tick
    // and its queue has taken no journal entry since the capture. Ticks and journal entries are how a row's world
    // state moves between checkpoints, so a frozen or held row is covered however long ago its checkpoint landed.
    // Host settings (rate, pause) carry no journal entry; the cadence recaptures them, which is why it never skips
    // a covered row.
    private static bool IsCovered(WorldInstance row, RowBookkeeping bookkeeping) => (
        (bookkeeping.LastCheckpointOrdinal >= 0) &&
        (row.CompletedTicks == bookkeeping.LastCheckpointTick) &&
        (Volatile.Read(location: ref bookkeeping.JournalAdmitted) == bookkeeping.CheckpointAdmitted)
    );
    // Runs on the tick thread. Records when a row first holds uncovered state, so checkpoint age is measured from
    // there and never from a checkpoint that still covers everything.
    private bool NoteCoverage(WorldInstance row, RowBookkeeping bookkeeping) {
        if (IsCovered(row: row, bookkeeping: bookkeeping)) {
            bookkeeping.UncoveredTimestamp = null;
            return true;
        }
        bookkeeping.UncoveredTimestamp ??= m_clock.GetTimestamp();
        return false;
    }
    // Runs once per master step: notes newly uncovered rows and asks for an early checkpoint of any row whose journal
    // tail above its landed checkpoint has outgrown the bound.
    private void ObserveCoverage() {
        foreach (var (name, bookkeeping) in m_rows) {
            if (
                bookkeeping.Initializing ||
                bookkeeping.Released ||
                !Instances.TryGet(
                    instance: out var row,
                    name: name
                ) ||
                (row is null) ||
                NoteCoverage(
                    bookkeeping: bookkeeping,
                    row: row
                ) ||
                bookkeeping.CadenceRequested
            ) {
                continue;
            }
            if (
                ((Volatile.Read(location: ref bookkeeping.JournalAdmitted) - bookkeeping.CheckpointAdmitted) > JournalTailEntryLimit) ||
                ((Volatile.Read(location: ref bookkeeping.JournalAdmittedBytes) - bookkeeping.CheckpointAdmittedBytes) > JournalTailByteLimit)
            ) {
                bookkeeping.CadenceRequested = true;
                m_hasCadenceRequests = true;
            }
        }
    }
    private void CaptureRowsArmedByCadence() {
        if (m_cadence.IsArmed) {
            foreach (var bookkeeping in m_rows.Values) {
                bookkeeping.CadenceRequested = true;
            }
            // The master alarm has been delivered. Each row now holds its own request until its upload lands.
            m_cadence.Clear();
            m_hasCadenceRequests = (m_rows.Count != 0);
        }
        if (!m_hasCadenceRequests) { return; }
        m_hasCadenceRequests = false;
        foreach (var (name, bookkeeping) in m_rows) {
            if (!bookkeeping.CadenceRequested) { continue; }
            if (bookkeeping.CadenceUpload is { } upload) {
                if (!upload.IsCompleted) { m_hasCadenceRequests = true; continue; }
                bookkeeping.CadenceUpload = null;
                var landed = (upload.IsCompletedSuccessfully
                    ? upload.Result
                    : WorldAuthorityStoreOutcome.Failed(detail: (upload.Exception?.GetBaseException().Message ?? "the upload was canceled"))
                );

                if (landed.Ok) {
                    CompleteCadence(bookkeeping: bookkeeping);
                    continue;
                }
                if (IsPermanentRefusal(kind: landed.Kind)) {
                    bookkeeping.CadenceRefusal = $"{landed.Kind}: {landed.Detail}";
                } else {
                    DeferCadence(bookkeeping: bookkeeping);
                }
            }
            if (bookkeeping.CadenceRefusal is not null) {
                // Nothing a retry can change; an explicit checkpoint that lands clears the refusal.
                CompleteCadence(bookkeeping: bookkeeping);
                continue;
            }
            m_hasCadenceRequests = true;
            if (
                bookkeeping.Initializing ||
                bookkeeping.Released ||
                bookkeeping.Publications.Blocked ||
                bookkeeping.CheckpointUpload is { IsCompleted: false } ||
                (m_masterElapsedEngineTicks < bookkeeping.CadenceRetryAfter) ||
                !Instances.TryGet(
                    instance: out var row,
                    name: name
                ) ||
                (row is not { AwaitingMirrors: false })
            ) {
                continue;
            }
            if (!TryQueueCheckpoint(
                cancellationToken: CancellationToken.None,
                identity: bookkeeping.Identity,
                outcome: out var outcome,
                reuseUnchanged: true,
                row: row,
                upload: out _
            )) {
                if (!bookkeeping.CadenceDeferred) {
                    bookkeeping.CheckpointDeferredCount++;
                    bookkeeping.CadenceDeferred = true;
                }
                bookkeeping.LastCheckpointOutcome = outcome;
                DeferCadence(bookkeeping: bookkeeping);
            }
        }
    }
    private static void CompleteCadence(RowBookkeeping bookkeeping) {
        bookkeeping.CadenceRequested = false;
        bookkeeping.CadenceDeferred = false;
        bookkeeping.CadenceFailures = 0;
        bookkeeping.CadenceRetryAfter = 0;
    }
    // Exponential backoff on engine time, from one second to the ceiling. Checkpointing is operational, never
    // simulation state, so the schedule needs no jitter: one silo owns each row.
    private void DeferCadence(RowBookkeeping bookkeeping) {
        var delay = Math.Min(
            val1: (Puck.Maths.FixedTickConversion.TicksPerSecond << Math.Min(val1: bookkeeping.CadenceFailures, val2: 6)),
            val2: CadenceBackoffCeilingTicks
        );

        bookkeeping.CadenceFailures++;
        bookkeeping.CadenceRetryAfter = (m_masterElapsedEngineTicks + delay);
    }
    // A refusal the same activation can never overcome by retrying: the store already holds different bytes at the
    // address, the capture contradicts the authoritative root, or an operation id names another payload. A stale or
    // uncertain fence blocks the whole activation through its publication queue instead.
    private static bool IsPermanentRefusal(WorldAuthorityStoreOutcomeKind kind) => kind is
        WorldAuthorityStoreOutcomeKind.AlreadyExists or
        WorldAuthorityStoreOutcomeKind.PreconditionFailed or
        WorldAuthorityStoreOutcomeKind.OperationConflict;

    // Capture and queue together: a socket worker cannot publish a crossing between the image and its upload.
    // The worker receives only captured data and never needs the authority gate held by a waiting crossing.
    // With reuseUnchanged, a row that has neither stepped nor journaled since its last queued capture uploads that
    // capture's bytes again instead of capturing and encoding a new image.
    private bool TryQueueCheckpoint(WorldInstance row, WorldAuthorityIdentity identity, CancellationToken cancellationToken,
        out Task<WorldAuthorityStoreOutcome>? upload, out string outcome, bool reuseUnchanged = false) {
        lock (row.Server.AuthorityGate) {
            var bookkeeping = m_rows[row.Name];
            RowCapture? capture;

            if (
                reuseUnchanged &&
                (bookkeeping.QueuedCapture is { } queued) &&
                queued.Coverage.IsCompletedSuccessfully &&
                (queued.Tick == row.CompletedTicks) &&
                (queued.Admitted == Volatile.Read(location: ref bookkeeping.JournalAdmitted))
            ) {
                capture = queued;
                outcome = "ok";
            } else if (!TryCaptureRow(
                capture: out capture,
                outcome: out outcome,
                row: row
            )) {
                upload = null;
                return false;
            }
            upload = QueueCheckpoint(
                bookkeeping: bookkeeping,
                cancellationToken: cancellationToken,
                capture: capture!,
                identity: identity,
                worldId: row.Name
            );
            return true;
        }
    }
    // The journal boundary joins the row's publication queue inside the capture's authority critical section, so the
    // checkpoint covers exactly the publications its image reflects. Upload callers retain the gate through queueing;
    // raw fixture captures retain this boundary without uploading another checkpoint.
    private bool TryCaptureRow(WorldInstance row, out RowCapture? capture, out string outcome) {
        lock (row.Server.AuthorityGate) {
            if (!row.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: Instances.CaptureRow(row: row),
                reason: out var reason
            )) {
                capture = null;
                outcome = reason;
                return false;
            }
            byte[] encoded;

            try {
                encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint!);
            } catch (InvalidDataException error) {
                capture = null;
                outcome = error.Message;
                return false;
            }
            var bookkeeping = m_rows[row.Name];

            FlushJournal(bookkeeping);
            capture = new(
                Admitted: Volatile.Read(location: ref bookkeeping.JournalAdmitted),
                AdmittedBytes: Volatile.Read(location: ref bookkeeping.JournalAdmittedBytes),
                Coverage: bookkeeping.Publications.After(operation: static published => Task.FromResult(result: published.JournalSequence)),
                Encoded: encoded,
                Tick: row.CompletedTicks
            );
        }
        outcome = "ok";
        return true;
    }
    // Called under the row's authority gate, so the upload joins the publication queue right behind its capture.
    private Task<WorldAuthorityStoreOutcome> QueueCheckpoint(RowCapture capture, WorldAuthorityIdentity identity, string worldId,
        RowBookkeeping bookkeeping, CancellationToken cancellationToken) {
        var queued = UploadCheckpointAsync(
            bookkeeping: bookkeeping,
            cancellationToken: cancellationToken,
            capture: capture,
            identity: identity,
            worldId: worldId
        );

        bookkeeping.CheckpointUpload = queued;
        bookkeeping.QueuedCapture = capture;
        if (bookkeeping.CadenceRequested) {
            bookkeeping.CadenceUpload = queued;
        }
        return queued;
    }
    private async Task<WorldAuthorityStoreOutcome> UploadCheckpointAsync(RowCapture capture, WorldAuthorityIdentity identity, string worldId,
        RowBookkeeping bookkeeping, CancellationToken cancellationToken) {
        WorldAuthorityStoreOutcome outcome;

        try {
            outcome = await bookkeeping.Publications.Enqueue(cancellationToken: cancellationToken, write: async fence => await m_store.WriteCheckpointAsync(
                cancellationToken: cancellationToken,
                encoded: capture.Encoded,
                identity: identity,
                tick: capture.Tick,
                fence: fence,
                capturedJournalSequence: await capture.Coverage.ConfigureAwait(continueOnCapturedContext: false)
            ).ConfigureAwait(continueOnCapturedContext: false)).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException error) {
            Record(landed: WorldAuthorityStoreOutcome.Failed(detail: error.Message));
            throw;
        }
        Record(landed: outcome);
        return outcome;

        void Record(WorldAuthorityStoreOutcome landed) => Post(action: () => {
            if (!m_rows.TryGetValue(key: worldId, value: out var current) || !ReferenceEquals(objA: current, objB: bookkeeping)) { return; }
            bookkeeping.LastCheckpointOutcome = (landed.Ok
                ? "ok"
                : $"failed ({landed.Detail})"
            );
            if (landed.PublishedRoot is not { } published) { return; }
            bookkeeping.RetiredObjects += landed.RetiredObjects;
            if (landed.RetentionFailure is { } failure) {
                bookkeeping.LastCheckpointOutcome = $"ok (retention will retry: {failure})";
                bookkeeping.RetentionFailure = failure;
                bookkeeping.RetentionFailures++;
            } else {
                bookkeeping.RetentionFailure = null;
                bookkeeping.RetentionFailures = 0;
            }
            bookkeeping.CheckpointTimestamp = m_clock.GetTimestamp();
            bookkeeping.LastCheckpointOrdinal = published.Root.CheckpointOrdinal;
            bookkeeping.LastCheckpointTick = published.Root.CheckpointTick;
            if (published.Root.CheckpointTick == capture.Tick) {
                bookkeeping.CheckpointAdmitted = capture.Admitted;
                bookkeeping.CheckpointAdmittedBytes = capture.AdmittedBytes;
            }
            bookkeeping.UncoveredTimestamp = null;
            bookkeeping.CadenceRefusal = null;
            if (ReferenceEquals(objA: bookkeeping.QueuedCapture, objB: capture)) {
                bookkeeping.QueuedCapture = null;
            }
        });
    }
}
