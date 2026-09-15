using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldStateDisclosureVectorTests {
    private static StateVector SampleVector(int dimensions) {
        var components = new sbyte[dimensions];
        components[0] = 127;
        StateVector.TryCreate(components: components, vector: out var vector, error: out _);
        return vector!;
    }

    private static StateSpace SampleSpace(string name = "lore", int dimensions = 32) =>
        new(
            Dimensions: dimensions,
            Model: "text-embedding-3-small",
            Name: CellName.Parse(candidate: name),
            Revision: "1"
        );

    private static WorldDefinition BuildDefinition(WorldStateRow[] rows, StateSpace[] spaces) =>
        new(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(
                Spaces: spaces,
                World: rows
            )
        );

    [Fact]
    public void DiscloseVectorRow_PreservesVectorAndFormatsAsBase64Url() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var principal = WorldPrincipal.Seat(slot: 0);

        var row = new WorldStateRow(
            Cells: [
                new StateCell(
                    Key: CellName.Parse(candidate: "k1"),
                    Vector: vector
                )
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore",
            Visibility: new StateVisibility(Readers: [principal.Describe()])
        );

        var definition = BuildDefinition(rows: [row], spaces: [space]);
        var disclosed = WorldStateDisclosure.Compose(definition: definition, recipient: principal);

        Assert.NotNull(disclosed);
        Assert.Single(disclosed);

        var observedRow = disclosed[0];
        Assert.Equal("vRow", observedRow.Name);
        Assert.Equal(CellKind.Vector, observedRow.Kind);
        Assert.Single(observedRow.Cells);

        var observedCell = observedRow.Cells[0];
        Assert.Equal("k1", observedCell.Key);
        Assert.False(observedCell.Hidden);
        Assert.NotNull(observedCell.Vector);
        Assert.Equal(vector, observedCell.Vector);

        // Verify JSON serialization produces base64url string
        var json = JsonSerializer.Serialize(disclosed);
        var jsonArray = JsonNode.Parse(json)?.AsArray();
        Assert.NotNull(jsonArray);
        Assert.Single(jsonArray);

        var rowNode = jsonArray[0]!;
        Assert.Equal("vRow", rowNode["Name"]?.ToString());
        var cellsArray = rowNode["Cells"]?.AsArray();
        Assert.NotNull(cellsArray);
        Assert.Single(cellsArray);

        var cellNode = cellsArray[0]!;
        Assert.Equal("k1", cellNode["Key"]?.ToString());
        Assert.Equal(vector.ToBase64Url(), cellNode["Vector"]?.ToString());
    }

    [Fact]
    public void DiscloseVectorRow_HiddenCellsPlaceholder_MasksVector() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var seat0 = WorldPrincipal.Seat(slot: 0);
        var seat1 = WorldPrincipal.Seat(slot: 1);

        var row = new WorldStateRow(
            Cells: [
                new StateCell(
                    Key: CellName.Parse(candidate: "secret"),
                    Vector: vector,
                    Visibility: new StateVisibility(Readers: [seat0.Describe()])
                )
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore",
            Visibility: new StateVisibility(Readers: [seat1.Describe()], Hidden: HiddenCells.Placeholder)
        );

        var definition = BuildDefinition(rows: [row], spaces: [space]);
        var disclosed = WorldStateDisclosure.Compose(definition: definition, recipient: seat1);

        Assert.NotNull(disclosed);
        Assert.Single(disclosed);

        var observedRow = disclosed[0];
        Assert.Equal(1, observedRow.HiddenCount);
        Assert.Single(observedRow.Cells);

        var placeholder = observedRow.Cells[0];
        Assert.True(placeholder.Hidden);
        Assert.Null(placeholder.Vector);
        Assert.Equal(string.Empty, placeholder.Key);

        var json = JsonSerializer.Serialize(disclosed);
        var jsonArray = JsonNode.Parse(json)?.AsArray();
        var cellNode = jsonArray?[0]?["Cells"]?.AsArray()?[0]!;
        Assert.Null(cellNode["Vector"]);
        Assert.Equal(true, cellNode["Hidden"]?.GetValue<bool>());
    }

    [Fact]
    public void DiscloseVectorRow_HiddenCellsOmit_OmitsCell() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var seat0 = WorldPrincipal.Seat(slot: 0);
        var seat1 = WorldPrincipal.Seat(slot: 1);

        var row = new WorldStateRow(
            Cells: [
                new StateCell(
                    Key: CellName.Parse(candidate: "secret"),
                    Vector: vector,
                    Visibility: new StateVisibility(Readers: [seat0.Describe()])
                )
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore",
            Visibility: new StateVisibility(Readers: [seat1.Describe()], Hidden: HiddenCells.Omit)
        );

        var definition = BuildDefinition(rows: [row], spaces: [space]);
        var disclosed = WorldStateDisclosure.Compose(definition: definition, recipient: seat1);

        Assert.NotNull(disclosed);
        Assert.Single(disclosed);

        var observedRow = disclosed[0];
        Assert.Equal(0, observedRow.HiddenCount);
        Assert.Empty(observedRow.Cells);
    }

    [Fact]
    public void DiscloseVectorRow_WithheldRow_OmittedEntirely() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var seat0 = WorldPrincipal.Seat(slot: 0);
        var seat1 = WorldPrincipal.Seat(slot: 1);

        var row = new WorldStateRow(
            Cells: [
                new StateCell(
                    Key: CellName.Parse(candidate: "secret"),
                    Vector: vector
                )
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore",
            Visibility: new StateVisibility(Readers: [seat0.Describe()])
        );

        var definition = BuildDefinition(rows: [row], spaces: [space]);
        var disclosed = WorldStateDisclosure.Compose(definition: definition, recipient: seat1);

        Assert.Null(disclosed);
    }
}
