using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Adapts hosted worlds to the release coordinator. The caller's host composition owns and runs each
/// simulation pump throughout the operation; this adapter never pumps a host from an I/O continuation.</summary>
public sealed class WorldSiloReleaseRuntime : IWorldReleaseRuntime {
    private readonly WorldSiloHost m_candidate;
    private readonly IReadOnlyList<WorldAuthorityIdentity> m_identities;
    private readonly Func<WorldSiloHost> m_recoveryFactory;
    private readonly WorldSiloHost m_source;

    private WorldSiloHost? m_recoverySource;

    public WorldSiloReleaseRuntime(WorldSiloHost source, WorldSiloHost candidate,
        IReadOnlyList<WorldAuthorityIdentity> identities, Func<WorldSiloHost> recoveryFactory) {
        m_source = (source ?? throw new ArgumentNullException(paramName: nameof(source)));
        m_candidate = (candidate ?? throw new ArgumentNullException(paramName: nameof(candidate)));
        m_identities = (identities ?? throw new ArgumentNullException(paramName: nameof(identities)));
        m_recoveryFactory = (recoveryFactory ?? throw new ArgumentNullException(paramName: nameof(recoveryFactory)));
        if (
            (identities.Count == 0) ||
            (identities.Distinct().Count() != identities.Count)
        ) {
            throw new ArgumentException(
                message: "A non-empty, unique world inventory is required.",
                paramName: nameof(identities)
            );
        }
    }

    private async Task<WorldReleaseRuntimeResult> ActivateAndVerifyAsync(WorldSiloHost host, CancellationToken cancellationToken) {
        foreach (var identity in m_identities) {
            if (
                !await host.ActivateAsync(
                ct: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false) ||
                !await host.CheckpointNowAsync(
                ct: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false)
            ) {
                return new(
                    false,
                    $"'{identity.World}' did not activate and checkpoint"
                );
            }
        }
        host.Ready = true;
        var health = await host.CheckPrivateHealthAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return new(
            (health.Length == 0),
            health
        );
    }
    private void CheckHost(WorldSiloHost host, WorldReleaseGroupRecord operation, string? release) {
        var managed = host.Definition.Release;
        var declared = host.Definition.Worlds.Where(predicate: static row => row.Pinned).Select(selector: static row => new WorldAuthorityIdentity(
            Owner: row.Owner,
            World: row.World
        )).ToHashSet();

        if (
            (managed is null) ||
            (managed.Group != operation.DeploymentGroup) ||
            (managed.Owner != operation.Owner) ||
            ((release is not null) && (managed.ExpectedRelease != release)) ||
            !declared.SetEquals(other: m_identities)
        ) {
            throw new InvalidOperationException(message: "the runtime host does not match the durable release and world inventory");
        }
    }
    private static WorldReleaseRuntimePublication Publication(WorldReleaseAdmissionPublication publication) => publication switch {
        WorldReleaseAdmissionPublication.Opened => WorldReleaseRuntimePublication.Opened,
        WorldReleaseAdmissionPublication.CandidatePrivate => WorldReleaseRuntimePublication.Private,
        _ => WorldReleaseRuntimePublication.Refused,
    };

    public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(
            host: m_source,
            operation: operation,
            release: operation.PendingSourceRelease
        );
        await m_source.DrainAsync(ct: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var roots = await m_source.CaptureReleaseRootsAsync(
            operation.PendingOperationId!.Value,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return new(
            Detail: "source drained and recovery roots protected",
            RecoveryRoots: roots,
            Succeeded: true
        );
    }
    public async Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(
            host: m_candidate,
            operation: operation,
            release: operation.PendingTargetRelease
        );
        return Publication(publication: await m_candidate.PublishManagedReleaseAdmissionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
    }
    public async Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        if (m_recoverySource is null) { return WorldReleaseRuntimePublication.Refused; }
        CheckHost(
            host: m_recoverySource,
            operation: operation,
            release: operation.PendingSourceRelease
        );
        return Publication(publication: await m_recoverySource.PublishManagedReleaseAdmissionAsync(
            cancellationToken,
            completeRecovery: true
        ).ConfigureAwait(continueOnCapturedContext: false));
    }
    public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(
            host: m_candidate,
            operation: operation,
            release: operation.PendingTargetRelease
        );
        return m_candidate.CaptureReleaseFencesAsync(ct: cancellationToken);
    }
    public async Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        if (
            (m_recoverySource is not null) &&
            !m_recoverySource.IsDraining
        ) {
            await m_recoverySource.AbandonPrivateReleaseAsync(ct: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        m_recoverySource = m_recoveryFactory();
        CheckHost(
            host: m_recoverySource,
            operation: operation,
            release: operation.PendingSourceRelease
        );
        await m_recoverySource.RestoreReleaseRootsAsync(
            ct: cancellationToken,
            operation: operation
        ).ConfigureAwait(continueOnCapturedContext: false);
        return new(
            true,
            "protected source roots restored"
        );
    }
    public async Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(
            host: m_candidate,
            operation: operation,
            release: operation.PendingTargetRelease
        );
        if (
            (operation.PendingPhase == WorldReleaseOperationPhase.Activate) &&
            (operation.RestorePoint is not null)
        ) {
            await m_candidate.ApplyRestorePointAsync(
                operation: operation,
                token: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        return await ActivateAndVerifyAsync(
            cancellationToken: cancellationToken,
            host: m_candidate
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        m_recoverySource ??= m_recoveryFactory();
        CheckHost(
            host: m_recoverySource,
            operation: operation,
            release: operation.PendingSourceRelease
        );
        return ActivateAndVerifyAsync(
            cancellationToken: cancellationToken,
            host: m_recoverySource
        );
    }
    public async Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(
            host: m_candidate,
            operation: operation,
            release: operation.PendingTargetRelease
        );
        if (!m_candidate.IsDraining) { await m_candidate.AbandonPrivateReleaseAsync(ct: cancellationToken).ConfigureAwait(continueOnCapturedContext: false); }
        return new(
            true,
            "private candidate stopped"
        );
    }
    public async Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
        CheckHost(
            host: m_candidate,
            operation: operation,
            release: operation.PendingTargetRelease
        );
        var health = await m_candidate.CheckPrivateHealthAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return new(
            (health.Length == 0),
            health
        );
    }
}
