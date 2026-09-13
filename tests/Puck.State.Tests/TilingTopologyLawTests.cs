using System.Numerics;
using Xunit;
using Puck.Assets.Documents;

namespace Puck.State.Tests;

/// <summary>A tiling compiles into the graph of its tiles: every shared side is one two-way adjacency along opposite
/// edge normals, every tile whose sides are all shared has as many neighbours as sides, and the tile at the origin is
/// what the family puts there.</summary>
public sealed class TilingTopologyLawTests {
    private static CompiledTopology Compile(TilingFamily family, int radius) => TopologyCompilation.Compile(
        topology: new LatticeTopology.Tiling(
            Name: "tiling",
            Origin: new DocumentVector3(
                x: 0f,
                y: 0f,
                z: 0f
            ),
            CellSize: 1f,
            Family: family,
            Radius: radius
        ),
        anchorOffset: Vector3.Zero
    );

    [Fact]
    public void APositionResolvesToTheNearestTileAndTheCellCountIsBounded() {
        var kagome = Compile(
            family: TilingFamily.Kagome,
            radius: 3
        );
        var hexagon = kagome.CellCentre(cell: 0);

        Assert.True(condition: kagome.TryCellOf(
            cell: out var cell,
            position: in hexagon
        ));
        Assert.Equal(
            actual: cell,
            expected: 0
        );
        Assert.False(condition: kagome.TryOffset(
            cell: 0,
            dx: 1,
            dz: 0,
            result: out _
        ));

        var huge = new LatticeTopology.Tiling(
            Name: "huge",
            Origin: new DocumentVector3(
                x: 0f,
                y: 0f,
                z: 0f
            ),
            CellSize: 1f,
            Family: TilingFamily.Triangular,
            Radius: 60
        );

        Assert.False(condition: TopologyCompilation.TryValidate(
            reason: out var reason,
            topology: huge
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "at most"
        );
        Assert.False(condition: TopologyCompilation.TryValidate(
            huge with { Radius = 0 },
            out _
        ));
    }
    [MemberData(nameof(Families))]
    [Theory]
    public void AdjacencyIsSymmetricAlongOppositeNormalsAndInteriorTilesAreSaturated(TilingFamily family, int radius) {
        var topology = Compile(
            family: family,
            radius: radius
        );
        var tiling = new LatticeTopology.Tiling(
            Name: "tiling",
            Origin: new DocumentVector3(
                x: 0f,
                y: 0f,
                z: 0f
            ),
            CellSize: 1f,
            Family: family,
            Radius: radius
        );
        var graph = TilingGenerator.Generate(tiling: tiling);

        Assert.True(
            condition: TopologyCompilation.TryValidate(
                reason: out var reason,
                topology: tiling
            ),
            userMessage: reason
        );
        Assert.Equal(
            TopologyKind.Tiling,
            topology.Kind
        );
        Assert.Equal(
            radius,
            topology.Radius
        );
        Assert.True(
            condition: (topology.CellCount > 6),
            userMessage: $"{family} has {topology.CellCount} tiles"
        );
        Assert.True(condition: (topology.DirectionCount is >= 3 and <= 64));

        var saturated = 0;

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            var neighbours = 0;

            for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                var next = topology.Neighbour(
                    cell: cell,
                    direction: direction
                );

                if (next < 0) {
                    continue;
                }
                neighbours++;
                Assert.NotEqual(
                    actual: next,
                    expected: cell
                );
                Assert.Equal(
                    cell,
                    topology.Neighbour(
                        cell: next,
                        direction: topology.Opposite(direction: direction)
                    )
                );
                // Edge-to-edge tiles of unit edge sit within the sum of their circumradii.
                var distance = (topology.CellCentre(cell: next) - topology.CellCentre(cell: cell)).Length;

                Assert.True(
                    condition: (distance < Puck.Maths.FixedQ4816.FromDouble(value: 4.0)),
                    userMessage: $"{family}: {cell}->{next} apart by {distance}"
                );
            }
            // A tile closer to the origin than the radius less two edges has every side shared.
            var reach = ((double)topology.CellCentre(cell: cell).Length);

            if (reach < (radius - 2.0)) {
                Assert.True(
                    condition: (neighbours >= 3),
                    userMessage: $"{family}: interior tile {cell} at {reach:0.##} has {neighbours} neighbours"
                );
                saturated++;
            }
        }
        Assert.True(
            condition: (saturated > 0),
            userMessage: $"{family} has no interior tile within radius {radius}"
        );
        Assert.Equal(
            topology.CellCount,
            graph.Cells.Count
        );
        Assert.Equal(
            1,
            topology.ElementCount
        );
    }
    [Fact]
    public void TheOriginTileIsWhatEachFamilyPutsThere() {
        static int Neighbours(CompiledTopology topology, int cell) {
            var count = 0;

            for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                if (topology.Neighbour(
                    cell: cell,
                    direction: direction
                ) >= 0) { count++; }
            }
            return count;
        }
        // Tile 0 is the nearest to the origin: the hexagon of the kagome, the octagon of 4.8.8, the dodecagon of 3.12.12 and 4.6.12.
        Assert.Equal(
            6,
            Neighbours(
                Compile(
                    family: TilingFamily.Kagome,
                    radius: 4
                ),
                0
            )
        );
        Assert.Equal(
            8,
            Neighbours(
                Compile(
                    family: TilingFamily.TruncatedSquare,
                    radius: 4
                ),
                0
            )
        );
        Assert.Equal(
            12,
            Neighbours(
                Compile(
                    family: TilingFamily.TruncatedHexagonal,
                    radius: 5
                ),
                0
            )
        );
        Assert.Equal(
            12,
            Neighbours(
                Compile(
                    family: TilingFamily.TruncatedTrihexagonal,
                    radius: 5
                ),
                0
            )
        );
        Assert.Equal(
            6,
            Neighbours(
                Compile(
                    family: TilingFamily.Rhombitrihexagonal,
                    radius: 4
                ),
                0
            )
        );
        Assert.Equal(
            3,
            Neighbours(
                Compile(
                    family: TilingFamily.Triangular,
                    radius: 3
                ),
                0
            )
        );
        // The Penrose sun: five thick rhombs meet at the origin, each with four neighbours.
        var penrose = Compile(
            family: TilingFamily.Penrose,
            radius: 4
        );

        Assert.Equal(
            10,
            penrose.DirectionCount
        );
        for (var cell = 0; (cell < 5); cell++) {
            Assert.Equal(
                4,
                Neighbours(
                    cell: cell,
                    topology: penrose
                )
            );
        }
    }

    public static TheoryData<TilingFamily, int> Families => new() {
        { TilingFamily.Triangular, 3 }, { TilingFamily.Kagome, 3 }, { TilingFamily.TruncatedSquare, 3 },
        { TilingFamily.Rhombitrihexagonal, 3 }, { TilingFamily.TruncatedHexagonal, 3 }, { TilingFamily.ElongatedTriangular, 3 },
        { TilingFamily.TruncatedTrihexagonal, 3 }, { TilingFamily.Penrose, 3 },
    };
}
