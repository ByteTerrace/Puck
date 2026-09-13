using System.Text;
using System.Text.Json;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldConfiguredExtensionLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task Complete(WorldConfiguredExtensions runtime, WorldServer server) {
        for (ulong tick = 2; (tick < 300); tick++) {
            server.DrainAdministrative();
            runtime.Pump(completedTick: tick);
            await runtime.FlushAsync(cancellationToken: Cancel);
            await runtime.Host.RunOnceAsync(cancellationToken: Cancel);
            server.DrainAdministrative();
            if (
                (Value(
                row: "status-a",
                server: server
            ) == ((long)WorldExternalOperationStatus.Succeeded)) &&
                (Value(
                row: "status-b",
                server: server
            ) == ((long)WorldExternalOperationStatus.Succeeded))
            ) { return; }
        }
        Assert.Fail(message: ("Connections did not finish: " + runtime.LastFailure));
    }
    private static WorldExtensionConfiguration Configuration() => new(
        "puck.world.extensions.v1",
        "extensions-test",
        Guid.Parse(input: "00000000-0000-0000-0000-000000000001"),
        [new(
                "first",
                "fake",
                Json(json: "{\"identity\":\"first\"}")
            ), new(
                "second",
                "fake",
                Json(json: "{\"identity\":\"second\"}")
            )],
        [new(
                "delete-a",
                "first",
                "First service",
                Json(json: "{}")
            ), new(
                "delete-b",
                "second",
                "Second service",
                Json(json: "{}")
            )],
        [new(
                WorldPrincipal.Console.Describe(),
                ["delete-a", "delete-b"],
                Requests()
            )],
        [new(
                "a",
                WorldPrincipal.Console.Describe(),
                "delete-a",
                "requests-a",
                "status-a"
            ),
         new(
                "b",
                WorldPrincipal.Console.Describe(),
                "delete-b",
                "requests-b",
                "status-b"
            )],
        ScanEveryTicks: 1
    );
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();

        return document with {
            DocumentId = "extensions-test",
            StateRaw = new(World: [.. document.State,
            Table(
                kind: CellKind.Text,
                name: "requests-a",
                request: true
            ), Table(
                kind: CellKind.Text,
                name: "requests-b",
                request: true
            ),
            Table(
                kind: CellKind.Int,
                name: "status-a",
                request: false
            ), Table(
                kind: CellKind.Int,
                name: "status-b",
                request: false
            )]),
        };
    }
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static WorldExtensionWorldRequest[] Requests() => [
        new(
            Capability: "observe",
            Subject: "state:requests-a"
        ), new(
            Capability: "observe",
            Subject: "state:requests-b"
        ),
        new(
            Capability: "observe",
            Subject: "state:status-a"
        ), new(
            Capability: "observe",
            Subject: "state:status-b"
        ),
        new(
            Capability: "mutate",
            Subject: GrantSubject.Section(section: WorldSection.State).Describe()
        ),
    ];
    private static WorldStateRow Table(string name, CellKind kind, bool request) => new(
        CellName.Parse(candidate: name),
        kind,
        Capacity: 8,
        Cells: (request
        ? [new(
                    CellName.Parse(candidate: "incarnation-1"),
                    Text: "{}"
                )]
        : []),
        Visibility: new()
    );
    private static DirectoryObjectStorageTarget Target() => new("unused");
    private static WorldExtensionRegistry<WorldExtensionProviderType> Types(List<Provider> providers) => new(
        extensions: [
        new(
                "fake",
                settings => { var provider = new Provider(identity: settings.GetProperty(propertyName: "identity").GetString()!); providers.Add(item: provider); return provider; }
            ),
    ],
        keyOf: type => type.Type
    );
    private static long? Value(WorldServer server, string row) => server.Definition.State.First(predicate: r => (r.Name.Value == row))
        .Cells?.FirstOrDefault(predicate: cell => (cell.Key.Value == "incarnation-1"))?.Value;

    [Fact]
    public async Task ChangedRequestKeyRefusesWithoutBlockingAnotherConnection() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        await using var runtime = WorldConfiguredExtensions.Create(
            Configuration(),
            Types(providers: providers),
            world.Server,
            new FakeObjectBlobStore(),
            Target(),
            () => world.Server.CaptureExternalOperationCause(hostRow: WorldAuthorityHostRowCheckpoint.Empty)
        );

        await Complete(
            runtime: runtime,
            server: world.Server
        );
        using var edit = new WorldRecordedExtension(
            world.Server,
            WorldPrincipal.Console,
            [new(
                    Capability: WorldCapability.Mutate,
                    Subject: GrantSubject.Section(section: WorldSection.State)
                )],
            8
        );

        edit.Submit(mutation: new WorldMutation.UpsertStateCell(
            WorldPrincipal.Console,
            "requests-a",
            "incarnation-1",
            0,
            WorldDocumentWriteKind.Set,
            Text: "{\"changed\":true}"
        ));
        edit.Submit(mutation: new WorldMutation.UpsertStateCell(
            WorldPrincipal.Console,
            "requests-b",
            "incarnation-2",
            0,
            WorldDocumentWriteKind.Set,
            Text: "{}"
        ));
        world.Server.DrainAdministrative();
        for (ulong tick = 300; (tick < 310); tick++) {
            runtime.Pump(completedTick: tick);
            await runtime.FlushAsync(cancellationToken: Cancel);
            await runtime.Host.RunOnceAsync(cancellationToken: Cancel);
            world.Server.DrainAdministrative();
        }
        Assert.Equal(
            1,
            providers[0].Executions
        );
        Assert.Equal(
            2,
            providers[1].Executions
        );
        Assert.Contains(
            "immutable",
            runtime.LastFailure
        );
    }
    [Fact]
    public void ConfigurationRejectsUnknownAndDuplicateMembersAtEveryDepth() {
        const string Json = """
            {"schema":"puck.world.extensions.v1","world":"extensions-test","lineage":"00000000-0000-0000-0000-000000000001",
             "providers":[],"operations":[],"clients":[],"connections":[]}
            """;

        Assert.Equal(
            "extensions-test",
            WorldExtensionConfiguration.Parse(utf8: Encoding.UTF8.GetBytes(s: Json)).World
        );
        Assert.Throws<JsonException>(testCode: () => WorldExtensionConfiguration.Parse(utf8: Encoding.UTF8.GetBytes(s: Json.Replace(
            newValue: "\"providers\":[],\"dll\":\"evil.dll\"",
            oldValue: "\"providers\":[]"
        ))));
        Assert.Throws<JsonException>(testCode: () => WorldExtensionConfiguration.Parse(utf8: Encoding.UTF8.GetBytes(s: Json.Replace(
            newValue: "\"providers\":[],\"providers\":[]",
            oldValue: "\"providers\":[]"
        ))));
        Assert.Throws<JsonException>(testCode: () => WorldExtensionConfiguration.Parse(utf8: Encoding.UTF8.GetBytes(s: Json.Replace(
            newValue: "\"providers\":[{\"name\":\"test\",\"type\":\"fake\",\"settings\":{\"x\":1,\"x\":2}}]",
            oldValue: "\"providers\":[]"
        ))));
    }
    [Fact]
    public async Task DataCompositionMixesProvidersProjectsThroughAuthorityAndRecoversWithoutResending() {
        using var world = Fixtures.FreshServer(Document());
        var store = new FakeObjectBlobStore();
        var providers = new List<Provider>();
        var types = Types(providers: providers);
        var configuration = Configuration();
        var pumpThread = 0;

        await using (var runtime = WorldConfiguredExtensions.Create(
            configuration,
            types,
            world.Server,
            store,
            Target(),
            () => {
            pumpThread = Environment.CurrentManagedThreadId;
            return world.Server.CaptureExternalOperationCause(hostRow: WorldAuthorityHostRowCheckpoint.Empty);
        }
        )) {
            var initialThread = Environment.CurrentManagedThreadId;

            runtime.Pump(completedTick: 1);
            Assert.Equal(
                actual: pumpThread,
                expected: initialThread
            );
            await Complete(
                runtime: runtime,
                server: world.Server
            );
            Assert.Equal(
                new[] { 1, 1 },
                providers.Select(selector: provider => provider.Executions)
            );
            Assert.Equal(
                ((long)WorldExternalOperationStatus.Succeeded),
                Value(
                    world.Server,
                    "status-a"
                )
            );
            Assert.Equal(
                ((long)WorldExternalOperationStatus.Succeeded),
                Value(
                    world.Server,
                    "status-b"
                )
            );
        }
        Assert.All(
            providers,
            provider => Assert.True(condition: provider.Disposed)
        );
        providers.Clear();
        var renamed = configuration with { Connections = configuration.Connections.Select(selector: connection => connection with { Name = ("renamed-" + connection.Name) }).ToArray() };
        await using var restored = WorldConfiguredExtensions.Create(
            renamed,
            types,
            world.Server,
            store,
            Target(),
            () => world.Server.CaptureExternalOperationCause(hostRow: WorldAuthorityHostRowCheckpoint.Empty)
        );

        await Complete(
            runtime: restored,
            server: world.Server
        );
        Assert.All(
            providers,
            provider => Assert.Equal(
                actual: provider.Executions,
                expected: 0
            )
        );
    }
    [Fact]
    public void FailedCompositionDisposesProvidersAndDoesNotGrantWorldAuthority() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        var configuration = Configuration() with {
            Clients = [new(
                "addon:untrusted",
                ["delete-a", "delete-b"],
                Requests()
            )],
            Connections = [new(
                "a",
                "addon:untrusted",
                "delete-a",
                "requests-a",
                "status-a"
            )],
        };

        Assert.Throws<InvalidOperationException>(testCode: () => WorldConfiguredExtensions.Create(
            configuration,
            Types(providers: providers),
            world.Server,
            new FakeObjectBlobStore(),
            Target(),
            () => "unused"
        ));
        Assert.Equal(
            2,
            providers.Count
        );
        Assert.All(
            providers,
            provider => { Assert.True(condition: provider.Disposed); Assert.Equal(
            actual: provider.Executions,
            expected: 0
        ); }
        );
    }
    [Fact]
    public async Task ReplayRevokesConnectionsAndRetainedClients() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        await using var runtime = WorldConfiguredExtensions.Create(
            Configuration(),
            Types(providers: providers),
            world.Server,
            new FakeObjectBlobStore(),
            Target(),
            () => "unused"
        );
        var client = runtime.Client(principal: WorldPrincipal.Console);

        world.Server.SuppressRecordedExtensions();
        runtime.Pump(completedTick: 1);
        await runtime.Host.RunOnceAsync(cancellationToken: Cancel);
        Assert.All(
            providers,
            provider => Assert.Equal(
                actual: provider.Executions,
                expected: 0
            )
        );
        Assert.Throws<ObjectDisposedException>(testCode: () => client.Discover());
    }
    [Fact]
    public void UninstalledTypesAndWrongWorldsCannotAcquireProviders() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        var types = Types(providers: providers);

        Assert.Throws<ArgumentException>(testCode: () => WorldConfiguredExtensions.Create(
            Configuration() with { World = "another-world" },
            types,
            world.Server,
            new FakeObjectBlobStore(),
            Target(),
            () => "unused"
        ));
        Assert.Throws<ArgumentException>(testCode: () => WorldConfiguredExtensions.Create(
            Configuration() with {
            Providers = [new(
                    "first",
                    "../evil.dll",
                    Json(json: "{}")
                )],
        },
            types,
            world.Server,
            new FakeObjectBlobStore(),
            Target(),
            () => "unused"
        ));
        Assert.Empty(collection: providers);
    }

    private sealed class Provider(string identity) : IWorldConfiguredProvider, IWorldExternalOperationProvider {
        public bool Disposed;
        public int Executions;

        public string Identity => identity;

        public WorldExtensionOperation Bind(string name, string description, JsonElement settings) =>
            new(
                new(
                    Description: description,
                    InputSchema: "{}",
                    Name: name
                ),
                this,
                (id, input) => new(
                    id,
                    name,
                    Identity,
                    input
                )
            );
        public void Dispose() => Disposed = true;
        public ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Interlocked.Increment(location: ref Executions);
            return ValueTask.FromResult(result: new WorldExternalOperationResult(
                Result: "complete",
                Status: WorldExternalOperationStatus.Succeeded
            ));
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation,
            WorldExternalOperationResult previous, CancellationToken cancellationToken) => throw new InvalidOperationException(message: "No resend expected.");
    }
}
