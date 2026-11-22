using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for a deployment that follows whatever operation its predecessor left pending.</summary>
public sealed partial class WorldReleaseCoordinatorSafetyLawTests {
    /// <summary>The durable state a predecessor's operation was left in.</summary>
    public enum Left {
        /// <summary>Begun; the source has not drained.</summary>
        Prepare,
        /// <summary>Admission closed for the drain; no protected root recorded.</summary>
        Drain,
        /// <summary>The drain's protected roots recorded.</summary>
        Drained,
        /// <summary>The candidate's private activation recorded.</summary>
        Activate,
        /// <summary>The candidate's private verification recorded.</summary>
        Verify,
        /// <summary>Pre-commit recovery begun.</summary>
        Recover,
        /// <summary>Pre-commit recovery restored the source's roots.</summary>
        RecoverActivate,
        /// <summary>Committed; admission not yet published.</summary>
        Committed,
        /// <summary>Committed and admitted, retained for its rollback window.</summary>
        Admitted,
    }

    // A deployment of release B follows release A's operation from every durable phase it can be left in, with no
    // hand-run step: an admitted operation is accepted, a committed one is admitted first, one in Prepare or a bootstrap
    // that captured nothing is abandoned, and every other pre-commit one recovers its source. B ends committed, admitted
    // and running, and A's operation reaches history.
    [Theory]
    [InlineData(Left.Prepare, false)]
    [InlineData(Left.Drain, false)]
    [InlineData(Left.Drained, false)]
    [InlineData(Left.Activate, false)]
    [InlineData(Left.Verify, false)]
    [InlineData(Left.Recover, false)]
    [InlineData(Left.RecoverActivate, false)]
    [InlineData(Left.Committed, false)]
    [InlineData(Left.Admitted, false)]
    [InlineData(Left.Prepare, true)]
    [InlineData(Left.Drain, true)]
    [InlineData(Left.Drained, true)]
    [InlineData(Left.Activate, true)]
    [InlineData(Left.Verify, true)]
    [InlineData(Left.Recover, true)]
    [InlineData(Left.Committed, true)]
    [InlineData(Left.Admitted, true)]
    public async Task ADeploymentFollowsWhateverItsPredecessorLeftPending(Left left, bool bootstrap) {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var source = Manifest(image: 'a');
        var predecessor = Manifest(image: 'b');
        var successor = Manifest(image: 'c');
        var host = new HostRuntime(
            groups: groups,
            owner: owner
        ) { Running = (bootstrap ? null : source.Identity) };
        var pending = await LeaveAsync(
            group: Required(result: await groups.CreateAsync(
                "primary",
                (bootstrap ? null : source.Identity),
                Token
            )),
            groups: groups,
            left: left,
            owner: owner,
            target: predecessor
        );
        var operation = pending.Record.PendingOperationId!.Value;

        Assert.Equal(
            ((left == Left.Admitted) ? WorldReleaseSuccession.Begin : WorldReleaseSuccession.Supersede),
            WorldReleaseCoordinator.Succession(
                operationId: null,
                record: pending.Record,
                release: successor.Identity
            )
        );
        var result = await DeployAsync(
            groups: groups,
            host: host,
            manifests: [source, predecessor, successor],
            release: successor
        );

        Assert.True(
            condition: result.Completed,
            userMessage: result.Detail
        );
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;
        var committed = (left is Left.Committed or Left.Admitted);

        Assert.Equal(
            successor.Identity,
            durable.ActiveRelease
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Open,
            durable.Admission
        );
        Assert.False(condition: durable.HasUnfinishedOperation);
        Assert.Equal(
            (committed ? predecessor.Identity : (bootstrap ? null : source.Identity)),
            durable.PreviousRelease
        );
        Assert.Equal(
            successor.Identity,
            host.Running
        );
        var entry = Assert.Single(
            durable.History,
            row => (row.OperationId == operation)
        );

        Assert.StartsWith(
            actualString: entry.Result,
            expectedStartString: (committed
                ? WorldReleaseGroupHistoryEntry.CommittedResult
                : (((left == Left.Prepare) || (bootstrap && (left == Left.Drain)))
                    ? WorldReleaseGroupHistoryEntry.AbandonedResult
                    : WorldReleaseGroupHistoryEntry.RecoveredResult))
        );
        // Recovered and accepted operations keep their protected roots; abandoned ones captured none.
        Assert.Equal(
            (((left >= Left.Drained) || (!bootstrap && (left == Left.Drain))) ? 1 : 0),
            entry.RecoveryRoots.Count
        );
    }

