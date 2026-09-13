using System.Security.Cryptography;
using System.Text.Json;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Durable group-state and managed silo admission laws.</summary>
public sealed class WorldReleaseGroupTests {
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

    private static async Task PumpAsync(WorldSiloHost host, Task operation) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 20));
        while (!operation.IsCompleted) {
            host.DrainActivationMailbox();
            await Task.Delay(
                1,
                deadline.Token
            );
        }
        await operation;
        host.DrainActivationMailbox();
    }

    [Fact]
    public void FenceCensusRejectsUnownedAndDuplicateWorlds() {
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var fence = new WorldAuthorityFence(
            1,
            Guid.NewGuid(),
            "root"
        );

        Assert.Throws<ArgumentException>(testCode: () => WorldReleaseFenceClaim.Compute(fences: [(identity, fence), (identity, fence)]));
        Assert.Throws<ArgumentException>(testCode: () => WorldReleaseFenceClaim.Compute(fences: [(identity, fence with { Token = Guid.Empty })]));
    }
    [Fact]
    public async Task FirstCommitHasNoRollbackTargetAndCanBeReloadedOpenedAndFinalized() {
        var token = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            owner
        );
        var group = (await groups.CreateAsync(
            activeRelease: null,
            cancellationToken: token,
            deploymentGroup: "primary"
        )).Snapshot!.Value;

        group = (await groups.BeginAsync(
            group,
            Guid.NewGuid(),
            "release-a",
            token
        )).Snapshot!.Value;
        var coordinator = new WorldReleaseCoordinator(groups: groups);

        group = (await coordinator.RecordDrainAsync(
            group,
            new Dictionary<string, string> { ["row"] = "protected-empty-root" },
            token
        )).Snapshot!.Value;
        group = (await coordinator.RecordActivationAsync(
            cancellationToken: token,
            current: group
        )).Snapshot!.Value;
        group = (await coordinator.RecordVerificationAsync(
            cancellationToken: token,
            current: group
        )).Snapshot!.Value;
        var committed = await groups.CommitAsync(
            group,
            [(new(
                    Owner: owner,
                    World: SafeName.Parse(candidate: "row")
                ), new(
                    1,
                    Guid.NewGuid(),
                    "root"
                ))],
            token
        );

        Assert.True(
            condition: committed.Ok,
            userMessage: committed.Detail
        );
        group = (await groups.LoadAsync(
            cancellationToken: token,
            deploymentGroup: "primary"
        ))!.Value;
        Assert.Null(@object: group.Record.PreviousRelease);
        Assert.False(condition: group.Record.RollbackEligible);
        Assert.Equal(
            "release-a",
            group.Record.ActiveRelease
        );
        var opened = await groups.OpenAdmissionAsync(
            group,
            "release-a",
            group.Record.PendingOperationId!.Value,
            group.Record.AuthorityLease,
            token
        );

        Assert.True(
            condition: opened.Ok,
            userMessage: opened.Detail
        );
        Assert.True(condition: (await groups.FinalizeAsync(
            opened.Snapshot!.Value,
            token
        )).Ok);
    }
    [Fact]
    public async Task GroupRecoveryLeavesSourceClosedAndStaleSnapshotCannotReopenIt() {
        var store = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: store,
            target: Target
        );
        var created = await groups.CreateAsync(
            "primary",
            "release-a",
            TestContext.Current.CancellationToken
        );
        var started = await groups.BeginAsync(
            created.Snapshot!.Value,
            Guid.NewGuid(),
            "release-b",
            TestContext.Current.CancellationToken
        );
        var drained = await groups.AdvanceAsync(
            started.Snapshot!.Value,
            started.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = new Dictionary<string, string> { ["row"] = "root-1" }, Revision = (started.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );
        var recovering = await groups.BeginRecoveryAsync(
            drained.Snapshot!.Value,
            "candidate refused",
            TestContext.Current.CancellationToken
        );

        Assert.False(condition: (await groups.CompleteRecoveryAsync(
            recovering.Snapshot!.Value,
            TestContext.Current.CancellationToken
        )).Ok);
        var restored = await groups.RecordRecoveryRestoredAsync(
            recovering.Snapshot.Value,
            TestContext.Current.CancellationToken
        );
        var recovered = await groups.CompleteRecoveryAsync(
            restored.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: recovered.Ok,
            userMessage: recovered.Detail
        );
        Assert.Equal(
            "release-a",
            recovered.Snapshot!.Value.Record.ActiveRelease
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Closed,
            recovered.Snapshot.Value.Record.Admission
        );
        var stale = await groups.OpenRecoveredAdmissionAsync(
            started.Snapshot.Value,
            "release-a",
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorldReleaseOperationOutcomeKind.Conflict,
            stale.Kind
        );
        var reopened = await groups.OpenRecoveredAdmissionAsync(
            recovered.Snapshot.Value,
            "release-a",
            Guid.NewGuid(),
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: reopened.Ok,
            userMessage: reopened.Detail
        );
    }
    [Fact]
    public async Task GroupStateCommitsFinalizesRetainsHistoryAndAllowsNextOperation() {
        var store = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: store,
            target: Target
        );
        var created = await groups.CreateAsync(
            "primary",
            "release-a",
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: created.Ok,
            userMessage: created.Detail
        );
        var started = await groups.BeginAsync(
            created.Snapshot!.Value,
            Guid.NewGuid(),
            "release-b",
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: started.Ok,
            userMessage: started.Detail
        );
        var phase = started.Snapshot!.Value;

        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => groups.AdvanceAsync(
            phase,
            phase.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = (phase.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        ));
        var drained = await groups.AdvanceAsync(
            phase,
            phase.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = new Dictionary<string, string> { ["row"] = "root-1" }, Revision = (phase.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: drained.Ok,
            userMessage: drained.Detail
        );
        var activated = await groups.AdvanceAsync(
            drained.Snapshot!.Value,
            drained.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = (drained.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: activated.Ok,
            userMessage: activated.Detail
        );
        var verified = await groups.AdvanceAsync(
            activated.Snapshot!.Value,
            activated.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = (activated.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: verified.Ok,
            userMessage: verified.Detail
        );
        var committed = await groups.CommitAsync(
            verified.Snapshot!.Value,
            [(new WorldAuthorityIdentity(
                    Owner: owner,
                    World: SafeName.Parse(candidate: "row")
                ), new WorldAuthorityFence(
                    1,
                    Guid.NewGuid(),
                    "root"
                ))],
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: committed.Ok,
            userMessage: committed.Detail
        );
        var committedState = committed.Snapshot!.Value;
        var opened = await groups.OpenAdmissionAsync(
            committedState,
            "release-b",
            committedState.Record.PendingOperationId!.Value,
            committedState.Record.AuthorityLease,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: opened.Ok,
            userMessage: opened.Detail
        );
        var finalized = await groups.FinalizeAsync(
            opened.Snapshot!.Value,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: finalized.Ok,
            userMessage: finalized.Detail
        );
        Assert.False(condition: finalized.Snapshot!.Value.Record.RollbackEligible);
        Assert.Single(collection: finalized.Snapshot.Value.Record.History);
        Assert.Equal(
            "root-1",
            finalized.Snapshot.Value.Record.History[0].RecoveryRoots["row"]
        );
        var next = await groups.BeginAsync(
            finalized.Snapshot.Value,
            Guid.NewGuid(),
            "release-c",
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: next.Ok,
            userMessage: next.Detail
        );
    }
    [Fact]
    public async Task ManagedHostKeepsCandidatePrivateUntilExplicitGroupPublication() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes(
            "world.key",
            key.ExportPkcs8PrivateKey()
        );
        var owner = Guid.NewGuid();
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "row")
        );
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var document = Fixtures.BuildDocument() with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None } };
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: target
        );

        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            document,
            TestContext.Current.CancellationToken
        )).Ok);
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: store,
            target: target
        );

        Assert.True(condition: (await groups.CreateAsync(
            "primary",
            "release-a",
            TestContext.Current.CancellationToken
        )).Ok);
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        var definition = new WorldSiloDefinition(
            [new(
                    owner,
                    identity.World,
                    new(KeyFile: keyFile),
                    Pinned: true
                )],
            new(Budget: 1),
            new(
                "directory",
                JsonElement.Parse("{}")
            ),
            directory.RootPath,
            new(Kind: "Localhost"),
            Release: new(
                ExpectedRelease: "release-a",
                Group: "primary",
                Owner: owner
            )
        );
        var host = new WorldSiloHost(
            definition,
            store,
            routing,
            target
        );
        using var instances = host.Instances;
        var activation = host.ActivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: activation
        );
        Assert.True(condition: await activation);
        Assert.False(condition: host.ReleaseAdmissionOpen);
        Assert.False(condition: routing.TryGetSession(
            session: out _,
            worldId: "row"
        ));
        var publication = host.PublishManagedReleaseAdmissionAsync(TestContext.Current.CancellationToken);

        await PumpAsync(
            host: host,
            operation: publication
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await publication
        );
        Assert.True(condition: host.ReleaseAdmissionOpen);
        Assert.True(condition: routing.TryGetSession(
            session: out _,
            worldId: "row"
        ));
        await PumpAsync(
            host: host,
            operation: host.DrainAsync(ct: TestContext.Current.CancellationToken)
        );

        var replacementSource = new TextCommandSource(new CommandRegistry(modules: []));
        var replacementRouting = new SiloConsoleRouting(
            source: () => replacementSource,
            tagging: new SiloConsoleTagging(output: output)
        );
        var replacement = new WorldSiloHost(
            definition,
            store,
            replacementRouting,
            target
        );
        using var replacementInstances = replacement.Instances;
        var replacementActivation = replacement.ActivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: replacement,
            operation: replacementActivation
        );
        Assert.True(condition: await replacementActivation);
        Assert.False(condition: replacement.ReleaseAdmissionOpen);
        Assert.False(condition: replacementRouting.TryGetSession(
            session: out _,
            worldId: "row"
        ));
        var replacementPublication = replacement.PublishManagedReleaseAdmissionAsync(TestContext.Current.CancellationToken);

        await PumpAsync(
            host: replacement,
            operation: replacementPublication
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await replacementPublication
        );
        Assert.True(condition: replacementRouting.TryGetSession(
            session: out _,
            worldId: "row"
        ));
        await PumpAsync(
            host: replacement,
            operation: replacement.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
    }
    [Fact]
    public async Task ManagedHostResumesCommittedTargetWithFreshFenceClaim() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes(
            "world.key",
            key.ExportPkcs8PrivateKey()
        );
        var owner = Guid.NewGuid();
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "row")
        );
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var document = Fixtures.BuildDocument() with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None } };
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: target
        );

        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            document,
            TestContext.Current.CancellationToken
        )).Ok);
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: store,
            target: target
        );
        var created = await groups.CreateAsync(
            "primary",
            "release-a",
            TestContext.Current.CancellationToken
        );
        var started = await groups.BeginAsync(
            created.Snapshot!.Value,
            Guid.NewGuid(),
            "release-b",
            TestContext.Current.CancellationToken
        );
        var drained = await groups.AdvanceAsync(
            started.Snapshot!.Value,
            started.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = new Dictionary<string, string> { ["row"] = "root-1" }, Revision = (started.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );
        var activated = await groups.AdvanceAsync(
            drained.Snapshot!.Value,
            drained.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = (drained.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );
        var verified = await groups.AdvanceAsync(
            activated.Snapshot!.Value,
            activated.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = (activated.Snapshot.Value.Record.Revision + 1) },
            TestContext.Current.CancellationToken
        );
        var committed = await groups.CommitAsync(
            verified.Snapshot!.Value,
            [(identity, new WorldAuthorityFence(
                    1,
                    Guid.NewGuid(),
                    "root"
                ))],
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: committed.Ok,
            userMessage: committed.Detail
        );
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        var definition = new WorldSiloDefinition(
            [new(
                    owner,
                    identity.World,
                    new(KeyFile: keyFile),
                    Pinned: true
                )],
            new(Budget: 1),
            new(
                "directory",
                JsonElement.Parse("{}")
            ),
            directory.RootPath,
            new(Kind: "Localhost"),
            Release: new(
                ExpectedRelease: "release-b",
                Group: "primary",
                Owner: owner
            )
        );
        var host = new WorldSiloHost(
            definition,
            store,
            routing,
            target
        );
        using var instances = host.Instances;
        var activation = host.ActivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: activation
        );
        Assert.True(condition: await activation);
        Assert.False(condition: host.ReleaseAdmissionOpen);
        var publication = host.PublishManagedReleaseAdmissionAsync(TestContext.Current.CancellationToken);

        await PumpAsync(
            host: host,
            operation: publication
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await publication
        );
        Assert.True(condition: routing.TryGetSession(
            session: out _,
            worldId: "row"
        ));
        await PumpAsync(
            host: host,
            operation: host.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
    }
    [Fact]
    public async Task RepeatedSourcePublicationRechecksTheVersionEvenWithTheSameFence() {
        var token = TestContext.Current.CancellationToken;
        var groups = new WorldReleaseGroupStore(
            new FakeObjectBlobStore(),
            Target,
            Guid.NewGuid()
        );
        var group = (await groups.CreateAsync(
            activeRelease: "release-a",
            cancellationToken: token,
            deploymentGroup: "primary"
        )).Snapshot!.Value;
        var claim = Guid.NewGuid();

        group = (await groups.RebindAdmissionAsync(
            cancellationToken: token,
            current: group,
            expectedRelease: "release-a",
            freshAuthorityLease: claim
        )).Snapshot!.Value;
        var repeated = await groups.RebindAdmissionAsync(
            cancellationToken: token,
            current: group,
            expectedRelease: "release-a",
            freshAuthorityLease: claim
        );

        Assert.True(
            condition: repeated.Ok,
            userMessage: repeated.Detail
        );
        var stale = group;

        group = (await groups.BeginAsync(
            repeated.Snapshot!.Value,
            Guid.NewGuid(),
            "release-b",
            token
        )).Snapshot!.Value;
        Assert.Equal(
            WorldReleaseOperationOutcomeKind.PreconditionFailed,
            (await groups.RebindAdmissionAsync(
                cancellationToken: token,
                current: stale,
                expectedRelease: "release-a",
                freshAuthorityLease: claim
            )).Kind
        );
        var prepared = await groups.RebindPrepareAdmissionAsync(
            cancellationToken: token,
            current: group,
            expectedRelease: "release-a",
            freshAuthorityLease: claim
        );

        Assert.True(
            condition: prepared.Ok,
            userMessage: prepared.Detail
        );
        Assert.True(condition: (await groups.RebindPrepareAdmissionAsync(
            prepared.Snapshot!.Value,
            "release-a",
            claim,
            token
        )).Ok);
    }
}
