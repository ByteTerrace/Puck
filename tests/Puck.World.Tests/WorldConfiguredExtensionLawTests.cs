using System.Text;
using System.Text.Json;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldConfiguredExtensionLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DataCompositionMixesProvidersProjectsThroughAuthorityAndRecoversWithoutResending() {
        using var world = Fixtures.FreshServer(Document());
        var store = new FakeObjectBlobStore();
        var providers = new List<Provider>();
        var types = Types(providers);
        var configuration = Configuration();
        var pumpThread = 0;
        await using (var runtime = WorldConfiguredExtensions.Create(configuration, types, world.Server, store, Target(), () => {
            pumpThread = Environment.CurrentManagedThreadId;
            return world.Server.CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint.Empty);
        })) {
            var initialThread = Environment.CurrentManagedThreadId;
            runtime.Pump(1);
            Assert.Equal(initialThread, pumpThread);
            await Complete(runtime, world.Server);
            Assert.Equal(new[] { 1, 1 }, providers.Select(provider => provider.Executions));
            Assert.Equal((long)WorldExternalOperationStatus.Succeeded, Value(world.Server, "status-a"));
            Assert.Equal((long)WorldExternalOperationStatus.Succeeded, Value(world.Server, "status-b"));
        }
        Assert.All(providers, provider => Assert.True(provider.Disposed));
        providers.Clear();
        var renamed = configuration with { Connections = configuration.Connections.Select(connection => connection with { Name = "renamed-" + connection.Name }).ToArray() };
        await using var restored = WorldConfiguredExtensions.Create(renamed, types, world.Server, store, Target(),
            () => world.Server.CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint.Empty));
        await Complete(restored, world.Server);
        Assert.All(providers, provider => Assert.Equal(0, provider.Executions));
    }

    [Fact]
    public async Task ChangedRequestKeyRefusesWithoutBlockingAnotherConnection() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        await using var runtime = WorldConfiguredExtensions.Create(Configuration(), Types(providers), world.Server,
            new FakeObjectBlobStore(), Target(), () => world.Server.CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint.Empty));
        await Complete(runtime, world.Server);
        using var edit = new WorldRecordedExtension(world.Server, WorldPrincipal.Console,
            [new(WorldCapability.Mutate, GrantSubject.Section(WorldSection.State))], 8);
        edit.Submit(new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "requests-a", "incarnation-1", 0,
            WorldDocumentWriteKind.Set, Text: "{\"changed\":true}"));
        edit.Submit(new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "requests-b", "incarnation-2", 0,
            WorldDocumentWriteKind.Set, Text: "{}"));
        world.Server.DrainAdministrative();
        for (ulong tick = 300; tick < 310; tick++) {
            runtime.Pump(tick);
            await runtime.FlushAsync(Cancel);
            await runtime.Host.RunOnceAsync(Cancel);
            world.Server.DrainAdministrative();
        }
        Assert.Equal(1, providers[0].Executions);
        Assert.Equal(2, providers[1].Executions);
        Assert.Contains("immutable", runtime.LastFailure);
    }

    [Fact]
    public void ConfigurationRejectsUnknownAndDuplicateMembersAtEveryDepth() {
        const string json = """
            {"schema":"puck.world.extensions.v1","world":"extensions-test","lineage":"00000000-0000-0000-0000-000000000001",
             "providers":[],"operations":[],"clients":[],"connections":[]}
            """;
        Assert.Equal("extensions-test", WorldExtensionConfiguration.Parse(Encoding.UTF8.GetBytes(json)).World);
        Assert.Throws<JsonException>(() => WorldExtensionConfiguration.Parse(Encoding.UTF8.GetBytes(json.Replace("\"providers\":[]", "\"providers\":[],\"dll\":\"evil.dll\""))));
        Assert.Throws<JsonException>(() => WorldExtensionConfiguration.Parse(Encoding.UTF8.GetBytes(json.Replace("\"providers\":[]", "\"providers\":[],\"providers\":[]"))));
        Assert.Throws<JsonException>(() => WorldExtensionConfiguration.Parse(Encoding.UTF8.GetBytes(json.Replace("\"providers\":[]",
            "\"providers\":[{\"name\":\"test\",\"type\":\"fake\",\"settings\":{\"x\":1,\"x\":2}}]"))));
    }

    [Fact]
    public void UninstalledTypesAndWrongWorldsCannotAcquireProviders() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        var types = Types(providers);
        Assert.Throws<ArgumentException>(() => WorldConfiguredExtensions.Create(Configuration() with { World = "another-world" },
            types, world.Server, new FakeObjectBlobStore(), Target(), () => "unused"));
        Assert.Throws<ArgumentException>(() => WorldConfiguredExtensions.Create(Configuration() with {
            Providers = [new("first", "../evil.dll", Json("{}"))],
        }, types, world.Server, new FakeObjectBlobStore(), Target(), () => "unused"));
        Assert.Empty(providers);
    }

    [Fact]
    public void FailedCompositionDisposesProvidersAndDoesNotGrantWorldAuthority() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        var configuration = Configuration() with {
            Clients = [new("addon:untrusted", ["delete-a", "delete-b"], Requests())],
            Connections = [new("a", "addon:untrusted", "delete-a", "requests-a", "status-a")],
        };
        Assert.Throws<InvalidOperationException>(() => WorldConfiguredExtensions.Create(configuration, Types(providers),
            world.Server, new FakeObjectBlobStore(), Target(), () => "unused"));
        Assert.Equal(2, providers.Count);
        Assert.All(providers, provider => { Assert.True(provider.Disposed); Assert.Equal(0, provider.Executions); });
    }

    [Fact]
    public async Task ReplayRevokesConnectionsAndRetainedClients() {
        using var world = Fixtures.FreshServer(Document());
        var providers = new List<Provider>();
        await using var runtime = WorldConfiguredExtensions.Create(Configuration(), Types(providers), world.Server,
            new FakeObjectBlobStore(), Target(), () => "unused");
        var client = runtime.Client(WorldPrincipal.Console);
        world.Server.SuppressRecordedExtensions();
        runtime.Pump(1);
        await runtime.Host.RunOnceAsync(Cancel);
        Assert.All(providers, provider => Assert.Equal(0, provider.Executions));
        Assert.Throws<ObjectDisposedException>(() => client.Discover());
    }

    private static async Task Complete(WorldConfiguredExtensions runtime, WorldServer server) {
        for (ulong tick = 2; tick < 300; tick++) {
            server.DrainAdministrative();
            runtime.Pump(tick);
            await runtime.FlushAsync(Cancel);
            await runtime.Host.RunOnceAsync(Cancel);
            server.DrainAdministrative();
            if (Value(server, "status-a") == (long)WorldExternalOperationStatus.Succeeded &&
                Value(server, "status-b") == (long)WorldExternalOperationStatus.Succeeded) { return; }
        }
        Assert.Fail("Connections did not finish: " + runtime.LastFailure);
    }

    private static long? Value(WorldServer server, string row) => server.Definition.State.First(r => r.Name.Value == row)
        .Cells?.FirstOrDefault(cell => cell.Key.Value == "incarnation-1")?.Value;
    private static DirectoryObjectStorageTarget Target() => new("unused");
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static WorldExtensionRegistry<WorldExtensionProviderType> Types(List<Provider> providers) => new([
        new("fake", settings => { var provider = new Provider(settings.GetProperty("identity").GetString()!); providers.Add(provider); return provider; }),
    ], type => type.Type);
    private static WorldExtensionWorldRequest[] Requests() => [
        new("observe", "state:requests-a"), new("observe", "state:requests-b"),
        new("observe", "state:status-a"), new("observe", "state:status-b"),
        new("mutate", GrantSubject.Section(WorldSection.State).Describe()),
    ];
    private static WorldExtensionConfiguration Configuration() => new("puck.world.extensions.v1", "extensions-test",
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        [new("first", "fake", Json("{\"identity\":\"first\"}")), new("second", "fake", Json("{\"identity\":\"second\"}"))],
        [new("delete-a", "first", "First service", Json("{}")), new("delete-b", "second", "Second service", Json("{}"))],
        [new(WorldPrincipal.Console.Describe(), ["delete-a", "delete-b"], Requests())],
        [new("a", WorldPrincipal.Console.Describe(), "delete-a", "requests-a", "status-a"),
         new("b", WorldPrincipal.Console.Describe(), "delete-b", "requests-b", "status-b")], ScanEveryTicks: 1);
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();
        return document with { DocumentId = "extensions-test", StateRaw = new(World: [.. document.State,
            Table("requests-a", CellKind.Text, true), Table("requests-b", CellKind.Text, true),
            Table("status-a", CellKind.Int, false), Table("status-b", CellKind.Int, false)]) };
    }
    private static WorldStateRow Table(string name, CellKind kind, bool request) => new(CellName.Parse(name), kind,
        Capacity: 8, Cells: request ? [new(CellName.Parse("incarnation-1"), Text: "{}")] : [], Visibility: new());

    private sealed class Provider(string identity) : IWorldConfiguredProvider, IWorldExternalOperationProvider {
        public int Executions;
        public bool Disposed;
        public string Identity => identity;
        public WorldExtensionOperation Bind(string name, string description, JsonElement settings) =>
            new(new(name, description, "{}"), this, (id, input) => new(id, name, Identity, input));
        public ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Interlocked.Increment(ref Executions);
            return ValueTask.FromResult(new WorldExternalOperationResult(WorldExternalOperationStatus.Succeeded, "complete"));
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation,
            WorldExternalOperationResult previous, CancellationToken cancellationToken) => throw new InvalidOperationException("No resend expected.");
        public void Dispose() => Disposed = true;
    }
}
