using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    /// <summary>Captures every managed row at one pump boundary and retains an immutable qualification fixture.
    /// Gameplay and authoritative roots remain unchanged. Repeating a completed request reuses its exact capture.</summary>
    /// <param name="requestId">Stable identity for retrying a lost export response.</param>
    /// <param name="cancellationToken">Cancels capture or upload; an incomplete upload cannot be loaded as a fixture.</param>
    /// <returns>The retained inventory, including the full digest to verify after download.</returns>
    public async Task<WorldReleaseFixtureManifest> ExportReleaseFixtureAsync(Guid requestId, CancellationToken cancellationToken = default) {
        if (requestId == Guid.Empty) { throw new ArgumentException("fixture request must not be empty", nameof(requestId)); }
        var managed = m_releaseManagement ?? throw new InvalidOperationException("fixture export requires a managed release");
        await RequireFixtureSourceAsync(cancellationToken).ConfigureAwait(false);
        var archive = new WorldReleaseFixtureArchive(m_blobStore, m_storageTarget, managed.Owner);
        if (await archive.LoadAsync(requestId, cancellationToken).ConfigureAwait(false) is { } retained) {
            RequireFixtureInventory(retained, managed);
            return retained;
        }
        var capture = new TaskCompletionSource<IReadOnlyDictionary<string, WorldReleaseFixtureCheckpoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        m_mailbox.Enqueue(() => {
            try {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDraining || m_drainTask is not null || !ReleaseAdmissionOpen || m_definition.Worlds.Any(row => !row.Pinned)) {
                    throw new InvalidOperationException("fixture export requires the complete admitted, non-retiring inventory");
                }
                var rows = new SortedDictionary<string, WorldReleaseFixtureCheckpoint>(StringComparer.Ordinal);
                foreach (var declared in m_definition.Worlds) {
                    if (declared.Owner != managed.Owner || !m_rows.TryGetValue(declared.World.Value, out var bookkeeping) ||
                        bookkeeping.Initializing || bookkeeping.Released || bookkeeping.PersistenceBlocked ||
                        !Instances.TryGet(declared.World.Value, out var row) || row is null) {
                        throw new InvalidOperationException("fixture export found an unavailable managed row");
                    }
                    if (!TryCaptureRow(row, out var bytes, out var reason, out var tick)) {
                        throw new InvalidOperationException($"fixture capture refused for '{declared.World}': {reason}");
                    }
                    rows.Add(declared.World.Value, new(bytes, tick));
                }
                capture.TrySetResult(rows);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { capture.TrySetCanceled(cancellationToken); }
            catch (Exception error) { capture.TrySetException(error); }
        });
        var captured = await capture.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _ = await CaptureReleaseFencesAsync(cancellationToken).ConfigureAwait(false);
        await RequireFixtureSourceAsync(cancellationToken).ConfigureAwait(false);
        var published = await archive.SaveAsync(requestId, managed.Group, managed.ExpectedRelease, m_machineId, captured, cancellationToken).ConfigureAwait(false);
        await RequireFixtureSourceAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }

    private async Task RequireFixtureSourceAsync(CancellationToken token) {
        var state = (await ReadManagedReleaseAsync(token).ConfigureAwait(false)).Record;
        if (state.HasUnfinishedOperation || state.Admission != WorldReleaseAdmissionState.Open ||
            state.ActiveRelease != m_releaseManagement!.ExpectedRelease || !ReleaseAdmissionOpen || IsDraining) {
            throw new InvalidOperationException("fixture export requires the admitted active release with no unfinished operation");
        }
    }

    private void RequireFixtureInventory(WorldReleaseFixtureManifest fixture, WorldSiloReleaseManagement managed) {
        if (fixture.Group != managed.Group || fixture.Release != managed.ExpectedRelease || fixture.MachineId != m_machineId ||
            !fixture.Worlds.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(m_definition.Worlds.Select(row => row.World.Value))) {
            throw new InvalidOperationException("fixture request belongs to another release, machine, or world inventory");
        }
    }
}
