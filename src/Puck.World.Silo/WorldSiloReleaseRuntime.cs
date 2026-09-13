using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Adapts hosted worlds to the release coordinator. The caller's host composition owns and runs each
/// simulation pump throughout the operation; this adapter never pumps a host from an I/O continuation.</summary>
public sealed class WorldSiloReleaseRuntime : IWorldReleaseRuntime {
    private readonly WorldSiloHost m_source;
    private readonly WorldSiloHost m_candidate;
    private readonly Func<WorldSiloHost> m_recoveryFactory;
    private readonly IReadOnlyList<WorldAuthorityIdentity> m_identities;
    private WorldSiloHost? m_recoverySource;

    public WorldSiloReleaseRuntime(WorldSiloHost source, WorldSiloHost candidate,
        IReadOnlyList<WorldAuthorityIdentity> identities, Func<WorldSiloHost> recoveryFactory) {
        m_source = source ?? throw new ArgumentNullException(nameof(source));
        m_candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        m_identities = identities ?? throw new ArgumentNullException(nameof(identities));
        m_recoveryFactory = recoveryFactory ?? throw new ArgumentNullException(nameof(recoveryFactory));
        if (identities.Count == 0 || identities.Distinct().Count() != identities.Count) {
            throw new ArgumentException("A non-empty, unique world inventory is required.", nameof(identities));
        }
    }

    public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(m_source, operation, operation.PendingSourceRelease);
        await m_source.DrainAsync(cancellationToken).ConfigureAwait(false);
        var roots = await m_source.CaptureReleaseRootsAsync(operation.PendingOperationId!.Value, cancellationToken).ConfigureAwait(false);
        return new(true, "source drained and recovery roots protected", roots);
    }

    public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(m_candidate, operation, operation.PendingTargetRelease);
        return ActivateAndVerifyAsync(m_candidate, cancellationToken);
    }

    public async Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(m_candidate, operation, operation.PendingTargetRelease);
        if (!m_candidate.IsDraining) { await m_candidate.AbandonPrivateReleaseAsync(cancellationToken).ConfigureAwait(false); }
        return new(true, "private candidate stopped");
    }

    public async Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(m_candidate, operation, operation.PendingTargetRelease);
        var health = await m_candidate.CheckPrivateHealthAsync(cancellationToken).ConfigureAwait(false);
        return new(health.Length == 0, health);
    }

    public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(m_candidate, operation, operation.PendingTargetRelease);
        return m_candidate.CaptureReleaseFencesAsync(cancellationToken);
    }

    public async Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(m_candidate, operation, operation.PendingTargetRelease);
        return Publication(await m_candidate.PublishManagedReleaseAdmissionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        if (m_recoverySource is not null && !m_recoverySource.IsDraining) {
            await m_recoverySource.AbandonPrivateReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        m_recoverySource = m_recoveryFactory();
        CheckHost(m_recoverySource, operation, operation.PendingSourceRelease);
        await m_recoverySource.RestoreReleaseRootsAsync(operation, cancellationToken).ConfigureAwait(false);
        return new(true, "protected source roots restored");
    }

    public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        m_recoverySource ??= m_recoveryFactory();
        CheckHost(m_recoverySource, operation, operation.PendingSourceRelease);
        return ActivateAndVerifyAsync(m_recoverySource, cancellationToken);
    }

    public async Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        if (m_recoverySource is null) { return WorldReleaseRuntimePublication.Refused; }
        CheckHost(m_recoverySource, operation, operation.PendingSourceRelease);
        return Publication(await m_recoverySource.PublishManagedReleaseAdmissionAsync(cancellationToken, completeRecovery: true).ConfigureAwait(false));
    }

    private async Task<WorldReleaseRuntimeResult> ActivateAndVerifyAsync(WorldSiloHost host, CancellationToken cancellationToken) {
        foreach (var identity in m_identities) {
            if (!await host.ActivateAsync(identity, cancellationToken).ConfigureAwait(false) ||
                !await host.CheckpointNowAsync(identity, cancellationToken).ConfigureAwait(false)) {
                return new(false, $"'{identity.World}' did not activate and checkpoint");
            }
        }
        host.Ready = true;
        var health = await host.CheckPrivateHealthAsync(cancellationToken).ConfigureAwait(false);
        return new(health.Length == 0, health);
    }

    private void CheckHost(WorldSiloHost host, WorldReleaseGroupRecord operation, string? release) {
        var managed = host.Definition.Release;
        var declared = host.Definition.Worlds.Where(static row => row.Pinned).Select(static row => new WorldAuthorityIdentity(row.Owner, row.World)).ToHashSet();
        if (managed is null || managed.Group != operation.DeploymentGroup || managed.Owner != operation.Owner ||
            (release is not null && managed.ExpectedRelease != release) || !declared.SetEquals(m_identities)) {
            throw new InvalidOperationException("the runtime host does not match the durable release and world inventory");
        }
    }

    private static WorldReleaseRuntimePublication Publication(WorldReleaseAdmissionPublication publication) => publication switch {
        WorldReleaseAdmissionPublication.Opened => WorldReleaseRuntimePublication.Opened,
        WorldReleaseAdmissionPublication.CandidatePrivate => WorldReleaseRuntimePublication.Private,
        _ => WorldReleaseRuntimePublication.Refused,
    };
}