    // A committed predecessor whose admission cannot be published truly needs its own cause fixed: its source's save is no
    // longer a fallback, so the successor refuses with the predecessor's failure, takes no step of its own, and leaves the
    // operation pending for a later deployment to admit.
    [Fact]
    public async Task ACommittedPredecessorThatCannotBeAdmittedRefusesItsSuccessorByName() {
        const string Cause = "private health reports a checkpoint retention failure";
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var source = Manifest(image: 'a');
        var predecessor = Manifest(image: 'b');
        var successor = Manifest(image: 'c');
        var pending = await LeaveAsync(
            group: Required(result: await groups.CreateAsync(
                "primary",
                source.Identity,
                Token
            )),
            groups: groups,
            left: Left.Committed,
            owner: owner,
            target: predecessor
        );
        var host = new HostRuntime(
            groups: groups,
            owner: owner
        ) { VerifyFailure = Cause };
        var result = await DeployAsync(
            groups: groups,
            host: host,
            manifests: [source, predecessor, successor],
            release: successor
        );

        Assert.False(condition: result.Completed);
        Assert.Contains(
            actualString: result.Detail,
            expectedSubstring: predecessor.Identity
        );
        Assert.Contains(
            actualString: result.Detail,
            expectedSubstring: Cause
        );
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;

        Assert.Equal(
            pending.Record.PendingOperationId,
            durable.PendingOperationId
        );
        Assert.Equal(
            predecessor.Identity,
            durable.ActiveRelease
        );
        Assert.Equal(
            0,
            host.Drains
        );
    }

    // A deployment of the pending operation's own release resumes it, as a hand-run resume would, and a different
    // requested identifier for that release refuses before any effect.
    [Fact]
    public async Task ADeploymentOfThePendingReleaseResumesIt() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var source = Manifest(image: 'a');
        var target = Manifest(image: 'b');
        var pending = await LeaveAsync(
            group: Required(result: await groups.CreateAsync(
                "primary",
                source.Identity,
                Token
            )),
            groups: groups,
            left: Left.Verify,
            owner: owner,
            target: target
        );

        Assert.Equal(
            WorldReleaseSuccession.Resume,
            WorldReleaseCoordinator.Succession(
                operationId: pending.Record.PendingOperationId,
                record: pending.Record,
                release: target.Identity
            )
        );
        Assert.Throws<InvalidOperationException>(testCode: () => WorldReleaseCoordinator.Succession(
            operationId: Guid.NewGuid(),
            record: pending.Record,
            release: target.Identity
        ));
        var host = new HostRuntime(
            groups: groups,
            owner: owner
        );
        var result = await DeployAsync(
            groups: groups,
            host: host,
            manifests: [source, target],
            release: target
        );

        Assert.True(
            condition: result.Completed,
            userMessage: result.Detail
        );
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;

