namespace Puck.World.Server;

/// <summary>
/// Results produced by a trusted runner executing the exact packaged release pair.
/// Accepting operator-authored JSON is not a qualification runner.
/// </summary>
public sealed record WorldReleaseQualificationReceipt {
    public string? SourceRelease { get; init; }
    public required string TargetRelease { get; init; }
    public required string EvidenceId { get; init; }
    public required string SourceStateHash { get; init; }
    public required string TargetStateHash { get; init; }
    public required string ReverseStateHash { get; init; }
}

/// <summary>Runner that performs the packaged, ordered pair qualification outside the deployment transaction.</summary>
public interface IWorldReleaseQualificationRunner {
    Task<WorldReleaseQualificationReceipt?> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default);
}

/// <summary>Runner that qualifies a candidate against an empty bootstrap source.</summary>
public interface IWorldReleaseBootstrapQualificationRunner {
    Task<WorldReleaseQualificationReceipt?> RunAsync(WorldReleaseManifest target, CancellationToken cancellationToken = default);
}

/// <summary>Result returned by a local or Azure release runtime driver.</summary>
public readonly record struct WorldReleaseRuntimeResult(
    bool Succeeded,
    string Detail,
    IReadOnlyDictionary<string, string>? RecoveryRoots = null);

/// <summary>Runtime publication result. Private means the candidate remains fenced and unadmitted.</summary>
public enum WorldReleaseRuntimePublication { Opened, Private, Refused }

/// <summary>Worker lifecycle seam used by the durable coordinator.</summary>
public interface IWorldReleaseRuntime {
    Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    /// <summary>Ensures the exact target is running, preserving an already running target on retries.</summary>
    Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    /// <summary>Restores protected roots without starting a source. This must be safe to repeat while Recover remains durable.</summary>
    Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
}

/// <summary>Result of one durable/runtime orchestration attempt.</summary>
public readonly record struct WorldReleaseRunResult(
    bool Completed,
    bool CandidatePrivate,
    string Detail,
    WorldReleaseGroupSnapshot? Snapshot = null) {
    /// <summary>The requested target failed, but the source was restored. This is not a successful deployment.</summary>
    public bool SourceRecovered { get; init; }
}

/// <summary>
/// Coordinates the guarded deployment-group state machine. Hosting adapters perform
/// worker lifecycle and private health checks; this type is the shared durable
/// transition path they call before and after those effects.
/// </summary>
public sealed class WorldReleaseCoordinator {
    private readonly WorldReleaseGroupStore m_groups;

    public WorldReleaseCoordinator(WorldReleaseGroupStore groups) => m_groups = groups ?? throw new ArgumentNullException(nameof(groups));

