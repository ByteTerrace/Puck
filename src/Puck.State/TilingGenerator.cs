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
/// through the graph path and answers every board query on the same terms. Boot-time geometry in doubles — the
/// coordinates are closed-form and the vertex merge quantizes to a fine grid, so the graph is the same on every
/// machine.</summary>
public static class TilingGenerator {
    /// <summary>The most inflation steps a Penrose patch takes: φ⁹ ≈ 76 edge lengths of radius.</summary>
    public const int MaxPenroseInflations = 9;
    private const double Sqrt2 = 1.4142135623730951;
    private const double Sqrt3 = 1.7320508075688772;
    private const double Phi = 1.618033988749895;
    private const double Quantum = 1e-6;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LatticeTopology.Tiling, LatticeTopology.Graph> s_graphs = new();

    /// <summary>Returns the graph a tiling generates, computed once per record instance.</summary>
    /// <param name="tiling">The tiling.</param>
    public static LatticeTopology.Graph Generate(LatticeTopology.Tiling tiling) {
        ArgumentNullException.ThrowIfNull(argument: tiling);

        return s_graphs.GetValue(key: tiling, createValueCallback: static t => Build(tiling: t));
    }

    // A polygon: its vertices in cyclic order and its centroid, in edge-length units.
    private readonly record struct Tile(Point[] Vertices, Point Centre);
    private readonly record struct Point(double X, double Y) {
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator *(Point a, double s) => new(a.X * s, a.Y * s);
        public double Length => Math.Sqrt((X * X) + (Y * Y));
        public static Point Polar(double radius, double degrees) => new(radius * Math.Cos(degrees * Math.PI / 180.0), radius * Math.Sin(degrees * Math.PI / 180.0));
    }
    private readonly record struct Prototile(int Sides, Point Centre, double FirstVertexDegrees);

    private static LatticeTopology.Graph Build(LatticeTopology.Tiling tiling) {
        var radius = Math.Max(1, tiling.Radius);
        var tiles = ((tiling.Family == TilingFamily.Penrose) ? Penrose(radius: radius) : Periodic(family: tiling.Family, radius: radius));
        // Rings outward from the origin, then by angle: consecutive ordinals cluster, and the order is a pure function
        // of the geometry.
        tiles.Sort(comparison: static (a, b) => {
            var byDistance = Quantize(a.Centre.Length).CompareTo(Quantize(b.Centre.Length));
            return (byDistance != 0) ? byDistance : Quantize(Math.Atan2(a.Centre.Y, a.Centre.X)).CompareTo(Quantize(Math.Atan2(b.Centre.Y, b.Centre.X)));
        });

        // Vertices merge by quantized position; an edge is a vertex pair; two tiles sharing an edge are neighbours.
        var vertexIds = new Dictionary<(long, long), int>();
        var edgeOwners = new Dictionary<(int, int), List<(int Tile, int Edge)>>();
        var tileVertexIds = new int[tiles.Count][];
        for (var index = 0; index < tiles.Count; index++) {
            var vertices = tiles[index].Vertices;
            var ids = new int[vertices.Length];
            for (var v = 0; v < vertices.Length; v++) {
                var key = (Quantize(vertices[v].X), Quantize(vertices[v].Y));
                if (!vertexIds.TryGetValue(key, out var id)) {
                    id = vertexIds.Count;
                    vertexIds[key] = id;
                }
                ids[v] = id;
            }
            tileVertexIds[index] = ids;
            for (var v = 0; v < ids.Length; v++) {
                var a = ids[v];
                var b = ids[(v + 1) % ids.Length];
                var edge = (Math.Min(a, b), Math.Max(a, b));
                if (!edgeOwners.TryGetValue(edge, out var owners)) {
                    owners = [];
                    edgeOwners[edge] = owners;
                }
                owners.Add((index, v));
            }
        }

        // Every edge's outward normal angle, quantized to the family's own angle set, names the direction slot.
        var directionDegrees = new SortedSet<int>();
        var normals = new int[tiles.Count][];
        for (var index = 0; index < tiles.Count; index++) {
            var tile = tiles[index];
            normals[index] = new int[tile.Vertices.Length];
            for (var v = 0; v < tile.Vertices.Length; v++) {
                var a = tile.Vertices[v];
                var b = tile.Vertices[(v + 1) % tile.Vertices.Length];
                // The side's true normal, turned to point away from the centroid: for a rhomb the centroid-to-midpoint
                // direction is not perpendicular to the side, and only the true normal is exactly opposite across it.
                var side = b - a;
                var normal = new Point(side.Y, -side.X);
                var midpoint = (a + b) * 0.5;
                var outwardness = ((midpoint.X - tile.Centre.X) * normal.X) + ((midpoint.Y - tile.Centre.Y) * normal.Y);
                var outward = (outwardness >= 0) ? normal : (normal * -1.0);
                var degrees = ((int)Math.Round(Math.Atan2(outward.Y, outward.X) * 180.0 / Math.PI) + 360) % 360;
                normals[index][v] = degrees;
                _ = directionDegrees.Add(degrees);
            }
        }
        var directions = new List<GraphDirection>(capacity: directionDegrees.Count);
        foreach (var degrees in directionDegrees) {
            var opposite = (degrees + 180) % 360;
            if (!directionDegrees.Contains(opposite)) {
                throw new InvalidOperationException($"tiling {tiling.Family}: edge normal {degrees}° has no opposite among the tiling's normals");
            }
            directions.Add(new GraphDirection(Name: DirectionName(degrees), Opposite: DirectionName(opposite)));
        }

        var cells = new List<GraphCell>(capacity: tiles.Count);
        for (var index = 0; index < tiles.Count; index++) {
            cells.Add(new GraphCell(Id: $"t{index}", Centre: new DocumentVector3(x: (float)tiles[index].Centre.X, y: 0f, z: (float)tiles[index].Centre.Y)));
        }
        var edges = new List<GraphEdge>();
        foreach (var owners in edgeOwners.Values) {
            if (owners.Count != 2) {
                continue;
            }
            var (fromTile, fromEdge) = owners[0];
            var (toTile, _) = owners[1];
            // One two-way edge per shared side: the reverse fills the neighbour's slot along the opposite normal.
            edges.Add(new GraphEdge(From: $"t{fromTile}", To: $"t{toTile}", Direction: DirectionName(normals[fromTile][fromEdge])));
        }

        return new LatticeTopology.Graph(Name: tiling.Name, Origin: tiling.Origin, CellSize: tiling.CellSize, Cells: cells, Directions: directions, Edges: edges);
    }

