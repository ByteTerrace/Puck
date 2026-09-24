using Puck.Assets;

namespace Puck.World.Server;

/// <summary>
/// Results produced by a trusted runner executing the exact packaged release pair.
/// Accepting operator-authored JSON is not a qualification runner.
/// </summary>
public sealed record WorldReleaseQualificationReceipt {
    public required string EvidenceId { get; init; }
    /// <summary>The target's import of its saved continuation after any reverse metadata transformation,
    /// compared with the source's import of that same prepared state.</summary>
    public required string ReverseReferenceStateHash { get; init; }
    public required string ReverseStateHash { get; init; }
    public string? SourceRelease { get; init; }
    public required string SourceStateHash { get; init; }
    public required string TargetRelease { get; init; }
    public required string TargetStateHash { get; init; }
}
/// <summary>
/// The outcome of one packaged qualification run: the receipt when every leg proved its claims, or the claim a leg
/// failed. A runner that cannot run at all (bad input, an unsupported pair, unavailable infrastructure) throws instead,
/// so a failure here always means the packaged pair was exercised and did not qualify.
/// </summary>
public sealed record WorldReleaseQualificationResult {
    private WorldReleaseQualificationResult(WorldReleaseQualificationReceipt? receipt, string? failure) {
        Failure = failure;
        Receipt = receipt;
    }

    /// <summary>Gets the claim a leg failed, or <see langword="null"/> when the pair qualified.</summary>
    public string? Failure { get; }
    /// <summary>Gets the qualified pair's receipt, or <see langword="null"/> when a leg failed.</summary>
    public WorldReleaseQualificationReceipt? Receipt { get; }

    /// <summary>Creates the result of a run in which a leg failed its claim.</summary>
    /// <param name="failure">The claim the leg failed, as one sentence.</param>
    /// <returns>A result carrying <paramref name="failure"/> and no receipt.</returns>
    /// <exception cref="ArgumentException"><paramref name="failure"/> is <see langword="null"/>, empty, or white space.</exception>
    public static WorldReleaseQualificationResult Failed(string failure) {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);

