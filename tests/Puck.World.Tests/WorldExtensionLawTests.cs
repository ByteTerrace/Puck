using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldExtensionLawTests {
    private static readonly WorldExternalOperation Request = new(
        Binding: "creature-resource",
        BindingIdentity: "resource-incarnation:3/delete/schema:1",
        Id: "creature:7:incarnation:3:death",
        Payload: "{}"
    );

    private static WorldExternalOperationDispatcher Dispatcher(WorldRecordedExtension extension, WorldExternalOperationJournal journal, Provider provider) =>
        new(
            extension,
            journal,
            new Dictionary<string, IWorldExternalOperationProvider> { [Request.Binding] = provider }
        );
    private static WorldRecordedExtension Extension(WorldServer server) => new(
        server,
        WorldPrincipal.Console,
        [new(
                Capability: WorldCapability.Mutate,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )],
        8
    );
    private static WorldExternalOperationJournal Journal(IObjectBlobStore store) => new(
        store,
        new DirectoryObjectStorageTarget("unused"),
        new ObjectBlobAddress(
            Key: "operations.json",
            ObjectId: Guid.Empty
        ),
        8,
        4194304,
        4
    );
    private static WorldMutation.UpsertStateRow Mutation() => new(
        Principal: WorldPrincipal.Console,
        Row: new WorldStateRow(
            Name: CellName.Parse(candidate: "external-result"),
            Kind: CellKind.Int
        )
    );

    [Fact]
    public async Task CommitSurvivesRestart_AndDuplicateDeliveryDoesNotRepeatTheEffect() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var store = new FakeObjectBlobStore();
        var journal = Journal(store: store);
        var provider = new Provider();
        var dispatcher = Dispatcher(
            extension: extension,
            journal: journal,
            provider: provider
        );
        var cause = fixture.Server.CaptureExternalOperationCause(hostRow: WorldAuthorityHostRowCheckpoint.Empty);
        var committed = await dispatcher.CommitAsync(
            Request,
            cause,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            actual: provider.Executions,
            expected: 0
        );
        Assert.Equal(
            cause,
            committed.Cause
        );
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: Convert.FromBase64String(s: cause["puck-checkpoint:".Length..]),
            checkpoint: out _,
            reason: out _
        ));

        var restartedJournal = Journal(store: store);
        var restarted = Dispatcher(
            extension: extension,
            journal: restartedJournal,
            provider: provider
        );

        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restarted.DispatchAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restarted.DispatchAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restarted.CommitAsync(
                Request,
                "later-observation",
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
        Assert.Equal(
            cause,
            Assert.Single(collection: await restartedJournal.ReadAsync(cancellationToken: TestContext.Current.CancellationToken)).Cause
        );
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => restarted.CommitAsync(
            Request with { Payload = "different" },
            "cause",
            TestContext.Current.CancellationToken
        ).AsTask());
    }
    [Fact]
    public async Task ConcurrentDispatchClaimsOnce_LateCompletionSurvivesUnload() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var journal = Journal(store: new FakeObjectBlobStore());
        var provider = new Provider { Finish = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously) };
        var dispatcher = Dispatcher(
            extension: extension,
            journal: journal,
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "cause",
            TestContext.Current.CancellationToken
        );
        var running = dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        ).AsTask();

        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
        Assert.Equal(
            WorldExternalOperationStatus.Dispatching,
            (await dispatcher.DispatchAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Throws<InvalidOperationException>(testCode: fixture.Server.SuppressRecordedExtensions);
        extension.Dispose();
        provider.Finish.SetResult();
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await running).Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            Assert.Single(collection: await journal.ReadAsync(cancellationToken: TestContext.Current.CancellationToken)).Status
        );
        Assert.Throws<ObjectDisposedException>(testCode: () => extension.Submit(mutation: Mutation()));
        fixture.Server.SuppressRecordedExtensions();
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        ).AsTask());
    }
    [Fact]
    public void ContributionsAreCopiedBoundedAndOnlyAdmittedOnThePump() {
        using var fixture = Fixtures.FreshServer();
        using var extension = new WorldRecordedExtension(
            fixture.Server,
            WorldPrincipal.Console,
            [new(
                    Capability: WorldCapability.Mutate,
                    Subject: GrantSubject.Section(section: WorldSection.State)
                )],
            1
        );
        var members = new List<WorldMutation> { Mutation() };
        var observed = 0;

        fixture.Server.MutationTap = (_, _) => observed++;
        extension.Submit(mutation: new WorldMutation.Batch(
            extension.Principal,
            members
        ));
        members.Clear();
        Assert.Equal(
            actual: observed,
            expected: 0
        );
        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: Mutation()));
        Assert.False(condition: fixture.Server.TryCaptureCheckpoint(
            WorldAuthorityHostRowCheckpoint.Empty,
            out _,
            out _
        ));
        fixture.Step();
        Assert.Equal(
            actual: observed,
            expected: 1
        );
        Assert.Contains(
            collection: fixture.Server.Definition.State,
            filter: row => (row.Name.Value == "external-result")
        );
        extension.Submit(mutation: Mutation());
        extension.Dispose();
        fixture.Step();
        Assert.Equal(
            actual: observed,
            expected: 1
        );
    }
    [Fact]
    public async Task CrashAfterServiceSuccessBeforeOutcomePersistenceLeavesAReconciliableClaim() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var storage = new FailOutcomeStore();
        var provider = new Provider();
        var dispatcher = Dispatcher(
            extension: extension,
            journal: Journal(store: storage),
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "cause",
            TestContext.Current.CancellationToken
        );
        await Assert.ThrowsAsync<IOException>(testCode: () => dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        ).AsTask());
        Assert.Equal(
            WorldExternalOperationStatus.Dispatching,
            Assert.Single(collection: await Journal(store: storage).ReadAsync(cancellationToken: TestContext.Current.CancellationToken)).Status
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
        var restarted = Dispatcher(
            extension: extension,
            journal: Journal(store: storage),
            provider: provider
        );

        Assert.Equal(
            WorldExternalOperationStatus.Dispatching,
            (await restarted.DispatchAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restarted.ReconcileAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
    }
    [Fact]
    public async Task FailedReconciliationPreservesDurableContinuationAcrossProviderRestart() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var store = new FakeObjectBlobStore();
        var provider = new Provider { ExecutionResult = "opaque-poll-reference", ExecutionStatus = WorldExternalOperationStatus.Running, LosePoll = true };
        var dispatcher = Dispatcher(
            extension: extension,
            journal: Journal(store: store),
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "private-cause",
            TestContext.Current.CancellationToken
        );
        await dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        );
        var unknown = await dispatcher.ReconcileAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            unknown.Status
        );
        Assert.Equal(
            "opaque-poll-reference",
            unknown.Result
        );
        Assert.Equal(
            new(
                Result: "opaque-poll-reference",
                Status: WorldExternalOperationStatus.Running
            ),
            provider.Previous
        );

        var restartedProvider = new Provider();

        await Dispatcher(
            extension: extension,
            journal: Journal(store: store),
            provider: restartedProvider
        ).ReconcileAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            new(
                Result: "opaque-poll-reference",
                Status: WorldExternalOperationStatus.Unknown
            ),
            restartedProvider.Previous
        );
        Assert.Equal(
            actual: restartedProvider.Executions,
            expected: 0
        );
    }
    [Fact]
    public async Task JournalCapacityRefusesBeforeDispatch_AndRealDirectoryStorageRoundTrips() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-extension-{Guid.NewGuid():N}"
        );
        var address = new ObjectBlobAddress(
            Guid.NewGuid(),
            "operations.json"
        );
        var journal = new WorldExternalOperationJournal(
            PuckStorageTestComposition.BuildStore(),
            new DirectoryObjectStorageTarget(path),
            address,
            1,
            16384,
            4
        );
        var provider = new Provider();
        var dispatcher = Dispatcher(
            extension: extension,
            journal: journal,
            provider: provider
        );

        try {
            await dispatcher.CommitAsync(
                Request,
                "cause",
                TestContext.Current.CancellationToken
            );
            await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => dispatcher.CommitAsync(
                Request with { Id = "another" },
                "cause",
                TestContext.Current.CancellationToken
            ).AsTask());
            Assert.Equal(
                actual: provider.Executions,
                expected: 0
            );
            Assert.Equal(
                WorldExternalOperationStatus.Succeeded,
                (await dispatcher.DispatchAsync(
                    Request.Id,
                    TestContext.Current.CancellationToken
                )).Status
            );
        } finally {
            if (Directory.Exists(path: path)) { Directory.Delete(
                path,
                recursive: true
            ); }
        }
    }
    [InlineData(WorldExternalOperationStatus.Succeeded)]
    [InlineData(WorldExternalOperationStatus.Failed)]
    [InlineData(WorldExternalOperationStatus.Running)]
    [Theory]
    public async Task LateExecutionResponseWinsOverConcurrentUncertainty(WorldExternalOperationStatus status) {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var journal = Journal(store: new FakeObjectBlobStore());
        var provider = new Provider { Finish = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously), ExecutionStatus = status, ExecutionResult = "receipt" };
        var dispatcher = Dispatcher(
            extension: extension,
            journal: journal,
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "cause",
            TestContext.Current.CancellationToken
        );
        var running = dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        ).AsTask();

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            (await dispatcher.ReconcileAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        provider.Finish.SetResult();
        var result = await running;

        Assert.Equal(
            status,
            result.Status
        );
        Assert.Equal(
            "receipt",
            result.Result
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
    }
    [Fact]
    public async Task LostResponseIsUnknown_AndRecoveryReconcilesWithoutExecutingAgain() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var store = new FakeObjectBlobStore();
        var provider = new Provider { LoseResponse = true };
        var dispatcher = Dispatcher(
            extension: extension,
            journal: Journal(store: store),
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "cause",
            TestContext.Current.CancellationToken
        );
        var unknown = await dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            unknown.Status
        );
        Assert.Equal(
            nameof(IOException),
            unknown.Result
        );
        Assert.DoesNotContain(
            "secret",
            unknown.Result
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );

        var restarted = Dispatcher(
            extension: extension,
            journal: Journal(store: store),
            provider: provider
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            (await restarted.DispatchAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restarted.ReconcileAsync(
                Request.Id,
                TestContext.Current.CancellationToken
            )).Status
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 1
        );
        Assert.Equal(
            actual: provider.Reconciliations,
            expected: 1
        );
    }
    [Fact]
    public void ManifestCanRequestOnePlacementWithoutRequestingTheWholeSection() {
        using var fixture = Fixtures.FreshServer();
        using var extension = new WorldRecordedExtension(
            fixture.Server,
            WorldPrincipal.Console,
            [new(
                    Capability: WorldCapability.Mutate,
                    Subject: GrantSubject.Placement(id: "creature")
                )],
            1
        );

        extension.Submit(mutation: new WorldMutation.RemovePlacement(
            extension.Principal,
            "creature"
        ));
        Assert.Equal(
            1,
            extension.PendingCount
        );
        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: new WorldMutation.RemovePlacement(
            extension.Principal,
            "another"
        )));
        fixture.Server.SuppressRecordedExtensions();
        Assert.Equal(
            0,
            extension.PendingCount
        );
    }
    [Fact]
    public void ObservationRequiresBothManifestAndAuthority() {
        using var fixture = Fixtures.FreshServer();
        var query = new WorldQuery.StateObservations();
        using var noRequest = Extension(server: fixture.Server);

        Assert.Throws<InvalidOperationException>(testCode: () => noRequest.Observe(query: query));
        using var denied = new WorldRecordedExtension(
            fixture.Server,
            WorldPrincipal.Addon(name: "observer"),
            [new(
                    Capability: WorldCapability.Observe,
                    Subject: query.ObservationSubject()
                )],
            1
        );

        Assert.True(condition: denied.Observe(query: query).Refused);
        using var allowed = new WorldRecordedExtension(
            fixture.Server,
            WorldPrincipal.Console,
            [new(
                    Capability: WorldCapability.Observe,
                    Subject: query.ObservationSubject()
                )],
            1
        );

        Assert.False(condition: allowed.Observe(query: query).Refused);
    }
    [Fact]
    public async Task RebindingANameCannotRedirectACommittedOperation() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var journal = Journal(store: new FakeObjectBlobStore());
        var provider = new Provider();
        var dispatcher = Dispatcher(
            extension: extension,
            journal: journal,
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "cause",
            TestContext.Current.CancellationToken
        );
        provider.Identity = "replacement-resource";
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        ).AsTask());
        Assert.Equal(
            actual: provider.Executions,
            expected: 0
        );
        Assert.Equal(
            WorldExternalOperationStatus.Pending,
            Assert.Single(collection: await journal.ReadAsync(cancellationToken: TestContext.Current.CancellationToken)).Status
        );
    }
    [Fact]
    public void RecordedMutationUsesTheRealTape_AndReplayNeedsNoProvider() {
        Fixtures.SkipIfReplayDirectoryUnwritable();
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var tape = new WorldReplayTape(
            fixture.Server,
            fixture.Server.Profiles,
            new LoopbackTransport(server: fixture.Server),
            [],
            Fixtures.MachineHostFactory,
            static (_, server) => {
                Assert.Throws<InvalidOperationException>(testCode: () => Extension(server: server));
                Assert.Throws<InvalidOperationException>(testCode: server.StartRecordedExtensionEpoch);
                return new NullAddonHost();
            }
        );

        Assert.True(
            condition: tape.TryBeginRecording(
                name: $"recorded-extension-{Guid.NewGuid():N}",
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        Assert.Throws<InvalidOperationException>(testCode: () => tape.CaptureExternalOperationCause());
        extension.Submit(mutation: Mutation());
        fixture.Step();
        tape.NoteTick();
        var cause = tape.CaptureExternalOperationCause();
        using var prefix = new MemoryStream(buffer: Convert.FromBase64String(s: cause["puck-replay:".Length..]));

        Assert.Single(collection: WorldReplaySnapshot.Read(stream: prefix).Ticks);
        Assert.Equal(
            WorldReplayMode.Recording,
            tape.Mode
        );
        var stopped = tape.StopRecording();

        Assert.Null(@object: stopped.VerifyFault);
        Assert.True(condition: stopped.Verdict!.Value.Match);
        using var stream = File.OpenRead(path: stopped.Path);
        var recorded = WorldReplaySnapshot.Read(stream: stream);

        Assert.Single(
            collection: recorded.Ticks.SelectMany(selector: tick => tick.Authority),
            predicate: entry => (entry.GetType().Name == "Mutation")
        );
        Assert.True(
            condition: tape.TryBeginDrive(
                Path.GetFileNameWithoutExtension(path: stopped.Path),
                toTick: 1,
                forkName: null,
                documentPath: null,
                out refusal
            ),
            userMessage: refusal
        );
        Assert.Throws<InvalidOperationException>(testCode: fixture.Server.StartRecordedExtensionEpoch);
        tape.CancelDrive();
        fixture.Server.StartRecordedExtensionEpoch();
        Assert.False(condition: extension.IsActive);
    }
    [Fact]
    public async Task ReplayCannotMountOrRunRecordedProviders_AndDoesNotTouchTheJournal() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);
        var store = new FakeObjectBlobStore();
        var provider = new Provider();
        var dispatcher = Dispatcher(
            extension: extension,
            journal: Journal(store: store),
            provider: provider
        );

        await dispatcher.CommitAsync(
            Request,
            "cause",
            TestContext.Current.CancellationToken
        );
        fixture.Server.SuppressRecordedExtensions();
        var reads = store.ReadCount;

        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => dispatcher.DispatchAsync(
            Request.Id,
            TestContext.Current.CancellationToken
        ).AsTask());
        Assert.Throws<InvalidOperationException>(testCode: () => Extension(server: fixture.Server));
        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: Mutation()));
        Assert.Equal(
            reads,
            store.ReadCount
        );
        Assert.Equal(
            actual: provider.Executions,
            expected: 0
        );
        fixture.Server.StartRecordedExtensionEpoch();
        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: Mutation()));
        using var resumed = Extension(server: fixture.Server);

        resumed.Submit(mutation: Mutation());
        Assert.Equal(
            1,
            resumed.PendingCount
        );
    }
    [Fact]
    public void RequestedScopeCannotBeEscapedThroughABatchOrAnotherIdentity() {
        using var fixture = Fixtures.FreshServer();
        using var extension = Extension(server: fixture.Server);

        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: Mutation() with { Principal = WorldPrincipal.Addon(name: "other") }));
        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: new WorldMutation.Batch(
            WorldPrincipal.Console,
            [Mutation(), new WorldMutation.RemoveAddon(
                    WorldPrincipal.Console,
                    "probe"
                )]
        )));
        using var ungranted = new WorldRecordedExtension(
            fixture.Server,
            WorldPrincipal.Addon(name: "ungranted"),
            [new(
                    Capability: WorldCapability.Mutate,
                    Subject: GrantSubject.Section(section: WorldSection.State)
                )],
            8
        );
        var before = fixture.DefinitionBytes();

        ungranted.Submit(mutation: Mutation() with { Principal = ungranted.Principal });
        fixture.Step();
        Assert.Equal(
            before,
            fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void TickHostCannotAdvertiseRecordedContributionsAndStillBeReexecuted() {
        using var fixture = Fixtures.FreshServer();
        using var host = new NullAddonHost { ReplayPolicy = WorldExtensionReplayPolicy.Recorded };

        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.AttachAddons(runtime: host));
        host.ReplayPolicy = WorldExtensionReplayPolicy.Recomputed;
        fixture.Server.AttachAddons(runtime: host);
    }

    private sealed class Provider : IWorldExternalOperationProvider {
        public string Identity { get; set; } = Request.BindingIdentity;
        public WorldExternalOperationStatus ExecutionStatus = WorldExternalOperationStatus.Succeeded;
        public string ExecutionResult = "deleted";

        private bool m_deleted;

        public int Executions;
        public TaskCompletionSource? Finish;
        public bool LosePoll;
        public bool LoseResponse;
        public WorldExternalOperationResult? Previous;
        public int Reconciliations;

        public async ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Executions++;
            if (Finish is { } finish) { await finish.Task.WaitAsync(cancellationToken: cancellationToken); }
            m_deleted = true;
            if (LoseResponse) { throw new IOException(message: "response lost; secret must not be journaled"); }
            return new(
                Result: ExecutionResult,
                Status: ExecutionStatus
            );
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation, WorldExternalOperationResult previous, CancellationToken cancellationToken) {
            Reconciliations++;
            Previous = previous;
            if (LosePoll) { throw new IOException(message: "poll lost; secret must not be journaled"); }
            return ValueTask.FromResult(result: new WorldExternalOperationResult(
                Result: "observed",
                Status: (m_deleted
                ? WorldExternalOperationStatus.Succeeded
                : WorldExternalOperationStatus.Unknown)
            ));
        }
    }
    private sealed class FailOutcomeStore : IObjectBlobStore {
        private readonly FakeObjectBlobStore m_inner = new();

        private int m_writes;

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            m_inner.ListAsync(
                cancellationToken: cancellationToken,
                keyPrefix: keyPrefix,
                objectId: objectId,
                target: target
            );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) =>
            m_inner.ReadAsync(
                address: address,
                cancellationToken: cancellationToken,
                target: target
            );
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content,
            ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (++m_writes == 3) { throw new IOException(message: "simulated process loss before outcome persistence"); }
            return m_inner.WriteAsync(
                address: address,
                cancellationToken: cancellationToken,
                content: content,
                ifMatchVersion: ifMatchVersion,
                mode: mode,
                target: target
            );
        }
    }
}
