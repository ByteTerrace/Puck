using System.Runtime.CompilerServices;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Storage;
using Puck.World.Embeddings;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: Embedding connections in <see cref="WorldConfiguredExtensions"/> deliver
/// runtime text embeddings from configured providers (such as <c>embedding.fixture</c>) into
/// Vector state tables with atomic status updates, LRU caching, change detection guards, and
/// zero per-tick overhead for unconfigured worlds.
/// </summary>
public sealed class EmbeddingConnectionLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static WorldStateRow Table(string name, CellKind kind, int capacity = 32, string? space = null) => new(
        Name: CellName.Parse(candidate: name),
        Kind: kind,
        Capacity: capacity,
        Space: space,
        Visibility: new()
    );
    private static WorldDefinition Document(
        string docId = "embeddings-test",
        int reqCapacity = 32,
        int resCapacity = 32,
        int statCapacity = 32,
        string spaceName = "lore",
        int spaceDimensions = 256,
        bool includeSpace = true,
        CellKind reqKind = CellKind.Text,
        CellKind resKind = CellKind.Vector,
        CellKind statKind = CellKind.Int
    ) {
        var baseDoc = Fixtures.BuildDocument();
        IReadOnlyList<StateSpace>? spaces = (includeSpace
            ? [new StateSpace(name: CellName.Parse(candidate: spaceName), model: "puck-fixture", revision: "1", dimensions: spaceDimensions)]
            : null);

        return baseDoc with {
            DocumentId = docId,
            StateRaw = new WorldStateSection(
                Spaces: spaces,
                World: [
                    .. baseDoc.State,
                    Table(name: "requests", kind: reqKind, capacity: reqCapacity),
                    Table(capacity: resCapacity, kind: resKind, name: "results", space: (((resKind == CellKind.Vector) && includeSpace) ? spaceName : null)),
                    Table(name: "status", kind: statKind, capacity: statCapacity),
                ]
            ),
        };
    }
    private static WorldExtensionWorldRequest[] Requests() => [
        new(Capability: "observe", Subject: "state:requests"),
        new(Capability: "observe", Subject: "state:results"),
        new(Capability: "observe", Subject: "state:status"),
        new(Capability: "mutate", Subject: GrantSubject.Section(section: WorldSection.State).Describe()),
    ];
    private static WorldExtensionConfiguration Configuration(
        string world = "embeddings-test",
        string space = "lore",
        string requests = "requests",
        string results = "results",
        string status = "status",
        int maxItems = 16,
        int batchSize = 16,
        int retryTicks = 10,
        int cacheEntries = 64,
        int connectionCount = 1,
        string providerType = "embedding.fixture",
        int dimensions = 256
    ) {
        var embeddings = new List<WorldExtensionEmbeddingSettings>();

        for (var i = 0; (i < connectionCount); i++) {
            var suffix = ((connectionCount > 1) ? $"-{i}" : string.Empty);

            embeddings.Add(item: new WorldExtensionEmbeddingSettings(
                Name: $"conn{suffix}",
                Provider: "fixture",
                Client: Principal.Console.Describe(),
                Space: space,
                Requests: ((connectionCount > 1) ? $"{requests}{suffix}" : requests),
                Results: ((connectionCount > 1) ? $"{results}{suffix}" : results),
                Status: ((connectionCount > 1) ? $"{status}{suffix}" : status),
                MaximumItems: maxItems,
                BatchSize: batchSize,
                RetryTicks: retryTicks,
                CacheEntries: cacheEntries
            ));
        }

        return new WorldExtensionConfiguration(
            Schema: "puck.world.extensions.v1",
            World: world,
            Lineage: Guid.Parse(input: "00000000-0000-0000-0000-000000000001"),
            Providers: [
                new WorldExtensionProviderSettings(
                    Name: "fixture",
                    Type: providerType,
                    Settings: Json(json: $"{{\"model\":\"puck-fixture\",\"revision\":\"1\",\"dimensions\":{dimensions}}}")
                )
            ],
            Operations: [],
            Clients: [
                new WorldExtensionClientSettings(
                    Principal: Principal.Console.Describe(),
                    Operations: [],
                    Requests: Requests()
                )
            ],
            Connections: [],
            ScanEveryTicks: 1,
            Embeddings: embeddings
        );
    }
    private static PuckExtensionSet Embeddings(params WorldExtensionEmbeddingProviderType[] types) => PuckExtensionSet.Compose(extensions: [new TestExtension(
        name: "embeddings",
        register: registry => {
            foreach (var type in types) { registry.AddEmbedding(provider: type); }
        }
    )]);
    private static PuckExtensionSet FixtureTypes() => Embeddings(new WorldExtensionEmbeddingProviderType(
        Create: FixtureConfiguredEmbeddingProvider.Create,
        Type: "embedding.fixture"
    ));
    private static DirectoryObjectStorageTarget Target() => new("unused");
    private static async Task StepRuntimeAsync(WorldConfiguredExtensions runtime, WorldServer server, ulong tick) {
        server.DrainAdministrative();
        runtime.Pump(completedTick: tick);
        await runtime.FlushAsync(cancellationToken: Cancel);
        await runtime.Host.RunOnceAsync(cancellationToken: Cancel);
        server.DrainAdministrative();
    }
    private static StateCell? FindCell(WorldServer server, string row, string key) =>
        server.Definition.State.FirstOrDefault(predicate: r => (r.Name.Value == row))
            ?.Cells?.FirstOrDefault(predicate: c => (c.Key.Value == key));
    private static void UpsertRequest(WorldServer server, string key, string text) {
        using var edit = new WorldRecordedExtension(
            server,
            Principal.Console,
            [new(Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(section: WorldSection.State))],
            8
        );

        edit.Submit(mutation: new WorldMutation.UpsertStateCell(
            Principal.Console,
            "requests",
            key,
            0,
            WorldDocumentWriteKind.Set,
            Text: text
        ));
        server.DrainAdministrative();
    }
    // Composing the extensions over a fresh world booted from the document must refuse the configuration.
    private static void AssertCreateThrows<TException>(WorldExtensionConfiguration configuration, WorldDefinition document) where TException : Exception {
        using var world = Fixtures.FreshServer(definition: document);

        AssertCreateThrows<TException>(
            configuration: configuration,
            world: world
        );
    }
    // Composing the extensions over a world must refuse the configuration with the named exception type.
    private static void AssertCreateThrows<TException>(WorldExtensionConfiguration configuration, WorldFixture world) where TException : Exception => Assert.Throws<TException>(testCode: () => WorldConfiguredExtensions.Create(
        configuration: configuration,
        server: world.Server,
        store: new FakeObjectBlobStore(),
        target: Target(),
        captureCause: static () => "cause",
        extensions: FixtureTypes()
    )); private static void RemoveRequest(WorldServer server, string key) {
        using var edit = new WorldRecordedExtension(
            server,
            Principal.Console,
            [new(Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(section: WorldSection.State))],
            8
        );

        edit.Submit(mutation: new WorldMutation.RemoveStateCell(
            Principal.Console,
            "requests",
            key
        ));
        server.DrainAdministrative();
    }

    [Fact]
    public void Law1_CompositionRefusals_RejectInvalidConfigurations() {
        // 1a: Undeclared space in world definition
        AssertCreateThrows<ArgumentException>(
            configuration: Configuration(space: "lore"),
            document: Document(spaceName: "other_space")
        );

        // 1b: Space identity mismatch (provider dimensions != world space dimensions)
        AssertCreateThrows<ArgumentException>(
            configuration: Configuration(dimensions: 128),
            document: Document(spaceDimensions: 256)
        );

        // 1c: Table capacities < MaximumItems
        AssertCreateThrows<ArgumentException>(
            configuration: Configuration(maxItems: 16),
            document: Document(reqCapacity: 8)
        );

        // 1d: Exclusivity: duplicate table used across connections
        {
            var doc = Document();
            using var world = Fixtures.FreshServer(definition: doc);
            var badConfig = new WorldExtensionConfiguration(
                Schema: "puck.world.extensions.v1",
                World: "embeddings-test",
                Lineage: Guid.Parse(input: "00000000-0000-0000-0000-000000000001"),
                Providers: [
                    new WorldExtensionProviderSettings(
                        Name: "fixture",
                        Type: "embedding.fixture",
                        Settings: Json(json: "{\"model\":\"puck-fixture\",\"revision\":\"1\",\"dimensions\":256}")
                    )
                ],
                Operations: [],
                Clients: [new WorldExtensionClientSettings(Principal: Principal.Console.Describe(), Operations: [], Requests: Requests())],
                Connections: [],
                Embeddings: [
                    new WorldExtensionEmbeddingSettings(Name: "c1", Provider: "fixture", Client: Principal.Console.Describe(), Space: "lore", Requests: "requests", Results: "results", Status: "status"),
                    new WorldExtensionEmbeddingSettings(Name: "c2", Provider: "fixture", Client: Principal.Console.Describe(), Space: "lore", Requests: "requests", Results: "results", Status: "status"),
                ]
            );

            AssertCreateThrows<ArgumentException>(
                configuration: badConfig,
                world: world
            );
        }

        // 1e: Wrong CellKind: requests is not Text, or results is not Vector
        {
            var docWrongReq = Document(reqKind: CellKind.Int);
            using var world = Fixtures.FreshServer(definition: docWrongReq);

            AssertCreateThrows<InvalidOperationException>(
                configuration: Configuration(),
                world: world
            );
        }

        // 1f: Bounds: >16 connections, maxItems out of [1, 128], batchSize out of [1, 2048], retryTicks < 1
        {
            var doc = Document();
            using var world = Fixtures.FreshServer(definition: doc);

            void TryCreate(WorldExtensionConfiguration badConfig) => WorldConfiguredExtensions.Create(
                configuration: badConfig,
                server: world.Server,
                store: new FakeObjectBlobStore(),
                target: Target(),
                captureCause: static () => "cause",
                extensions: FixtureTypes()
            );

            // >16 connections
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(connectionCount: 17)));

            // maxItems bounds
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(maxItems: 0)));
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(maxItems: 129)));

            // batchSize bounds
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(batchSize: 0)));
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(batchSize: 2049)));

            // retryTicks bounds
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(retryTicks: 0)));

            // cacheEntries bounds
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(cacheEntries: -1)));
            Assert.Throws<ArgumentException>(testCode: () => TryCreate(badConfig: Configuration(cacheEntries: 65537)));
        }
    }
    [Fact]
    public async Task Law2_RequestToVectorWithStatus3InOneBatch() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var config = Configuration();

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: FixtureTypes()
        );

        UpsertRequest(server: world.Server, key: "req-1", text: "Bandits ambushed the caravan on the north road");

        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: 1);

        var resultCell = FindCell(server: world.Server, row: "results", key: "req-1");
        var statusCell = FindCell(server: world.Server, row: "status", key: "req-1");

        Assert.NotNull(@object: resultCell);
        Assert.True(condition: resultCell.Value.HasValue);
        Assert.Equal(expected: 256, actual: resultCell.Value.AsVector.Length);

        Assert.NotNull(@object: statusCell);
        Assert.Equal(expected: ((long)WorldExternalOperationStatus.Succeeded), actual: statusCell.Value.AsInt);
        Assert.Equal(expected: 3L, actual: statusCell.Value.AsInt);
    }

    private sealed class CountingEmbeddingSource(IWorldEmbeddingSource inner, StrongBox<int> counter) : IWorldEmbeddingSource {
        public EmbeddingIdentity Identity => inner.Identity;

        public async Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) {
            counter.Value++;
            return await inner.EmbedAsync(cancellationToken: cancellationToken, texts: texts);
        }
        public void Dispose() => inner.Dispose();
    }
    private sealed class CountingEmbeddingProvider(IWorldConfiguredEmbeddingProvider inner, StrongBox<int> counter) : IWorldConfiguredEmbeddingProvider {
        public IWorldEmbeddingSource BindEmbedding(JsonElement settings) =>
            new CountingEmbeddingSource(inner: inner.BindEmbedding(settings: settings), counter: counter);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task Law3_CacheHitMakesZeroProviderCalls() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var config = Configuration(cacheEntries: 64);

        var callCount = new StrongBox<int>(value: 0);
        var countingTypes = Embeddings(
            new WorldExtensionEmbeddingProviderType(
                Type: "embedding.fixture",
                Create: json => new CountingEmbeddingProvider(
                    inner: FixtureConfiguredEmbeddingProvider.Create(settings: json),
                    counter: callCount
                )
            )
        );

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: countingTypes
        );

        var text = "A stranger shared bread and water with the guards";

        // First request: misses cache, calls provider once
        UpsertRequest(server: world.Server, key: "req-1", text: text);
        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: 1);

        Assert.Equal(actual: callCount.Value, expected: 1);
        var result1 = FindCell(server: world.Server, row: "results", key: "req-1");

        Assert.NotNull(@object: result1);
        Assert.True(condition: result1.Value.HasValue);

        // Second request with identical text: answers from LRU cache, 0 provider calls
        UpsertRequest(server: world.Server, key: "req-2", text: text);
        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: 2);

        Assert.Equal(actual: callCount.Value, expected: 1);
        var result2 = FindCell(server: world.Server, row: "results", key: "req-2");

        Assert.NotNull(@object: result2);
        Assert.True(condition: result2.Value.HasValue);
        Assert.True(condition: result1.Value.AsVector.Span.SequenceEqual(other: result2.Value.AsVector.Span));
    }

    private sealed class DelayingEmbeddingSource(IWorldEmbeddingSource inner, TaskCompletionSource<bool> tcs) : IWorldEmbeddingSource {
        public EmbeddingIdentity Identity => inner.Identity;

        public async Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) {
            await tcs.Task.WaitAsync(cancellationToken: cancellationToken);
            return await inner.EmbedAsync(cancellationToken: cancellationToken, texts: texts);
        }
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task Law4_ChangedRequestTextIsNeverAnsweredStale_ExpectedCellsGuard() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var config = Configuration(cacheEntries: 0);

        var tcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var delayingTypes = Embeddings(
            new WorldExtensionEmbeddingProviderType(
                Type: "embedding.fixture",
                Create: json => {
                    var fixture = FixtureConfiguredEmbeddingProvider.Create(settings: json);

                    return new DelegatingConfiguredEmbeddingProvider(
                        bind: s => new DelayingEmbeddingSource(inner: fixture.BindEmbedding(settings: s), tcs: tcs),
                        dispose: fixture.Dispose
                    );
                }
            )
        );

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: delayingTypes
        );

        // Step 1: Add initial request text
        UpsertRequest(server: world.Server, key: "req-1", text: "Initial text");

        // Step 2: Pump runtime — selects req-1 with "Initial text" and launches EmbedAsync which awaits tcs
        runtime.Pump(completedTick: 1);

        // Step 3: While EmbedAsync is in flight, mutate req-1 in the server to "Updated text"
        UpsertRequest(server: world.Server, key: "req-1", text: "Updated text");

        // Step 4: Unblock EmbedAsync
        tcs.SetResult(result: true);

        // Step 5: FlushAsync finishes in-flight task and submits batch guarded by ExpectedCells ("Initial text")
        await runtime.FlushAsync(cancellationToken: Cancel);
        await runtime.Host.RunOnceAsync(cancellationToken: Cancel);
        world.Server.DrainAdministrative();

        // The submission for "Initial text" failed the ExpectedCells check because req-1 was changed to "Updated text".
        // Therefore results table must NOT have the vector for "Initial text".
        // On next pump, it selects the updated text and embeds it.
        var nextTcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        nextTcs.SetResult(result: true);
        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: 2);

        var resultCell = FindCell(server: world.Server, row: "results", key: "req-1");
        var statusCell = FindCell(server: world.Server, row: "status", key: "req-1");

        Assert.NotNull(@object: resultCell);
        Assert.True(condition: resultCell.Value.HasValue);
        Assert.NotNull(@object: statusCell);
        Assert.Equal(expected: 3L, actual: statusCell.Value.AsInt);
    }

    private sealed class DelegatingConfiguredEmbeddingProvider(Func<JsonElement, IWorldEmbeddingSource> bind, Action dispose) : IWorldConfiguredEmbeddingProvider {
        public IWorldEmbeddingSource BindEmbedding(JsonElement settings) => bind(settings);
        public void Dispose() => dispose();
    }
    private sealed class RefusingEmbeddingSource(EmbeddingIdentity identity, StrongBox<int> callCounter) : IWorldEmbeddingSource {
        public EmbeddingIdentity Identity => identity;

        public Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) {
            callCounter.Value++;
            var answers = texts.Select(selector: static _ => new EmbeddingAnswer(Refusal: "Moderation refusal", Vector: null)).ToList();

            return Task.FromResult<IReadOnlyList<EmbeddingAnswer>>(result: answers);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Law5_RefusalSetsStatus4AndWaitsRetryTicks() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var retryTicks = 5;
        var config = Configuration(retryTicks: retryTicks, cacheEntries: 0);

        var callCount = new StrongBox<int>(value: 0);
        var refusingTypes = Embeddings(
            new WorldExtensionEmbeddingProviderType(
                Type: "embedding.fixture",
                Create: _ => new DelegatingConfiguredEmbeddingProvider(
                    bind: _ => new RefusingEmbeddingSource(
                        identity: new EmbeddingIdentity(Dimensions: 256, Model: "puck-fixture", Revision: "1"),
                        callCounter: callCount
                    ),
                    dispose: static () => { }
                )
            )
        );

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: refusingTypes
        );

        UpsertRequest(server: world.Server, key: "req-1", text: "Refuse me");

        // Tick 1: provider called, returns refusal -> status becomes 4
        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: 1);

        Assert.Equal(actual: callCount.Value, expected: 1);
        var statusCell = FindCell(server: world.Server, row: "status", key: "req-1");

        Assert.NotNull(@object: statusCell);
        Assert.Equal(expected: 4L, actual: statusCell.Value.AsInt);
        Assert.Null(@object: FindCell(server: world.Server, row: "results", key: "req-1"));

        // Ticks 2 through 5: within retryTicks, provider must NOT be called again
        for (ulong tick = 2; (tick < (1 + ((ulong)retryTicks))); tick++) {
            await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: tick);
            Assert.Equal(actual: callCount.Value, expected: 1);
        }

        // Tick 6: at (1 + retryTicks), provider is retried
        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: (1 + ((ulong)retryTicks)));
        Assert.Equal(actual: callCount.Value, expected: 2);
    }
    [Fact]
    public async Task Law6_RemovingRequestCellStopsOngoingWorkCleanly() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var config = Configuration(cacheEntries: 0);

        var tcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var delayingTypes = Embeddings(
            new WorldExtensionEmbeddingProviderType(
                Type: "embedding.fixture",
                Create: json => {
                    var fixture = FixtureConfiguredEmbeddingProvider.Create(settings: json);

                    return new DelegatingConfiguredEmbeddingProvider(
                        bind: s => new DelayingEmbeddingSource(inner: fixture.BindEmbedding(settings: s), tcs: tcs),
                        dispose: fixture.Dispose
                    );
                }
            )
        );

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: delayingTypes
        );

        UpsertRequest(server: world.Server, key: "req-1", text: "To be removed");
        runtime.Pump(completedTick: 1);

        // Remove the cell while call is in flight
        RemoveRequest(server: world.Server, key: "req-1");

        // Complete call
        tcs.SetResult(result: true);
        await runtime.FlushAsync(cancellationToken: Cancel);
        await runtime.Host.RunOnceAsync(cancellationToken: Cancel);
        world.Server.DrainAdministrative();

        // ExpectedCells guard dropped the batch because req-1 was removed
        Assert.Null(@object: FindCell(server: world.Server, row: "results", key: "req-1"));
    }
    [Fact]
    public async Task Law7_ReplayReproducesVectorsWithoutProviderWithIdenticalStateHash() {
        var doc = Document();
        using var liveWorld = Fixtures.FreshServer(definition: doc);
        var config = Configuration();

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: liveWorld.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: FixtureTypes()
        );

        UpsertRequest(server: liveWorld.Server, key: "msg-1", text: "Alpha text");
        UpsertRequest(server: liveWorld.Server, key: "msg-2", text: "Beta text");

        await StepRuntimeAsync(runtime: runtime, server: liveWorld.Server, tick: 1);

        // Live server has both results and statuses
        var liveHash = WorldStateHashComposition.HashAuthoritative(server: liveWorld.Server, tick: 1);

        // Checkpoint capture from live server and restoration into fresh server (without provider/extensions)
        Assert.True(condition: liveWorld.Server.TryCaptureCheckpoint(
            hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            checkpoint: out var checkpoint,
            reason: out var reason
        ), userMessage: reason);
        Assert.NotNull(@object: checkpoint);

        using var replayedWorld = Fixtures.FreshServer(definition: doc);

        replayedWorld.Server.RestoreCheckpoint(checkpoint: checkpoint);

        var replayedHash = WorldStateHashComposition.HashAuthoritative(server: replayedWorld.Server, tick: 1);

        Assert.Equal(actual: replayedHash, expected: liveHash);
    }
    [Fact]
    public async Task Law8_LiveReplayRevokesTheClient() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var config = Configuration();

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: FixtureTypes()
        );

        var client = runtime.Client(principal: Principal.Console);

        // Live replay suppresses recorded extensions
        world.Server.SuppressRecordedExtensions();

        // Attempting to observe through the revoked client throws InvalidOperationException
        Assert.Throws<InvalidOperationException>(testCode: () => client.Observe(
            query: new WorldQuery.StateObservations(Row: "requests")
        ));
    }

    private sealed class ChatTestCommandModule(WorldServer server) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                name: "chat.say",
                description: "Appends text to requests table.",
                handler: (context, args) => {
                    var text = args.Tail(start: 0);
                    using var edit = new WorldRecordedExtension(
                        server,
                        Principal.Console,
                        [new(Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(section: WorldSection.State))],
                        8
                    );

                    edit.Submit(mutation: new WorldMutation.UpsertStateCell(
                        Principal.Console,
                        "requests",
                        "chat-msg-1",
                        0,
                        WorldDocumentWriteKind.Set,
                        Text: text
                    ));
                    server.DrainAdministrative();
                    return new CommandResult(Output: $"[chat.say: {text}]");
                },
                routing: CommandRouting.Immediate
            );
        }
    }

    [Fact]
    public async Task Law9_ChatCommandModuleLineIsEmbeddedIntoVectorTable() {
        var doc = Document();
        using var world = Fixtures.FreshServer(definition: doc);
        var config = Configuration();

        await using var runtime = WorldConfiguredExtensions.Create(
            configuration: config,
            server: world.Server,
            store: new FakeObjectBlobStore(),
            target: Target(),
            captureCause: static () => "cause",
            extensions: FixtureTypes()
        );

        // Execute chat command to speak line into requests table
        var chatModule = new ChatTestCommandModule(server: world.Server);
        var registry = new CommandRegistry(modules: [chatModule]);
        var source = new TextCommandSource(registry: registry);
        using var session = source.CreateSession(principal: Principal.Console);

        session.Enqueue(line: "chat.say Where is the road north?");
        source.Collect();

        // Step runtime to pump embedding connection
        await StepRuntimeAsync(runtime: runtime, server: world.Server, tick: 1);

        var resultCell = FindCell(server: world.Server, row: "results", key: "chat-msg-1");
        var statusCell = FindCell(server: world.Server, row: "status", key: "chat-msg-1");

        Assert.NotNull(@object: resultCell);
        Assert.True(condition: resultCell.Value.HasValue);
        Assert.Equal(expected: 256, actual: resultCell.Value.AsVector.Length);
        Assert.NotNull(@object: statusCell);
        Assert.Equal(expected: 3L, actual: statusCell.Value.AsInt);
    }
}
