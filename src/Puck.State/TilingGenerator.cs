using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.State;

/// <summary>The regular, Archimedean, and Penrose tilings a <see cref="LatticeTopology.Tiling"/> generates; every
/// tile is a regular polygon or a Penrose rhomb of edge length 1 (one <c>cellSize</c>), and the tiling is the graph
/// of tiles sharing an edge.</summary>
[JsonConverter(typeof(StrictEnumConverter<TilingFamily>))]
public enum TilingFamily : byte {
    /// <summary>3.3.3.3.3.3 — equilateral triangles, three neighbours each.</summary>
    Triangular,
    /// <summary>3.6.3.6 — hexagons and triangles, the trihexagonal (kagome) tiling.</summary>
    Kagome,
    /// <summary>4.8.8 — octagons with squares in the gaps.</summary>
    TruncatedSquare,
    /// <summary>3.4.6.4 — hexagons ringed by squares and triangles.</summary>
    Rhombitrihexagonal,
    /// <summary>3.12.12 — dodecagons with triangles in the gaps.</summary>
    TruncatedHexagonal,
    /// <summary>3.3.3.4.4 — rows of squares alternating with rows of triangles.</summary>
    ElongatedTriangular,
    /// <summary>4.6.12 — dodecagons, hexagons, and squares.</summary>
    TruncatedTrihexagonal,
    /// <summary>The Penrose P3 rhomb tiling (thick and thin rhombs), grown by inflation from a sun vertex.</summary>
    Penrose,
}
/// <summary>Materializes a <see cref="LatticeTopology.Tiling"/> as the <see cref="LatticeTopology.Graph"/> it is:
/// tiles within the radius, their centres, and one direction slot per outward edge normal, so the tiling compiles
/// through the graph path and answers every board query on the same terms. Boot-time geometry in doubles, computed
/// only with addition, subtraction, multiplication, division, square root, and comparison — operations IEEE 754
/// rounds identically everywhere — so the graph is the same bits on every machine and runtime: every angle a family
/// uses is a multiple of 7.5° or 18°, whose cosine and sine are closed-form square-root expressions, and no libm
/// transcendental is ever called.</summary>
public static class TilingGenerator {
    private const double Phi = 1.618033988749895;
    private const double Quantum = 1e-6;
    private const double Sqrt2 = 1.4142135623730951;
    private const double Sqrt3 = 1.7320508075688772;

    // The cosine and sine of the angles in [0°, 45°] the families reach, each one fixed expression of square roots
    // and the four operations, initialized in declaration order: 15° from √6 and √2, 7.5° by halving 15°, 18° and 36°
    // from √5, 22.5° from √2, and 37.5° as 45° less 7.5°.
    private static readonly double Sqrt5 = Math.Sqrt(d: 5.0);
    private static readonly double Cos15 = ((Math.Sqrt(d: 6.0) + Sqrt2) / 4.0);
    private static readonly double Sin15 = ((Math.Sqrt(d: 6.0) - Sqrt2) / 4.0);
    private static readonly double Cos7Half = Math.Sqrt(d: ((1.0 + Cos15) / 2.0));
    private static readonly double Sin7Half = (Sin15 / (2.0 * Cos7Half));
    private static readonly double Cos18 = (Math.Sqrt(d: (10.0 + (2.0 * Sqrt5))) / 4.0);
    private static readonly double Sin18 = ((Sqrt5 - 1.0) / 4.0);
    private static readonly double Cos22Half = (Math.Sqrt(d: (2.0 + Sqrt2)) / 2.0);
    private static readonly double Sin22Half = (Math.Sqrt(d: (2.0 - Sqrt2)) / 2.0);
    private static readonly double Cos36 = ((1.0 + Sqrt5) / 4.0);
    private static readonly double Sin36 = (Math.Sqrt(d: (10.0 - (2.0 * Sqrt5))) / 4.0);
    private static readonly double Cos37Half = ((Sqrt2 / 2.0) * (Cos7Half + Sin7Half));
    private static readonly double Sin37Half = ((Sqrt2 / 2.0) * (Cos7Half - Sin7Half));
    // Every heading an edge normal can take, in half-degrees: the multiples of 7.5° and of 18°, at least 1.5° apart.
    private static readonly (int HalfDegrees, Point Unit)[] Headings = EveryHeading();