    /// <summary>
    /// Runs or resumes a deployment at the durable boundary. Runtime effects happen only
    /// after the corresponding group phase is durable; pre-commit failures recover only
    /// after protected roots exist, while post-commit failures remain on the target.
    /// </summary>
    public async Task<WorldReleaseRunResult> RunAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest source,
        WorldReleaseManifest target,
        IWorldReleaseQualificationRunner? qualificationRunner,
        Guid operationId,
        IWorldReleaseRuntime runtime,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(runtime);
        var durable = await m_groups.LoadAsync(current.Record.DeploymentGroup, cancellationToken).ConfigureAwait(false);
        if (durable is not { } currentState) { return RefusedRun("the deployment-group state disappeared before release start"); }
        var pending = currentState;
        if (pending.Record.PendingOperationId is null) {
            var begun = await BeginDeploymentAsync(pending, source, target, qualificationRunner, operationId, cancellationToken).ConfigureAwait(false);
            if (!begun.Ok) { return RefusedRun(begun.Detail); }
            pending = begun.Snapshot!.Value;
        } else if (pending.Record.PendingOperationId != operationId ||
            !string.Equals(pending.Record.PendingSourceRelease, source.Identity, StringComparison.Ordinal) ||
            !string.Equals(pending.Record.PendingTargetRelease, target.Identity, StringComparison.Ordinal)) {
            return RefusedRun("the requested operation or release pair does not match the durable pending operation");
        }
        return await ResumeAsync(pending, target, runtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes the exact durable operation without accepting a different source or target.</summary>
    public async Task<WorldReleaseRunResult> ResumeAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest target,
        IWorldReleaseRuntime runtime,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(runtime);
        var durable = await m_groups.LoadAsync(current.Record.DeploymentGroup, cancellationToken).ConfigureAwait(false);
        if (durable is not { } state) { return RefusedRun("the deployment-group state disappeared before resume"); }
        if (current.Record.PendingOperationId is null || current.Record.PendingOperationId != state.Record.PendingOperationId ||
            !string.Equals(current.Record.PendingSourceRelease, state.Record.PendingSourceRelease, StringComparison.Ordinal) ||
            !string.Equals(state.Record.PendingTargetRelease, target.Identity, StringComparison.Ordinal)) {
            return RefusedRun("the requested target does not match the durable pending operation");
        }
        try {
            while (state.Record.PendingOperationId is { } operationId) {
                switch (state.Record.PendingPhase) {
                    case WorldReleaseOperationPhase.Prepare: {
                        var advanced = await RecordDrainPhaseAsync(state, cancellationToken).ConfigureAwait(false);
                        if (!advanced.Ok) { return RefusedRun(advanced.Detail); }
                        state = advanced.Snapshot!.Value;
                        continue;
                    }
                    case WorldReleaseOperationPhase.Drain: {
                        if (state.Record.RecoveryRoots.Count == 0) {
                            var drained = await runtime.DrainSourceAndCaptureAsync(state.Record, cancellationToken).ConfigureAwait(false);
                            if (!drained.Succeeded || drained.RecoveryRoots is not { Count: > 0 }) {
                                return RefusedRun($"source drain did not produce protected roots; retry drain: {drained.Detail}");
                            }
                            var captured = await RecordCapturedRootsAsync(state, drained.RecoveryRoots, cancellationToken).ConfigureAwait(false);
                            if (!captured.Ok) { return RefusedRun(captured.Detail); }
                            state = captured.Snapshot!.Value;
                        }
                        var advanced = await RecordActivationAsync(state, cancellationToken).ConfigureAwait(false);
                        if (!advanced.Ok) { return RefusedRun(advanced.Detail); }
                        state = advanced.Snapshot!.Value;
                        continue;
                    }
                    case WorldReleaseOperationPhase.Activate: {
                        var started = await runtime.StartCandidatePrivatelyAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        if (!started.Succeeded) { return await RecoverPreCommitAsync(state, runtime, started.Detail, cancellationToken).ConfigureAwait(false); }
                        var advanced = await RecordVerificationAsync(state, cancellationToken).ConfigureAwait(false);
                        if (!advanced.Ok) { return RefusedRun(advanced.Detail); }
                        state = advanced.Snapshot!.Value;
                        continue;
                    }
                    case WorldReleaseOperationPhase.Verify: {
                        var started = await runtime.StartCandidatePrivatelyAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        if (!started.Succeeded) { return await RecoverPreCommitAsync(state, runtime, started.Detail, cancellationToken).ConfigureAwait(false); }
                        var verified = await runtime.VerifyCandidatePrivatelyAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        if (!verified.Succeeded) { return await RecoverPreCommitAsync(state, runtime, verified.Detail, cancellationToken).ConfigureAwait(false); }
                        var fences = await runtime.ReadFenceCensusAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        var committed = await CommitAsync(state, fences, cancellationToken).ConfigureAwait(false);
                        if (!committed.Ok) { return RefusedRun(committed.Detail); }
                        state = committed.Snapshot!.Value;
                        continue;
                    }
                    case WorldReleaseOperationPhase.Commit: {
                        var started = await runtime.StartCandidatePrivatelyAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        if (!started.Succeeded) { return RefusedRun($"committed target restart failed; resume the target: {started.Detail}"); }
                        var verified = await runtime.VerifyCandidatePrivatelyAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        if (!verified.Succeeded) { return RefusedRun($"committed target verification failed: {verified.Detail}"); }
                        var publication = await runtime.PublishCandidateAsync(state.Record, cancellationToken).ConfigureAwait(false);
                        if (publication == WorldReleaseRuntimePublication.Private) { return new(false, true, "candidate remains private until the group publication barrier succeeds", state); }
                        if (publication != WorldReleaseRuntimePublication.Opened) { return RefusedRun("committed target admission publication was refused"); }
                        return new(true, false, "release committed and admitted", await m_groups.LoadAsync(state.Record.DeploymentGroup, cancellationToken).ConfigureAwait(false));
                    }
                    case WorldReleaseOperationPhase.Recover:
                    case WorldReleaseOperationPhase.RecoverActivate:
                        return await RecoverPreCommitAsync(state, runtime, state.Record.PendingFailure!, cancellationToken).ConfigureAwait(false);
                    default:
                        return RefusedRun("the durable operation is in an unsupported terminal phase");
                }
            }
            return new(true, false, "no pending release operation", state);
        } catch (Exception error) when (error is not OperationCanceledException) {
            WorldReleaseGroupSnapshot? latest;
            try { latest = await m_groups.LoadAsync(state.Record.DeploymentGroup, cancellationToken).ConfigureAwait(false); }
            catch (Exception readError) when (readError is not OperationCanceledException) {
                return RefusedRun($"cannot establish the durable commit boundary; restore nothing and retry status: {readError.Message}");
            }
            if (latest is not { } fresh || fresh.Record.PendingOperationId != state.Record.PendingOperationId) {
                return RefusedRun("the operation changed while its result was uncertain; inspect durable status before resuming");
            }
            state = fresh;
            if (state.Record.PendingCommitted || state.Record.PendingPhase is WorldReleaseOperationPhase.Commit or WorldReleaseOperationPhase.Finalized) {
                return RefusedRun($"post-commit release requires target resume: {error.Message}");
            }
            return await RecoverPreCommitAsync(state, runtime, error.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Validates an engine-only pair and qualification receipt before claiming the group.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginDeploymentAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest source,
        WorldReleaseManifest target,
        IWorldReleaseQualificationRunner? qualificationRunner,
        Guid operationId,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(current.Record.ActiveRelease, source.Identity, StringComparison.Ordinal)) {
            return Refused("the durable active release does not match the deployment source manifest");
        }
        var qualification = qualificationRunner is null ? null : await qualificationRunner.RunAsync(source, target, cancellationToken).ConfigureAwait(false);
        if (!TryQualifyPair(source, target, qualification, out var reason)) {
            return Refused(reason);
        }
        return await m_groups.BeginAsync(current, operationId, target.Identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a private first deployment without inventing a source-release identity.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginBootstrapAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest target,
        IWorldReleaseBootstrapQualificationRunner? qualificationRunner,
        Guid operationId,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        if (current.Record.ActiveRelease is not null || current.Record.Admission != WorldReleaseAdmissionState.Closed) {
            return Refused("bootstrap requires a closed group with no active release");
        }
        if (qualificationRunner is null) {
            return Refused("bootstrap requires a qualification runner for the candidate release");
        }
        var receipt = await qualificationRunner.RunAsync(target, cancellationToken).ConfigureAwait(false);
        if (receipt is null || !string.Equals(receipt.TargetRelease, target.Identity, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.EvidenceId) || string.IsNullOrWhiteSpace(receipt.TargetStateHash)) {
            return Refused("bootstrap qualification did not produce a verified candidate receipt");
        }
        return await m_groups.BeginAsync(current, operationId, target.Identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Claims the retained predecessor as a new rollback operation after pair qualification.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginRollbackAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest active,
        WorldReleaseManifest predecessor,
        IWorldReleaseQualificationRunner? qualificationRunner,
        Guid operationId,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(predecessor);
        if (!string.Equals(current.Record.ActiveRelease, active.Identity, StringComparison.Ordinal) ||
            !string.Equals(current.Record.PreviousRelease, predecessor.Identity, StringComparison.Ordinal)) {
            return Refused("the durable active/previous release pointers do not match the rollback manifests");
        }
        var qualification = qualificationRunner is null ? null : await qualificationRunner.RunAsync(active, predecessor, cancellationToken).ConfigureAwait(false);
        if (!TryQualifyPair(active, predecessor, qualification, out var reason)) {
            return Refused(reason);
        }
        return await m_groups.BeginRollbackAsync(current, operationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the maintenance boundary and the protected source roots captured by a successful drain.</summary>
    public Task<WorldReleaseGroupOutcome> RecordDrainAsync(
        WorldReleaseGroupSnapshot current,
        IReadOnlyDictionary<string, string> recoveryRoots,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(recoveryRoots);
        if (recoveryRoots.Count == 0 || recoveryRoots.Any(static item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value))) {
            return Task.FromResult(Refused("a successful drain must provide a non-empty protected root for every managed row"));
        }
        return m_groups.AdvanceAsync(current, current.Record with {
            PendingPhase = WorldReleaseOperationPhase.Drain,
            Admission = WorldReleaseAdmissionState.Closed,
            RecoveryRoots = new SortedDictionary<string, string>(recoveryRoots.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            Revision = checked(current.Record.Revision + 1),
        }, cancellationToken);
    }

    /// <summary>Closes admission before a drain begins; a failed drain remains retryable in this phase.</summary>
    public Task<WorldReleaseGroupOutcome> RecordDrainPhaseAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        m_groups.AdvanceAsync(current, current.Record with {
            PendingPhase = WorldReleaseOperationPhase.Drain,
            Admission = WorldReleaseAdmissionState.Closed,
            Revision = checked(current.Record.Revision + 1),
        }, cancellationToken);

    /// <summary>Attaches roots captured by a successful drain to its already durable drain phase.</summary>
    public Task<WorldReleaseGroupOutcome> RecordCapturedRootsAsync(
        WorldReleaseGroupSnapshot current,
        IReadOnlyDictionary<string, string> recoveryRoots,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(recoveryRoots);
        if (recoveryRoots.Count == 0 || recoveryRoots.Any(static item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value))) {
            return Task.FromResult(Refused("a successful drain must provide a non-empty protected root for every managed row"));
        }
        return m_groups.AdvanceAsync(current, current.Record with {
            PendingPhase = current.Record.PendingPhase,
            RecoveryRoots = new SortedDictionary<string, string>(recoveryRoots.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            Revision = checked(current.Record.Revision + 1),
        }, cancellationToken);
    }

    /// <summary>Records private candidate activation after all source roots are protected.</summary>
    public Task<WorldReleaseGroupOutcome> RecordActivationAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        AdvancePhaseAsync(current, WorldReleaseOperationPhase.Activate, cancellationToken);

    /// <summary>Records private verification without opening public admission.</summary>
    public Task<WorldReleaseGroupOutcome> RecordVerificationAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        AdvancePhaseAsync(current, WorldReleaseOperationPhase.Verify, cancellationToken);

    /// <summary>Publishes target pointers at the durable commit boundary using a fresh per-world fence census.</summary>
    public Task<WorldReleaseGroupOutcome> CommitAsync(
        WorldReleaseGroupSnapshot current,
        IEnumerable<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> fences,
        CancellationToken cancellationToken = default) =>
        m_groups.CommitAsync(current, fences, cancellationToken);

    /// <summary>Opens admission after the hosting adapter has verified the committed target.</summary>
    public Task<WorldReleaseGroupOutcome> PublishAdmissionAsync(
        WorldReleaseGroupSnapshot current,
        string release,
        Guid operationId,
        Guid authorityLease,
        CancellationToken cancellationToken = default) =>
        m_groups.OpenAdmissionAsync(current, release, operationId, authorityLease, cancellationToken);

    /// <summary>Completes an admitted operation while retaining its immutable recovery references in history.</summary>
    public Task<WorldReleaseGroupOutcome> FinalizeAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        m_groups.FinalizeAsync(current, cancellationToken);

    /// <summary>Completes pre-commit recovery without ever making the failed target active.</summary>
    public Task<WorldReleaseGroupOutcome> RecoverToSourceAsync(WorldReleaseGroupSnapshot current, string failure, CancellationToken cancellationToken = default) =>
        m_groups.RecoverToSourceAsync(current, failure, cancellationToken);

    /// <summary>Checks externally supplied qualification evidence for the exact ordered pair.</summary>
    public static bool TryQualifyPair(
        WorldReleaseManifest source,
        WorldReleaseManifest target,
        WorldReleaseQualificationReceipt? evidence,
        out string reason) {
        if (!WorldReleaseTransitionPolicy.TryPrepare(source, target, out _, out reason)) {
            return false;
        }
        if (evidence is null ||
            !string.Equals(evidence.SourceRelease, source.Identity, StringComparison.Ordinal) ||
            !string.Equals(evidence.TargetRelease, target.Identity, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
            string.IsNullOrWhiteSpace(evidence.SourceStateHash) ||
            string.IsNullOrWhiteSpace(evidence.TargetStateHash) ||
            string.IsNullOrWhiteSpace(evidence.ReverseStateHash)) {
            reason = "release pair requires a verified qualification runner receipt for both directions before deployment";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private Task<WorldReleaseGroupOutcome> AdvancePhaseAsync(WorldReleaseGroupSnapshot current, WorldReleaseOperationPhase phase, CancellationToken cancellationToken) =>
        m_groups.AdvanceAsync(current, current.Record with { PendingPhase = phase, Revision = checked(current.Record.Revision + 1) }, cancellationToken);

    private async Task<WorldReleaseRunResult> RecoverPreCommitAsync(WorldReleaseGroupSnapshot state, IWorldReleaseRuntime runtime, string failure, CancellationToken cancellationToken) {
        if (state.Record.RecoveryRoots.Count == 0) {
            return RefusedRun($"pre-commit failure before protected roots; retry drain without reopening an older checkpoint: {failure}");
        }
        if (state.Record.PendingPhase is not (WorldReleaseOperationPhase.Recover or WorldReleaseOperationPhase.RecoverActivate)) {
            var begun = await m_groups.BeginRecoveryAsync(state, failure, cancellationToken).ConfigureAwait(false);
            if (!begun.Ok) { return RefusedRun(begun.Detail); }
            state = begun.Snapshot!.Value;
        }
        if (state.Record.PendingPhase == WorldReleaseOperationPhase.Recover) {
            var stopped = await runtime.StopCandidateAsync(state.Record, cancellationToken).ConfigureAwait(false);
            if (!stopped.Succeeded) { return RefusedRun($"candidate stop remains pending during recovery: {stopped.Detail}"); }
            var restored = await runtime.RecoverSourceAsync(state.Record, cancellationToken).ConfigureAwait(false);
            if (!restored.Succeeded) { return RefusedRun($"source recovery remains pending after a failed private restore: {restored.Detail}"); }
            if (state.Record.PendingSourceRelease is null) {
                var completed = await m_groups.CompleteRecoveryAsync(state, cancellationToken).ConfigureAwait(false);
                return completed.Ok
                    ? new(false, false, "initial deployment failed; its private state was recovered and admission remains closed", completed.Snapshot) { SourceRecovered = true }
                    : RefusedRun(completed.Detail);
            }
            var prepared = await m_groups.RecordRecoveryRestoredAsync(state, cancellationToken).ConfigureAwait(false);
            if (!prepared.Ok) { return RefusedRun(prepared.Detail); }
            state = prepared.Snapshot!.Value;
        }
        var source = await runtime.StartRecoveredSourceAsync(state.Record, cancellationToken).ConfigureAwait(false);
        if (!source.Succeeded) { return RefusedRun($"restored source remains private; retry its activation: {source.Detail}"); }
        var publication = await runtime.PublishRecoveredSourceAsync(state.Record, cancellationToken).ConfigureAwait(false);
        return publication == WorldReleaseRuntimePublication.Opened
            ? new(false, false, $"target failed; source recovered: {failure}", await m_groups.LoadAsync(state.Record.DeploymentGroup, cancellationToken).ConfigureAwait(false)) { SourceRecovered = true }
            : RefusedRun("source recovered privately; recovery remains pending until its admission publication succeeds");
    }

    private static WorldReleaseRunResult RefusedRun(string reason) =>
        new(false, false, reason);

    private static WorldReleaseGroupOutcome Refused(string reason) =>
        new(WorldReleaseOperationOutcomeKind.Conflict, reason);
}
