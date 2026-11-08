using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class DisclosureReviewLawTests {
    private static WorldRecipientView View(WorldServer server, Principal recipient, WorldDisclosureTier tier = WorldDisclosureTier.Replica) =>
        new(() => server.Definition, () => server.Arena, recipient, tier, () => true, new WorldDisclosureImages(),
            server.AuthorityIdentity, () => server.DeliveryTime);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawCompiledDiagnosticQueriesRequireAuthority(bool interaction) {
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument());
        WorldQuery query = interaction ? new WorldQuery.Interactions() : new WorldQuery.Rules();
        Assert.True(fixture.Server.AnswerSubmittedQuery(query, Principal.Seat(0)).Refused);
        Assert.False(fixture.Server.AnswerSubmittedQuery(query, Principal.Console).Refused);
    }

    [Fact]
    public void PropertyQueriesReadTheCallersLiveView() {
        var definition = Fixtures.BuildDocument() with {
            Properties = new WorldPropertyRegistrySection(Names: ["secretTag", "publicTag"]),
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(CellName.Parse("secretTag"), CellKind.Int, Domain: StateDomain.Keys.Instance,
                    Visibility: new StateVisibility(Readers: ["seat1"]), Cells: [new StateCell(CellName.Parse("0"), CellValue.Int(1))]),
                new WorldStateRow(CellName.Parse("publicTag"), CellKind.Int, Domain: StateDomain.Keys.Instance,
                    Visibility: new StateVisibility(), Cells: [new StateCell(CellName.Parse("0"), CellValue.Int(1))])])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var host = fixture.Server.RuleHost;
        var query = new WorldQuery.Properties(BodyIndex: 0);
        Assert.Contains("secretTag", host.AnswerSubmittedQuery(query, Principal.Console).Text);
        Assert.Contains("secretTag", host.AnswerSubmittedQuery(query, Principal.Seat(0)).Text);
        var answer = host.AnswerSubmittedQuery(query, Principal.Seat(1));
        Assert.Contains("publicTag", answer.Text);
        Assert.DoesNotContain("secretTag", answer.Text);
        Assert.True(fixture.Server.Arena.Catalog.TryResolve(StateLane.Document, "publicTag", out var row));
        var key = fixture.Server.Arena.Keys.Intern(CellName.Parse("0"));
        Assert.True(fixture.Server.Arena.TryWriteVisibility(row.Ordinal, key, new StateVisibility(Readers: ["seat1"])));
        Assert.DoesNotContain("publicTag", host.AnswerSubmittedQuery(query, Principal.Seat(1)).Text);
        Assert.Contains("publicTag", host.AnswerSubmittedQuery(query, Principal.Console).Text);
        Assert.True(host.AnswerSubmittedQuery(query, Principal.Session(1, 1)).Refused);
    }

    [Fact]
    public void ReplicaDefinitionsUseTheSameFieldProjectionAsPresentation() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Lattices: [new WorldFieldTopology(Name: "region", Origin: new(0, 0, 0), CellSize: 1, Width: 2, Depth: 1)],
                World: [
                    new WorldStateRow(CellName.Parse("privateField"), CellKind.Fixed, Domain: new StateDomain.CellsOf("region"),
                        Field: new WorldStateFieldTrait(Initial: 0.75f), Visibility: new StateVisibility(Readers: ["seat1"])),
                    new WorldStateRow(CellName.Parse("publicField"), CellKind.Fixed, Domain: new StateDomain.CellsOf("region"),
                        Field: new WorldStateFieldTrait(Initial: 0.25f), Visibility: new StateVisibility(ReadersFrom: "readers")),
                    new WorldStateRow(CellName.Parse("readers"), CellKind.Text, Domain: StateDomain.Keys.Instance,
                        Cells: [new StateCell(CellName.Parse("viewer"), CellValue.Text("seat2"))])])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var server = fixture.Server;
        var replica = View(server, Principal.Seat(1));
        void Check(WorldDefinition disclosed) {
            Assert.Equal("publicField", Assert.Single(Assert.Single(disclosed.FieldRegions).Fields).Name);
            Assert.Null(disclosed.AuthoredState.FirstOrDefault(row => row.Name.Value == "privateField")?.Field);
        }
        Check(replica.Definition);
        Assert.Equal(server.Definition.AuthoredState.Select(row => row.Name), replica.Definition.AuthoredState.Select(row => row.Name));
        Check(WorldStateDisclosure.Disclose(server.Definition, server.Arena, Principal.Seat(1)).Definition);
        Check(View(server, Principal.Seat(1), WorldDisclosureTier.Presentation).Definition);
        Assert.Equal(2, Assert.Single(View(server, Principal.Console).Definition.FieldRegions).Fields.Count);
        Assert.True(server.Arena.Catalog.TryResolve(StateLane.Document, "readers", out var row));
        var key = server.Arena.Keys.Intern(CellName.Parse("viewer"));
        Assert.True(server.Arena.TryWrite(row.Ordinal, key, CellValue.Text("seat1"), out var reason), reason);
        Assert.Empty(replica.Definition.FieldRegions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationPreservesAnEmptyRowsStorageShape(bool hiddenCell) {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(CellName.Parse("keys"), CellKind.Int, Domain: StateDomain.Keys.Instance, Capacity: 8,
                    Visibility: new StateVisibility(), Cells: hiddenCell ? [new StateCell(CellName.Parse("secret"), CellValue.Int(9),
                        Visibility: new StateVisibility(Readers: ["seat1"]))] : []),
                new WorldStateRow(CellName.Parse("slot"), CellKind.Int, Domain: StateDomain.Slot.Instance,
                    Visibility: new StateVisibility(), Cells: [])])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var view = View(fixture.Server, Principal.Seat(1), WorldDisclosureTier.Presentation);
        var hydrated = view.Definition;
        var keyed = Assert.Single(hydrated.AuthoredState, row => row.Name.Value == "keys");
        Assert.IsType<StateDomain.Keys>(keyed.Domain);
        Assert.Equal(8, keyed.Capacity);
        Assert.Empty(keyed.Cells!);
        Assert.IsType<StateDomain.Slot>(Assert.Single(hydrated.AuthoredState, row => row.Name.Value == "slot").Domain);
    }
}
