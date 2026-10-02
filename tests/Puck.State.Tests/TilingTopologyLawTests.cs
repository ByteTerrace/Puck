using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Puck.Assets.Documents;

namespace Puck.State.Tests;

/// <summary>A tiling compiles into the graph of its tiles: every shared side is one two-way adjacency along opposite
/// edge normals, every tile whose sides are all shared has as many neighbours as sides, the tile at the origin is
/// what the family puts there, and the graph is the same bits on every machine — cells outward by ring, then
/// counterclockwise from +X, with no platform-dependent arithmetic deciding either.</summary>
public sealed class TilingTopologyLawTests {
    // Generated centres reach the law as floats. Consecutive rings in the patches below stand far further apart than
    // float rounding moves a centre, and ring-mates differ by far less.
    private const double RingTolerance = 1e-4;
    private const double Quantum = 1e-6;

    private static LatticeTopology.Tiling Tiling(TilingFamily family, int radius) => new(
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
    private static CompiledTopology Compile(TilingFamily family, int radius) => TopologyCompilation.Compile(
        topology: Tiling(
            family: family,
            radius: radius
        ),
        anchorOffset: Vector3.Zero
    );
    // The half of the turn a centre's angle in [0, 2π) falls in: 0 for [0, π), 1 for [π, 2π), read on the quantum
    // grid the generator sorts on, so a centre on −X sits at exactly π.
    private static int HalfPlane(long x, long y) => (((y > 0) || ((y == 0) && (x >= 0)))
        ? 0
        : 1
    );
    private static long OnGrid(float value) => ((long)Math.Round(a: (((double)value) / Quantum)));
    private static bool PrecedesByAngle(DocumentVector3 first, DocumentVector3 second) {
        var (ax, ay) = (OnGrid(value: first.X), OnGrid(value: first.Z));
        var (bx, by) = (OnGrid(value: second.X), OnGrid(value: second.Z));
        var (aHalf, bHalf) = (HalfPlane(
            x: ax,
            y: ay
        ), HalfPlane(
            x: bx,
            y: by
        ));

        return ((aHalf != bHalf)
            ? (aHalf < bHalf)
            : ((((Int128)ax) * by) > (((Int128)ay) * bx))
        );
    }
    private static double Reach(DocumentVector3 centre) => Math.Sqrt(d: ((((double)centre.X) * centre.X) + (((double)centre.Z) * centre.Z)));

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
    [MemberData(nameof(Orders))]
    [Theory]
    public void CellsRunOutwardByRingThenCounterclockwiseFromPositiveX(TilingFamily family, int radius) {
        var cells = TilingGenerator.Generate(tiling: Tiling(
            family: family,
            radius: radius
        )).Cells;
        var seam = 0;

        for (var index = 1; (index < cells.Count); index++) {
            var previous = cells[(index - 1)].Centre;
            var next = cells[index].Centre;
            var step = (Reach(centre: next) - Reach(centre: previous));

            if (step > RingTolerance) {
                continue;
            }
            Assert.True(
                condition: (step >= -RingTolerance),
                userMessage: $"{family} radius {radius}: cell {index} lies {-step} nearer the origin than cell {(index - 1)}"
            );
            Assert.True(
                condition: PrecedesByAngle(
                    first: previous,
                    second: next
                ),
                userMessage: $"{family} radius {radius}: ring-mates {(index - 1)} at ({previous.X}, {previous.Z}) and {index} at ({next.X}, {next.Z}) are not counterclockwise from +X"
            );
            // A centre on −X between a ring-mate in (0, π) and one in (π, 2π) shows the seam sits at π.
            var after = (((index + 1) < cells.Count)
                ? cells[(index + 1)].Centre
                : null
            );

            if ((after is not null) && (OnGrid(value: next.Z) == 0) && (OnGrid(value: next.X) < 0) &&
                (OnGrid(value: previous.Z) > 0) && (OnGrid(value: after.Z) < 0) &&
                (Math.Abs(value: (Reach(centre: after) - Reach(centre: next))) <= RingTolerance)) {
                seam++;
            }
        }
        Assert.True(
            condition: (seam > 0),
            userMessage: $"{family} radius {radius} has no ring crossing −X between the half-planes, so the seam goes unchecked"
        );
    }
    // A cross-machine pin: CI runs this law on Linux and on Windows, so a graph that leans on a platform's libm, or
    // on an order its sort leaves to chance, fails on one of them. A deliberate change to the generator re-records
    // these digests in the same change.
    [InlineData(TilingFamily.Triangular, 3, "1ce9a85732f0a55bf1443ba6cf11a51069afe72991ef1dc8194fac821692bfe7")]
    [InlineData(TilingFamily.Kagome, 3, "d7f04378eecb3e4ad6e4bd1900a5a3d6701e1a710437e8bea172db9fb798761b")]
    [InlineData(TilingFamily.TruncatedSquare, 3, "5f2d12305ac3fd8340dd1b6af8eb1f113fcfdcb4c5c4939a87e64d6945c4dff2")]
    [InlineData(TilingFamily.Rhombitrihexagonal, 3, "f334752124f358b68bbb003074e7fdc6bc64ec42da81e369251fbcd66aeab37d")]
    [InlineData(TilingFamily.TruncatedHexagonal, 4, "b9814dbcaea15550daba84ed793f20575f8db82627bf461079ced22caa889321")]
    [InlineData(TilingFamily.ElongatedTriangular, 3, "61b0a1f1ece2527d1c591a38e26140c6cf45416cc2bf06e5f85ce158f1f6cbfb")]
    [InlineData(TilingFamily.TruncatedTrihexagonal, 4, "ffcb1a12396b77e86c3129d32a4437253e0649d4efdef30c71eae8b3e675dfc3")]
    [InlineData(TilingFamily.Penrose, 3, "4334556816a196f3e872c5a764625419c754ae10299a83ff6f3d4de6865614d9")]
    [InlineData(TilingFamily.Penrose, 6, "3f74c6656b181e81ee3d0b0a87be8dc24203f5be1da6932eaabe4b24f210d3aa")]
    [Theory]
    public void TheGeneratedGraphIsTheSameBitsOnEveryMachine(TilingFamily family, int radius, string digest) {
        var tiling = Tiling(
            family: family,
            radius: radius
        );
        var graph = TilingGenerator.Generate(tiling: tiling);
        var word = new byte[4];
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        void Number(int value) {
            BinaryPrimitives.WriteInt32LittleEndian(
                destination: word,
                value: value
            );
            hash.AppendData(data: word);
        }
        void Text(string value) {
            var bytes = Encoding.UTF8.GetBytes(s: value);

            Number(value: bytes.Length);
            hash.AppendData(data: bytes);
        }

        Number(value: graph.Cells.Count);
        foreach (var cell in graph.Cells) {
            Text(value: cell.Id);
            Number(value: BitConverter.SingleToInt32Bits(value: cell.Centre.X));
            Number(value: BitConverter.SingleToInt32Bits(value: cell.Centre.Y));
            Number(value: BitConverter.SingleToInt32Bits(value: cell.Centre.Z));
        }
        Number(value: graph.Directions.Count);
        foreach (var direction in graph.Directions) {
            Text(value: direction.Name);
            Text(value: direction.Opposite);
        }
        Number(value: graph.Edges.Count);
        foreach (var edge in graph.Edges) {
            Text(value: edge.From);
            Text(value: edge.To);
            Text(value: edge.Direction.Value);
            Number(value: (edge.OneWay ? 1 : 0));
        }
        if (TilingGenerator.TryDescribePenrose(
            reason: out _,
            tiles: out var penrose,
            tiling: tiling
        )) {
            for (var tile = 0; (tile < penrose.Kinds.Count); tile++) {
                Number(value: ((int)penrose.Kinds[tile]));
                foreach (var side in penrose.Sides[tile]) {
                    Number(value: side);
                }
            }
        }
        var actual = Convert.ToHexStringLower(inArray: hash.GetHashAndReset());

        Assert.True(
            condition: (actual == digest),
            userMessage: $"{family} radius {radius} digests to {actual}, not the recorded {digest}"
        );
    }

    public static TheoryData<TilingFamily, int> Families => new() {
        { TilingFamily.Triangular, 3 }, { TilingFamily.Kagome, 3 }, { TilingFamily.TruncatedSquare, 3 },
        { TilingFamily.Rhombitrihexagonal, 3 }, { TilingFamily.TruncatedHexagonal, 3 }, { TilingFamily.ElongatedTriangular, 3 },
        { TilingFamily.TruncatedTrihexagonal, 3 }, { TilingFamily.Penrose, 3 },
    };
    public static TheoryData<TilingFamily, int> Orders => new() {
        { TilingFamily.Kagome, 4 }, { TilingFamily.TruncatedSquare, 4 }, { TilingFamily.Rhombitrihexagonal, 5 },
        { TilingFamily.TruncatedTrihexagonal, 8 }, { TilingFamily.Penrose, 4 }, { TilingFamily.Penrose, 8 },
    };
}
