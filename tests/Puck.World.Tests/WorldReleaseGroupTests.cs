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
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri("UseDevelopmentStorage=true");

    [Fact]
    public async Task GroupStateCommitsFinalizesRetainsHistoryAndAllowsNextOperation() {
        var store = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(store, Target, owner);
        var created = await groups.CreateAsync("primary", "release-a", TestContext.Current.CancellationToken);
        Assert.True(created.Ok, created.Detail);
        var started = await groups.BeginAsync(created.Snapshot!.Value, Guid.NewGuid(), "release-b", TestContext.Current.CancellationToken);
        Assert.True(started.Ok, started.Detail);
        var phase = started.Snapshot!.Value;
        await Assert.ThrowsAsync<InvalidDataException>(() => groups.AdvanceAsync(phase, phase.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = phase.Record.Revision + 1 }, TestContext.Current.CancellationToken));
        var drained = await groups.AdvanceAsync(phase, phase.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = new Dictionary<string, string> { ["row"] = "root-1" }, Revision = phase.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.True(drained.Ok, drained.Detail);
        var activated = await groups.AdvanceAsync(drained.Snapshot!.Value, drained.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = drained.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.True(activated.Ok, activated.Detail);
        var verified = await groups.AdvanceAsync(activated.Snapshot!.Value, activated.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = activated.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        Assert.True(verified.Ok, verified.Detail);
        var committed = await groups.CommitAsync(verified.Snapshot!.Value, [(new WorldAuthorityIdentity(owner, SafeName.Parse("row")), new WorldAuthorityFence(1, Guid.NewGuid(), "root"))], TestContext.Current.CancellationToken);
        Assert.True(committed.Ok, committed.Detail);
        var committedState = committed.Snapshot!.Value;
        var opened = await groups.OpenAdmissionAsync(committedState, "release-b", committedState.Record.PendingOperationId!.Value, committedState.Record.AuthorityLease, TestContext.Current.CancellationToken);
        Assert.True(opened.Ok, opened.Detail);
        var finalized = await groups.FinalizeAsync(opened.Snapshot!.Value, TestContext.Current.CancellationToken);
        Assert.True(finalized.Ok, finalized.Detail);
        Assert.False(finalized.Snapshot!.Value.Record.RollbackEligible);
        Assert.Single(finalized.Snapshot.Value.Record.History);
        Assert.Equal("root-1", finalized.Snapshot.Value.Record.History[0].RecoveryRoots["row"]);
        var next = await groups.BeginAsync(finalized.Snapshot.Value, Guid.NewGuid(), "release-c", TestContext.Current.CancellationToken);
        Assert.True(next.Ok, next.Detail);
    }

    [Fact]
    public async Task GroupRecoveryLeavesSourceClosedAndStaleSnapshotCannotReopenIt() {
        var store = new FakeObjectBlobStore();
        var owner = Guid.NewGuid();
        var groups = new WorldReleaseGroupStore(store, Target, owner);
        var created = await groups.CreateAsync("primary", "release-a", TestContext.Current.CancellationToken);
        var started = await groups.BeginAsync(created.Snapshot!.Value, Guid.NewGuid(), "release-b", TestContext.Current.CancellationToken);
        var drained = await groups.AdvanceAsync(started.Snapshot!.Value, started.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = new Dictionary<string, string> { ["row"] = "root-1" }, Revision = started.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        var recovered = await groups.RecoverToSourceAsync(drained.Snapshot!.Value, "candidate refused", TestContext.Current.CancellationToken);
        Assert.True(recovered.Ok, recovered.Detail);
        Assert.Equal("release-a", recovered.Snapshot!.Value.Record.ActiveRelease);
        Assert.Equal(WorldReleaseAdmissionState.Closed, recovered.Snapshot.Value.Record.Admission);
        var stale = await groups.OpenRecoveredAdmissionAsync(started.Snapshot.Value, "release-a", Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(WorldReleaseOperationOutcomeKind.Conflict, stale.Kind);
        var reopened = await groups.OpenRecoveredAdmissionAsync(recovered.Snapshot.Value, "release-a", Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.True(reopened.Ok, reopened.Detail);
    }

    [Fact]
    public async Task ManagedHostKeepsCandidatePrivateUntilExplicitGroupPublication() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes("world.key", key.ExportPkcs8PrivateKey());
        var owner = Guid.NewGuid();
        var identity = new WorldAuthorityIdentity(owner, SafeName.Parse("row"));
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var document = Fixtures.BuildDocument() with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None } };
        var backend = new WorldAuthorityBlobStore(store, target);
        Assert.True((await backend.PublishDefinitionAsync(identity, document, TestContext.Current.CancellationToken)).Ok);
        var groups = new WorldReleaseGroupStore(store, target, owner);
        Assert.True((await groups.CreateAsync("primary", "release-a", TestContext.Current.CancellationToken)).Ok);
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(() => source, new SiloConsoleTagging(output));
        var definition = new WorldSiloDefinition([new(owner, identity.World, new(keyFile), Pinned: true)], new(1), new("directory", JsonElement.Parse("{}")), directory.RootPath, new("Localhost"), Release: new("primary", owner, "release-a"));
        var host = new WorldSiloHost(definition, store, routing, target);
        using var instances = host.Instances;
        var activation = host.ActivateAsync(identity, TestContext.Current.CancellationToken);
        await PumpAsync(host, activation);
        Assert.True(await activation);
        Assert.False(host.ReleaseAdmissionOpen);
        Assert.False(routing.TryGetSession("row", out _));
        var publication = host.PublishManagedReleaseAdmissionAsync(TestContext.Current.CancellationToken);
        await PumpAsync(host, publication);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await publication);
        Assert.True(host.ReleaseAdmissionOpen);
        Assert.True(routing.TryGetSession("row", out _));
        await PumpAsync(host, host.DrainAsync(TestContext.Current.CancellationToken));

        var replacementSource = new TextCommandSource(new CommandRegistry(modules: []));
        var replacementRouting = new SiloConsoleRouting(() => replacementSource, new SiloConsoleTagging(output));
        var replacement = new WorldSiloHost(definition, store, replacementRouting, target);
        using var replacementInstances = replacement.Instances;
        var replacementActivation = replacement.ActivateAsync(identity, TestContext.Current.CancellationToken);
        await PumpAsync(replacement, replacementActivation);
        Assert.True(await replacementActivation);
        Assert.False(replacement.ReleaseAdmissionOpen);
        Assert.False(replacementRouting.TryGetSession("row", out _));
        var replacementPublication = replacement.PublishManagedReleaseAdmissionAsync(TestContext.Current.CancellationToken);
        await PumpAsync(replacement, replacementPublication);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await replacementPublication);
        Assert.True(replacementRouting.TryGetSession("row", out _));
        await PumpAsync(replacement, replacement.DrainAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManagedHostResumesCommittedTargetWithFreshFenceClaim() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes("world.key", key.ExportPkcs8PrivateKey());
        var owner = Guid.NewGuid();
        var identity = new WorldAuthorityIdentity(owner, SafeName.Parse("row"));
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var document = Fixtures.BuildDocument() with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None } };
        var backend = new WorldAuthorityBlobStore(store, target);
        Assert.True((await backend.PublishDefinitionAsync(identity, document, TestContext.Current.CancellationToken)).Ok);
        var groups = new WorldReleaseGroupStore(store, target, owner);
        var created = await groups.CreateAsync("primary", "release-a", TestContext.Current.CancellationToken);
        var started = await groups.BeginAsync(created.Snapshot!.Value, Guid.NewGuid(), "release-b", TestContext.Current.CancellationToken);
        var drained = await groups.AdvanceAsync(started.Snapshot!.Value, started.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Drain, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = new Dictionary<string, string> { ["row"] = "root-1" }, Revision = started.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        var activated = await groups.AdvanceAsync(drained.Snapshot!.Value, drained.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Activate, Revision = drained.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        var verified = await groups.AdvanceAsync(activated.Snapshot!.Value, activated.Snapshot.Value.Record with { PendingPhase = WorldReleaseOperationPhase.Verify, Revision = activated.Snapshot.Value.Record.Revision + 1 }, TestContext.Current.CancellationToken);
        var committed = await groups.CommitAsync(verified.Snapshot!.Value, [(identity, new WorldAuthorityFence(1, Guid.NewGuid(), "root"))], TestContext.Current.CancellationToken);
        Assert.True(committed.Ok, committed.Detail);
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(() => source, new SiloConsoleTagging(output));
        var definition = new WorldSiloDefinition([new(owner, identity.World, new(keyFile), Pinned: true)], new(1), new("directory", JsonElement.Parse("{}")), directory.RootPath, new("Localhost"), Release: new("primary", owner, "release-b"));
        var host = new WorldSiloHost(definition, store, routing, target);
        using var instances = host.Instances;
        var activation = host.ActivateAsync(identity, TestContext.Current.CancellationToken);
        await PumpAsync(host, activation);
        Assert.True(await activation);
        Assert.False(host.ReleaseAdmissionOpen);
        var publication = host.PublishManagedReleaseAdmissionAsync(TestContext.Current.CancellationToken);
        await PumpAsync(host, publication);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await publication);
        Assert.True(routing.TryGetSession("row", out _));
        await PumpAsync(host, host.DrainAsync(TestContext.Current.CancellationToken));
    }

    private static async Task PumpAsync(WorldSiloHost host, Task operation) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!operation.IsCompleted) {
            host.DrainActivationMailbox();
            await Task.Delay(1, deadline.Token);
        }
        await operation;
        host.DrainActivationMailbox();
    }
}
