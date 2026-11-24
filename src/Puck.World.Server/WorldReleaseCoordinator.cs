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
/// <summary>A bootstrap qualification that already ran, answered for the release it qualified: a deployment that must
/// qualify before it changes anything (a replace deployment qualifies before discarding the serving world) hands the
/// coordinator this instead of running the legs twice.</summary>
/// <param name="qualified">The release the qualification ran for.</param>
/// <param name="result">The qualification's result.</param>
public sealed class WorldReleaseBootstrapQualification(WorldReleaseManifest qualified, WorldReleaseQualificationResult result) : IWorldReleaseBootstrapQualificationRunner {
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException"><paramref name="target"/> is not the release this qualification ran
    /// for.</exception>
    public Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
        string.Equals(a: target.Identity, b: qualified.Identity, comparisonType: StringComparison.Ordinal)
            ? Task.FromResult(result: result)
            : throw new InvalidOperationException(message: $"the qualification ran for release {qualified.Identity}, not {target.Identity}");
}
/// <summary>Result returned by a release runtime driver, local or a hosting provider's.</summary>
public readonly record struct WorldReleaseRuntimeResult(
    bool Succeeded,
    string Detail,
    IReadOnlyDictionary<string, string>? RecoveryRoots = null);
/// <summary>Runtime publication result. Private means the candidate remains fenced and unadmitted.</summary>
public enum WorldReleaseRuntimePublication { Opened, Private, Refused }
/// <summary>The answer of one runtime admission publication and, when it did not open, why: the coordinator reports
/// <see cref="Detail"/> in its run result, so a refused or private publication names its cause.</summary>
public readonly record struct WorldReleaseRuntimePublicationResult(WorldReleaseRuntimePublication Publication, string Detail);
/// <summary>Worker lifecycle seam used by the durable coordinator.</summary>
public interface IWorldReleaseRuntime {
    Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    /// <summary>Ensures the exact target is running, preserving an already running target on retries.</summary>
    [Replaces(root: typeof(Puck.State.StateArena))]
    Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimePublicationResult> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    /// <summary>Restores protected roots without starting a source. This must be safe to repeat while Recover remains durable.</summary>
    Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    [Replaces(root: typeof(Puck.State.StateArena))]
    Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
    Task<WorldReleaseRuntimePublicationResult> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default);
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
    // The one door to pre-commit recovery: every outcome, recovered or still pending, names the failure that began it, so
    // a deploy log shows the cause however recovery ends. The core never formats the failure itself.
    private async Task<WorldReleaseRunResult> RecoverPreCommitAsync(WorldReleaseGroupSnapshot state, IWorldReleaseRuntime runtime, string failure, CancellationToken cancellationToken) {
        var result = await RecoverPreCommitCoreAsync(
            cancellationToken: cancellationToken,
            failure: failure,
            runtime: runtime,
            state: state
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (result with { Detail = $"{result.Detail}; cause: {failure}" });
    }
    private async Task<WorldReleaseRunResult> RecoverPreCommitCoreAsync(WorldReleaseGroupSnapshot state, IWorldReleaseRuntime runtime, string failure, CancellationToken cancellationToken) {
        if (state.Record.RecoveryRoots.Count == 0) {
            return RefusedRun(reason: "pre-commit failure before protected roots; retry drain without reopening an older checkpoint");
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

        return ((publication.Publication == WorldReleaseRuntimePublication.Opened)
            ? new(
                false,
                false,
                "target failed; source recovered",
                await m_groups.LoadAsync(
                    state.Record.DeploymentGroup,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false)
            ) { SourceRecovered = true }
            : RefusedRun(reason: $"source recovered privately; recovery remains pending until its admission publication succeeds: {publication.Detail}")
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
        if (await QualifyFreshAsync(
            cancellationToken: cancellationToken,
            qualificationRunner: qualificationRunner,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false) is { } refusal) {
            return Refused(reason: $"bootstrap {refusal}");
        }
        return await m_groups.BeginAsync(
            current,
            operationId,
            target.Identity,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Returns why a deployment from <paramref name="source"/> to <paramref name="target"/> cannot carry hosted
    /// state across its definitions, or <see langword="null"/> when it can. A world whose published definition changes
    /// anywhere but its metadata (<see cref="WorldReleaseMetadataTransition.ChangesOutsideMetadata"/>) has no admitted
    /// preservation rule, and hosted data is scratch until its owner says otherwise, so the deployment starts its target
    /// fresh (<see cref="BeginResetAsync"/>) instead of refusing. A metadata-only change carries state through the
    /// metadata transition. Worlds whose definition pins are equal are not read. A pair that is not structurally
    /// compatible (<see cref="WorldReleaseCompatibility.TryCheckStructuralCompatibility"/>: another world inventory,
    /// persistence or peer contract, or packaged pins) resets for the same reason.</summary>
    /// <param name="archive">The verified package reader holding both manifests' definition files.</param>
    /// <param name="source">The active release.</param>
    /// <param name="target">The release being deployed.</param>
    /// <param name="documentDirectory">The directory the definitions' relative paths resolve beside
    /// (<see cref="WorldSiloDefinition.DocumentDirectory"/>), or <see langword="null"/> for documents that author none.</param>
    /// <param name="cancellationToken">The token that cancels package reads.</param>
    /// <returns>The reset's reason, naming the first changed world, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidDataException">A changed world's package definition does not parse.</exception>
    public static async Task<string?> FindDefinitionResetAsync(WorldReleaseArchive archive, WorldReleaseManifest source, WorldReleaseManifest target,
        string? documentDirectory, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // A pair whose inventory, contracts or packaged pins differ carries no state by any rule, so it resets too.
        if (!WorldReleaseCompatibility.TryCheckStructuralCompatibility(candidate: target, previous: source, reason: out var structural)) {
            return $"the release cannot carry state across its pair: {structural}";
        }
        foreach (var key in source.DefinitionFiles.Keys.Order(comparer: StringComparer.Ordinal)) {
            if (
                !target.DefinitionFiles.TryGetValue(key: key, value: out var targetFile) ||
                string.Equals(a: source.Definitions[key], b: target.Definitions[key], comparisonType: StringComparison.Ordinal)
            ) { continue; }
            var before = await ParsePackageAsync(archive, source, source.DefinitionFiles[key], documentDirectory, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var after = await ParsePackageAsync(archive, target, targetFile, documentDirectory, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (WorldReleaseMetadataTransition.ChangesOutsideMetadata(after: after, before: before)) {
                return $"the release changes '{key}' outside its metadata, which no preservation rule carries state across";
            }
        }
        return null;
    }
    private static async Task<WorldDefinition> ParsePackageAsync(WorldReleaseArchive archive, WorldReleaseManifest manifest, string file, string? documentDirectory,
        CancellationToken cancellationToken) {
        var bytes = await archive.ReadFileAsync(manifest, file, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return (WorldDefinitionLoader.TryParseBeside(definition: out var definition, documentDirectory: documentDirectory, reason: out var reason, sourceName: file, utf8: bytes)
            ? definition
            : throw new InvalidDataException(message: reason));
    }
    /// <summary>Returns why the active source's captured state cannot be carried into this build, or
    /// <see langword="null"/> when it can: a world whose checkpoint this build cannot decode means the hosted state
    /// predates a format change, and hosted data is scratch until its owner says otherwise, so the deployment starts its
    /// target fresh (<see cref="BeginResetAsync"/>) instead of refusing. State that decodes is carried, and any later
    /// qualification failure still refuses. A deployment decides to reset in one place,
    /// <c>WorldReleaseFixtureBuilder.BuildAsync</c>, which asks <see cref="FindDefinitionResetAsync"/> first and this
    /// second.</summary>
    /// <param name="checkpoints">The source's captured checkpoint bytes, by world name.</param>
    /// <param name="documentDirectory">The directory the hosted documents' relative paths resolve beside under this build
    /// (<see cref="WorldSiloDefinition.DocumentDirectory"/>), exactly as the silo that would restore the state decodes
    /// it; <see langword="null"/> for documents that author none. A checkpoint embeds its definition with relative asset
    /// paths unresolved, so decoding without it refuses every world that has any.</param>
    /// <returns>The reset's reason, naming the first world that does not decode and why, or <see langword="null"/>.</returns>
    public static string? FindStateReset(IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> checkpoints, string? documentDirectory) {
        ArgumentNullException.ThrowIfNull(checkpoints);
        foreach (var row in checkpoints.OrderBy(
            keySelector: static row => row.Key,
            comparer: StringComparer.Ordinal
        )) {
            if (!WorldAuthorityCheckpointCodec.TryDecode(
                bytes: row.Value.Span,
                checkpoint: out _,
                documentDirectory: documentDirectory,
                reason: out var reason
            )) {
                return $"the source's '{row.Key}' state does not decode under this build: {reason}";
            }
        }
        return null;
    }
    /// <summary>Claims the group for a deployment that discards the active source's state because its definitions change
    /// outside metadata (<see cref="FindDefinitionResetAsync"/>) or its state does not decode
    /// (<see cref="FindStateReset"/>): the target qualifies alone from its own fresh state, as a first deployment does,
    /// and the claiming write records the reset in the operation's history. The source still drains and its protected
    /// roots still permit pre-commit recovery; Activate replaces every drained root with the target's fresh one.</summary>
    /// <param name="current">The group state, whose active release is <paramref name="source"/>.</param>
    /// <param name="source">The active release whose state is discarded.</param>
    /// <param name="target">The release that starts fresh.</param>
    /// <param name="qualificationRunner">The runner that qualifies <paramref name="target"/> from a fixture without
    /// gameplay.</param>
    /// <param name="operationId">The new operation's identifier.</param>
    /// <param name="reason">Why the source's state is discarded, as <see cref="FindDefinitionResetAsync"/> or <see cref="FindStateReset"/> named it.</param>
    /// <param name="cancellationToken">The token that cancels qualification and the guarded write.</param>
    /// <returns>The claimed group, or a conflict naming why it was not claimed.</returns>
    public async Task<WorldReleaseGroupOutcome> BeginResetAsync(
        WorldReleaseGroupSnapshot current,
        WorldReleaseManifest source,
        WorldReleaseManifest target,
        IWorldReleaseBootstrapQualificationRunner? qualificationRunner,
        Guid operationId,
        string reason,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!string.Equals(
            a: current.Record.ActiveRelease,
            b: source.Identity,
            comparisonType: StringComparison.Ordinal
        )) {
            return Refused(reason: "the durable active release does not match the reset's source manifest");
        }
        if (await QualifyFreshAsync(
            cancellationToken: cancellationToken,
            qualificationRunner: qualificationRunner,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false) is { } refusal) {
            return Refused(reason: $"reset {refusal}");
        }
        return await m_groups.BeginAsync(
            current,
            operationId,
            target.Identity,
            cancellationToken,
            reason
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    // Qualifies a target from its own fresh state, as a first deployment or a reset starts it; returns why it did not
    // qualify, or null.
    private static async Task<string?> QualifyFreshAsync(IWorldReleaseBootstrapQualificationRunner? qualificationRunner, WorldReleaseManifest target,
        CancellationToken cancellationToken) {
        if (qualificationRunner is null) { return "requires a qualification runner for the candidate release"; }
        var qualification = await qualificationRunner.RunAsync(
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (qualification.Failure is { } failure) { return $"qualification failed: {failure}"; }
        var receipt = qualification.Receipt;

        return (((receipt is null) ||
            (receipt.SourceRelease is not null) ||
            !string.Equals(
                a: receipt.TargetRelease,
                b: target.Identity,
                comparisonType: StringComparison.Ordinal
            ) ||
            !ValidQualificationHashes(evidence: receipt))
            ? "qualification did not produce a verified candidate receipt"
            : null);
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
    /// <summary>Returns what a deployment of <paramref name="release"/> does with the group's pending operation. A group
    /// with no unfinished operation begins the deployment's own (which accepts an admitted one), the deployment's own
    /// release resumes, and another release's unfinished operation is superseded first
    /// (<see cref="SupersedeAsync"/>), so a deployment always follows whatever its predecessor left behind.</summary>
    /// <param name="record">The durable group state, or <see langword="null"/> for a group not yet created.</param>
    /// <param name="release">The identity of the release being deployed.</param>
    /// <param name="operationId">The operation identifier the deployment requested, or <see langword="null"/> for a
    /// fresh one.</param>
    /// <returns>The deployment's next step.</returns>
    /// <exception cref="InvalidOperationException">The pending operation is <paramref name="release"/>'s own under
    /// another identifier than <paramref name="operationId"/>.</exception>
    public static WorldReleaseSuccession Succession(WorldReleaseGroupRecord? record, string release, Guid? operationId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        if (record?.PendingOperationId is not { } pending) { return WorldReleaseSuccession.Begin; }
        if (string.Equals(
            a: record.PendingTargetRelease,
            b: release,
            comparisonType: StringComparison.Ordinal
        )) {
            return (((operationId is { } requested) && (requested != pending))
                ? throw new InvalidOperationException(message: $"operation {pending:D} is already pending for release {release}; repeat the deployment with its identifier, or without one, to resume it")
                : WorldReleaseSuccession.Resume);
        }
        return (record.HasUnfinishedOperation
            ? WorldReleaseSuccession.Supersede
            : WorldReleaseSuccession.Begin);
    }
    /// <summary>Ends the group's pending operation of another release so <paramref name="successor"/> can begin, at the
    /// cause of each durable phase rather than by retracting any effect:
    /// <para>An admitted, committed operation is left as it is: the successor's claim accepts it
    /// (<see cref="WorldReleaseGroupStore.BeginAsync"/>). A committed operation that is not yet admitted is resumed
    /// through its target's admission, since its source's save is no longer a fallback; when that fails the run refuses
    /// with the target's cause and the operation stays pending. An operation still in Prepare, and a first deployment
    /// that captured no recovery root, are abandoned without a runtime effect
    /// (<see cref="WorldReleaseGroupStore.AbandonAsync"/>). Every other pre-commit operation is recovered as a failed
    /// start would be: a source still draining finishes its protected capture first, then the candidate stops, the
    /// source's protected roots are restored and the source is admitted again (a first deployment's compute is deleted
    /// instead).</para></summary>
    /// <param name="current">The group state holding the pending operation.</param>
    /// <param name="successor">The identity of the release that supersedes it.</param>
    /// <param name="pendingTarget">The manifest of the pending operation's own target release.</param>
    /// <param name="capturedRoots">The recovery roots the operation saved under its identifier without recording them,
    /// kept in the history of an abandoned operation.</param>
    /// <param name="runtime">The runtime driver for the pending operation's own source and target.</param>
    /// <param name="cancellationToken">The token that cancels storage and runtime work.</param>
    /// <returns>The run result; its snapshot holds no unfinished operation when the successor may begin, and its detail
    /// names why it may not otherwise.</returns>
    public async Task<WorldReleaseRunResult> SupersedeAsync(
        WorldReleaseGroupSnapshot current,
        string successor,
        WorldReleaseManifest pendingTarget,
        IReadOnlyDictionary<string, string> capturedRoots,
        IWorldReleaseRuntime runtime,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(successor);
        ArgumentNullException.ThrowIfNull(pendingTarget);
        ArgumentNullException.ThrowIfNull(capturedRoots);
        ArgumentNullException.ThrowIfNull(runtime);
        var record = current.Record;

        if (
            (record.PendingOperationId is not { } operation) ||
            !string.Equals(
                a: record.PendingTargetRelease,
                b: pendingTarget.Identity,
                comparisonType: StringComparison.Ordinal
            ) ||
            string.Equals(
                a: record.PendingTargetRelease,
                b: successor,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return RefusedRun(reason: "only a pending operation of another release can be superseded");
        }
        if (!record.HasUnfinishedOperation) {
            return new(
                false,
                false,
                $"operation {operation:D} is admitted; release {successor} accepts it when it claims the group",
                current
            );
        }
        if (record.PendingCommitted) {
            var resumed = await ResumeAsync(
                cancellationToken: cancellationToken,
                current: current,
                runtime: runtime,
                target: pendingTarget
            ).ConfigureAwait(continueOnCapturedContext: false);

            return ((resumed.Completed && (resumed.Snapshot is { Record.HasUnfinishedOperation: false }))
                ? new(
                    false,
                    false,
                    $"the committed release {pendingTarget.Identity} was admitted before release {successor} follows it",
                    resumed.Snapshot
                )
                : RefusedRun(reason: $"the committed release {pendingTarget.Identity} must be admitted before release {successor} can follow it, because its source's save is no longer a fallback: {resumed.Detail}"));
        }
        var reason = $"superseded by release {successor}";

        if (
            (record.RecoveryRoots.Count == 0) &&
            ((record.PendingPhase == WorldReleaseOperationPhase.Prepare) || (record.PendingSourceRelease is null))
        ) {
            var abandoned = await m_groups.AbandonAsync(
                cancellationToken: cancellationToken,
                capturedRoots: capturedRoots,
                current: current,
                reason: reason
            ).ConfigureAwait(continueOnCapturedContext: false);

            return (abandoned.Ok
                ? new(
                    false,
                    false,
                    $"operation {operation:D} had no effect to undo and was abandoned: {reason}",
                    abandoned.Snapshot
                )
                : RefusedRun(reason: abandoned.Detail)
            );
        }
        // A source in Drain may already be frozen or stopped, so only its complete protected capture lets recovery
        // restore and readmit it.
        var drained = await CaptureDrainAsync(
            cancellationToken: cancellationToken,
            runtime: runtime,
            state: current
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!drained.Ok) { return RefusedRun(reason: $"the source must finish its protected capture before release {successor} supersedes operation {operation:D}: {drained.Detail}"); }
        return await RecoverPreCommitAsync(
            cancellationToken: cancellationToken,
            failure: reason,
            runtime: runtime,
            state: drained.Snapshot!.Value
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    // Drains the operation's source and records the protected roots it captured. A state that already holds roots is
    // returned unchanged, so a retry neither contacts a stopped source nor recaptures newer state.
    private async Task<WorldReleaseGroupOutcome> CaptureDrainAsync(WorldReleaseGroupSnapshot state, IWorldReleaseRuntime runtime, CancellationToken cancellationToken) {
        if (state.Record.RecoveryRoots.Count != 0) {
            return new(
                WorldReleaseOperationOutcomeKind.Ok,
                string.Empty,
                state
            );
        }
        var drained = await runtime.DrainSourceAndCaptureAsync(
            state.Record,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            !drained.Succeeded ||
            (drained.RecoveryRoots is not { Count: > 0 })
        ) {
            return Refused(reason: $"source drain did not produce protected roots; retry drain: {drained.Detail}");
        }
        return await RecordCapturedRootsAsync(
            state,
            drained.RecoveryRoots,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
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
                            var captured = await CaptureDrainAsync(
                                cancellationToken: cancellationToken,
                                runtime: runtime,
                                state: state
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            if (!captured.Ok) { return RefusedRun(reason: captured.Detail); }
                            state = captured.Snapshot!.Value;
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

                            if (publication.Publication == WorldReleaseRuntimePublication.Private) {
                                return new(
                                CandidatePrivate: true,
                                Completed: false,
                                Detail: $"candidate remains private until the group publication barrier succeeds: {publication.Detail}",
                                Snapshot: state
                            );
                            }
                            if (publication.Publication != WorldReleaseRuntimePublication.Opened) { return RefusedRun(reason: $"committed target admission publication was refused: {publication.Detail}"); }
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
                return RefusedRun(reason: $"cannot establish the durable commit boundary; restore nothing and retry status: {readError.Message}; cause: {error.Message}");
            }
            if (
                (latest is not { } fresh) ||
                (fresh.Record.PendingOperationId != state.Record.PendingOperationId)
            ) {
                return RefusedRun(reason: $"the operation changed while its result was uncertain; inspect durable status before resuming; cause: {error.Message}");
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

        if (
            (pending.Record.PendingOperationId is null) ||
            (!pending.Record.HasUnfinishedOperation && (pending.Record.PendingOperationId != operationId))
        ) {
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
