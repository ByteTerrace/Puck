using System.Numerics;
using Xunit;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>A graph topology is the adjacency table authored directly: an edge fills one (cell, direction) slot and,
/// unless one-way, the reverse slot along the direction's opposite; a position resolves to the nearest centre within
/// half a cell size; there is no axial offset and the symmetry group is the identity.</summary>
public sealed class GraphTopologyLawTests {
    // A five-point star: a hub with four spokes, one spoke's far end joined back to another by a one-way ladder.
    private static LatticeTopology.Graph Star(params GraphEdge[] extraEdges) => new(
        Name: "star",
        Origin: new DocumentVector3(x: 10f, y: 0f, z: 10f),
        CellSize: 1f,
        Cells: [
            new GraphCell(Id: "hub", Centre: new DocumentVector3(x: 0f, y: 0f, z: 0f)),
            new GraphCell(Id: "n", Centre: new DocumentVector3(x: 0f, y: 0f, z: -2f)),
            new GraphCell(Id: "e", Centre: new DocumentVector3(x: 2f, y: 0f, z: 0f)),
            new GraphCell(Id: "s", Centre: new DocumentVector3(x: 0f, y: 0f, z: 2f)),
            new GraphCell(Id: "w", Centre: new DocumentVector3(x: -2f, y: 0f, z: 0f)),
        ],
        Directions: [
            new GraphDirection(Name: "N", Opposite: "S"),
            new GraphDirection(Name: "S", Opposite: "N"),
            new GraphDirection(Name: "E", Opposite: "W"),
            new GraphDirection(Name: "W", Opposite: "E"),
            new GraphDirection(Name: "ladder", Opposite: "ladder"),
        ],
        Edges: [
            new GraphEdge(From: "hub", To: "n", Direction: "N"),
            new GraphEdge(From: "hub", To: "e", Direction: "E"),
            new GraphEdge(From: "hub", To: "s", Direction: "S"),
            new GraphEdge(From: "hub", To: "w", Direction: "W"),
            new GraphEdge(From: "w", To: "n", Direction: "ladder", OneWay: true),
            .. extraEdges,
        ]
    );

    [Fact]
    public void EdgesFillSlotsAndTheirReversesUnlessOneWay() {
        var graph = Star();
        Assert.True(TopologyCompilation.TryValidate(graph, out var reason), reason);
        var topology = TopologyCompilation.Compile(topology: graph, anchorOffset: Vector3.Zero);

        Assert.Equal(TopologyKind.Graph, topology.Kind);
        Assert.Equal(5, topology.CellCount);
        Assert.Equal(5, topology.DirectionCount);
        Assert.Equal(new[] { "N", "S", "E", "W", "ladder" }, Enumerable.Range(0, 5).Select(topology.DirectionName));
        Assert.Equal(1, topology.Opposite(direction: topology.Direction(token: "N")));
        Assert.Equal(4, topology.Opposite(direction: topology.Direction(token: "ladder")));

        Assert.Equal(1, topology.Neighbour(cell: 0, direction: topology.Direction(token: "N")));
        Assert.Equal(0, topology.Neighbour(cell: 1, direction: topology.Direction(token: "S")));
        Assert.Equal(-1, topology.Neighbour(cell: 1, direction: topology.Direction(token: "N")));
        Assert.Equal(1, topology.Neighbour(cell: 4, direction: topology.Direction(token: "ladder")));
        Assert.Equal(-1, topology.Neighbour(cell: 1, direction: topology.Direction(token: "ladder")));
        Assert.False(topology.TryOffset(cell: 0, dx: 1, dz: 0, result: out _));

        Assert.Equal(1, topology.ElementCount);
        Assert.Equal("identity", topology.ElementName(element: 0));
        for (var cell = 0; cell < 5; cell++) {
            Assert.Equal(cell, topology.Image(element: 0, cell: cell));
        }
    }

    [Fact]
    public void PositionsResolveToTheNearestCentreWithinHalfACellSize() {
        var topology = TopologyCompilation.Compile(topology: Star(), anchorOffset: new Vector3(x: 1f, y: 0f, z: 0f));

        var east = topology.CellCentre(cell: 2);
        Assert.Equal(FixedQ4816.FromDouble(value: 13.0), east.X);
        Assert.Equal(FixedQ4816.FromDouble(value: 10.0), east.Z);

        Assert.True(topology.TryCellOf(position: in east, cell: out var cell));
        Assert.Equal(2, cell);
        var near = new FixedVector3(X: FixedQ4816.FromDouble(value: 13.4), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: 10.2));
        Assert.True(topology.TryCellOf(position: in near, cell: out cell));
        Assert.Equal(2, cell);
        var between = new FixedVector3(X: FixedQ4816.FromDouble(value: 12.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: 10.0));
        Assert.False(topology.TryCellOf(position: in between, cell: out _));
        var away = new FixedVector3(X: FixedQ4816.FromDouble(value: 11.0), Y: FixedQ4816.FromDouble(value: 3.0), Z: FixedQ4816.FromDouble(value: 10.0));
        Assert.False(topology.TryCellOf(position: in away, cell: out _));
    }

    [Fact]
    public void ValidationRefusesCollidingSlotsLoopsAndAsymmetricOpposites() {
        Assert.False(TopologyCompilation.TryValidate(Star(new GraphEdge(From: "hub", To: "e", Direction: "N")), out var slot));
        Assert.Contains("already fills", slot, StringComparison.Ordinal);
        Assert.False(TopologyCompilation.TryValidate(Star(new GraphEdge(From: "hub", To: "w", Direction: "ladder")), out var reverse));
        Assert.Contains("implies", reverse, StringComparison.Ordinal);
        Assert.False(TopologyCompilation.TryValidate(Star(new GraphEdge(From: "e", To: "e", Direction: "ladder")), out var loop));
        Assert.Contains("to itself", loop, StringComparison.Ordinal);
        Assert.False(TopologyCompilation.TryValidate(Star(new GraphEdge(From: "e", To: "ghost", Direction: "N")), out var ghost));
        Assert.Contains("undeclared cell", ghost, StringComparison.Ordinal);

        var asymmetric = Star() with { Directions = [new GraphDirection(Name: "N", Opposite: "S"), new GraphDirection(Name: "S", Opposite: "S"), new GraphDirection(Name: "E", Opposite: "W"), new GraphDirection(Name: "W", Opposite: "E"), new GraphDirection(Name: "ladder", Opposite: "ladder")] };
        Assert.False(TopologyCompilation.TryValidate(asymmetric, out var opposite));
        Assert.Contains("whose own opposite", opposite, StringComparison.Ordinal);

        var duplicate = Star() with { Cells = [new GraphCell(Id: "a", Centre: new DocumentVector3(x: 0f, y: 0f, z: 0f)), new GraphCell(Id: "a", Centre: new DocumentVector3(x: 1f, y: 0f, z: 0f))] };
        Assert.False(TopologyCompilation.TryValidate(duplicate, out var repeated));
        Assert.Contains("repeats id", repeated, StringComparison.Ordinal);
    }
}
