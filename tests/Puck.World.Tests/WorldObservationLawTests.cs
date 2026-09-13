using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldObservationLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static StateCell Cell(WorldServer server, string rowName, string key) => server.Definition.State.First(predicate: row => (row.Name.Value == rowName))
        .Cells!.Single(predicate: cell => (cell.Key.Value == key));
    private static WorldExtensionConfiguration Configuration() => new(
        "puck.world.extensions.v1",
        "observations",
        Guid.NewGuid(),
        [new(
                "source",
                "test",
                JsonDocument.Parse("{}").RootElement.Clone()
            )],
        [],
        [new(
                "console",
                [],
                [new(
                        Capability: "observe",
                        Subject: "state:observedNames"
                    ), new(
                        Capability: "observe",
                        Subject: "state:observedScores"
                    ), new(
                        Capability: "observe",
                        Subject: "state:observedDepths"
                    ),
            new(
                        Capability: "mutate",
                        Subject: "section:state"
                    )]
            )],
        [],
        ScanEveryTicks: 1,
        Observations: [new(
                "inventory",
                "source",
                "console",
                JsonDocument.Parse("{}").RootElement.Clone(),
                new Dictionary<string, string> { ["name"] = "observedNames", ["score"] = "observedScores", ["depth"] = "observedDepths" },
                RefreshTicks: 10,
                MaximumItems: 4
            )]
    );
    private static WorldConfiguredExtensions Create(WorldServer server, Source source, WorldExtensionConfiguration? configuration = null) =>
        WorldConfiguredExtensions.Create(
            (configuration ?? Configuration()),
            new(
                extensions: [new WorldExtensionProviderType(
                        Create: _ => source,
                        Type: "test"
                    )],
                keyOf: type => type.Type
            ),
            server,
            new FakeObjectBlobStore(),
            new DirectoryObjectStorageTarget("unused"),
            () => throw new InvalidOperationException(message: "Reads must not capture effect recovery images.")
        );
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();

        return document with {
            DocumentId = "observations",
            StateRaw = new(World: [.. document.State,
            new(
                CellName.Parse(candidate: "observedNames"),
                CellKind.Text,
                Capacity: 4,
                Cells: [],
                Visibility: new()
            ),
            new(
                CellName.Parse(candidate: "observedScores"),
                CellKind.Int,
                Capacity: 4,
                Cells: [],
                Visibility: new()
            ),
            new(
                CellName.Parse(candidate: "observedDepths"),
                CellKind.Fixed,
                Capacity: 4,
                Cells: [],
                Visibility: new()
            )]),
        };
    }
    private static WorldExtensionObservationItem Item(string name, string score = "0", string depth = "0") =>
        new(
            name,
            new Dictionary<string, string> { ["name"] = name, ["score"] = score, ["depth"] = depth }
        );
    private static string[] Names(WorldServer server) => server.Definition.State.First(predicate: row => (row.Name.Value == "observedNames"))
        .Cells!.Select(selector: cell => cell.Text!).ToArray();
    private static async Task Observe(WorldConfiguredExtensions runtime, WorldServer server, ulong tick, bool fails = false) {
        runtime.Pump(completedTick: tick);
        if (fails) { await Assert.ThrowsAnyAsync<Exception>(testCode: () => runtime.FlushObservationsAsync(cancellationToken: Cancel)); } else { await runtime.FlushObservationsAsync(cancellationToken: Cancel); }
        runtime.Pump(completedTick: (tick + 1));
        server.DrainAdministrative();
        runtime.Pump(completedTick: (tick + 2));
    }

    [Fact]
    public async Task ANonNumericValueRefusesTheItemByFieldAndKeepsThePreviousProjection() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item(
                "a",
                score: "1",
                depth: "1"
            )] };
        await using var runtime = Create(
            world.Server,
            provider
        );

        await Observe(
            runtime,
            world.Server,
            1
        );
        Assert.Equal(
            1,
            Cell(
                world.Server,
                "observedScores",
                "a"
            ).Value
        );
        provider.Items = [Item(
                "a",
                score: "not-a-number",
                depth: "1"
            )];
        await Observe(
            runtime,
            world.Server,
            20
        );
        Assert.Equal(
            1,
            Cell(
                world.Server,
                "observedScores",
                "a"
            ).Value
        );
        Assert.NotNull(@object: Assert.Single(collection: runtime.Observations).Failure);
        Assert.Equal(
            "score",
            Assert.Single(collection: runtime.Observations).LastRefusedField
        );
    }
    [Fact]
    public async Task AReadThatFinishesAfterReplayCannotProjectItsResult() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Deferred = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var runtime = Create(
            world.Server,
            provider
        );

        runtime.Pump(completedTick: 1);
        await provider.Started.Task.WaitAsync(cancellationToken: Cancel);
        world.Server.SuppressRecordedExtensions();
        provider.Deferred.SetResult(result: [Item("late")]);
        await runtime.FlushObservationsAsync(cancellationToken: Cancel);
        runtime.Pump(completedTick: 2);
        world.Server.DrainAdministrative();
        Assert.Empty(collection: Names(server: world.Server));
        Assert.Equal(
            0,
            Assert.Single(collection: runtime.Observations).Submissions
        );
        Assert.Equal(
            actual: provider.Reads,
            expected: 1
        );
    }
    [Fact]
    public async Task CompleteSnapshotsProjectAtomicallyAndStayQuietOnAnUnchangedCollection() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item("b"), Item("a")] };
        await using var runtime = Create(
            world.Server,
            provider
        );

        await Observe(
            runtime,
            world.Server,
            1
        );
        Assert.Equal(
            new[] { "a", "b" },
            Names(server: world.Server)
        );
        var submissions = Assert.Single(collection: runtime.Observations).Submissions;

        Assert.Equal(
            actual: submissions,
            expected: 1
        );
        await Observe(
            runtime,
            world.Server,
            20
        );
        Assert.Equal(
            submissions,
            Assert.Single(collection: runtime.Observations).Submissions
        );
        provider.Items = [Item("b")];
        await Observe(
            runtime,
            world.Server,
            40
        );
        Assert.Equal(
            new[] { "b" },
            Names(server: world.Server)
        );
        provider.Items = [];
        await Observe(
            runtime,
            world.Server,
            60
        );
        Assert.Empty(collection: Names(server: world.Server));
        Assert.True(condition: Assert.Single(collection: runtime.Observations).Applied);
        Assert.Equal(
            "test",
            Assert.Single(collection: runtime.Observations).Kind
        );
    }
    [Fact]
    public void ConfigurationParserRefusesADeletedPlacementsMember() {
        var json = """
            {"schema":"puck.world.extensions.v1","world":"observations","lineage":"00000000-0000-0000-0000-000000000001",
             "providers":[{"name":"source","type":"test","settings":{}}],"operations":[],
             "clients":[{"principal":"console","operations":[],"requests":[
                {"capability":"observe","subject":"state:observedNames"},{"capability":"mutate","subject":"section:state"}]}],
             "connections":[],"scanEveryTicks":1,
             "observations":[{"name":"inventory","provider":"source","client":"console","settings":{},
                "fields":{"name":"observedNames"},
                "placements":{"template":"court","prefix":"observed-","columns":2,"spacingX":4,"spacingZ":4}}]}
            """;

        Assert.Throws<JsonException>(testCode: () => WorldExtensionConfiguration.Parse(utf8: System.Text.Encoding.UTF8.GetBytes(s: json)));
    }
    [Fact]
    public async Task FailedOrOversizedReadsRetainLastCompleteCollectionAndReplayRevokesPolling() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item("a")] };
        await using var runtime = Create(
            world.Server,
            provider
        );

        await Observe(
            runtime,
            world.Server,
            1
        );
        provider.Fail = true;
        await Observe(
            runtime,
            world.Server,
            20,
            fails: true
        );
        Assert.Equal(
            new[] { "a" },
            Names(server: world.Server)
        );
        Assert.NotNull(@object: Assert.Single(collection: runtime.Observations).Failure);
        provider.Fail = false;
        provider.Items = Enumerable.Range(
            count: 5,
            start: 0
        ).Select(selector: i => Item(("x" + i))).ToArray();
        await Observe(
            runtime,
            world.Server,
            40,
            fails: true
        );
        Assert.Equal(
            new[] { "a" },
            Names(server: world.Server)
        );
        Assert.Equal(
            1,
            Assert.Single(collection: runtime.Observations).Items
        );
        world.Server.SuppressRecordedExtensions();
        var reads = provider.Reads;

        runtime.Pump(completedTick: 60);
        Assert.Equal(
            actual: provider.Reads,
            expected: reads
        );
    }
    [Fact]
    public async Task NumericAndFixedFieldsParseByTheirRowKindWithTheFixedConversionsOwnRounding() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item(
                "a",
                score: "42",
                depth: "12.375"
            )] };
        await using var runtime = Create(
            world.Server,
            provider
        );

        await Observe(
            runtime,
            world.Server,
            1
        );
        Assert.Equal(
            42,
            Cell(
                world.Server,
                "observedScores",
                "a"
            ).Value
        );
        Assert.Equal(
            NumericLiteral.ToFixed(value: 12.375m).Value,
            Cell(
                world.Server,
                "observedDepths",
                "a"
            ).Value
        );
    }
    [Fact]
    public async Task OrdinaryAuthorityRefusalDoesNotMarkProjectionApplied() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item("a")] };
        var config = Configuration() with {
            Clients = [new(
                "console",
                [],
                [new(
                        Capability: "observe",
                        Subject: "state:observedNames"
                    ), new(
                        Capability: "observe",
                        Subject: "state:observedScores"
                    ), new(
                        Capability: "observe",
                        Subject: "state:observedDepths"
                    )]
            )],
        };
        await using var runtime = Create(
            world.Server,
            provider,
            config
        );

        runtime.Pump(completedTick: 1);
        await runtime.FlushObservationsAsync(cancellationToken: Cancel);
        runtime.Pump(completedTick: 2);
        world.Server.DrainAdministrative();
        Assert.Empty(collection: Names(server: world.Server));
        Assert.NotNull(@object: Assert.Single(collection: runtime.Observations).Failure);
        Assert.False(condition: Assert.Single(collection: runtime.Observations).Applied);
    }

    private sealed class Source : IWorldConfiguredProvider, IWorldConfiguredObservationProvider, IWorldExtensionObservationSource {
        public IReadOnlyList<WorldExtensionObservationItem> Items = [];
        public TaskCompletionSource Started { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<WorldExtensionObservationItem>>? Deferred;
        public bool Fail;
        public int Reads;

        public string Kind => "test";

        public WorldExtensionOperation Bind(string name, string description, JsonElement settings) => throw new NotSupportedException();
        public IWorldExtensionObservationSource BindObservation(JsonElement settings, int maximumItems) => this;
        public void Dispose() { }
        public ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) {
            Reads++;
            Started.TrySetResult();
            if (Deferred is { } deferred) { return new(task: deferred.Task.WaitAsync(cancellationToken: cancellationToken)); }
            return (Fail
                ? ValueTask.FromException<IReadOnlyList<WorldExtensionObservationItem>>(exception: new IOException(message: "offline"))
                : ValueTask.FromResult(result: Items)
            );
        }
    }
}