        Assert.Equal(
            pending.Record.PendingOperationId,
            durable.PendingOperationId
        );
        Assert.Equal(
            target.Identity,
            durable.ActiveRelease
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Open,
            durable.Admission
        );
    }

    // A source whose exported state this build cannot decode (a checkpoint written before a schema rename) is scratch: the
    // deployment records the reset and its reason in the operation's history, qualifies and starts the target fresh, and
    // commits. State that decodes is carried and never resets.
    [Fact]
    public async Task ADeploymentOverUnreadableSourceStateResetsAndCommits() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var source = Manifest(image: 'a');
        var target = Manifest(image: 'b');

        Required(result: await groups.CreateAsync(
            "primary",
            source.Identity,
            Token
        ));
        var host = new HostRuntime(
            groups: groups,
            owner: owner
        ) { Running = source.Identity };
        var result = await DeployAsync(
            exported: UnreadableExport(),
            groups: groups,
            host: host,
            manifests: [source, target],
            release: target
        );

        Assert.True(
            condition: result.Completed,
            userMessage: result.Detail
        );
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;

        Assert.Equal(
            target.Identity,
            durable.ActiveRelease
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Open,
            durable.Admission
        );
        Assert.Equal(
            target.Identity,
            host.Running
        );
        Assert.NotNull(@object: host.ActivatedReset);
        Assert.Contains(
            actualString: host.ActivatedReset,
            expectedSubstring: "'row' state does not decode"
        );
        Assert.Equal(
            host.ActivatedReset,
            durable.PendingStateReset
        );
        var entry = Assert.Single(collection: durable.History);

        Assert.Equal(
            durable.PendingOperationId,
            entry.OperationId
        );
        Assert.Equal(
            (WorldReleaseGroupHistoryEntry.ResetResult + host.ActivatedReset),
            entry.Result
        );
        Assert.Null(@object: WorldReleaseCoordinator.FindStateReset(checkpoints: ReadableExport()));
    }
    // A reset operation that fails before its commit recovers the source exactly as any other: the reset entry stays in
    // history beside the recovery, and the recovered group no longer reports a pending reset.
    [Fact]
    public async Task AResetThatFailsBeforeCommitRecoversTheSource() {
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var source = Manifest(image: 'a');
        var target = Manifest(image: 'b');

        Required(result: await groups.CreateAsync(
            "primary",
            source.Identity,
            Token
        ));
        var host = new HostRuntime(
            groups: groups,
            owner: owner
        ) { Running = source.Identity, VerifyFailure = "the candidate failed private health" };
        var result = await DeployAsync(
            exported: UnreadableExport(),
            groups: groups,
            host: host,
            manifests: [source, target],
            release: target
        );

        Assert.False(condition: result.Completed);
        Assert.True(condition: result.SourceRecovered);
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;

        Assert.Equal(
            source.Identity,
            durable.ActiveRelease
        );
        Assert.Null(@object: durable.PendingStateReset);
        Assert.Equal(
            2,
            durable.History.Count
        );
        Assert.StartsWith(
            actualString: durable.History[0].Result,
            expectedStartString: WorldReleaseGroupHistoryEntry.ResetResult
        );
        Assert.StartsWith(
            actualString: durable.History[1].Result,
            expectedStartString: WorldReleaseGroupHistoryEntry.RecoveredResult
        );
        Assert.Equal(
            source.Identity,
            host.Running
        );
    }

    // One world's export in a shape this build does not decode, standing for a checkpoint an earlier build wrote.
    private static Dictionary<string, ReadOnlyMemory<byte>> UnreadableExport() => new() { ["row"] = "{\"relightPerSecond\":4}"u8.ToArray() };
    // One world's export as this build writes it.
    private static Dictionary<string, ReadOnlyMemory<byte>> ReadableExport() {
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument());

        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var checkpoint,
                out var reason
            ),
            userMessage: reason
        );
        return new() { ["row"] = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!) };
    }
    // A deployment as the release host runs one: the shared succession decision, then the coordinator's own doors. With
    // an export, the source's state is decided exactly where the host decides it (WorldReleaseCoordinator.FindStateReset).
    private static async Task<WorldReleaseRunResult> DeployAsync(WorldReleaseGroupStore groups, HostRuntime host,
        WorldReleaseManifest[] manifests, WorldReleaseManifest release, IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? exported = null) {
        var coordinator = new WorldReleaseCoordinator(groups: groups);
        var state = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;
        WorldReleaseManifest Find(string identity) => manifests.Single(predicate: manifest => (manifest.Identity == identity));

        switch (WorldReleaseCoordinator.Succession(
            operationId: null,
            record: state.Record,
            release: release.Identity
        )) {
            case WorldReleaseSuccession.Resume:
                return await coordinator.ResumeAsync(
                    state,
                    release,
                    host,
                    Token
                );
            case WorldReleaseSuccession.Supersede:
                var superseded = await coordinator.SupersedeAsync(
                    state,
                    release.Identity,
                    Find(identity: state.Record.PendingTargetRelease!),
                    new Dictionary<string, string>(),
                    host,
                    Token
                );

                if (superseded.Snapshot is not { Record.HasUnfinishedOperation: false } ended) { return superseded; }
                state = ended;
                break;
        }
        var reset = ((exported is null) ? null : WorldReleaseCoordinator.FindStateReset(checkpoints: exported));
        var begun = ((state.Record.ActiveRelease is { } active)
            ? ((reset is null)
                ? await coordinator.BeginDeploymentAsync(
                    state,
                    Find(identity: active),
                    release,
                    new QualifyingRunner(),
                    Guid.NewGuid(),
                    Token
                )
                : await coordinator.BeginResetAsync(
                    state,
                    Find(identity: active),
                    release,
                    new QualifyingRunner(),
                    Guid.NewGuid(),
                    reset,
                    Token
                ))
            : await coordinator.BeginBootstrapAsync(
                state,
                release,
                new QualifyingRunner(),
                Guid.NewGuid(),
                Token
            ));

        return await coordinator.ResumeAsync(
            Required(result: begun),
            release,
            host,
            Token
        );
    }
    // Begins an operation toward target and advances it through the coordinator's and store's own doors to left.
    private static async Task<WorldReleaseGroupSnapshot> LeaveAsync(WorldReleaseGroupStore groups, WorldReleaseGroupSnapshot group,
        Guid owner, WorldReleaseManifest target, Left left) {
        var coordinator = new WorldReleaseCoordinator(groups: groups);

        group = Required(result: await groups.BeginAsync(
            group,
            Guid.NewGuid(),
            target.Identity,
            Token
        ));
        if (left == Left.Prepare) { return group; }
        group = Required(result: await coordinator.RecordDrainPhaseAsync(
            group,
            Token
        ));
        if (left == Left.Drain) { return group; }
        group = Required(result: await coordinator.RecordCapturedRootsAsync(
            group,
            HostRuntime.Roots(owner: owner),
            Token
        ));
        if (left == Left.Drained) { return group; }
        group = Required(result: await coordinator.RecordActivationAsync(
            group,
            Token
        ));
        if (left == Left.Activate) { return group; }
        group = Required(result: await coordinator.RecordVerificationAsync(
            group,
            Token
        ));
        if (left == Left.Verify) { return group; }
        if (left is Left.Recover or Left.RecoverActivate) {
            group = Required(result: await groups.BeginRecoveryAsync(
                group,
                "the candidate failed private health",
                Token
            ));
            return ((left == Left.Recover)
                ? group
                : Required(result: await groups.RecordRecoveryRestoredAsync(
                    group,
                    Token
                )));
        }
        group = Required(result: await coordinator.CommitAsync(
            group,
            HostRuntime.Fences(owner: owner),
            Token
        ));
        if (left == Left.Committed) { return group; }
        return Required(result: await groups.OpenAdmissionAsync(
            group,
            target.Identity,
            group.Record.PendingOperationId!.Value,
            Guid.NewGuid(),
            Token
        ));
    }

    // Qualifies every pair and every bootstrap with matching state hashes.
    private sealed class QualifyingRunner : IWorldReleaseQualificationRunner, IWorldReleaseBootstrapQualificationRunner {
        private static readonly string Hash = ("sha256/" + new string(
            c: 'e',
            count: 64
        ));

        private static WorldReleaseQualificationResult Qualified(string? source, string target) => WorldReleaseQualificationResult.Qualified(receipt: new() {
            EvidenceId = Hash,
            ReverseReferenceStateHash = Hash,
            ReverseStateHash = Hash,
            SourceRelease = source,
            SourceStateHash = Hash,
            TargetRelease = target,
            TargetStateHash = Hash,
        });

        public Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: Qualified(
                source: source.Identity,
                target: target.Identity
            ));
        public Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: Qualified(
                source: null,
                target: target.Identity
            ));
    }
    // A single-worker host that makes the durable writes a managed worker makes: publishing a committed target opens the
    // group's admission, and publishing a recovered source completes its recovery under a fresh lease. Running names the
    // release whose process is up.
    private sealed class HostRuntime(WorldReleaseGroupStore groups, Guid owner) : IWorldReleaseRuntime {
        // The reset the last private activation followed, where the Azure host replaces every drained root
        // (WorldAuthorityBlobStore.PrepareReleaseResetAsync), or null when it carried the state.
        public string? ActivatedReset { get; private set; }
        public int Drains { get; private set; }
        public string? Running { get; set; }
        public string? VerifyFailure { get; init; }

        private static Task<WorldReleaseRuntimeResult> Done(string detail) => Task.FromResult(result: new WorldReleaseRuntimeResult(
            true,
            detail
        ));
        private async Task<WorldReleaseGroupSnapshot> CurrentAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken) =>
            (await groups.LoadAsync(
                operation.DeploymentGroup,
                cancellationToken
            ))!.Value;
        private static WorldReleaseRuntimePublicationResult Publication(WorldReleaseGroupOutcome outcome) => new(
            (outcome.Ok ? WorldReleaseRuntimePublication.Opened : WorldReleaseRuntimePublication.Refused),
            outcome.Detail
        );

        public static IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> Fences(Guid owner) => [(new(
            Owner: owner,
            World: SafeName.Parse(candidate: "row")
        ), new(
            1,
            Guid.NewGuid(),
            "root"
        ))];
        public static IReadOnlyDictionary<string, string> Roots(Guid owner) => new Dictionary<string, string> { [$"{owner:D}/row"] = "protected-root" };

        public Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Drains++;
            Running = null;
            return Task.FromResult(result: new WorldReleaseRuntimeResult(
                true,
                "source drained",
                Roots(owner: owner)
            ));
        }
        public async Task<WorldReleaseRuntimePublicationResult> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Publication(outcome: await groups.OpenAdmissionAsync(
                await CurrentAsync(
                    cancellationToken: cancellationToken,
                    operation: operation
                ),
                operation.PendingTargetRelease!,
                operation.PendingOperationId!.Value,
                Guid.NewGuid(),
                cancellationToken
            ));
        public async Task<WorldReleaseRuntimePublicationResult> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Publication(outcome: await groups.CompleteRecoveryAsync(
                await CurrentAsync(
                    cancellationToken: cancellationToken,
                    operation: operation
                ),
                cancellationToken,
                Guid.NewGuid()
            ));
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: Fences(owner: owner));
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Done(detail: "roots restored");
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (operation.PendingPhase == WorldReleaseOperationPhase.Activate) { ActivatedReset = operation.PendingStateReset; }
            Running = operation.PendingTargetRelease;
            return Done(detail: "candidate running");
        }
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Running = operation.PendingSourceRelease;
            return Done(detail: "source running");
        }
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            Running = null;
            return Done(detail: "candidate stopped");
        }
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            ((VerifyFailure is { } failure)
                ? Task.FromResult(result: new WorldReleaseRuntimeResult(
                    false,
                    failure
                ))
                : Done(detail: "candidate verified"));
    }
}
