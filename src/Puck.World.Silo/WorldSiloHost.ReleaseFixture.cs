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
        if (requestId == Guid.Empty) {
            throw new ArgumentException(
            message: "fixture request must not be empty",
            paramName: nameof(requestId)
        );
        }
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

        Post(action: () => {
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
                capturedAt = m_clock.GetUtcNow();
                foreach (var declared in m_definition.Worlds) {
                    if (
                        (declared.Owner != managed.Owner) ||
                        !m_rows.TryGetValue(
                        key: declared.World.Value,
                        value: out var bookkeeping
                    ) ||
                        bookkeeping.Initializing ||
                        bookkeeping.Released ||
                        bookkeeping.Publications.Blocked ||
                        !Instances.TryGet(
                        declared.World.Value,
                        out var row
                    ) ||
                        (row is null)
                    ) {
                        throw new InvalidOperationException(message: "fixture export found an unavailable managed row");
                    }
                    if (!TryCaptureRow(
                        capture: out var capture,
                        outcome: out var reason,
                        row: row
                    )) {
                        throw new InvalidOperationException(message: $"fixture capture refused for '{declared.World}': {reason}");
                    }
                    var (bytes, tick) = (capture!.Encoded, capture.Tick);
                    if (ClosedGroupRewind) {
                        if (!WorldAuthorityCheckpointCodec.TryDecode(
                            bytes: bytes,
                            checkpoint: out var checkpoint,
                            documentDirectory: m_documentDirectory,
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
                    // The root read joins the queue behind this capture's preceding publications and ahead of all later
                    // mutations. Only the small root read holds the queue; its immutable graph is copied afterward.
                    rows.Add(
                        key: declared.World.Value,
                        value: new(
                            Checkpoint: bytes,
                            Identity: identity,
                            Root: bookkeeping.Publications.After(operation: published => ReadFixtureRootAsync(identity: identity, published: published, token: cancellationToken)),
                            Tick: tick
                        )
                    );
                }
                capture.TrySetResult(result: rows);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { capture.TrySetCanceled(cancellationToken: cancellationToken); } catch (Exception error) { capture.TrySetException(exception: error); }
        });
        var boundary = await capture.Task.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var receiptStore = new WorldAuthorityBlobStore(
            documentDirectory: m_documentDirectory,
            store: m_blobStore,
            target: m_storageTarget,
            timeProvider: m_clock
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

    // Runs in the row's publication queue; a failed or cancelled read belongs to the export request and leaves the
    // queue serving.
    private async Task<WorldAuthorityRootSnapshot> ReadFixtureRootAsync(WorldAuthorityIdentity identity, RowPublished published, CancellationToken token) {
        if (published.Blocked) { throw new InvalidOperationException(message: "fixture capture requires healthy preceding publications"); }
        var selected = await m_store.LoadRootAsync(
            cancellationToken: token,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (selected is not { } root) ||
            (root.Root.Epoch != published.Fence.Epoch) ||
            (root.Root.FenceToken != published.Fence.Token) ||
            (root.Root.JournalSequence != published.JournalSequence)
        ) {
            throw new InvalidOperationException(message: "fixture capture lost its authority or publication boundary");
        }
        return (ClosedGroupRewind
            ? await new WorldAuthorityBlobStore(
                documentDirectory: m_documentDirectory,
                store: m_blobStore,
                target: m_storageTarget,
                timeProvider: m_clock
            ).EstablishRewindBoundaryAsync(
                identity,
                published.Fence,
                RewindBoundary,
                token
            ).ConfigureAwait(continueOnCapturedContext: false)
            : root
        );
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
