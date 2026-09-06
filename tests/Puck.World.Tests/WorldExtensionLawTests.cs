using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldExtensionLawTests {
    private static readonly WorldExternalOperation Request = new("creature:7:incarnation:3:death", "creature-resource", "resource-incarnation:3/delete/schema:1", "{}");

    [Fact]
    public async Task FailedReconciliationPreservesDurableContinuationAcrossProviderRestart() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var store = new FakeObjectBlobStore();
        var provider = new Provider { ExecutionStatus = WorldExternalOperationStatus.Running, ExecutionResult = "opaque-poll-reference", LosePoll = true };
        var dispatcher = Dispatcher(extension, Journal(store), provider);
        await dispatcher.CommitAsync(Request, "private-cause", TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken);
        var unknown = await dispatcher.ReconcileAsync(Request.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorldExternalOperationStatus.Unknown, unknown.Status);
        Assert.Equal("opaque-poll-reference", unknown.Result);
        Assert.Equal(new(WorldExternalOperationStatus.Running, "opaque-poll-reference"), provider.Previous);

        var restartedProvider = new Provider();
        await Dispatcher(extension, Journal(store), restartedProvider).ReconcileAsync(Request.Id, TestContext.Current.CancellationToken);
        Assert.Equal(new(WorldExternalOperationStatus.Unknown, "opaque-poll-reference"), restartedProvider.Previous);
        Assert.Equal(0, restartedProvider.Executions);
    }

    [Fact]
    public void TickHostCannotAdvertiseRecordedContributionsAndStillBeReexecuted() {
        using var fixture = Fixtures.FreshServer();
        using var host = new NullAddonHost { ReplayPolicy = WorldExtensionReplayPolicy.Recorded };
        Assert.Throws<InvalidOperationException>(() => fixture.Server.AttachAddons(host));
        host.ReplayPolicy = WorldExtensionReplayPolicy.Recomputed;
        fixture.Server.AttachAddons(host);
    }

    [Fact]
    public async Task CommitSurvivesRestart_AndDuplicateDeliveryDoesNotRepeatTheEffect() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var store = new FakeObjectBlobStore();
        var journal = Journal(store);
        var provider = new Provider();
        var dispatcher = Dispatcher(extension, journal, provider);
        var cause = fixture.Server.CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint.Empty);
        var committed = await dispatcher.CommitAsync(Request, cause, TestContext.Current.CancellationToken);
        Assert.Equal(0, provider.Executions);
        Assert.Equal(cause, committed.Cause);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(Convert.FromBase64String(cause["puck-checkpoint:".Length..]), out _, out _));

        var restartedJournal = Journal(store);
        var restarted = Dispatcher(extension, restartedJournal, provider);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restarted.DispatchAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restarted.DispatchAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restarted.CommitAsync(Request, "later-observation", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, provider.Executions);
        Assert.Equal(cause, Assert.Single(await restartedJournal.ReadAsync(TestContext.Current.CancellationToken)).Cause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CommitAsync(Request with { Payload = "different" }, "cause", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task LostResponseIsUnknown_AndRecoveryReconcilesWithoutExecutingAgain() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var store = new FakeObjectBlobStore();
        var provider = new Provider { LoseResponse = true };
        var dispatcher = Dispatcher(extension, Journal(store), provider);
        await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
        var unknown = await dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorldExternalOperationStatus.Unknown, unknown.Status);
        Assert.Equal(nameof(IOException), unknown.Result);
        Assert.DoesNotContain("secret", unknown.Result);
        Assert.Equal(1, provider.Executions);

        var restarted = Dispatcher(extension, Journal(store), provider);
        Assert.Equal(WorldExternalOperationStatus.Unknown, (await restarted.DispatchAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restarted.ReconcileAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, provider.Executions);
        Assert.Equal(1, provider.Reconciliations);
    }

    [Fact]
    public async Task RebindingANameCannotRedirectACommittedOperation() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var journal = Journal(new FakeObjectBlobStore());
        var provider = new Provider();
        var dispatcher = Dispatcher(extension, journal, provider);
        await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
        provider.Identity = "replacement-resource";
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, provider.Executions);
        Assert.Equal(WorldExternalOperationStatus.Pending, Assert.Single(await journal.ReadAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CrashAfterServiceSuccessBeforeOutcomePersistenceLeavesAReconciliableClaim() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var storage = new FailOutcomeStore();
        var provider = new Provider();
        var dispatcher = Dispatcher(extension, Journal(storage), provider);
        await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(WorldExternalOperationStatus.Dispatching, Assert.Single(await Journal(storage).ReadAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, provider.Executions);
        var restarted = Dispatcher(extension, Journal(storage), provider);
        Assert.Equal(WorldExternalOperationStatus.Dispatching, (await restarted.DispatchAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restarted.ReconcileAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, provider.Executions);
    }

    [Fact]
    public async Task ConcurrentDispatchClaimsOnce_LateCompletionSurvivesUnload() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var journal = Journal(new FakeObjectBlobStore());
        var provider = new Provider { Finish = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var dispatcher = Dispatcher(extension, journal, provider);
        await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
        var running = dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(1, provider.Executions);
        Assert.Equal(WorldExternalOperationStatus.Dispatching, (await dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Throws<InvalidOperationException>(fixture.Server.SuppressRecordedExtensions);
        extension.Dispose();
        provider.Finish.SetResult();
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await running).Status);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, Assert.Single(await journal.ReadAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Throws<ObjectDisposedException>(() => extension.Submit(Mutation()));
        fixture.Server.SuppressRecordedExtensions();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(WorldExternalOperationStatus.Succeeded)]
    [InlineData(WorldExternalOperationStatus.Failed)]
    [InlineData(WorldExternalOperationStatus.Running)]
    public async Task LateExecutionResponseWinsOverConcurrentUncertainty(WorldExternalOperationStatus status) {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var journal = Journal(new FakeObjectBlobStore());
        var provider = new Provider { Finish = new(TaskCreationOptions.RunContinuationsAsynchronously), ExecutionStatus = status, ExecutionResult = "receipt" };
        var dispatcher = Dispatcher(extension, journal, provider);
        await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
        var running = dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(WorldExternalOperationStatus.Unknown, (await dispatcher.ReconcileAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        provider.Finish.SetResult();
        var result = await running;
        Assert.Equal(status, result.Status);
        Assert.Equal("receipt", result.Result);
        Assert.Equal(1, provider.Executions);
    }

    [Fact]
    public async Task ReplayCannotMountOrRunRecordedProviders_AndDoesNotTouchTheJournal() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var store = new FakeObjectBlobStore();
        var provider = new Provider();
        var dispatcher = Dispatcher(extension, Journal(store), provider);
        await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
        fixture.Server.SuppressRecordedExtensions();
        var reads = store.ReadCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken).AsTask());
        Assert.Throws<InvalidOperationException>(() => Extension(fixture.Server));
        Assert.Throws<InvalidOperationException>(() => extension.Submit(Mutation()));
        Assert.Equal(reads, store.ReadCount);
        Assert.Equal(0, provider.Executions);
        fixture.Server.StartRecordedExtensionEpoch();
        Assert.Throws<InvalidOperationException>(() => extension.Submit(Mutation()));
        using var resumed = Extension(fixture.Server);
        resumed.Submit(Mutation());
        Assert.Equal(1, resumed.PendingCount);
    }

    [Fact]
    public void RecordedMutationUsesTheRealTape_AndReplayNeedsNoProvider() {
        Fixtures.SkipIfReplayDirectoryUnwritable();
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var tape = new WorldReplayTape(fixture.Server, fixture.Server.Profiles, new LoopbackTransport(fixture.Server), [],
            static (_, server) => {
                Assert.Throws<InvalidOperationException>(() => Extension(server));
                Assert.Throws<InvalidOperationException>(server.StartRecordedExtensionEpoch);
                return new NullAddonHost();
            });
        Assert.True(tape.TryBeginRecording($"recorded-extension-{Guid.NewGuid():N}", out var refusal), refusal);
        Assert.Throws<InvalidOperationException>(() => tape.CaptureExternalOperationCause());
        extension.Submit(Mutation());
        fixture.Step();
        tape.NoteTick();
        var cause = tape.CaptureExternalOperationCause();
        using var prefix = new MemoryStream(Convert.FromBase64String(cause["puck-replay:".Length..]));
        Assert.Single(WorldReplaySnapshot.Read(prefix).Ticks);
        Assert.Equal(WorldReplayMode.Recording, tape.Mode);
        var stopped = tape.StopRecording();
        Assert.Null(stopped.VerifyFault);
        Assert.True(stopped.Verdict!.Value.Match);
        using var stream = File.OpenRead(stopped.Path);
        var recorded = WorldReplaySnapshot.Read(stream);
        Assert.Single(recorded.Ticks.SelectMany(tick => tick.Authority), entry => entry.GetType().Name == "Mutation");
        Assert.True(tape.TryBeginDrive(Path.GetFileNameWithoutExtension(stopped.Path), toTick: 1, forkName: null, documentPath: null, out refusal), refusal);
        Assert.Throws<InvalidOperationException>(fixture.Server.StartRecordedExtensionEpoch);
        tape.CancelDrive();
        fixture.Server.StartRecordedExtensionEpoch();
        Assert.False(extension.IsActive);
    }

    [Fact]
    public void RequestedScopeCannotBeEscapedThroughABatchOrAnotherIdentity() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        Assert.Throws<InvalidOperationException>(() => extension.Submit(Mutation() with { Principal = WorldPrincipal.Addon("other") }));
        Assert.Throws<InvalidOperationException>(() => extension.Submit(new WorldMutation.Batch(WorldPrincipal.Console,
            [Mutation(), new WorldMutation.RemoveAddon(WorldPrincipal.Console, "probe")])));
        using var ungranted = new WorldRecordedExtension(fixture.Server, WorldPrincipal.Addon("ungranted"),
            [new(WorldCapability.Mutate, GrantSubject.Section(WorldSection.State))], 8);
        var before = fixture.DefinitionBytes();
        ungranted.Submit(Mutation() with { Principal = ungranted.Principal });
        fixture.Step();
        Assert.Equal(before, fixture.DefinitionBytes());
    }

    [Fact]
    public void ContributionsAreCopiedBoundedAndOnlyAdmittedOnThePump() {
        using var fixture = Fixtures.FreshServer();
        using var extension = new WorldRecordedExtension(fixture.Server, WorldPrincipal.Console,
            [new(WorldCapability.Mutate, GrantSubject.Section(WorldSection.State))], 1);
        var members = new List<WorldMutation> { Mutation() };
        var observed = 0;
        fixture.Server.MutationTap = (_, _) => observed++;
        extension.Submit(new WorldMutation.Batch(extension.Principal, members));
        members.Clear();
        Assert.Equal(0, observed);
        Assert.Throws<InvalidOperationException>(() => extension.Submit(Mutation()));
        Assert.False(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out _, out _));
        fixture.Step();
        Assert.Equal(1, observed);
        Assert.Contains(fixture.Server.Definition.State, row => row.Name.Value == "external-result");
        extension.Submit(Mutation());
        extension.Dispose();
        fixture.Step();
        Assert.Equal(1, observed);
    }

    [Fact]
    public void ObservationRequiresBothManifestAndAuthority() {
        using var fixture = Fixtures.FreshServer();
        var query = new WorldQuery.StateObservations();
        using var noRequest = Extension(fixture.Server);
        Assert.Throws<InvalidOperationException>(() => noRequest.Observe(query));
        using var denied = new WorldRecordedExtension(fixture.Server, WorldPrincipal.Addon("observer"),
            [new(WorldCapability.Observe, query.ObservationSubject())], 1);
        Assert.True(denied.Observe(query).Refused);
        using var allowed = new WorldRecordedExtension(fixture.Server, WorldPrincipal.Console,
            [new(WorldCapability.Observe, query.ObservationSubject())], 1);
        Assert.False(allowed.Observe(query).Refused);
    }

    [Fact]
    public void ManifestCanRequestOnePlacementWithoutRequestingTheWholeSection() {
        using var fixture = Fixtures.FreshServer();
        using var extension = new WorldRecordedExtension(fixture.Server, WorldPrincipal.Console,
            [new(WorldCapability.Mutate, GrantSubject.Placement("creature"))], 1);
        extension.Submit(new WorldMutation.RemovePlacement(extension.Principal, "creature"));
        Assert.Equal(1, extension.PendingCount);
        Assert.Throws<InvalidOperationException>(() => extension.Submit(new WorldMutation.RemovePlacement(extension.Principal, "another")));
        fixture.Server.SuppressRecordedExtensions();
        Assert.Equal(0, extension.PendingCount);
    }

    [Fact]
    public async Task JournalCapacityRefusesBeforeDispatch_AndRealDirectoryStorageRoundTrips() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(fixture.Server);
        var path = Path.Combine(Path.GetTempPath(), $"puck-extension-{Guid.NewGuid():N}");
        var address = new ObjectBlobAddress(Guid.NewGuid(), "operations.json");
        var journal = new WorldExternalOperationJournal(PuckStorageTestComposition.BuildStore(), new DirectoryObjectStorageTarget(path), address, 1, 16384, 4);
        var provider = new Provider();
        var dispatcher = Dispatcher(extension, journal, provider);
        try {
            await dispatcher.CommitAsync(Request, "cause", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.CommitAsync(Request with { Id = "another" }, "cause", TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, provider.Executions);
            Assert.Equal(WorldExternalOperationStatus.Succeeded, (await dispatcher.DispatchAsync(Request.Id, TestContext.Current.CancellationToken)).Status);
        } finally {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }

    private static WorldRecordedExtension Extension(WorldServer server) => new(server, WorldPrincipal.Console,
        [new(WorldCapability.Mutate, GrantSubject.Section(WorldSection.State))], 8);
    private static WorldMutation.UpsertStateRow Mutation() => new(WorldPrincipal.Console,
        new WorldStateRow(Name: CellName.Parse("external-result"), Kind: CellKind.Int));
    private static WorldExternalOperationJournal Journal(IObjectBlobStore store) => new(store,
        new DirectoryObjectStorageTarget("unused"), new ObjectBlobAddress(Guid.Empty, "operations.json"), 8, 4194304, 4);
    private static WorldExternalOperationDispatcher Dispatcher(WorldRecordedExtension extension, WorldExternalOperationJournal journal, Provider provider) =>
        new(extension, journal, new Dictionary<string, IWorldExternalOperationProvider> { [Request.Binding] = provider });

    private sealed class Provider : IWorldExternalOperationProvider {
        public string Identity { get; set; } = Request.BindingIdentity;
        public int Executions;
        public int Reconciliations;
        public bool LoseResponse;
        public bool LosePoll;
        public WorldExternalOperationStatus ExecutionStatus = WorldExternalOperationStatus.Succeeded;
        public string ExecutionResult = "deleted";
        public WorldExternalOperationResult? Previous;
        public TaskCompletionSource? Finish;
        private bool m_deleted;

        public async ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Executions++;
            if (Finish is { } finish) { await finish.Task.WaitAsync(cancellationToken); }
            m_deleted = true;
            if (LoseResponse) { throw new IOException("response lost; secret must not be journaled"); }
            return new(ExecutionStatus, ExecutionResult);
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation, WorldExternalOperationResult previous, CancellationToken cancellationToken) {
            Reconciliations++;
            Previous = previous;
            if (LosePoll) { throw new IOException("poll lost; secret must not be journaled"); }
            return ValueTask.FromResult(new WorldExternalOperationResult(
                m_deleted ? WorldExternalOperationStatus.Succeeded : WorldExternalOperationStatus.Unknown, "observed"));
        }
    }

    private sealed class FailOutcomeStore : IObjectBlobStore {
        private readonly FakeObjectBlobStore m_inner = new();
        private int m_writes;
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) =>
            m_inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            m_inner.ListAsync(target, objectId, keyPrefix, cancellationToken);
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content,
            ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (++m_writes == 3) { throw new IOException("simulated process loss before outcome persistence"); }
            return m_inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
        }
    }
}
