using System.Numerics;
using Xunit;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>A hex topology is <see cref="HexagonalIndex"/> made spatial: cell <c>i</c> is index <c>i</c> (rings
/// outward, consecutive indices adjacent), its six directions are <see cref="HexagonalCoordinate.Direction"/> in
/// order, its centre sits at origin + cellSize · (q − r/2, 0, r·√3/2), and every position inside the disk resolves
/// back to the nearest cell — the same answers a grid gives for its squares.</summary>
public sealed class HexTopologyLawTests {
    private const int Radius = 4;
    private const int CellCount = 61;

    private static CompiledTopology Compile() => TopologyCompilation.Compile(
        topology: new LatticeTopology.Hex(Name: "disk", Origin: new DocumentVector3(x: 1f, y: 2f, z: 3f), CellSize: 0.5f, Radius: Radius),
        anchorOffset: Vector3.Zero
    );

    private static int IndexOf(HexagonalCoordinate coordinate) {
        var index = HexagonalIndex.FromCoordinate(coordinate: coordinate);

        return ((index.Radius <= Radius) ? ((int)index.Value) : -1);
    }

    [Fact]
    public void CellsAreHexagonalIndicesAndNeighboursAreTheSixDirections() {
        var topology = Compile();

        Assert.Equal(TopologyKind.Hex, topology.Kind);
        Assert.Equal(CellCount, topology.CellCount);
        Assert.Equal(Radius, topology.Radius);
        Assert.Equal(TopologyCompilation.HexDirectionNames, Enumerable.Range(0, topology.DirectionCount).Select(topology.DirectionName));

        for (var cell = 0; cell < CellCount; cell++) {
            var coordinate = new HexagonalIndex(value: cell).ToCoordinate();

            for (var direction = 0; direction < 6; direction++) {
                Assert.Equal(IndexOf(coordinate + HexagonalCoordinate.Direction(direction: direction)), topology.Neighbour(cell: cell, direction: direction));
            }
            if (cell > 0) {
                Assert.Contains(cell, Enumerable.Range(0, 6).Select(direction => topology.Neighbour(cell: (cell - 1), direction: direction)));
            }
        }
        Assert.Equal(5, topology.Direction(token: "NE"));
        Assert.Equal(3, topology.Opposite(direction: 0));
    }

    [Fact]
    public void CentresRoundTripThroughPositionToCell() {
        var topology = Compile();
        var spacing = topology.CellSize;

        for (var cell = 0; cell < CellCount; cell++) {
            var coordinate = new HexagonalIndex(value: cell).ToCoordinate();
            var centre = topology.CellCentre(cell: cell);
            var expectedX = (1.0 + (0.5 * (coordinate.Q - (coordinate.R / 2.0))));
            var expectedZ = (3.0 + (0.5 * coordinate.R * Math.Sqrt(3.0) / 2.0));

            Assert.Equal(expectedX, (double)centre.X, precision: 4);
            Assert.Equal(2.0, (double)centre.Y, precision: 4);
            Assert.Equal(expectedZ, (double)centre.Z, precision: 4);
            Assert.True(topology.TryCellOf(position: in centre, cell: out var back) && (back == cell), $"centre of {cell} resolved to {back}");

            // Anywhere inside the cell's own hexagon resolves to it: 0.4 of a step toward each neighbour is inside.
            for (var direction = 0; direction < 6; direction++) {
                var next = topology.Neighbour(cell: cell, direction: direction);
                if (next < 0) { continue; }
                var toward = topology.CellCentre(cell: next);
                var nudged = new FixedVector3(
                    X: (centre.X + ((toward.X - centre.X) * FixedQ4816.FromDouble(value: 0.4))),
                    Y: centre.Y,
                    Z: (centre.Z + ((toward.Z - centre.Z) * FixedQ4816.FromDouble(value: 0.4)))
                );
                Assert.True(topology.TryCellOf(position: in nudged, cell: out var near) && (near == cell), $"{cell} toward {next} resolved to {near}");
            }
        }

        var outside = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.0 + (0.5 * (Radius + 1))), Y: FixedQ4816.FromDouble(value: 2.0), Z: FixedQ4816.FromDouble(value: 3.0));

        Assert.False(topology.TryCellOf(position: in outside, cell: out _));
        _ = spacing;
    }

    [Fact]
    public void OffsetsAreAxialSteps() {
        var topology = Compile();

        for (var cell = 0; cell < CellCount; cell++) {
            var coordinate = new HexagonalIndex(value: cell).ToCoordinate();

            for (var dq = -2; dq <= 2; dq++) {
                for (var dr = -2; dr <= 2; dr++) {
                    var expected = IndexOf(coordinate + new HexagonalCoordinate(Q: dq, R: dr));
                    var landed = topology.TryOffset(cell: cell, dx: dq, dz: dr, result: out var result);

                    Assert.Equal(expected >= 0, landed);
                    Assert.Equal(Math.Max(expected, -1), result);
                }
            }
        }
    }

    [Fact]
    public void SymmetryElementsPermuteCellsAndPreserveAdjacency() {
        var topology = Compile();

        Assert.Equal(12, topology.ElementCount);

        for (var element = 0; element < topology.ElementCount; element++) {
            var seen = new HashSet<int>();

            Assert.Equal(0, topology.Image(element: element, cell: 0));
            for (var cell = 0; cell < CellCount; cell++) {
                var image = topology.Image(element: element, cell: cell);

                Assert.True(seen.Add(item: image));
                for (var direction = 0; direction < 6; direction++) {
                    var next = topology.Neighbour(cell: cell, direction: direction);
                    if (next < 0) { continue; }
                    var carried = topology.Image(element: element, cell: next);

                    Assert.Contains(carried, Enumerable.Range(0, 6).Select(d => topology.Neighbour(cell: image, direction: d)));
                }
            }
        }
    }
}