    private static string DirectionName(int degrees) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"a{degrees}");
    private static long Quantize(double value) => (long)Math.Round(value / Quantum);

    private static Tile Regular(int sides, Point centre, double firstVertexDegrees) {
        var circumradius = 1.0 / (2.0 * Math.Sin(Math.PI / sides));
        var vertices = new Point[sides];
        for (var index = 0; index < sides; index++) {
            vertices[index] = centre + Point.Polar(circumradius, firstVertexDegrees + (360.0 * index / sides));
        }
        return new Tile(vertices, centre);
    }

    // Each periodic family is a lattice (two translation vectors) and the prototiles of one cell: a regular polygon's
    // side count, centre, and the angle of its first vertex, all in edge-length units with the polygons edge to edge.
    private static (Point A, Point B, Prototile[] Cell) UnitCell(TilingFamily family) {
        const double Hex = Sqrt3 / 2.0;
        return family switch {
            TilingFamily.Triangular => (new Point(1, 0), new Point(0.5, Hex), [
                new Prototile(3, new Point(0.5, Sqrt3 / 6.0), 90), new Prototile(3, new Point(1, Sqrt3 / 3.0), 270)]),
            TilingFamily.Kagome => (new Point(2, 0), new Point(1, Sqrt3), [
                new Prototile(6, new Point(0, 0), 0), new Prototile(3, new Point(1, Sqrt3 / 3.0), 270), new Prototile(3, new Point(1, -Sqrt3 / 3.0), 90)]),
            TilingFamily.TruncatedSquare => (new Point(1 + Sqrt2, 0), new Point(0, 1 + Sqrt2), [
                new Prototile(8, new Point(0, 0), 22.5), new Prototile(4, new Point((1 + Sqrt2) / 2.0, (1 + Sqrt2) / 2.0), 0)]),
            TilingFamily.Rhombitrihexagonal => (new Point(1 + Sqrt3, 0), Point.Polar(1 + Sqrt3, 60), [
                new Prototile(6, new Point(0, 0), 30),
                new Prototile(4, Point.Polar(Hex + 0.5, 0), 45), new Prototile(4, Point.Polar(Hex + 0.5, 60), 105), new Prototile(4, Point.Polar(Hex + 0.5, 120), 165),
                new Prototile(3, Point.Polar(1 + (1.0 / Sqrt3), 30), 90), new Prototile(3, Point.Polar(1 + (1.0 / Sqrt3), 90), 150)]),
            TilingFamily.TruncatedHexagonal => (new Point(2 + Sqrt3, 0), Point.Polar(2 + Sqrt3, 60), [
                new Prototile(12, new Point(0, 0), 15),
                new Prototile(3, Point.Polar((2 + Sqrt3) / Sqrt3, 30), 30), new Prototile(3, Point.Polar(2.0 * (2 + Sqrt3) / Sqrt3, 30), 210)]),
            TilingFamily.ElongatedTriangular => (new Point(1, 0), new Point(0.5, 1 + Hex), [
                new Prototile(4, new Point(0.5, 0.5), 45), new Prototile(3, new Point(0.5, 1 + (Sqrt3 / 6.0)), 90), new Prototile(3, new Point(1, 1 + (Sqrt3 / 3.0)), 270)]),
            TilingFamily.TruncatedTrihexagonal => (new Point(3 + Sqrt3, 0), Point.Polar(3 + Sqrt3, 60), [
                new Prototile(12, new Point(0, 0), 15),
                new Prototile(4, Point.Polar(((2 + Sqrt3) / 2.0) + 0.5, 0), 45), new Prototile(4, Point.Polar(((2 + Sqrt3) / 2.0) + 0.5, 60), 105), new Prototile(4, Point.Polar(((2 + Sqrt3) / 2.0) + 0.5, 120), 165),
                new Prototile(6, Point.Polar(((2 + Sqrt3) / 2.0) + Hex, 30), 60), new Prototile(6, Point.Polar(((2 + Sqrt3) / 2.0) + Hex, 90), 120)]),
            _ => throw new InvalidOperationException($"'{family}' is not a periodic tiling family"),
        };
    }

    private static List<Tile> Periodic(TilingFamily family, int radius) {
        var (a, b, cell) = UnitCell(family: family);
        var reach = (int)Math.Ceiling(radius / Math.Min(a.Length, b.Length)) + 2;
        var tiles = new List<Tile>();
        for (var i = -reach; i <= reach; i++) {
            for (var j = -reach; j <= reach; j++) {
                var offset = (a * i) + (b * j);
                foreach (var prototile in cell) {
                    var centre = prototile.Centre + offset;
                    if (centre.Length <= radius + Quantum) {
                        tiles.Add(Regular(sides: prototile.Sides, centre: centre, firstVertexDegrees: prototile.FirstVertexDegrees));
                    }
                }
            }
        }
        return tiles;
    }

    // Robinson-triangle inflation from a sun of ten acute halves; twins sharing a base merge into a rhomb whose edges
    // are the triangles' equal sides, scaled so that edge is 1.
    private readonly record struct Half(bool Acute, Point A, Point B, Point C);

    private static List<Tile> Penrose(int radius) {
        var inflations = 0;
        while ((inflations < MaxPenroseInflations) && (Math.Pow(Phi, inflations) < radius + 2)) {
            inflations++;
        }
        var halves = new List<Half>(capacity: 10);
        for (var index = 0; index < 10; index++) {
            var b = Point.Polar(1, 36 * index);
            var c = Point.Polar(1, 36 * (index + 1));
            halves.Add(((index % 2) == 0) ? new Half(true, new Point(0, 0), b, c) : new Half(true, new Point(0, 0), c, b));
        }
        for (var step = 0; step < inflations; step++) {
            var next = new List<Half>(capacity: halves.Count * 3);
            foreach (var half in halves) {
                var (a, b, c) = (half.A, half.B, half.C);
                if (half.Acute) {
                    var p = a + ((b - a) * (1.0 / Phi));
                    next.Add(new Half(true, c, p, b));
                    next.Add(new Half(false, p, c, a));
                } else {
                    var q = b + ((a - b) * (1.0 / Phi));
                    var r = b + ((c - b) * (1.0 / Phi));
                    next.Add(new Half(false, r, c, a));
                    next.Add(new Half(false, q, r, b));
                    next.Add(new Half(true, r, q, a));
                }
            }
            halves = next;
        }
        var scale = Math.Pow(Phi, inflations);
        var byBase = new Dictionary<((long, long), (long, long), bool), List<Half>>();
        foreach (var half in halves) {
            var scaled = new Half(half.Acute, half.A * scale, half.B * scale, half.C * scale);
            var kb = (Quantize(scaled.B.X), Quantize(scaled.B.Y));
            var kc = (Quantize(scaled.C.X), Quantize(scaled.C.Y));
            var key = ((kb.CompareTo(kc) <= 0) ? (kb, kc, scaled.Acute) : (kc, kb, scaled.Acute));
            if (!byBase.TryGetValue(key, out var twins)) {
                twins = [];
                byBase[key] = twins;
            }
            twins.Add(scaled);
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
            var centre = (first.A + second.A) * 0.5;
            if (centre.Length <= radius + Quantum) {
                tiles.Add(new Tile(Counterclockwise(vertices), centre));
            }
        }
        return tiles;
    }

    private static Point[] Counterclockwise(Point[] vertices) {
        var area = 0.0;
        for (var index = 0; index < vertices.Length; index++) {
            var a = vertices[index];
            var b = vertices[(index + 1) % vertices.Length];
            area += (a.X * b.Y) - (b.X * a.Y);
        }
        if (area < 0) {
            Array.Reverse(vertices);
        }
        return vertices;
    }
}
