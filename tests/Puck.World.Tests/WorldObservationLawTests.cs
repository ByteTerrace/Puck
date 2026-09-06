using System.Numerics;
using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldObservationLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CompleteSnapshotsProjectAtomicallyStayQuietAndRemoveOnlyOwnedPlacements() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item("b"), Item("a")] };
        await using var runtime = Create(world.Server, provider);
        await Observe(runtime, world.Server, 1);
        Assert.Equal(new[] { "a", "b" }, Names(world.Server));
        Assert.Contains(world.Server.Definition.Placements, row => row.Id == "observed-a" && row.Parent == "court");
        var submissions = Assert.Single(runtime.Observations).Submissions;
        Assert.Equal(1, submissions);
        await Observe(runtime, world.Server, 20);
        Assert.Equal(submissions, Assert.Single(runtime.Observations).Submissions);
        provider.Items = [Item("b")];
        await Observe(runtime, world.Server, 40);
        Assert.Equal(new[] { "b" }, Names(world.Server));
        Assert.DoesNotContain(world.Server.Definition.Placements, row => row.Id == "observed-a");
        Assert.Contains(world.Server.Definition.Placements, row => row.Id == "court");
    }

    [Fact]
    public async Task FailedOrOversizedReadsRetainLastCompleteCollectionAndReplayRevokesPolling() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item("a")] };
        await using var runtime = Create(world.Server, provider);
        await Observe(runtime, world.Server, 1);
        provider.Fail = true;
        await Observe(runtime, world.Server, 20, fails: true);
        Assert.Equal(new[] { "a" }, Names(world.Server));
        Assert.NotNull(Assert.Single(runtime.Observations).Failure);
        provider.Fail = false;
        provider.Items = Enumerable.Range(0, 5).Select(i => Item("x" + i)).ToArray();
        await Observe(runtime, world.Server, 40, fails: true);
        Assert.Equal(new[] { "a" }, Names(world.Server));
        world.Server.SuppressRecordedExtensions();
        var reads = provider.Reads;
        runtime.Pump(60);
        Assert.Equal(reads, provider.Reads);
    }

    [Fact]
    public async Task OrdinaryAuthorityRefusalDoesNotMarkProjectionApplied() {
        using var world = Fixtures.FreshServer(Document());
        using var provider = new Source { Items = [Item("a")] };
        var config = Configuration() with { Clients = [new("console", [], [new("observe", "state:observedNames")])] };
        await using var runtime = Create(world.Server, provider, config);
        runtime.Pump(1);
        await runtime.FlushObservationsAsync(Cancel);
        runtime.Pump(2);
        world.Server.DrainAdministrative();
        Assert.Empty(Names(world.Server));
        Assert.NotNull(Assert.Single(runtime.Observations).Failure);
    }

    private static async Task Observe(WorldConfiguredExtensions runtime, WorldServer server, ulong tick, bool fails = false) {
        runtime.Pump(tick);
        if (fails) { await Assert.ThrowsAnyAsync<Exception>(() => runtime.FlushObservationsAsync(Cancel)); }
        else { await runtime.FlushObservationsAsync(Cancel); }
        runtime.Pump(tick + 1);
        server.DrainAdministrative();
        runtime.Pump(tick + 2);
    }
    private static string[] Names(WorldServer server) => server.Definition.State.First(row => row.Name.Value == "observedNames")
        .Cells!.Select(cell => cell.Text!).ToArray();
    private static WorldExtensionObservationItem Item(string name) => new(name, new Dictionary<string, string> { ["name"] = name });
    private static WorldConfiguredExtensions Create(WorldServer server, Source source, WorldExtensionConfiguration? configuration = null) =>
        WorldConfiguredExtensions.Create(configuration ?? Configuration(), new([new WorldExtensionProviderType("test", _ => source)], type => type.Type),
            server, new FakeObjectBlobStore(), new DirectoryObjectStorageTarget("unused"), () => throw new InvalidOperationException("Reads must not capture effect recovery images."));
    private static WorldExtensionConfiguration Configuration() => new("puck.world.extensions.v1", "observations", Guid.NewGuid(),
        [new("source", "test", JsonDocument.Parse("{}").RootElement.Clone())], [],
        [new("console", [], [new("observe", "state:observedNames"), new("mutate", "section:state"), new("mutate", "section:placements")])], [],
        ScanEveryTicks: 1, Observations: [new("inventory", "source", "console", JsonDocument.Parse("{}").RootElement.Clone(),
            new Dictionary<string, string> { ["name"] = "observedNames" }, new("court", "observed-", 2, 4, 4), RefreshTicks: 10, MaximumItems: 4)]);
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();
        return document with { DocumentId = "observations", StateRaw = new(World: [.. document.State,
            new(CellName.Parse("observedNames"), CellKind.Text, Capacity: 4, Cells: [], Visibility: new())]),
            CreationsRaw = [new("store", new("puck.creation.v1", "store", [new("#AA7755", null, null, null)],
                [new(0, "box", Puck.SignedDistance.SdfSolidPrimitive.Box, Vector3.Zero, Quaternion.Identity, Vector3.One, 0, null, 0, null)], null))],
            PlacementsRaw = new(Rows: [new("court", "store", Vector3.Zero, 0, 1)]) };
    }
    private sealed class Source : IWorldConfiguredProvider, IWorldConfiguredObservationProvider, IWorldExtensionObservationSource {
        public IReadOnlyList<WorldExtensionObservationItem> Items = [];
        public bool Fail;
        public int Reads;
        public WorldExtensionOperation Bind(string name, string description, JsonElement settings) => throw new NotSupportedException();
        public IWorldExtensionObservationSource BindObservation(JsonElement settings, int maximumItems) => this;
        public ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) {
            Reads++;
            return Fail ? ValueTask.FromException<IReadOnlyList<WorldExtensionObservationItem>>(new IOException("offline")) : ValueTask.FromResult(Items);
        }
        public void Dispose() { }
    }
}
