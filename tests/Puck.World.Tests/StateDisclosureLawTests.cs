using System.Text;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class StateDisclosureLawTests {
    [Fact]
    public async Task TwoAuthenticatedSocketsReceiveOnlyTheirOwnCells() {
        var first = AdmissionWireFixture.GenerateIdentity("table-player-a");
        var second = AdmissionWireFixture.GenerateIdentity("table-player-b");
        using var firstKey = first.Key;
        using var secondKey = second.Key;
        var grants = new[] { new WorldAdmissionGrant(WorldCapability.Observe, GrantSubject.State("hands"), Budget: 100) };
        var baseline = AdmissionWireFixture.BuildAdmissionDocument(AdmissionWireFixture.BuildEntry(first, grants));
        var start = AdmissionWireFixture.PeerBodyIndex;
        var definition = baseline with {
            Admission = [AdmissionWireFixture.BuildEntry(first, grants), AdmissionWireFixture.BuildEntry(second, grants)],
            PopulationRaw = baseline.Population with { CapacityRaw = start + 2, NetworkPlayers = 2 },
            StateRaw = new(World: [new(Name("hands"), CellKind.Int, Capacity: 2, Visibility: new(), Cells: [
                Cell("cardA", 101) with { Visibility = new([WorldPrincipal.Peer(start, 1).Describe()]) },
                Cell("cardB", 202) with { Visibility = new([WorldPrincipal.Peer(start + 1, 1).Describe()]) }
            ])])
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        using var host = new WorldPeerHost(fixture.Server);
        host.Start("127.0.0.1:0");
        using var pumpCancellation = new CancellationTokenSource();
        var pump = AdmissionWireFixture.RunPumpAsync(fixture, host, pumpCancellation.Token);
        try {
            using var deadline = Laws.SocketDeadline();
            var a = await AdmissionWireFixture.ConnectAndAdmitAsync(host, first, deadline.Token);
            using var clientA = a.Client;
            var b = await AdmissionWireFixture.ConnectAndAdmitAsync(host, second, deadline.Token);
            using var clientB = b.Client;
            Assert.Equal(1, a.Generation);
            Assert.Equal(1, b.Generation);
            var seenA = await AdmissionWireFixture.SubmitQueryAsync(clientA.GetStream(), new WorldQuery.StateObservations("hands"), deadline.Token);
            var seenB = await AdmissionWireFixture.SubmitQueryAsync(clientB.GetStream(), new WorldQuery.StateObservations("hands"), deadline.Token);
            Assert.False(seenA.Refused, seenA.Text);
            Assert.False(seenB.Refused, seenB.Text);
            var expectedA = a.PeerIndex == start ? "cardA" : "cardB";
            var expectedB = b.PeerIndex == start ? "cardA" : "cardB";
            Assert.NotEqual(expectedA, expectedB);
            Assert.Contains(expectedA, seenA.Text);
            Assert.DoesNotContain(expectedB, seenA.Text);
            Assert.Contains(expectedB, seenB.Text);
            Assert.DoesNotContain(expectedA, seenB.Text);
        } finally {
            pumpCancellation.Cancel();
            await pump;
        }
    }

    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value) => new(Name(key), value);
    private static WorldDefinition Cards(string first = "seat1", string second = "seat2") => Fixtures.BuildDocument() with {
        StateRaw = new(World: [
            new(Name("cards"), CellKind.Int, Cells: [Cell("ace", 101), Cell("king", 202)], Visibility: new()),
            new(Name("handA"), CellKind.Bool, Cells: [Cell("ace", 1)], Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards"), Ordered: true), Visibility: new([first])),
            new(Name("handB"), CellKind.Bool, Cells: [Cell("king", 1)], Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards"), Ordered: true), Visibility: new([second]))
        ])
    };

    [Fact]
    public void ProjectionOmitsHiddenIdentitiesAndAttributesWithoutChangingAuthority() {
        var definition = Cards();
        var before = WorldDefinitionSerialization.Serialize(definition);
        var a = Encoding.UTF8.GetString(WorldProjection.Serialize(WorldProjection.Compose(definition, WorldDisclosureTier.Presentation, "test", 1, WorldPrincipal.Seat(0))!));
        var b = Encoding.UTF8.GetString(WorldProjection.Serialize(WorldProjection.Compose(definition, WorldDisclosureTier.Presentation, "test", 1, WorldPrincipal.Seat(1))!));
        Assert.Contains("ace", a);
        Assert.DoesNotContain("king", a);
        Assert.DoesNotContain("202", a);
        Assert.Contains("king", b);
        Assert.DoesNotContain("\"ace\"", b);
        Assert.DoesNotContain("drawCursor", a);
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(definition));
        var persisted = System.Text.Json.JsonSerializer.Deserialize(before, WorldJsonContext.Default.WorldDefinition)!;
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(persisted));
    }

    [Fact]
    public void KnowledgeRetainsLastSeenValueWhenSightIsLostAndRoundTrips() {
        var definition = Fixtures.BuildDocument() with { StateRaw = new(
            Lattices: [new("map", new DocumentVector3(0,0,0), 1, 2, 1, Kind: WorldTopologyKind.Grid)],
            World: [
                new(Name("truth"), CellKind.Int, Cells: [Cell("0", 7), Cell("1", 9)], Domain: new WorldStateDomain.CellsOf("map"), Visibility: new([])),
                new(Name("sight"), CellKind.Bool, Cells: [Cell("0", 1)], Domain: new WorldStateDomain.CellsOf("map"), Visibility: new([])),
                new(Name("known"), CellKind.Int, Cells: [], Domain: new WorldStateDomain.CellsOf("map"), Visibility: new(["seat1"]), Knowledge: new("truth", "sight"))
            ]) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.Observe("known"), WorldPrincipal.Seat(0), 8, "test", out _, out _));
        Assert.True(WorldStateTransforms.TryApply(definition, new WorldStateTransform.Observe("known"), WorldPrincipal.World, 8, "test", out var seen, out reason), reason);
        var changed = seen.WithWorldState(seen.State.Select(r => r.Name.Value switch {
            "truth" => r with { Cells = [Cell("0", 42)] },
            "sight" => r with { Cells = [] },
            _ => r
        }).ToArray());
        Assert.True(WorldStateTransforms.TryApply(changed, new WorldStateTransform.Observe("known"), WorldPrincipal.World, 12, "test", out var remembered, out reason), reason);
        var cell = Assert.Single(Assert.Single(WorldStateDisclosure.Compose(remembered, WorldPrincipal.Seat(0))!).Cells);
        Assert.Equal(7, cell.Value);
        Assert.Equal(new WorldStateObservation(8, false), cell.Observation);
        var bytes = WorldDefinitionSerialization.Serialize(remembered);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize(bytes, WorldJsonContext.Default.WorldDefinition)!;
        Assert.Equal(bytes, WorldDefinitionSerialization.Serialize(reloaded));
        Assert.True(WorldDefinitionValidator.TryValidateLocally(reloaded, out reason), reason);
    }

    [Fact]
    public void SecretStreamsResumeAndRefuseIncompatibleSources() {
        var key = new ClosedBitset256(1, 2, 3, 4);
        var generator = new WorldGenerator(Source: WorldGeneratorSource.StreamDraw);
        Assert.True(WorldGeneratorEngine.TryFire(generator, CellKind.Int, 8, 9, 100, null, out var a, out _, key));
        Assert.True(WorldGeneratorEngine.TryFire(generator, CellKind.Int, 8, 9, 100, null, out var b, out _, key));
        Assert.Equal(a, b);
        Assert.True(WorldGeneratorEngine.TryFire(generator, CellKind.Int, 8, 9, 101, null, out var c, out _, key));
        Assert.NotEqual(a.Numeric, c.Numeric);
        Assert.False(WorldGeneratorEngine.TryFire(generator, CellKind.Fixed, 8, 9, 100, null, out _, out _, key));
    }
}