    /// <summary>The most inflation steps a Penrose patch takes: φ⁹ ≈ 76 edge lengths of radius.</summary>
    public const int MaxPenroseInflations = 9;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LatticeTopology.Tiling, LatticeTopology.Graph> Graphs = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LatticeTopology.Tiling, PenroseTiles> PenroseTilesValue = new();

    private static LatticeTopology.Graph Build(LatticeTopology.Tiling tiling) {
        var tiles = Sorted(tiling: tiling);
        var edgeOwners = ShareEdges(
            tileVertexIds: out var tileVertexIds,
            tiles: tiles
        );

        // Every edge's outward normal, snapped to the nearest heading a family lays down, names the direction slot.
        var directionDegrees = new SortedSet<int>();
        var normals = new int[tiles.Count][];

        for (var index = 0; (index < tiles.Count); index++) {
            var tile = tiles[index];

            normals[index] = new int[tile.Vertices.Length];
            for (var v = 0; (v < tile.Vertices.Length); v++) {
                var a = tile.Vertices[v];
                var b = tile.Vertices[((v + 1) % tile.Vertices.Length)];
                // The side's true normal, turned to point away from the centroid: for a rhomb the centroid-to-midpoint
                // direction is not perpendicular to the side, and only the true normal is exactly opposite across it.
                var side = (b - a);
                var normal = new Point(
                    X: side.Y,
                    Y: -side.X
                );
                var midpoint = ((a + b) * 0.5);
                var outwardness = (((midpoint.X - tile.Centre.X) * normal.X) + ((midpoint.Y - tile.Centre.Y) * normal.Y));
                var outward = ((outwardness >= 0)
                    ? normal
                    : (normal * -1.0)
                );
                var degrees = Degrees(
                    direction: outward,
                    family: tiling.Family
                );

                normals[index][v] = degrees;
                _ = directionDegrees.Add(item: degrees);
            }
        }
        var directions = new List<GraphDirection>(capacity: directionDegrees.Count);

        foreach (var degrees in directionDegrees) {
            var opposite = ((degrees + 180) % 360);

            if (!directionDegrees.Contains(item: opposite)) {
                throw new InvalidOperationException(message: $"tiling {tiling.Family}: edge normal {degrees}° has no opposite among the tiling's normals");
            }
            directions.Add(item: new GraphDirection(
                Name: DirectionName(degrees: degrees),
                Opposite: DirectionName(degrees: opposite)
            ));
        }

        var cells = new List<GraphCell>(capacity: tiles.Count);

        for (var index = 0; (index < tiles.Count); index++) {
            cells.Add(item: new GraphCell(
                Id: $"t{index}",
                Centre: new DocumentVector3(
                    x: ((float)tiles[index].Centre.X),
                    y: 0f,
                    z: ((float)tiles[index].Centre.Y)
                )
            ));
        }
        var edges = new List<GraphEdge>();

        foreach (var owners in edgeOwners.Values) {
            if (owners.Count != 2) {
                continue;
            }
            var (fromTile, fromEdge) = owners[0];
            var (toTile, _) = owners[1];
            // One two-way edge per shared side: the reverse fills the neighbour's slot along the opposite normal.
            edges.Add(item: new GraphEdge(
                From: $"t{fromTile}",
                To: $"t{toTile}",
                Direction: CellName.Parse(candidate: DirectionName(degrees: normals[fromTile][fromEdge]))
            ));
        }

        return new LatticeTopology.Graph(
            Name: tiling.Name,
            Origin: tiling.Origin,
            CellSize: tiling.CellSize,
            Cells: cells,
            Directions: directions,
            Edges: edges
        );
    }
    private static Point[] Counterclockwise(Point[] vertices) {
        var area = 0.0;

        for (var index = 0; (index < vertices.Length); index++) {
            var a = vertices[index];
            var b = vertices[((index + 1) % vertices.Length)];

            area += ((a.X * b.Y) - (b.X * a.Y));
        }
        if (area < 0) {
            Array.Reverse(array: vertices);
        }
        return vertices;
    }
    // Orders two quantized directions by angle in [0, 2π), counterclockwise from +X: the half of the turn first, so a
    // direction on −X is exactly π, then the sign of the cross product, which within one half is the angle order.
    private static int CompareAngle(long ax, long ay, long bx, long by) {
        var aHalf = HalfTurn(
            x: ax,
            y: ay
        );
        var bHalf = HalfTurn(
            x: bx,
            y: by
        );

        return ((aHalf != bHalf)
            ? aHalf.CompareTo(value: bHalf)
            : (((Int128)ay) * bx).CompareTo(value: (((Int128)ax) * by))
        );
    }
    // The whole degrees of the heading nearest a direction: the greatest dot product over every heading a family can
    // lay down, which stand at least 1.5° apart, so rounding many orders finer than that cannot change the winner.
    private static int Degrees(Point direction, TilingFamily family) {
        var nearest = Headings[0];
        var best = double.NegativeInfinity;

        foreach (var heading in Headings) {
            var dot = ((heading.Unit.X * direction.X) + (heading.Unit.Y * direction.Y));

            if (dot > best) {
                best = dot;
                nearest = heading;
            }
        }
        if ((nearest.HalfDegrees % 2) != 0) {
            throw new InvalidOperationException(message: $"tiling {family}: an edge normal at {(nearest.HalfDegrees / 2)}.5° has no whole-degree slot name");
        }
        return (nearest.HalfDegrees / 2);
    }
    private static string DirectionName(int degrees) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"a{degrees}"
    );
    private static (int HalfDegrees, Point Unit)[] EveryHeading() {
        var headings = new List<(int HalfDegrees, Point Unit)>();

        for (var half = 0; (half < 720); half += 3) {
            if (((half % 15) == 0) || ((half % 36) == 0)) {
                headings.Add(item: (half, Unit(halfDegrees: half)));
            }
        }
        return [.. headings];
    }
    // 0 for a direction whose angle lies in [0, π), 1 for [π, 2π); the origin counts as angle 0.
    private static int HalfTurn(long x, long y) => (((y > 0) || ((y == 0) && (x >= 0)))
        ? 0
        : 1
    );
    // An angle in degrees as whole half-degrees in [0, 720): every angle a family names is a multiple of 7.5° or 18°,
    // exact in a double, so doubling it is exact too.
    private static int HalfDegrees(double degrees) {
        var half = (degrees * 2.0);

        if (half != Math.Floor(d: half)) {
            throw new InvalidOperationException(message: $"{degrees}° is not a whole number of half-degrees");
        }
        return (((((int)half) % 720) + 720) % 720);
    }
    // The cosine and sine of an angle in [0°, 45°] a family reaches, by its half-degrees.
    private static (double Cos, double Sin) Octant(int halfDegrees) => halfDegrees switch {
        0 => (1.0, 0.0),
        15 => (Cos7Half, Sin7Half),
        30 => (Cos15, Sin15),
        36 => (Cos18, Sin18),
        45 => (Cos22Half, Sin22Half),
        60 => ((Sqrt3 / 2.0), 0.5),
        72 => (Cos36, Sin36),
        75 => (Cos37Half, Sin37Half),
        90 => ((Sqrt2 / 2.0), (Sqrt2 / 2.0)),
        _ => throw new InvalidOperationException(message: $"{(halfDegrees / 2.0)}° is not an angle a tiling family uses"),
    };
    // The tiles a family lays down, in the order the cell ordinals read them: rings outward from the origin, then
    // counterclockwise from +X, so consecutive ordinals cluster and the order is a function of the geometry alone. A
    // ring is a run of centres whose distances step by no more than the quantum, so a ring is never split by where a
    // rounding boundary falls; the angle compares exactly on the quantized centre.
    private static List<Tile> Sorted(LatticeTopology.Tiling tiling) {
        var radius = Math.Max(
            val1: 1,
            val2: tiling.Radius
        );
        var tiles = ((tiling.Family == TilingFamily.Penrose)
            ? Penrose(radius: radius)
            : Periodic(
                family: tiling.Family,
                radius: radius
            )
        );
        var keyed = new (double Distance, int Ring, long X, long Y, Tile Tile)[tiles.Count];

        for (var index = 0; (index < tiles.Count); index++) {
            var centre = tiles[index].Centre;

            keyed[index] = (centre.Length, 0, Quantize(value: centre.X), Quantize(value: centre.Y), tiles[index]);
        }
        Array.Sort(
            array: keyed,
            comparison: static (a, b) => a.Distance.CompareTo(value: b.Distance)
        );
        for (var index = 1; (index < keyed.Length); index++) {
            keyed[index].Ring = (((keyed[index].Distance - keyed[(index - 1)].Distance) > Quantum)
                ? (keyed[(index - 1)].Ring + 1)
                : keyed[(index - 1)].Ring
            );
        }
        Array.Sort(
            array: keyed,
            comparison: static (a, b) => ((a.Ring != b.Ring)
                ? a.Ring.CompareTo(value: b.Ring)
                : CompareAngle(
                    ax: a.X,
                    ay: a.Y,
                    bx: b.X,
                    by: b.Y
                )
            )
        );

        return [.. keyed.Select(selector: static key => key.Tile)];
    }
    // Vertices merge by quantized position; an edge is a vertex pair; two tiles sharing an edge are neighbours.
    private static Dictionary<(int, int), List<(int Tile, int Edge)>> ShareEdges(List<Tile> tiles, out int[][] tileVertexIds) {
        var edgeOwners = new Dictionary<(int, int), List<(int Tile, int Edge)>>();
        var vertexIds = new Dictionary<(long, long), int>();

        tileVertexIds = new int[tiles.Count][];

        for (var index = 0; (index < tiles.Count); index++) {
            var vertices = tiles[index].Vertices;
            var ids = new int[vertices.Length];

            for (var v = 0; (v < vertices.Length); v++) {
                var key = (Quantize(value: vertices[v].X), Quantize(value: vertices[v].Y));

                if (!vertexIds.TryGetValue(
                    key: key,
                    value: out var id
                )) {
                    id = vertexIds.Count;
                    vertexIds[key] = id;
                }
                ids[v] = id;
            }
            tileVertexIds[index] = ids;
            for (var v = 0; (v < ids.Length); v++) {
                var a = ids[v];
                var b = ids[((v + 1) % ids.Length)];
                var edge = (Math.Min(
                    val1: a,
                    val2: b
                ), Math.Max(
                    val1: a,
                    val2: b
                ));

                if (!edgeOwners.TryGetValue(
                    key: edge,
                    value: out var owners
                )) {
                    owners = [];
                    edgeOwners[edge] = owners;
                }
                owners.Add(item: (index, v));
            }
        }

        return edgeOwners;
    }
    private static List<Tile> Penrose(int radius) {
        // Each inflation splits every triangle into ones 1/φ the size; scale, φ^inflations kept as a running product,
        // grows them back to edge 1 and so sets how far the patch reaches.
        var inflations = 0;
        var scale = 1.0;

        while ((inflations < MaxPenroseInflations) && (scale < (radius + 2))) {
            inflations++;
            scale *= Phi;
        }
        var halves = new List<Half>(capacity: 10);

        for (var index = 0; (index < 10); index++) {
            var b = Point.Polar(
                degrees: (36 * index),
                radius: 1
            );
            var c = Point.Polar(
                degrees: (36 * (index + 1)),
                radius: 1
            );

            halves.Add(item: (((index % 2) == 0)
                ? new Half(
                    true,
                    new Point(
                        X: 0,
                        Y: 0
                    ),
                    b,
                    c
                )
                : new Half(
                    true,
                    new Point(
                        X: 0,
                        Y: 0
                    ),
                    c,
                    b
                )));
        }
        for (var step = 0; (step < inflations); step++) {
            var next = new List<Half>(capacity: (halves.Count * 3));

            foreach (var half in halves) {
                var (a, b, c) = (half.A, half.B, half.C);
                if (half.Acute) {
                    var p = (a + ((b - a) * (1.0 / Phi)));

                    next.Add(item: new Half(
                        A: c,
                        Acute: true,
                        B: p,
                        C: b
                    ));
                    next.Add(item: new Half(
                        A: p,
                        Acute: false,
                        B: c,
                        C: a
                    ));
                } else {
                    var q = (b + ((a - b) * (1.0 / Phi)));
                    var r = (b + ((c - b) * (1.0 / Phi)));

                    next.Add(item: new Half(
                        A: r,
                        Acute: false,
                        B: c,
                        C: a
                    ));
                    next.Add(item: new Half(
                        A: q,
                        Acute: false,
                        B: r,
                        C: b
                    ));
                    next.Add(item: new Half(
                        A: r,
                        Acute: true,
                        B: q,
                        C: a
                    ));
                }
            }
            halves = next;
        }
        var byBase = new Dictionary<((long, long), (long, long), bool), List<Half>>();

        foreach (var half in halves) {
            var scaled = new Half(
                half.Acute,
                (half.A * scale),
                (half.B * scale),
                (half.C * scale)
            );
            var kb = (Quantize(value: scaled.B.X), Quantize(value: scaled.B.Y));
            var kc = (Quantize(value: scaled.C.X), Quantize(value: scaled.C.Y));
            var key = ((kb.CompareTo(other: kc) <= 0)
                ? (kb, kc, scaled.Acute)
                : (kc, kb, scaled.Acute)
            );

            if (!byBase.TryGetValue(
                key: key,
                value: out var twins
            )) {
                twins = [];
                byBase[key] = twins;
            }
            twins.Add(item: scaled);
        }
        var tiles = new List<Tile>();

        foreach (var twins in byBase.Values) {
            if (twins.Count != 2) {
                continue;
            }
            var first = twins[0];
            var second = twins[1];
            // Rhomb A, B, A', C: the two apexes mirror across the shared base B–C.
            var vertices = new[] { first.A, first.B, second.A, first.C };
            var centre = ((first.A + second.A) * 0.5);

            if (centre.Length <= (radius + Quantum)) {
                tiles.Add(item: new Tile(
                    Counterclockwise(vertices: vertices),
                    centre
                ));
            }
        }
        return tiles;
    }
    private static List<Tile> Periodic(TilingFamily family, int radius) {
        var (a, b, cell) = UnitCell(family: family);
        var reach = (((int)Math.Ceiling(a: (radius / Math.Min(
            val1: a.Length,
            val2: b.Length
        )))) + 2);
        var tiles = new List<Tile>();

        for (var i = -reach; (i <= reach); i++) {
            for (var j = -reach; (j <= reach); j++) {
                var offset = ((a * i) + (b * j));

                foreach (var prototile in cell) {
                    var centre = (prototile.Centre + offset);

                    if (centre.Length <= (radius + Quantum)) {
                        tiles.Add(item: Regular(
                            sides: prototile.Sides,
                            centre: centre,
                            firstVertexDegrees: prototile.FirstVertexDegrees
                        ));
                    }
                }
            }
        }
        return tiles;
    }
    private static long Quantize(double value) => ((long)Math.Round(a: (value / Quantum)));
    private static Tile Regular(int sides, Point centre, double firstVertexDegrees) {
        var circumradius = (1.0 / (2.0 * Unit(halfDegrees: HalfDegrees(degrees: (180.0 / sides))).Y));
        var vertices = new Point[sides];

        for (var index = 0; (index < sides); index++) {
            vertices[index] = (centre + Point.Polar(
                degrees: (firstVertexDegrees + ((360.0 * index) / sides)),
                radius: circumradius
            ));
        }
        return new Tile(
            Centre: centre,
            Vertices: vertices
        );
    }
    // The unit vector at an angle in half-degrees, counterclockwise from +X: the first-octant value, its swap past 45°,
    // then a quarter-turn rotation by negation and swap, so symmetric angles get bitwise-symmetric vectors.
    private static Point Unit(int halfDegrees) {
        var quadrant = (halfDegrees / 180);
        var within = (halfDegrees % 180);
        var mirrored = Octant(halfDegrees: ((within <= 90)
            ? within
            : (180 - within)
        ));

        var (cos, sin) = ((within <= 90)
            ? mirrored
            : (mirrored.Sin, mirrored.Cos)
        );

        return quadrant switch {
            0 => new Point(
                X: cos,
                Y: sin
            ),
            1 => new Point(
                X: -sin,
                Y: cos
            ),
            2 => new Point(
                X: -cos,
                Y: -sin
            ),
            _ => new Point(
                X: sin,
                Y: -cos
            ),
        };
    }
    // Each periodic family is a lattice (two translation vectors) and the prototiles of one cell: a regular polygon's
    // side count, centre, and the angle of its first vertex, all in edge-length units with the polygons edge to edge.
    private static (Point A, Point B, Prototile[] Cell) UnitCell(TilingFamily family) {
        const double Hex = (Sqrt3 / 2.0);

        return family switch {
            TilingFamily.Triangular => (new Point(
            X: 1,
            Y: 0
        ), new Point(
            X: 0.5,
            Y: Hex
        ), [
                new Prototile(
                3,
                new Point(
                    X: 0.5,
                    Y: (Sqrt3 / 6.0)
                ),
                90
            ), new Prototile(
                3,
                new Point(
                    X: 1,
                    Y: (Sqrt3 / 3.0)
                ),
                270
            )]),
            TilingFamily.Kagome => (new Point(
            X: 2,
            Y: 0
        ), new Point(
            X: 1,
            Y: Sqrt3
        ), [
                new Prototile(
                6,
                new Point(
                    X: 0,
                    Y: 0
                ),
                0
            ), new Prototile(
                3,
                new Point(
                    X: 1,
                    Y: (Sqrt3 / 3.0)
                ),
                270
            ), new Prototile(
                3,
                new Point(
                    X: 1,
                    Y: (-Sqrt3 / 3.0)
                ),
                90
            )]),
            TilingFamily.TruncatedSquare => (new Point(
            X: (1 + Sqrt2),
            Y: 0
        ), new Point(
            X: 0,
            Y: (1 + Sqrt2)
        ), [
                new Prototile(
                8,
                new Point(
                    X: 0,
                    Y: 0
                ),
                22.5
            ), new Prototile(
                4,
                new Point(
                    X: ((1 + Sqrt2) / 2.0),
                    Y: ((1 + Sqrt2) / 2.0)
                ),
                0
            )]),
            TilingFamily.Rhombitrihexagonal => (new Point(
            X: (1 + Sqrt3),
            Y: 0
        ), Point.Polar(
            degrees: 60,
            radius: (1 + Sqrt3)
        ), [
                new Prototile(
                6,
                new Point(
                    X: 0,
                    Y: 0
                ),
                30
            ),
                new Prototile(
                4,
                Point.Polar(
                    degrees: 0,
                    radius: (Hex + 0.5)
                ),
                45
            ), new Prototile(
                4,
                Point.Polar(
                    degrees: 60,
                    radius: (Hex + 0.5)
                ),
                105
            ), new Prototile(
                4,
                Point.Polar(
                    degrees: 120,
                    radius: (Hex + 0.5)
                ),
                165
            ),
                new Prototile(
                3,
                Point.Polar(
                    degrees: 30,
                    radius: (1 + (1.0 / Sqrt3))
                ),
                90
            ), new Prototile(
                3,
                Point.Polar(
                    degrees: 90,
                    radius: (1 + (1.0 / Sqrt3))
                ),
                150
            )]),
            TilingFamily.TruncatedHexagonal => (new Point(
            X: (2 + Sqrt3),
            Y: 0
        ), Point.Polar(
            degrees: 60,
            radius: (2 + Sqrt3)
        ), [
                new Prototile(
                12,
                new Point(
                    X: 0,
                    Y: 0
                ),
                15
            ),
                new Prototile(
                3,
                Point.Polar(
                    degrees: 30,
                    radius: ((2 + Sqrt3) / Sqrt3)
                ),
                30
            ), new Prototile(
                3,
                Point.Polar(
                    degrees: 30,
                    radius: ((2.0 * (2 + Sqrt3)) / Sqrt3)
                ),
                210
            )]),
            TilingFamily.ElongatedTriangular => (new Point(
            X: 1,
            Y: 0
        ), new Point(
            X: 0.5,
            Y: (1 + Hex)
        ), [
                new Prototile(
                4,
                new Point(
                    X: 0.5,
                    Y: 0.5
                ),
                45
            ), new Prototile(
                3,
                new Point(
                    X: 0.5,
                    Y: (1 + (Sqrt3 / 6.0))
                ),
                90
            ), new Prototile(
                3,
                new Point(
                    X: 1,
                    Y: (1 + (Sqrt3 / 3.0))
                ),
                270
            )]),
            TilingFamily.TruncatedTrihexagonal => (new Point(
            X: (3 + Sqrt3),
            Y: 0
        ), Point.Polar(
            degrees: 60,
            radius: (3 + Sqrt3)
        ), [
                new Prototile(
                12,
                new Point(
                    X: 0,
                    Y: 0
                ),
                15
            ),
                new Prototile(
                4,
                Point.Polar(
                    degrees: 0,
                    radius: (((2 + Sqrt3) / 2.0) + 0.5)
                ),
                45
            ), new Prototile(
                4,
                Point.Polar(
                    degrees: 60,
                    radius: (((2 + Sqrt3) / 2.0) + 0.5)
                ),
                105
            ), new Prototile(
                4,
                Point.Polar(
                    degrees: 120,
                    radius: (((2 + Sqrt3) / 2.0) + 0.5)
                ),
                165
            ),
                new Prototile(
                6,
                Point.Polar(
                    degrees: 30,
                    radius: (((2 + Sqrt3) / 2.0) + Hex)
                ),
                60
            ), new Prototile(
                6,
                Point.Polar(
                    degrees: 90,
                    radius: (((2 + Sqrt3) / 2.0) + Hex)
                ),
                120
            )]),
            _ => throw new InvalidOperationException(message: $"'{family}' is not a periodic tiling family"),
        };
    }
    // A rhomb of edge length 1 with acute angle t has diagonals 2*sin(t/2) and 2*cos(t/2): 0.618 and 1.902 for the
    // thin rhomb's 36 degrees, 1.176 and 1.618 for the fat rhomb's 72. A short diagonal under 1 therefore names the
    // thin rhomb with the quantum to spare.
    private static PenroseRhomb Rhomb(Tile tile) {
        var vertices = tile.Vertices;
        var shorter = Math.Min(
            val1: (vertices[2] - vertices[0]).Length,
            val2: (vertices[3] - vertices[1]).Length
        );

        return ((shorter < 1.0)
            ? PenroseRhomb.Thin
            : PenroseRhomb.Fat
        );
    }
    private static PenroseTiles DescribePenrose(LatticeTopology.Tiling tiling) {
        var tiles = Sorted(tiling: tiling);
        var edgeOwners = ShareEdges(
            tileVertexIds: out _,
            tiles: tiles
        );
        var kinds = new PenroseRhomb[tiles.Count];
        var sides = new int[tiles.Count][];

        for (var index = 0; (index < tiles.Count); index++) {
            kinds[index] = Rhomb(tile: tiles[index]);
            sides[index] = [-1, -1, -1, -1];
        }

        foreach (var owners in edgeOwners.Values) {
            if (owners.Count != 2) {
                continue;
            }

            var (first, firstSide) = owners[0];
            var (second, secondSide) = owners[1];

            sides[first][firstSide] = second;
            sides[second][secondSide] = first;
        }

        var shared = new IReadOnlyList<int>[tiles.Count];

        for (var index = 0; (index < tiles.Count); index++) {
            shared[index] = sides[index];
        }

        return new PenroseTiles(
            Kinds: kinds,
            Sides: shared
        );
    }

    /// <summary>Returns the graph a tiling generates, computed once per record instance.</summary>
    /// <param name="tiling">The tiling.</param>
    public static LatticeTopology.Graph Generate(LatticeTopology.Tiling tiling) {
        ArgumentNullException.ThrowIfNull(argument: tiling);

        return Graphs.GetValue(
            key: tiling,
            createValueCallback: static t => Build(tiling: t)
        );
    }
    /// <summary>Returns a Penrose patch's per-tile facts, computed once per record instance.</summary>
    /// <param name="tiling">The tiling.</param>
    /// <param name="tiles">The patch's tiles in cell order, on success.</param>
    /// <param name="reason">Why the tiling carries no rhombs, or empty on success.</param>
    /// <returns><see langword="true"/> when the tiling is a Penrose one.</returns>
    public static bool TryDescribePenrose(LatticeTopology.Tiling tiling, [NotNullWhen(true)] out PenroseTiles? tiles, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: tiling);

        if (tiling.Family != TilingFamily.Penrose) {
            reason = $"tiling '{tiling.Name}' lays down {tiling.Family} tiles, and only a Penrose tiling carries rhomb kinds and ribbons";
            tiles = null;

            return false;
        }

        reason = string.Empty;
        tiles = PenroseTilesValue.GetValue(
            key: tiling,
            createValueCallback: static t => DescribePenrose(tiling: t)
        );

        return true;
    }

    // A polygon: its vertices in cyclic order and its centroid, in edge-length units.
    private readonly record struct Tile(Point[] Vertices, Point Centre);
    private readonly record struct Point(double X, double Y) {
        public static Point operator +(Point a, Point b) => new(
            X: (a.X + b.X),
            Y: (a.Y + b.Y)
        );
        public static Point operator -(Point a, Point b) => new(
            X: (a.X - b.X),
            Y: (a.Y - b.Y)
        );
        public static Point operator *(Point a, double s) => new(
            X: (a.X * s),
            Y: (a.Y * s)
        );

        public double Length => Math.Sqrt(d: ((X * X) + (Y * Y)));

        public static Point Polar(double radius, double degrees) => (Unit(halfDegrees: HalfDegrees(degrees: degrees)) * radius);
    }
    private readonly record struct Prototile(int Sides, Point Centre, double FirstVertexDegrees);
    // Robinson-triangle inflation from a sun of ten acute halves; twins sharing a base merge into a rhomb whose edges
    // are the triangles' equal sides, scaled so that edge is 1.
    private readonly record struct Half(bool Acute, Point A, Point B, Point C);
}
