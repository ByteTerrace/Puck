using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private sealed record ReleaseFixtureCapture(WorldAuthorityIdentity Identity, byte[] Checkpoint, ulong Tick, Task<WorldAuthorityRootSnapshot> Root);

    /// <summary>Captures every managed row at one pump boundary and retains an immutable qualification fixture.
    /// Gameplay remains unchanged. The first rewindable capture records its enforced boundary in the roots;
    /// repeating a completed request reuses its exact capture.</summary>
    /// <param name="requestId">Stable identity for retrying a lost export response.</param>
    /// <param name="cancellationToken">Cancels capture or upload; an incomplete upload cannot be loaded as a fixture.</param>
    /// <returns>The retained inventory, including the full digest to verify after download.</returns>
    public async Task<WorldReleaseFixtureManifest> ExportReleaseFixtureAsync(Guid requestId, CancellationToken cancellationToken = default) {
        if (requestId == Guid.Empty) { throw new ArgumentException(
            message: "fixture request must not be empty",
            paramName: nameof(requestId)
        ); }
        var managed = (m_releaseManagement ?? throw new InvalidOperationException(message: "fixture export requires a managed release"));

        await RequireFixtureSourceAsync(token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var archive = new WorldReleaseFixtureArchive(
            m_blobStore,
            m_storageTarget,
            managed.Owner
        );

        if (await archive.LoadAsync(
            cancellationToken: cancellationToken,
            requestId: requestId
        ).ConfigureAwait(continueOnCapturedContext: false) is { } retained) {
            RequireFixtureInventory(
                fixture: retained,
                managed: managed
            );
            return retained;
        }
        var capture = new TaskCompletionSource<IReadOnlyDictionary<string, ReleaseFixtureCapture>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset? capturedAt = null;

        m_mailbox.Enqueue(item: () => {
            try {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    IsDraining ||
                    (m_drainTask is not null) ||
                    !ReleaseAdmissionOpen ||
                    m_definition.Worlds.Any(predicate: row => !row.Pinned)
                ) {
                    throw new InvalidOperationException(message: "fixture export requires the complete admitted, non-retiring inventory");
                }
                var rows = new SortedDictionary<string, ReleaseFixtureCapture>(comparer: StringComparer.Ordinal);

                if (
                    ClosedGroupRewind &&
                    !Instances.Names.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: m_definition.Worlds.Select(selector: row => row.World.Value))
                ) {
                    throw new InvalidOperationException(message: "rewind capture requires exactly the declared world inventory");
                }
                capturedAt = DateTimeOffset.UtcNow;
                foreach (var declared in m_definition.Worlds) {
                    if (
                        (declared.Owner != managed.Owner) ||
                        !m_rows.TryGetValue(
                        key: declared.World.Value,
                        value: out var bookkeeping
                    ) ||
                        bookkeeping.Initializing ||
                        bookkeeping.Released ||
                        bookkeeping.PersistenceBlocked ||
                        !Instances.TryGet(
                        declared.World.Value,
                        out var row
                    ) ||
                        (row is null)
                    ) {
                        throw new InvalidOperationException(message: "fixture export found an unavailable managed row");
                    }
                    if (!TryCaptureRow(
                        encoded: out var bytes,
                        outcome: out var reason,
                        row: row,
                        tick: out var tick
                    )) {
                        throw new InvalidOperationException(message: $"fixture capture refused for '{declared.World}': {reason}");
                    }
                    if (ClosedGroupRewind) {
                        if (!WorldAuthorityCheckpointCodec.TryDecode(
                            bytes: bytes,
                            checkpoint: out var checkpoint,
                            reason: out reason
                        )) { throw new InvalidDataException(message: reason); }
                        WorldReleaseRewindBoundary.RequireContained(
                            checkpoint: checkpoint!,
                            containsAuthority: ContainsRewindAuthority
                        );
                    }
                    var identity = new WorldAuthorityIdentity(
                        Owner: declared.Owner,
                        World: declared.World
                    );
                    // Insert the root read behind this capture's preceding publications and ahead of all later
                    // mutations. Only the small root read holds the queue; its immutable graph is copied afterward.
                    var selected = ReadFixtureRootAsync(
                        bookkeeping: bookkeeping,
                        identity: identity,
                        previous: bookkeeping.JournalTail,
                        token: cancellationToken
                    );

                    bookkeeping.JournalTail = ObserveFixtureReadAsync(selected: selected);
                    rows.Add(
                        key: declared.World.Value,
                        value: new(
                            Checkpoint: bytes,
                            Identity: identity,
                            Root: selected,
                            Tick: tick
                        )
                    );
                }
                capture.TrySetResult(result: rows);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { capture.TrySetCanceled(cancellationToken: cancellationToken); } catch (Exception error) { capture.TrySetException(exception: error); }
        });
        var boundary = await capture.Task.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var receiptStore = new WorldAuthorityBlobStore(
            store: m_blobStore,
            target: m_storageTarget
        );
        var captured = new SortedDictionary<string, WorldReleaseFixtureCheckpoint>(comparer: StringComparer.Ordinal);

        foreach (var row in boundary) {
            var root = await row.Value.Root.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var receipts = await receiptStore.CaptureReceiptSnapshotAsync(
                row.Value.Identity,
                root,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            captured.Add(
                key: row.Key,
                value: new(
                    row.Value.Checkpoint,
                    row.Value.Tick,
                    receipts
                )
            );
        }
        _ = await CaptureReleaseFencesAsync(ct: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        await RequireFixtureSourceAsync(token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var published = await archive.SaveAsync(
            requestId,
            managed.Group,
            managed.ExpectedRelease,
            m_machineId,
            captured,
            cancellationToken,
            (ClosedGroupRewind
            ? RewindBoundary
            : null),
            (ClosedGroupRewind
            ? capturedAt
            : null)
        ).ConfigureAwait(continueOnCapturedContext: false);

        await RequireFixtureSourceAsync(token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return published;
    }

    private async Task<WorldAuthorityRootSnapshot> ReadFixtureRootAsync(WorldAuthorityIdentity identity, RowBookkeeping bookkeeping,
        Task previous, CancellationToken token) {
        await previous.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
        if (bookkeeping.PersistenceBlocked) { throw new InvalidOperationException(message: "fixture capture requires healthy preceding publications"); }
        var selected = await m_store.LoadRootAsync(
            cancellationToken: token,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (selected is not { } root) ||
            (root.Root.Epoch != bookkeeping.Fence.Epoch) ||
            (root.Root.FenceToken != bookkeeping.Fence.Token) ||
            (root.Root.JournalSequence != bookkeeping.PublishedJournalSequence)
        ) {
            throw new InvalidOperationException(message: "fixture capture lost its authority or publication boundary");
        }
        return (ClosedGroupRewind
            ? await new WorldAuthorityBlobStore(
                store: m_blobStore,
                target: m_storageTarget
            ).EstablishRewindBoundaryAsync(
                identity,
                bookkeeping.Fence,
                RewindBoundary,
                token
            ).ConfigureAwait(continueOnCapturedContext: false)
            : root
        );
    }
    private static async Task ObserveFixtureReadAsync(Task selected) {
        try { await selected.ConfigureAwait(continueOnCapturedContext: false); }
        // A failed/canceled read belongs to the export request; it must not poison the serving publication queue.
        catch { }
    }
    private async Task RequireFixtureSourceAsync(CancellationToken token) {
        var state = (await ReadManagedReleaseAsync(ct: token).ConfigureAwait(continueOnCapturedContext: false)).Record;

        if (
            state.HasUnfinishedOperation ||
            (state.Admission != WorldReleaseAdmissionState.Open) ||
            (state.ActiveRelease != m_releaseManagement!.ExpectedRelease) ||
            !ReleaseAdmissionOpen ||
            IsDraining
        ) {
            throw new InvalidOperationException(message: "fixture export requires the admitted active release with no unfinished operation");
        }
    }
    private void RequireFixtureInventory(WorldReleaseFixtureManifest fixture, WorldSiloReleaseManagement managed) {
        if (
            (fixture.Group != managed.Group) ||
            (fixture.Release != managed.ExpectedRelease) ||
            (fixture.MachineId != m_machineId) ||
            !fixture.Worlds.Keys.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: m_definition.Worlds.Select(selector: row => row.World.Value))
        ) {
            throw new InvalidOperationException(message: "fixture request belongs to another release, machine, or world inventory");
        }
    }
}
