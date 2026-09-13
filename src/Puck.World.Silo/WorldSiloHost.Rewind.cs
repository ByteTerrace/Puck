using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    /// <summary>Applies the selected group point before this candidate activates any row. Its machine identity
    /// and closed inventory must match the retained capture.</summary>
    public async Task ApplyRestorePointAsync(WorldReleaseGroupRecord operation, CancellationToken token = default) {
        if (!ClosedGroupRewind || operation.RestorePoint is not { } selection ||
            operation.DeploymentGroup != m_releaseManagement!.Group || operation.PendingTargetRelease != m_releaseManagement.ExpectedRelease) {
            throw new InvalidOperationException("restore candidate does not enforce the selected closed group");
        }
        var point = await new WorldReleaseFixtureArchive(m_blobStore, m_storageTarget, m_releaseManagement.Owner)
            .LoadAsync(selection.PointId, token).ConfigureAwait(false);
        if (point is null || point.Identity != selection.Identity || point.MachineId != m_machineId || point.RewindBoundary != RewindBoundary) {
            throw new InvalidOperationException("restore candidate identity differs from the captured group");
        }
        await new WorldReleaseRestore(m_blobStore, m_storageTarget, m_releaseManagement.Owner).ApplyAsync(operation, token).ConfigureAwait(false);
    }

    private readonly Lock m_rewindAuthoritiesGate = new();
    private readonly HashSet<string> m_rewindAuthorities = new(StringComparer.Ordinal);
    private bool ClosedGroupRewind => m_releaseManagement?.ClosedGroupRewind == true;
    private string RewindBoundary => WorldReleaseRewindBoundary.Compute(m_releaseManagement!.Owner,
        m_releaseManagement.Group, m_definition.Worlds.Select(row => row.World.Value));

    private bool ContainsRewindAuthority(string authority) {
        lock (m_rewindAuthoritiesGate) { return m_rewindAuthorities.Contains(authority); }
    }

    private async Task RequireRewindActivationAsync(WorldAuthorityIdentity identity, CancellationToken token) {
        var root = await m_store.LoadRootAsync(identity, token).ConfigureAwait(false);
        if (root?.Root.RewindBoundary is { } boundary && (!ClosedGroupRewind || boundary != RewindBoundary)) {
            throw new InvalidOperationException("this world requires its original closed rewind group before activation");
        }
    }
}
