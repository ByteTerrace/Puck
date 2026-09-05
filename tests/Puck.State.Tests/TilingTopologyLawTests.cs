using System.Numerics;
using Xunit;
using Puck.Assets.Documents;

namespace Puck.State.Tests;

/// <summary>A tiling compiles into the graph of its tiles: every shared side is one two-way adjacency along opposite
/// edge normals, every tile whose sides are all shared has as many neighbours as sides, and the tile at the origin is
/// what the family puts there.</summary>
public sealed class TilingTopologyLawTests {
    private static CompiledTopology Compile(TilingFamily family, int radius) => TopologyCompilation.Compile(
        topology: new LatticeTopology.Tiling(Name: "tiling", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Family: family, Radius: radius),
        anchorOffset: Vector3.Zero
    );

    public static TheoryData<TilingFamily, int> Families => new() {
        { TilingFamily.Triangular, 3 }, { TilingFamily.Kagome, 3 }, { TilingFamily.TruncatedSquare, 3 },
        { TilingFamily.Rhombitrihexagonal, 3 }, { TilingFamily.TruncatedHexagonal, 3 }, { TilingFamily.ElongatedTriangular, 3 },
        { TilingFamily.TruncatedTrihexagonal, 3 }, { TilingFamily.Penrose, 3 },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void AdjacencyIsSymmetricAlongOppositeNormalsAndInteriorTilesAreSaturated(TilingFamily family, int radius) {
        var topology = Compile(family, radius);
        var tiling = new LatticeTopology.Tiling(Name: "tiling", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Family: family, Radius: radius);
        var graph = TilingGenerator.Generate(tiling);

        Assert.True(TopologyCompilation.TryValidate(tiling, out var reason), reason);
        Assert.Equal(TopologyKind.Tiling, topology.Kind);
        Assert.Equal(radius, topology.Radius);
        Assert.True(topology.CellCount > 6, $"{family} has {topology.CellCount} tiles");
        Assert.True(topology.DirectionCount is >= 3 and <= 64);

        var saturated = 0;
        for (var cell = 0; cell < topology.CellCount; cell++) {
            var neighbours = 0;
            for (var direction = 0; direction < topology.DirectionCount; direction++) {
                var next = topology.Neighbour(cell, direction);
                if (next < 0) {
                    continue;
                }
                neighbours++;
                Assert.NotEqual(cell, next);
                Assert.Equal(cell, topology.Neighbour(next, topology.Opposite(direction)));
                // Edge-to-edge tiles of unit edge sit within the sum of their circumradii.
                var distance = (topology.CellCentre(next) - topology.CellCentre(cell)).Length;
                Assert.True(distance < Puck.Maths.FixedQ4816.FromDouble(4.0), $"{family}: {cell}->{next} apart by {distance}");
            }
            // A tile closer to the origin than the radius less two edges has every side shared.
            var reach = (double)topology.CellCentre(cell).Length;
            if (reach < radius - 2.0) {
                Assert.True(neighbours >= 3, $"{family}: interior tile {cell} at {reach:0.##} has {neighbours} neighbours");
                saturated++;
            }
        }
        Assert.True(saturated > 0, $"{family} has no interior tile within radius {radius}");
        Assert.Equal(topology.CellCount, graph.Cells.Count);
        Assert.Equal(1, topology.ElementCount);
    }

    [Fact]
    public void TheOriginTileIsWhatEachFamilyPutsThere() {
        static int Neighbours(CompiledTopology topology, int cell) {
            var count = 0;
            for (var direction = 0; direction < topology.DirectionCount; direction++) {
                if (topology.Neighbour(cell, direction) >= 0) { count++; }
            }
            return count;
        }
        // Tile 0 is the nearest to the origin: the hexagon of the kagome, the octagon of 4.8.8, the dodecagon of 3.12.12 and 4.6.12.
        Assert.Equal(6, Neighbours(Compile(TilingFamily.Kagome, 4), 0));
        Assert.Equal(8, Neighbours(Compile(TilingFamily.TruncatedSquare, 4), 0));
        Assert.Equal(12, Neighbours(Compile(TilingFamily.TruncatedHexagonal, 5), 0));
        Assert.Equal(12, Neighbours(Compile(TilingFamily.TruncatedTrihexagonal, 5), 0));
        Assert.Equal(6, Neighbours(Compile(TilingFamily.Rhombitrihexagonal, 4), 0));
        Assert.Equal(3, Neighbours(Compile(TilingFamily.Triangular, 3), 0));
        // The Penrose sun: five thick rhombs meet at the origin, each with four neighbours.
        var penrose = Compile(TilingFamily.Penrose, 4);
        Assert.Equal(10, penrose.DirectionCount);
        for (var cell = 0; cell < 5; cell++) {
            Assert.Equal(4, Neighbours(penrose, cell));
        }
    }

    [Fact]
    public void APositionResolvesToTheNearestTileAndTheCellCountIsBounded() {
        var kagome = Compile(TilingFamily.Kagome, 3);
        var hexagon = kagome.CellCentre(0);
        Assert.True(kagome.TryCellOf(position: in hexagon, cell: out var cell));
        Assert.Equal(0, cell);
        Assert.False(kagome.TryOffset(cell: 0, dx: 1, dz: 0, result: out _));

        var huge = new LatticeTopology.Tiling(Name: "huge", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Family: TilingFamily.Triangular, Radius: 60);
        Assert.False(TopologyCompilation.TryValidate(huge, out var reason));
        Assert.Contains("at most", reason, StringComparison.Ordinal);
        Assert.False(TopologyCompilation.TryValidate(huge with { Radius = 0 }, out _));
    }
}