        return new(
            failure: failure,
            receipt: null
        );
    }
    /// <summary>Creates the result of a run in which every leg proved its claims.</summary>
    /// <param name="receipt">The qualified pair's receipt.</param>
    /// <returns>A result carrying <paramref name="receipt"/> and no failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    public static WorldReleaseQualificationResult Qualified(WorldReleaseQualificationReceipt receipt) {
        ArgumentNullException.ThrowIfNull(receipt);

        return new(
            failure: null,
            receipt: receipt
        );
    }
}
/// <summary>Runner that performs the packaged, ordered pair qualification outside the deployment transaction.</summary>
public interface IWorldReleaseQualificationRunner {
    Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default);
}
/// <summary>Runner that qualifies a candidate against an empty bootstrap source.</summary>
public interface IWorldReleaseBootstrapQualificationRunner {
    Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest target, CancellationToken cancellationToken = default);
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

    public WorldReleaseCoordinator(WorldReleaseGroupStore groups) => m_groups = (groups ?? throw new ArgumentNullException(paramName: nameof(groups)));

    private Task<WorldReleaseGroupOutcome> AdvancePhaseAsync(WorldReleaseGroupSnapshot current, WorldReleaseOperationPhase phase, CancellationToken cancellationToken) =>
        m_groups.AdvanceAsync(
            current,
            current.Record with { PendingPhase = phase, Revision = checked((current.Record.Revision + 1)) },
            cancellationToken
        );
    private static bool FullPin(string? value) => ContentPin.TryParse(
        pin: out _,
        text: value
    );
    private async Task<WorldReleaseRunResult> RecoverPreCommitAsync(WorldReleaseGroupSnapshot state, IWorldReleaseRuntime runtime, string failure, CancellationToken cancellationToken) {
        if (state.Record.RecoveryRoots.Count == 0) {
            return RefusedRun(reason: $"pre-commit failure before protected roots; retry drain without reopening an older checkpoint: {failure}");
        }
        if (state.Record.PendingPhase is not (WorldReleaseOperationPhase.Recover or WorldReleaseOperationPhase.RecoverActivate)) {
            var begun = await m_groups.BeginRecoveryAsync(
                cancellationToken: cancellationToken,
                current: state,
                failure: failure
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!begun.Ok) { return RefusedRun(reason: begun.Detail); }
            state = begun.Snapshot!.Value;
        }
        if (state.Record.PendingPhase == WorldReleaseOperationPhase.Recover) {
            var stopped = await runtime.StopCandidateAsync(
                state.Record,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!stopped.Succeeded) { return RefusedRun(reason: $"candidate stop remains pending during recovery: {stopped.Detail}"); }
            var restored = await runtime.RecoverSourceAsync(
                state.Record,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!restored.Succeeded) { return RefusedRun(reason: $"source recovery remains pending after a failed private restore: {restored.Detail}"); }
            if (state.Record.PendingSourceRelease is null) {
                var completed = await m_groups.CompleteRecoveryAsync(
                    state,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                return (completed.Ok
                    ? new(
                        false,
                        false,
                        "initial deployment failed; its private state was recovered and admission remains closed",
                        completed.Snapshot
                    ) { SourceRecovered = true }
                    : RefusedRun(reason: completed.Detail)
                );
            }
            var prepared = await m_groups.RecordRecoveryRestoredAsync(
                cancellationToken: cancellationToken,
                current: state
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!prepared.Ok) { return RefusedRun(reason: prepared.Detail); }
            state = prepared.Snapshot!.Value;
        }
        var source = await runtime.StartRecoveredSourceAsync(
            state.Record,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!source.Succeeded) { return RefusedRun(reason: $"restored source remains private; retry its activation: {source.Detail}"); }
        var publication = await runtime.PublishRecoveredSourceAsync(
            state.Record,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return ((publication == WorldReleaseRuntimePublication.Opened)
            ? new(
                false,
                false,
                $"target failed; source recovered: {failure}",
                await m_groups.LoadAsync(
                    state.Record.DeploymentGroup,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false)
            ) { SourceRecovered = true }
            : RefusedRun(reason: "source recovered privately; recovery remains pending until its admission publication succeeds")
        );
    }
    private static WorldReleaseGroupOutcome Refused(string reason) =>
        new(
            WorldReleaseOperationOutcomeKind.Conflict,
            reason
        );
    private static WorldReleaseRunResult RefusedRun(string reason) =>
        new(
            false,
            false,
            reason
        );
    private static bool ValidQualificationHashes(WorldReleaseQualificationReceipt evidence) =>
        (FullPin(value: evidence.EvidenceId) && FullPin(value: evidence.SourceStateHash) && FullPin(value: evidence.ReverseStateHash) &&
        string.Equals(
            a: evidence.SourceStateHash,
            b: evidence.TargetStateHash,
            comparisonType: StringComparison.Ordinal
        ) &&
        string.Equals(
            a: evidence.ReverseStateHash,
            b: evidence.ReverseReferenceStateHash,
            comparisonType: StringComparison.Ordinal
        ));

    /// <summary>Starts a private first deployment without inventing a source-release identity.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginBootstrapAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest target,
        IWorldReleaseBootstrapQualificationRunner? qualificationRunner,
        Guid operationId,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        if (
            (current.Record.ActiveRelease is not null) ||
            (current.Record.Admission != WorldReleaseAdmissionState.Closed)
        ) {
            return Refused(reason: "bootstrap requires a closed group with no active release");
        }
        if (qualificationRunner is null) {
            return Refused(reason: "bootstrap requires a qualification runner for the candidate release");
        }
        var qualification = await qualificationRunner.RunAsync(
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (qualification.Failure is { } failure) {
            return Refused(reason: $"bootstrap qualification failed: {failure}");
        }
        var receipt = qualification.Receipt;

        if (
            (receipt is null) ||
            (receipt.SourceRelease is not null) ||
            !string.Equals(
            a: receipt.TargetRelease,
            b: target.Identity,
            comparisonType: StringComparison.Ordinal
        ) ||
            !ValidQualificationHashes(evidence: receipt)
        ) {
            return Refused(reason: "bootstrap qualification did not produce a verified candidate receipt");
        }
        return await m_groups.BeginAsync(
            current,
            operationId,
            target.Identity,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
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
        if (!string.Equals(
            a: current.Record.ActiveRelease,
            b: source.Identity,
            comparisonType: StringComparison.Ordinal
        )) {
            return Refused(reason: "the durable active release does not match the deployment source manifest");
        }
        var qualification = ((qualificationRunner is null)
            ? null
            : await qualificationRunner.RunAsync(
                cancellationToken: cancellationToken,
                source: source,
                target: target
            ).ConfigureAwait(continueOnCapturedContext: false)
        );

        if (qualification?.Failure is { } failure) {
            return Refused(reason: $"release pair qualification failed: {failure}");
        }
        if (!TryQualifyPair(
            evidence: qualification?.Receipt,
            reason: out var reason,
            source: source,
            target: target
        )) {
            return Refused(reason: reason);
        }
        return await m_groups.BeginAsync(
            current,
            operationId,
            target.Identity,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
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
        if (
            !string.Equals(
            a: current.Record.ActiveRelease,
            b: active.Identity,
            comparisonType: StringComparison.Ordinal
        ) ||
            !string.Equals(
            a: current.Record.PreviousRelease,
            b: predecessor.Identity,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return Refused(reason: "the durable active/previous release pointers do not match the rollback manifests");
        }
        var qualification = ((qualificationRunner is null)
            ? null
            : await qualificationRunner.RunAsync(
                cancellationToken: cancellationToken,
                source: active,
                target: predecessor
            ).ConfigureAwait(continueOnCapturedContext: false)
        );

        if (qualification?.Failure is { } failure) {
            return Refused(reason: $"rollback pair qualification failed: {failure}");
        }
        if (!TryQualifyPair(
            evidence: qualification?.Receipt,
            reason: out var reason,
            source: active,
            target: predecessor
        )) {
            return Refused(reason: reason);
        }
        return await m_groups.BeginRollbackAsync(
            cancellationToken: cancellationToken,
            current: current,
            operationId: operationId
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Publishes target pointers at the durable commit boundary using a fresh per-world fence census.</summary>
    public Task<WorldReleaseGroupOutcome> CommitAsync(
        WorldReleaseGroupSnapshot current,
        IEnumerable<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> fences,
        CancellationToken cancellationToken = default) =>
        m_groups.CommitAsync(
            cancellationToken: cancellationToken,
            current: current,
            fences: fences
        );
    /// <summary>Completes an admitted operation while retaining its immutable recovery references in history.</summary>
    public Task<WorldReleaseGroupOutcome> FinalizeAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        m_groups.FinalizeAsync(
            cancellationToken: cancellationToken,
            current: current
        );
    /// <summary>Opens admission after the hosting adapter has verified the committed target.</summary>
    public Task<WorldReleaseGroupOutcome> PublishAdmissionAsync(
        WorldReleaseGroupSnapshot current,
        string release,
        Guid operationId,
        Guid authorityLease,
        CancellationToken cancellationToken = default) =>
        m_groups.OpenAdmissionAsync(
            authorityLease: authorityLease,
            cancellationToken: cancellationToken,
            current: current,
            expectedRelease: release,
            operationId: operationId
        );
    /// <summary>Records private candidate activation after all source roots are protected.</summary>
    public Task<WorldReleaseGroupOutcome> RecordActivationAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        AdvancePhaseAsync(
            cancellationToken: cancellationToken,
            current: current,
            phase: WorldReleaseOperationPhase.Activate
        );
    /// <summary>Attaches roots captured by a successful drain to its already durable drain phase.</summary>
    public Task<WorldReleaseGroupOutcome> RecordCapturedRootsAsync(
        WorldReleaseGroupSnapshot current,
        IReadOnlyDictionary<string, string> recoveryRoots,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(recoveryRoots);
        if (
            (recoveryRoots.Count == 0) ||
            recoveryRoots.Any(predicate: static item => (string.IsNullOrWhiteSpace(value: item.Key) || string.IsNullOrWhiteSpace(value: item.Value)))
        ) {
            return Task.FromResult(result: Refused(reason: "a successful drain must provide a non-empty protected root for every managed row"));
        }
        return m_groups.AdvanceAsync(
            current,
            current.Record with {
                PendingPhase = current.Record.PendingPhase,
                RecoveryRoots = new SortedDictionary<string, string>(
                recoveryRoots.ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal
                ),
                StringComparer.Ordinal
            ),
                Revision = checked((current.Record.Revision + 1)),
            },
            cancellationToken
        );
    }
    /// <summary>Records the maintenance boundary and the protected source roots captured by a successful drain.</summary>
    public Task<WorldReleaseGroupOutcome> RecordDrainAsync(
        WorldReleaseGroupSnapshot current,
        IReadOnlyDictionary<string, string> recoveryRoots,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(recoveryRoots);
        if (
            (recoveryRoots.Count == 0) ||
            recoveryRoots.Any(predicate: static item => (string.IsNullOrWhiteSpace(value: item.Key) || string.IsNullOrWhiteSpace(value: item.Value)))
        ) {
            return Task.FromResult(result: Refused(reason: "a successful drain must provide a non-empty protected root for every managed row"));
        }
        return m_groups.AdvanceAsync(
            current,
            current.Record with {
                PendingPhase = WorldReleaseOperationPhase.Drain,
                Admission = WorldReleaseAdmissionState.Closed,
                RecoveryRoots = new SortedDictionary<string, string>(
                recoveryRoots.ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal
                ),
                StringComparer.Ordinal
            ),
                Revision = checked((current.Record.Revision + 1)),
            },
            cancellationToken
        );
    }
    /// <summary>Closes admission before a drain begins; a failed drain remains retryable in this phase.</summary>
    public Task<WorldReleaseGroupOutcome> RecordDrainPhaseAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        m_groups.AdvanceAsync(
            current,
            current.Record with {
                PendingPhase = WorldReleaseOperationPhase.Drain,
                Admission = WorldReleaseAdmissionState.Closed,
                Revision = checked((current.Record.Revision + 1)),
            },
            cancellationToken
        );
    /// <summary>Records private verification without opening public admission.</summary>
    public Task<WorldReleaseGroupOutcome> RecordVerificationAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) =>
        AdvancePhaseAsync(
            cancellationToken: cancellationToken,
            current: current,
            phase: WorldReleaseOperationPhase.Verify
        );
    /// <summary>Resumes the exact durable operation without accepting a different source or target.</summary>
    public async Task<WorldReleaseRunResult> ResumeAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest target,
        IWorldReleaseRuntime runtime,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(runtime);
        var durable = await m_groups.LoadAsync(
            current.Record.DeploymentGroup,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (durable is not { } state) { return RefusedRun(reason: "the deployment-group state disappeared before resume"); }
        if (
            (current.Record.PendingOperationId is null) ||
            (current.Record.PendingOperationId != state.Record.PendingOperationId) ||
            !string.Equals(
            a: current.Record.PendingSourceRelease,
            b: state.Record.PendingSourceRelease,
            comparisonType: StringComparison.Ordinal
        ) ||
            !string.Equals(
            a: state.Record.PendingTargetRelease,
            b: target.Identity,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return RefusedRun(reason: "the requested target does not match the durable pending operation");
        }
        try {
            while (state.Record.PendingOperationId is { } operationId) {
                switch (state.Record.PendingPhase) {
                    case WorldReleaseOperationPhase.Prepare: {
                            var advanced = await RecordDrainPhaseAsync(
                                cancellationToken: cancellationToken,
                                current: state
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!advanced.Ok) { return RefusedRun(reason: advanced.Detail); }
                            state = advanced.Snapshot!.Value;
                            continue;
                        }
                    case WorldReleaseOperationPhase.Drain: {
                            if (state.Record.RecoveryRoots.Count == 0) {
                                var drained = await runtime.DrainSourceAndCaptureAsync(
                                    state.Record,
                                    cancellationToken
                                ).ConfigureAwait(continueOnCapturedContext: false);

                                if (
                                    !drained.Succeeded ||
                                    (drained.RecoveryRoots is not { Count: > 0 })
                                ) {
                                    return RefusedRun(reason: $"source drain did not produce protected roots; retry drain: {drained.Detail}");
                                }
                                var captured = await RecordCapturedRootsAsync(
                                    state,
                                    drained.RecoveryRoots,
                                    cancellationToken
                                ).ConfigureAwait(continueOnCapturedContext: false);

                                if (!captured.Ok) { return RefusedRun(reason: captured.Detail); }
                                state = captured.Snapshot!.Value;
                            }
                            var advanced = await RecordActivationAsync(
                                cancellationToken: cancellationToken,
                                current: state
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!advanced.Ok) { return RefusedRun(reason: advanced.Detail); }
                            state = advanced.Snapshot!.Value;
                            continue;
                        }
                    case WorldReleaseOperationPhase.Activate: {
                            var started = await runtime.StartCandidatePrivatelyAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!started.Succeeded) {
                                return await RecoverPreCommitAsync(
                                state,
                                runtime,
                                started.Detail,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);
                            }
                            var advanced = await RecordVerificationAsync(
                                cancellationToken: cancellationToken,
                                current: state
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!advanced.Ok) { return RefusedRun(reason: advanced.Detail); }
                            state = advanced.Snapshot!.Value;
                            continue;
                        }
                    case WorldReleaseOperationPhase.Verify: {
                            var started = await runtime.StartCandidatePrivatelyAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!started.Succeeded) {
                                return await RecoverPreCommitAsync(
                                state,
                                runtime,
                                started.Detail,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);
                            }
                            var verified = await runtime.VerifyCandidatePrivatelyAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!verified.Succeeded) {
                                return await RecoverPreCommitAsync(
                                state,
                                runtime,
                                verified.Detail,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);
                            }
                            var fences = await runtime.ReadFenceCensusAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);
                            var committed = await CommitAsync(
                                cancellationToken: cancellationToken,
                                current: state,
                                fences: fences
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!committed.Ok) { return RefusedRun(reason: committed.Detail); }
                            state = committed.Snapshot!.Value;
                            continue;
                        }
                    case WorldReleaseOperationPhase.Commit: {
                            var started = await runtime.StartCandidatePrivatelyAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!started.Succeeded) { return RefusedRun(reason: $"committed target restart failed; resume the target: {started.Detail}"); }
                            var verified = await runtime.VerifyCandidatePrivatelyAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!verified.Succeeded) { return RefusedRun(reason: $"committed target verification failed: {verified.Detail}"); }
                            var publication = await runtime.PublishCandidateAsync(
                                state.Record,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (publication == WorldReleaseRuntimePublication.Private) {
                                return new(
                                CandidatePrivate: true,
                                Completed: false,
                                Detail: "candidate remains private until the group publication barrier succeeds",
                                Snapshot: state
                            );
                            }
                            if (publication != WorldReleaseRuntimePublication.Opened) { return RefusedRun(reason: "committed target admission publication was refused"); }
                            return new(
                                true,
                                false,
                                "release committed and admitted",
                                await m_groups.LoadAsync(
                                    state.Record.DeploymentGroup,
                                    cancellationToken
                                ).ConfigureAwait(continueOnCapturedContext: false)
                            );
                        }
                    case WorldReleaseOperationPhase.Recover:
                    case WorldReleaseOperationPhase.RecoverActivate:
                        return await RecoverPreCommitAsync(
                            state,
                            runtime,
                            state.Record.PendingFailure!,
                            cancellationToken
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    default:
                        return RefusedRun(reason: "the durable operation is in an unsupported terminal phase");
                }
            }
            return new(
                CandidatePrivate: false,
                Completed: true,
                Detail: "no pending release operation",
                Snapshot: state
            );
        } catch (Exception error) when ((error is not OperationCanceledException)) {
            WorldReleaseGroupSnapshot? latest;

            try {
                latest = await m_groups.LoadAsync(
                state.Record.DeploymentGroup,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception readError) when ((readError is not OperationCanceledException)) {
                return RefusedRun(reason: $"cannot establish the durable commit boundary; restore nothing and retry status: {readError.Message}");
            }
            if (
                (latest is not { } fresh) ||
                (fresh.Record.PendingOperationId != state.Record.PendingOperationId)
            ) {
                return RefusedRun(reason: "the operation changed while its result was uncertain; inspect durable status before resuming");
            }
            state = fresh;
            if (
                state.Record.PendingCommitted ||
                (state.Record.PendingPhase is WorldReleaseOperationPhase.Commit or WorldReleaseOperationPhase.Finalized)
            ) {
                return RefusedRun(reason: $"post-commit release requires target resume: {error.Message}");
            }
            return await RecoverPreCommitAsync(
                state,
                runtime,
                error.Message,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
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
        var durable = await m_groups.LoadAsync(
            current.Record.DeploymentGroup,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (durable is not { } currentState) { return RefusedRun(reason: "the deployment-group state disappeared before release start"); }
        var pending = currentState;

        if (pending.Record.PendingOperationId is null) {
            var begun = await BeginDeploymentAsync(
                cancellationToken: cancellationToken,
                current: pending,
                operationId: operationId,
                qualificationRunner: qualificationRunner,
                source: source,
                target: target
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!begun.Ok) { return RefusedRun(reason: begun.Detail); }
            pending = begun.Snapshot!.Value;
        } else if (
            (pending.Record.PendingOperationId != operationId) ||
            !string.Equals(
            a: pending.Record.PendingSourceRelease,
            b: source.Identity,
            comparisonType: StringComparison.Ordinal
        ) ||
            !string.Equals(
            a: pending.Record.PendingTargetRelease,
            b: target.Identity,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return RefusedRun(reason: "the requested operation or release pair does not match the durable pending operation");
        }
        return await ResumeAsync(
            cancellationToken: cancellationToken,
            current: pending,
            runtime: runtime,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Checks externally supplied qualification evidence for the exact ordered pair.</summary>
    public static bool TryQualifyPair(
        WorldReleaseManifest source,
        WorldReleaseManifest target,
        WorldReleaseQualificationReceipt? evidence,
        out string reason) {
        if (!WorldReleaseTransitionPolicy.TryPrepare(
            changes: out _,
            reason: out reason,
            source: source,
            target: target
        )) {
            return false;
        }
        if (
            (evidence is null) ||
            !string.Equals(
            a: evidence.SourceRelease,
            b: source.Identity,
            comparisonType: StringComparison.Ordinal
        ) ||
            !string.Equals(
            a: evidence.TargetRelease,
            b: target.Identity,
            comparisonType: StringComparison.Ordinal
        ) ||
            !ValidQualificationHashes(evidence: evidence)
        ) {
            reason = "release pair requires a verified qualification runner receipt for both directions before deployment";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
