using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The bake's coarser levels of detail (<see cref="SdfMesh.Levels"/>): a merged mesh (<see cref="SdfMeshPlanarMerge"/>)
/// simplified by half-edge collapse, each collapse moving one place's vertices onto a neighbouring place's, so every
/// level draws a subset of the mesh's own vertices with their own normals and no vertex is made or moved.
/// <para>Collapses are tried cheapest first by the quadric of the planes about each place, with the planes along creases
/// and material edges weighted so those simplify last, and are taken only where every vertex of the mesh's own triangles
/// stays within the level's error of a triangle of its own material whose interpolated normal there is within the level's
/// angle of its own, every vertex and edge midpoint of a crease or material edge stays within it of that line as the
/// level draws it, and every new triangle's centroid and edge midpoints stay within it of the surface of their material
/// it replaces (with what that surface had strayed already), so a triangle no vertex needs, one cutting through a thin
/// rod, is measured too. Those distances are measured, never estimated, and the level records the largest
/// (<see cref="SdfMeshLevel.Error"/>), which the draws' level choice projects (<see cref="SdfMeshDetail"/>). A place on a
/// crease slides only along it, with each side keeping its own vertices, and a place on material edges only along them; a
/// corner where three or more faces meet, a place on the mesh's open edge, and one whose triangles do not close one ring
/// about it never move, so neighbouring meshes keep meeting edge for edge and a corner stays where it was. No collapse
/// turns a triangle to face away from its vertices' normals, which the opaque pass's back-face cull would show as a hole.
/// The levels are a pure function of the mesh, and presentation alone.</para>
/// </summary>
public static class SdfMeshCollapse {
    /// <summary>The share of a mesh's contour cell (<see cref="SdfMesh.BakeCell"/>, its largest side) each coarser level's
    /// vertices stay within, finest first.</summary>
    public static ReadOnlySpan<float> CellShares => [0.25f, 2f];
    /// <summary>The angle, in degrees, each level's interpolated normal at a vertex's nearest point stays within of the
    /// vertex's own normal, finest first.</summary>
    public static ReadOnlySpan<float> NormalDegrees => [2f, 6f];

    /// <summary>The most of the next finer level's triangles a level keeps and still is made: one that saves less is not
    /// worth its region words.</summary>
    public const float KeptShare = 0.75f;

    // How much heavier a crease's or a material edge's planes weigh than a face's, so those collapse last.
    private const double FeatureWeight = 8.0;
    // The least cosine between a collapsed triangle's normal and its own before: a collapse that turns a triangle further
    // is refused, so no triangle folds over.
    private const double FlipCosine = 0.2;
    // The most passes a level takes, and the least share of its places a pass must collapse for another to follow.
    private const int MaxPasses = 48;
    private const int TailShare = 512;

    /// <summary>Returns a mesh with its coarser levels of detail.</summary>
    /// <param name="mesh">The merged mesh.</param>
    /// <param name="preserveFeatures">Limits crease and material-edge error to the largest bake-cell dimension divided
    /// by 8, while face interiors use the ordinary level budget. A mesh without detectable features retains its base surface.</param>
    /// <returns>The mesh with <see cref="SdfMesh.Levels"/> set; <paramref name="mesh"/> itself when it carries no vertex
    /// normals, no contour cell, members (whose draws no host box bounds, so none chooses a level), or no level saves
    /// enough (<see cref="KeptShare"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static SdfMesh Levels(SdfMesh mesh, bool preserveFeatures = false) {
        ArgumentNullException.ThrowIfNull(argument: mesh);

        var cell = MathF.Max(x: mesh.BakeCell.X, y: MathF.Max(x: mesh.BakeCell.Y, y: mesh.BakeCell.Z));

        if ((mesh.Normals.Length == 0) || !mesh.Members.IsEmpty || !(cell > 0f) || (mesh.TriangleCount < 4)) {
            return mesh;
        }

        var collapser = new Collapser(mesh: mesh, preserveFeatures: preserveFeatures);

        if (preserveFeatures && !collapser.HasFeatures) { return mesh; }
        var levels = new List<SdfMeshLevel>(capacity: CellShares.Length);
        var kept = mesh.TriangleCount;

        for (var level = 0; (level < CellShares.Length); level++) {
            var made = collapser.Run(cosine: Math.Cos(d: ((NormalDegrees[level] * Math.PI) / 180.0)), error: (CellShares[level] * cell));

            if (made.TriangleCount > (KeptShare * kept)) {
                continue;
            }

            // A sample later tracked to a nearer triangle can measure a coarser level nearer than a finer one; each bound
            // holds, and the larger holds for both.
            levels.Add(item: ((levels.Count == 0) ? made : (made with { Error = MathF.Max(x: made.Error, y: levels[^1].Error) })));
            kept = made.TriangleCount;
        }

        return ((levels.Count == 0) ? mesh : (mesh with { Levels = levels }));
    }

    // One mesh's collapses. A place is a position some vertices share (a crease's copies, one per face about it); a
    // collapse moves every vertex at one place onto a neighbouring place's vertices, keeping each triangle's own side of a
    // crease. Each vertex and material its triangles carry is a sample, tracked to the triangle it is measured to.
    // Collapses run in passes: each pass lists every place's triangles, and a place a collapse moved triangles onto takes
    // part in no other collapse that pass, since its list misses them.
    private sealed class Collapser {
        private const byte Free = 0;
        private const byte Locked = 2;
        private const byte Slides = 1;

        private readonly int[] m_placeOf;
        private readonly double[] m_x;
        private readonly double[] m_y;
        private readonly double[] m_z;
        private readonly Vector3[] m_normals;
        private readonly int[] m_corners;
        private readonly uint[] m_materials;
        private readonly bool[] m_alive;
        private readonly double[] m_quadrics;
        private readonly int m_places;
        private readonly int m_triangles;
        private readonly bool m_carriesMaterials;

        private int m_aliveTriangles;

        // The samples: each one's vertex, material, distance and normal cosine at the triangle it is tracked to, and the
        // next sample tracked to the same triangle.
        private readonly int[] m_sampleVertex;
        private readonly uint[] m_sampleMaterial;
        private readonly double[] m_sampleDistance;
        private readonly int[] m_sampleNext;
        private readonly int[] m_tracked;

        // The line samples: each crease or material edge vertex and each such edge's midpoint, tracked to the edge of the
        // line it lies nearest now, keyed by the edge's places; and each one's distance there. A surface sample can sit
        // inside a triangle that fills a notch in a face's outline at no distance, so the line itself is measured.
        private readonly List<double> m_lineX = [];
        private readonly List<double> m_lineY = [];
        private readonly List<double> m_lineZ = [];
        private readonly List<double> m_lineDistance = [];
        private readonly Dictionary<long, List<int>> m_lines = [];

        // Each triangle's bound on how far it strays from the mesh's own surface (zero for the mesh's own triangles).
        private readonly double[] m_stray;
        // A pass's triangles about each place; the pass a collapse moved triangles onto a place (touched) or changed the
        // triangles about it (changed); what each place may do.
        private readonly int[] m_fanStart;
        private readonly int[] m_fan;
        private readonly int[] m_filled;
        private readonly int[] m_touched;
        private readonly int[] m_changed;
        private readonly int[] m_stamp;
        private readonly byte[] m_kind;
        private readonly double m_lineLimit;
        private readonly double[] m_candidateCosts;
        private readonly int[] m_candidatePlaces;

        private int m_pass;
        private int m_stampValue;

        // Scratch every collapse reuses.
        private readonly List<int> m_from = [];
        private readonly List<int> m_to = [];
        private readonly List<int> m_targetPlaces = [];
        private readonly List<int> m_features = [];
        private readonly List<int> m_seams = [];
        private readonly List<(double Cost, int Place)> m_targets = [];
        private readonly List<int> m_samples = [];
        private readonly List<(int Triangle, double Distance)> m_choices = [];
        private readonly List<int> m_lineNeighbours = [];
        private readonly List<(int Sample, int Other, double Distance)> m_lineChoices = [];
        private readonly List<double> m_strays = [];

        private int m_laidCount;

        private int[] m_fanCorner = new int[16];
        private int[] m_fanNext = new int[16];
        private int[] m_fanAfter = new int[16];
        private Candidate[] m_measured = new Candidate[16];
        private Candidate[] m_previous = new Candidate[16];
        private int[] m_laid = new int[16];

        public bool HasFeatures => (m_kind.Contains(value: Locked) || m_kind.Contains(value: Slides));

        public Collapser(SdfMesh mesh, bool preserveFeatures) {
            m_lineLimit = (preserveFeatures ? (Math.Max(val1: mesh.BakeCell.X, val2: Math.Max(val1: mesh.BakeCell.Y, val2: mesh.BakeCell.Z)) / 8.0) : double.PositiveInfinity);
            var positions = mesh.Positions.Span;
            var indices = mesh.Indices.Span;
            var materials = mesh.TriangleMaterials.Span;
            var vertices = positions.Length;
            var first = new Dictionary<Vector3, int>(capacity: vertices);

            m_placeOf = new int[vertices];
            for (var vertex = 0; (vertex < vertices); vertex++) {
                if (!first.TryGetValue(key: positions[vertex], value: out var place)) {
                    place = first.Count;
                    first.Add(key: positions[vertex], value: place);
                }
                m_placeOf[vertex] = place;
            }

            m_places = first.Count;
            m_x = new double[m_places];
            m_y = new double[m_places];
            m_z = new double[m_places];
            for (var vertex = 0; (vertex < vertices); vertex++) {
                m_x[m_placeOf[vertex]] = positions[vertex].X;
                m_y[m_placeOf[vertex]] = positions[vertex].Y;
                m_z[m_placeOf[vertex]] = positions[vertex].Z;
            }

            m_normals = mesh.Normals.ToArray();
            m_carriesMaterials = !materials.IsEmpty;
            m_triangles = mesh.TriangleCount;
            m_corners = new int[(3 * m_triangles)];
            m_materials = new uint[m_triangles];
            m_alive = new bool[m_triangles];
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                m_corners[(3 * triangle)] = ((int)indices[(3 * triangle)]);
                m_corners[((3 * triangle) + 1)] = ((int)indices[((3 * triangle) + 1)]);
                m_corners[((3 * triangle) + 2)] = ((int)indices[((3 * triangle) + 2)]);
                m_materials[triangle] = (materials.IsEmpty ? 0u : materials[triangle]);
                m_alive[triangle] = Distinct(triangle: triangle);
                m_aliveTriangles += (m_alive[triangle] ? 1 : 0);
            }

            m_fanStart = new int[(m_places + 1)];
            m_fan = new int[(3 * m_triangles)];
            m_filled = new int[m_places];
            m_touched = new int[m_places];
            m_changed = new int[m_places];
            m_stamp = new int[m_places];
            m_kind = new byte[m_places];
            m_candidateCosts = new double[m_places];
            m_candidatePlaces = new int[m_places];
            m_quadrics = new double[(10 * m_places)];
            m_tracked = new int[m_triangles];
            m_stray = new double[m_triangles];
            Array.Fill(array: m_tracked, value: -1);

            var sampleOf = new HashSet<(int Vertex, uint Material)>();
            var sampleTriangles = new List<int>();
            var sampleVertices = new List<int>();

            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                for (var corner = 0; (corner < 3); corner++) {
                    if (sampleOf.Add(item: (m_corners[((3 * triangle) + corner)], m_materials[triangle]))) {
                        sampleTriangles.Add(item: triangle);
                        sampleVertices.Add(item: m_corners[((3 * triangle) + corner)]);
                    }
                }
            }

            var samples = sampleTriangles.Count;

            m_sampleVertex = [.. sampleVertices];
            m_sampleMaterial = new uint[samples];
            m_sampleDistance = new double[samples];
            m_sampleNext = new int[samples];
            for (var sample = 0; (sample < samples); sample++) {
                m_sampleMaterial[sample] = m_materials[sampleTriangles[sample]];
                Track(sample: sample, triangle: sampleTriangles[sample]);
            }

            Fans();
            for (var place = 0; (place < m_places); place++) {
                m_kind[place] = Kind(penalize: true, place: place);
                Lines(fan: Alive(fan: m_from, place: place), into: m_lineNeighbours, place: place);
                for (var at = 0; (at < m_lineNeighbours.Count); at++) {
                    var other = m_lineNeighbours[at];

                    if (at == 0) { AddLine(a: place, b: other, x: m_x[place], y: m_y[place], z: m_z[place]); }
                    if (place < other) { AddLine(a: place, b: other, x: (0.5 * (m_x[place] + m_x[other])), y: (0.5 * (m_y[place] + m_y[other])), z: (0.5 * (m_z[place] + m_z[other]))); }
                }
            }
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (m_alive[triangle]) { AddFace(triangle: triangle); }
            }
        }

        // Collapses until no pass takes enough within the bounds, then returns the level the alive triangles make.
        public SdfMeshLevel Run(double error, double cosine) {
            for (var pass = 0; (pass < MaxPasses); pass++) {
                // The constructor's lists and kinds stand for the first pass; later, a place no collapse changed keeps its
                // triangles and its kind.
                if (m_pass > 0) {
                    Fans();
                    for (var place = 0; (place < m_places); place++) {
                        if ((m_touched[place] == m_pass) || (m_changed[place] == m_pass)) { m_kind[place] = Kind(penalize: false, place: place); }
                    }
                }

                var taken = Pass(cosine: cosine, error: error);

                if ((taken == 0) || (taken < (m_places / TailShare))) { break; }
            }

            var indices = new uint[(3 * m_aliveTriangles)];
            var materials = new uint[(m_carriesMaterials ? m_aliveTriangles : 0)];
            var at = 0;

            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                indices[(3 * at)] = ((uint)m_corners[(3 * triangle)]);
                indices[((3 * at) + 1)] = ((uint)m_corners[((3 * triangle) + 1)]);
                indices[((3 * at) + 2)] = ((uint)m_corners[((3 * triangle) + 2)]);
                if (m_carriesMaterials) { materials[at] = m_materials[triangle]; }
                at++;
            }

            var largest = 0.0;

            foreach (var distance in m_sampleDistance) {
                largest = Math.Max(val1: largest, val2: distance);
            }
            foreach (var distance in m_lineDistance) {
                largest = Math.Max(val1: largest, val2: distance);
            }
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                largest = (m_alive[triangle] ? Math.Max(val1: largest, val2: m_stray[triangle]) : largest);
            }

            return new SdfMeshLevel(Error: ((float)largest), Indices: indices, TriangleMaterials: materials);
        }

        private int Pass(double error, double cosine) {
            m_pass++;

            var count = 0;

            for (var place = 0; (place < m_places); place++) {
                if (m_kind[place] == Locked) { continue; }

                var best = double.PositiveInfinity;

                Targets(place: place);
                foreach (var target in m_targetPlaces) {
                    best = Math.Min(val1: best, val2: Cost(from: place, to: target));
                }
                m_candidateCosts[count] = best;
                m_candidatePlaces[count] = place;
                count++;
            }

            // The sort draws on no randomness, so places of equal cost come out in one order on every run.
            Array.Sort(index: 0, items: m_candidatePlaces, keys: m_candidateCosts, length: count);

            var taken = 0;

            for (var at = 0; (at < count); at++) {
                var place = m_candidatePlaces[at];

                if (m_touched[place] == m_pass) { continue; }
                if ((m_changed[place] == m_pass) && ((m_kind[place] = Kind(penalize: false, place: place)) == Locked)) { continue; }

                m_targets.Clear();
                Targets(place: place);
                foreach (var target in m_targetPlaces) {
                    if (m_touched[target] != m_pass) { m_targets.Add(item: (Cost(from: place, to: target), target)); }
                }
                m_targets.Sort();
                foreach (var (_, target) in m_targets) {
                    if (TryCollapse(cosine: cosine, error: error, from: place, to: target)) {
                        taken++;
                        break;
                    }
                }
            }

            return taken;
        }
        // Lists a place's alive triangles: the pass's list less those a collapse this pass removed.
        private List<int> Alive(int place, List<int> fan) {
            fan.Clear();
            for (var at = m_fanStart[place]; (at < m_fanStart[(place + 1)]); at++) {
                if (m_alive[m_fan[at]]) { fan.Add(item: m_fan[at]); }
            }

            return fan;
        }
        // Lists the places a place may collapse onto: any neighbour for a free place, those along its creases and material
        // edges for one that slides.
        private void Targets(int place) {
            m_targetPlaces.Clear();
            if (m_kind[place] == Slides) {
                _ = Kind(penalize: false, place: place);
                m_targetPlaces.AddRange(collection: m_features);

                return;
            }

            foreach (var triangle in Alive(fan: m_from, place: place)) {
                var (_, next, _) = Around(place: place, triangle: triangle);

                m_targetPlaces.Add(item: next);
            }
        }
        // Whether a triangle's three corners lie at three places.
        private bool Distinct(int triangle) {
            var a = m_placeOf[m_corners[(3 * triangle)]];
            var b = m_placeOf[m_corners[((3 * triangle) + 1)]];
            var c = m_placeOf[m_corners[((3 * triangle) + 2)]];

            return ((a != b) && (b != c) && (a != c));
        }
        // A triangle seen from one of its places: its corner there, then the places after it in winding order.
        private (int Corner, int Next, int After) Around(int place, int triangle) {
            var first = (3 * triangle);

            for (var corner = 0; (corner < 2); corner++) {
                if (m_placeOf[m_corners[(first + corner)]] == place) {
                    return ((first + corner), m_placeOf[m_corners[(first + ((corner + 1) % 3))]], m_placeOf[m_corners[(first + ((corner + 2) % 3))]]);
                }
            }

            return ((first + 2), m_placeOf[m_corners[first]], m_placeOf[m_corners[(first + 1)]]);
        }
        private int CornerAt(int place, int triangle) {
            var first = (3 * triangle);

            return ((m_placeOf[m_corners[first]] == place) ? first : ((m_placeOf[m_corners[(first + 1)]] == place) ? (first + 1) : (first + 2)));
        }
        private static int NextCorner(int corner) => (((corner % 3) == 2) ? (corner - 2) : (corner + 1));
        private static int PreviousCorner(int corner) => (((corner % 3) == 0) ? (corner + 2) : (corner - 1));
        // Lists each place's alive triangles.
        private void Fans() {
            Array.Clear(array: m_fanStart);
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                for (var corner = 0; (corner < 3); corner++) {
                    m_fanStart[(m_placeOf[m_corners[((3 * triangle) + corner)]] + 1)]++;
                }
            }
            for (var place = 0; (place < m_places); place++) {
                m_fanStart[(place + 1)] += m_fanStart[place];
            }

            Array.Clear(array: m_filled);
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                for (var corner = 0; (corner < 3); corner++) {
                    var place = m_placeOf[m_corners[((3 * triangle) + corner)]];

                    m_fan[(m_fanStart[place] + m_filled[place]++)] = triangle;
                }
            }
        }
        // Sorts a place into free (on at most one crease or material edge, which ends there), sliding along its creases
        // and material edges, or locked where its triangles do not close one ring of edges two triangles share; lists the
        // far places of those edges (m_features) and, with `penalize`, adds the planes along them to its quadric. Which
        // slide keeps every side's vertices is the collapse's to judge.
        private byte Kind(int place, bool penalize) {
            var fan = Alive(fan: m_from, place: place);
            var count = fan.Count;
            var seams = m_seams;

            m_features.Clear();
            seams.Clear();
            if (count < 3) { return Locked; }
            if (m_fanCorner.Length < count) {
                m_fanCorner = new int[(2 * count)];
                m_fanNext = new int[(2 * count)];
                m_fanAfter = new int[(2 * count)];
            }
            for (var at = 0; (at < count); at++) {
                (m_fanCorner[at], m_fanNext[at], m_fanAfter[at]) = Around(place: place, triangle: fan[at]);
            }

            for (var at = 0; (at < count); at++) {
                var next = m_fanNext[at];
                var outgoing = 0;
                var incoming = -1;
                var incomingCount = 0;

                // The edge from this place to `next` runs out of this triangle; its twin runs in to this place from `next`.
                for (var other = 0; (other < count); other++) {
                    outgoing += ((m_fanNext[other] == next) ? 1 : 0);
                    if (m_fanAfter[other] == next) {
                        incoming = other;
                        incomingCount++;
                    }
                }
                if ((outgoing != 1) || (incomingCount != 1)) { return Locked; }

                var corner = m_fanCorner[at];
                var twinCorner = m_fanCorner[incoming];
                var feature = ((m_corners[corner] != m_corners[twinCorner]) || (m_corners[NextCorner(corner: corner)] != m_corners[PreviousCorner(corner: twinCorner)]) || (m_materials[fan[at]] != m_materials[fan[incoming]]));

                if (!feature) { continue; }

                if (penalize) {
                    AddEdge(other: next, place: place, triangle: fan[at]);
                    AddEdge(other: next, place: place, triangle: fan[incoming]);
                }
                if (m_corners[corner] != m_corners[twinCorner]) {
                    seams.Add(item: next);
                } else {
                    m_features.Add(item: next);
                }
            }
            if (!OneRing(count: count)) { return Locked; }

            // A collapse maps each of the place's vertices to the neighbour's vertex on its side of the collapsed edge, so
            // a place with three or more never moves, and one with two moves only along the crease between them.
            var copies = 1;

            for (var at = 1; (at < count); at++) {
                if (m_corners[m_fanCorner[at]] != m_corners[m_fanCorner[0]]) {
                    copies++;
                    break;
                }
            }
            if (copies > 1) {
                if (seams.Count != 2) { return Locked; }

                m_features.Clear();
                m_features.AddRange(collection: seams);

                return Slides;
            }

            return ((m_features.Count <= 1) ? Free : Slides);
        }
        // Whether the triangles Kind laid out close one ring about their place, each triangle's after place the next place
        // of the one following it.
        private bool OneRing(int count) {
            var at = 0;
            var steps = 0;

            do {
                var after = m_fanAfter[at];
                var following = -1;

                for (var other = 0; (other < count); other++) {
                    if (m_fanNext[other] == after) { following = other; }
                }

                at = following;
                steps++;
            } while ((at > 0) && (steps <= count));

            return ((at == 0) && (steps == count));
        }
        // Tries to collapse one place onto a neighbour, taking it only where the mesh stays a closed ring of untwisted
        // triangles about the neighbour and every sample of the triangles it changes stays within the bounds.
        private bool TryCollapse(int from, int to, double error, double cosine) {
            var fan = Alive(fan: m_from, place: from);
            var first = -1;
            var second = -1;

            foreach (var triangle in fan) {
                var (_, next, after) = Around(place: from, triangle: triangle);

                if (next == to) { first = triangle; }
                if (after == to) { second = triangle; }
            }
            if ((first < 0) || (second < 0)) { return false; }

            var (_, _, firstOpposite) = Around(place: from, triangle: first);
            var (_, secondOpposite, _) = Around(place: from, triangle: second);

            if ((firstOpposite == secondOpposite) || !Links(fan: fan, firstOpposite: firstOpposite, from: from, secondOpposite: secondOpposite, to: to)) { return false; }

            // Each side of the collapsed edge keeps its own vertices: a triangle about the place takes, at the neighbour,
            // the vertex the edge's triangle on its side has there.
            var fromFirst = m_corners[CornerAt(place: from, triangle: first)];
            var toFirst = m_corners[CornerAt(place: to, triangle: first)];
            var fromSecond = m_corners[CornerAt(place: from, triangle: second)];
            var toSecond = m_corners[CornerAt(place: to, triangle: second)];

            if ((fromFirst == fromSecond) && (toFirst != toSecond)) { return false; }

            var (tx, ty, tz) = (m_x[to], m_y[to], m_z[to]);

            foreach (var triangle in fan) {
                if ((triangle == first) || (triangle == second)) { continue; }

                var corner = CornerAt(place: from, triangle: triangle);
                var vertex = m_corners[corner];

                if ((vertex != fromFirst) && (vertex != fromSecond)) { return false; }

                var (ax, ay, az, bx, by, bz, cx, cy, cz) = Positions(triangle: triangle);
                var (ox, oy, oz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
                var moved = (corner - (3 * triangle));

                if (moved == 0) { (ax, ay, az) = (tx, ty, tz); } else if (moved == 1) { (bx, by, bz) = (tx, ty, tz); } else { (cx, cy, cz) = (tx, ty, tz); }

                var (nx, ny, nz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
                var oldLength = Math.Sqrt(d: (((ox * ox) + (oy * oy)) + (oz * oz)));
                var newLength = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

                if (!(newLength > (1e-12 * oldLength)) || ((((ox * nx) + (oy * ny)) + (oz * nz)) < ((FlipCosine * oldLength) * newLength))) { return false; }

                // The face keeps the side of its vertices' normals it had, or the opaque pass, which culls back faces,
                // would leave a hole where the shading faces the camera.
                var before = Vector3.Zero;
                var after = Vector3.Zero;

                for (var other = (3 * triangle); (other < ((3 * triangle) + 3)); other++) {
                    before += m_normals[m_corners[other]];
                    after += m_normals[((other == corner) ? Mapped(fromFirst: fromFirst, toFirst: toFirst, toSecond: toSecond, vertex: vertex) : m_corners[other])];
                }
                if (((((ox * before.X) + (oy * before.Y)) + (oz * before.Z)) > 0.0) != ((((nx * after.X) + (ny * after.Y)) + (nz * after.Z)) > 0.0)) { return false; }
            }

            if (!Measure(cosine: cosine, error: error, fan: fan, first: first, from: from, fromFirst: fromFirst, second: second, toFirst: toFirst, toSecond: toSecond)) { return false; }
            if (!MeasureReverse(error: error, fan: fan)) { return false; }
            if (!MeasureLines(error: Math.Min(val1: error, val2: m_lineLimit), fan: fan, from: from, to: to)) { return false; }

            CommitLines(from: from, to: to);
            Commit(fan: fan, first: first, from: from, fromFirst: fromFirst, second: second, to: to, toFirst: toFirst, toSecond: toSecond);
            for (var at = 0; (at < m_strays.Count); at++) {
                m_stray[m_measured[at].Triangle] = m_strays[at];
            }

            return true;
        }
        // Bounds how far each triangle Measure laid out strays from the mesh's own surface: its centroid's and its edge
        // midpoints' distance to the nearest triangle of its material about the collapsing place as it stands, plus that
        // triangle's own bound. A surface sample is measured to the nearest triangle alone, so a triangle no sample needs
        // (one cutting through a thin rod, its corners on opposite sides) is measured here. False when one strays past the
        // bound.
        private bool MeasureReverse(List<int> fan, double error) {
            m_strays.Clear();
            for (var at = 0; (at < m_laidCount); at++) {
                ref readonly var candidate = ref m_measured[at];
                var bound = 0.0;

                for (var point = 0; (point < 4); point++) {
                    var (px, py, pz) = point switch {
                        0 => (candidate.Cx, candidate.Cy, candidate.Cz),
                        1 => ((0.5 * (candidate.Ax + candidate.Bx)), (0.5 * (candidate.Ay + candidate.By)), (0.5 * (candidate.Az + candidate.Bz))),
                        2 => ((0.5 * (candidate.Bx + candidate.Ex)), (0.5 * (candidate.By + candidate.Ey)), (0.5 * (candidate.Bz + candidate.Ez))),
                        _ => ((0.5 * (candidate.Ex + candidate.Ax)), (0.5 * (candidate.Ey + candidate.Ay)), (0.5 * (candidate.Ez + candidate.Az))),
                    };
                    var nearest = double.PositiveInfinity;

                    for (var oldAt = 0; (oldAt < fan.Count); oldAt++) {
                        ref readonly var old = ref m_previous[oldAt];

                        if (old.Material != candidate.Material) { continue; }
                        var stray = m_stray[old.Triangle];

                        if (Beyond(candidate: in old, distance: (nearest - stray), px: px, py: py, pz: pz)) { continue; }

                        var (ax, ay, az, bx, by, bz, cx, cy, cz) = (old.Ax, old.Ay, old.Az, old.Bx, old.By, old.Bz, old.Ex, old.Ey, old.Ez);
                        var (u, v, w) = Closest(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz, px: px, py: py, pz: pz);
                        var (dx, dy, dz) = (((((u * ax) + (v * bx)) + (w * cx)) - px), ((((u * ay) + (v * by)) + (w * cy)) - py), ((((u * az) + (v * bz)) + (w * cz)) - pz));

                        nearest = Math.Min(val1: nearest, val2: (Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz))) + stray));
                    }

                    bound = Math.Max(val1: bound, val2: nearest);
                }
                if (!(bound <= error)) { return false; }

                m_strays.Add(item: bound);
            }

            return true;
        }
        // Lists the far places of a place's crease and material edges: the edges whose two triangles keep different
        // vertices at either end or carry different materials. The place's triangles close one ring of edges two
        // triangles share wherever it can move, so each edge has its twin.
        private void Lines(int place, List<int> fan, List<int> into) {
            into.Clear();
            foreach (var triangle in fan) {
                var (corner, next, _) = Around(place: place, triangle: triangle);

                foreach (var twin in fan) {
                    var (twinCorner, _, after) = Around(place: place, triangle: twin);

                    if ((after == next) && ((m_corners[corner] != m_corners[twinCorner]) || (m_corners[NextCorner(corner: corner)] != m_corners[PreviousCorner(corner: twinCorner)]) || (m_materials[triangle] != m_materials[twin]))) {
                        into.Add(item: next);
                    }
                }
            }
        }
        private static long Key(int a, int b) => (((long)Math.Min(val1: a, val2: b)) << 32) | ((uint)Math.Max(val1: a, val2: b));
        private void AddLine(int a, int b, double x, double y, double z) {
            if (!m_lines.TryGetValue(key: Key(a: a, b: b), value: out var samples)) {
                samples = [];
                m_lines.Add(key: Key(a: a, b: b), value: samples);
            }

            samples.Add(item: m_lineX.Count);
            m_lineX.Add(item: x);
            m_lineY.Add(item: y);
            m_lineZ.Add(item: z);
            m_lineDistance.Add(item: 0.0);
        }
        // Measures every line sample on an edge the collapsing place's lines leave against the edges they become, from the
        // neighbour to each of the place's other line places, choosing the nearest within the bound; false when one has
        // none.
        private bool MeasureLines(List<int> fan, int from, int to, double error) {
            m_lineChoices.Clear();
            Lines(fan: fan, into: m_lineNeighbours, place: from);
            foreach (var line in m_lineNeighbours) {
                if (!m_lines.TryGetValue(key: Key(a: from, b: line), value: out var samples)) { continue; }

                foreach (var sample in samples) {
                    var bestOther = -1;
                    var bestDistance = double.PositiveInfinity;

                    foreach (var other in m_lineNeighbours) {
                        if (other == to) { continue; }

                        var distance = Segment(a: to, b: other, sample: sample);

                        if (distance < bestDistance) {
                            bestOther = other;
                            bestDistance = distance;
                        }
                    }
                    if (!(bestDistance <= error)) { return false; }

                    m_lineChoices.Add(item: (sample, bestOther, bestDistance));
                }
            }

            return true;
        }
        // Moves the measured line samples onto the edges they now lie on.
        private void CommitLines(int from, int to) {
            foreach (var line in m_lineNeighbours) {
                _ = m_lines.Remove(key: Key(a: from, b: line));
            }
            foreach (var (sample, other, distance) in m_lineChoices) {
                if (!m_lines.TryGetValue(key: Key(a: to, b: other), value: out var samples)) {
                    samples = [];
                    m_lines.Add(key: Key(a: to, b: other), value: samples);
                }

                samples.Add(item: sample);
                m_lineDistance[sample] = distance;
            }
        }
        // A line sample's distance to the segment between two places.
        private double Segment(int a, int b, int sample) {
            var (ax, ay, az) = (m_x[a], m_y[a], m_z[a]);
            var (ex, ey, ez) = ((m_x[b] - ax), (m_y[b] - ay), (m_z[b] - az));
            var (px, py, pz) = ((m_lineX[sample] - ax), (m_lineY[sample] - ay), (m_lineZ[sample] - az));
            var length = (((ex * ex) + (ey * ey)) + (ez * ez));
            var along = ((length > 0.0) ? Math.Clamp(max: 1.0, min: 0.0, value: ((((px * ex) + (py * ey)) + (pz * ez)) / length)) : 0.0);

            var (dx, dy, dz) = ((px - (along * ex)), (py - (along * ey)), (pz - (along * ez)));

            return Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz)));
        }
        // Whether the two places share no neighbour but the edge's two opposite places, so the collapse pinches nothing.
        private bool Links(List<int> fan, int from, int to, int firstOpposite, int secondOpposite) {
            m_stampValue++;
            foreach (var triangle in fan) {
                var (_, next, after) = Around(place: from, triangle: triangle);

                m_stamp[next] = m_stampValue;
                m_stamp[after] = m_stampValue;
            }
            foreach (var triangle in Alive(fan: m_to, place: to)) {
                var (_, next, after) = Around(place: to, triangle: triangle);

                if ((next != from) && (m_stamp[next] == m_stampValue) && (next != firstOpposite) && (next != secondOpposite)) { return false; }
                if ((after != from) && (m_stamp[after] == m_stampValue) && (after != firstOpposite) && (after != secondOpposite)) { return false; }
            }

            return true;
        }
        // The vertex a triangle about the collapsing place takes at the neighbour.
        private static int Mapped(int vertex, int fromFirst, int toFirst, int toSecond) =>
            ((vertex == fromFirst) ? toFirst : toSecond);
        // Measures every sample tracked to a triangle about the collapsing place against the triangles that stay, as they
        // will be, choosing for each the nearest of its material within the bounds; false when one has none.
        private bool Measure(List<int> fan, int from, int first, int second, int fromFirst, int toFirst, int toSecond, double error, double cosine) {
            m_samples.Clear();
            m_choices.Clear();

            var candidates = Lay(fan: fan, first: first, from: from, fromFirst: fromFirst, second: second, toFirst: toFirst, toSecond: toSecond);

            m_laidCount = candidates;
            for (var index = 0; (index < fan.Count); index++) {
                // A sample's own triangle, where it stays, is measured first: it is most often the nearest, and the
                // others then fall to the bound on the nearest yet.
                var laid = m_laid[index];

                for (var sample = m_tracked[fan[index]]; (sample >= 0); sample = m_sampleNext[sample]) {
                    var vertex = m_sampleVertex[sample];
                    var place = m_placeOf[vertex];

                    var (px, py, pz) = (m_x[place], m_y[place], m_z[place]);
                    var own = m_normals[vertex];
                    var material = m_sampleMaterial[sample];
                    var best = -1;
                    var bestDistance = double.PositiveInfinity;
                    var bestCosine = -1.0;

                    if (laid >= 0) {
                        Offer(at: laid, best: ref best, bestCosine: ref bestCosine, bestDistance: ref bestDistance, cosine: cosine, error: error, material: material, own: own, px: px, py: py, pz: pz);
                    }
                    for (var at = 0; (at < candidates); at++) {
                        if (at != laid) {
                            Offer(at: at, best: ref best, bestCosine: ref bestCosine, bestDistance: ref bestDistance, cosine: cosine, error: error, material: material, own: own, px: px, py: py, pz: pz);
                        }
                    }
                    if (best < 0) { return false; }

                    m_samples.Add(item: sample);
                    m_choices.Add(item: (m_measured[best].Triangle, bestDistance));
                }
            }

            return true;
        }
        // Measures a sample against one laid-out triangle of its material, keeping it where it is the nearest within the
        // bounds yet. A triangle wholly beyond the bound, or farther than the nearest yet, is not measured.
        private void Offer(int at, double px, double py, double pz, Vector3 own, uint material, double error, double cosine, ref int best, ref double bestDistance, ref double bestCosine) {
            ref readonly var candidate = ref m_measured[at];

            if (candidate.Material != material) { return; }
            if (Beyond(candidate: in candidate, px: px, py: py, pz: pz, distance: Math.Min(val1: error, val2: bestDistance))) { return; }

            var (sx, sy, sz) = ((px - candidate.Cx), (py - candidate.Cy), (pz - candidate.Cz));
            var reach = (Math.Sqrt(d: (((sx * sx) + (sy * sy)) + (sz * sz))) - candidate.Radius);

            if ((reach > error) || (reach > bestDistance)) { return; }

            var (distance, cos) = Nearest(candidate: in candidate, own: own, px: px, py: py, pz: pz);

            if ((distance <= error) && (cos >= cosine) && ((distance < bestDistance) || ((distance == bestDistance) && (cos > bestCosine)))) {
                best = at;
                bestDistance = distance;
                bestCosine = cos;
            }
        }
        // Lays out the triangles that stay about the collapsing place as they will be, once for every sample measured
        // against them, and returns their count.
        private int Lay(List<int> fan, int from, int first, int second, int fromFirst, int toFirst, int toSecond) {
            if (m_measured.Length < fan.Count) {
                m_measured = new Candidate[(2 * fan.Count)];
                m_previous = new Candidate[(2 * fan.Count)];
                m_laid = new int[(2 * fan.Count)];
            }

            var count = 0;

            for (var index = 0; (index < fan.Count); index++) {
                var triangle = fan[index];

                var (ax, ay, az, bx, by, bz, ex, ey, ez) = Positions(triangle: triangle);
                m_previous[index] = new Candidate { Ax = ax, Ay = ay, Az = az, Bx = bx, By = by, Bz = bz, Ex = ex, Ey = ey, Ez = ez, Material = m_materials[triangle], Triangle = triangle };
                Bounds(candidate: ref m_previous[index]);

                m_laid[index] = (((triangle == first) || (triangle == second)) ? -1 : count);
                if (m_laid[index] < 0) { continue; }

                var corner = (3 * triangle);
                var a = m_corners[corner];
                var b = m_corners[(corner + 1)];
                var c = m_corners[(corner + 2)];

                a = ((m_placeOf[a] == from) ? Mapped(fromFirst: fromFirst, toFirst: toFirst, toSecond: toSecond, vertex: a) : a);
                b = ((m_placeOf[b] == from) ? Mapped(fromFirst: fromFirst, toFirst: toFirst, toSecond: toSecond, vertex: b) : b);
                c = ((m_placeOf[c] == from) ? Mapped(fromFirst: fromFirst, toFirst: toFirst, toSecond: toSecond, vertex: c) : c);

                var (pa, pb, pc) = (m_placeOf[a], m_placeOf[b], m_placeOf[c]);
                var (cx, cy, cz) = ((((m_x[pa] + m_x[pb]) + m_x[pc]) / 3.0), (((m_y[pa] + m_y[pb]) + m_y[pc]) / 3.0), (((m_z[pa] + m_z[pb]) + m_z[pc]) / 3.0));
                var radius = 0.0;

                foreach (var p in ((ReadOnlySpan<int>)[pa, pb, pc])) {
                    var (dx, dy, dz) = ((m_x[p] - cx), (m_y[p] - cy), (m_z[p] - cz));

                    radius = Math.Max(val1: radius, val2: Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz))));
                }

                m_measured[count++] = new Candidate {
                    Ax = m_x[pa],
                    Ay = m_y[pa],
                    Az = m_z[pa],
                    Bx = m_x[pb],
                    By = m_y[pb],
                    Bz = m_z[pb],
                    Ex = m_x[pc],
                    Ey = m_y[pc],
                    Ez = m_z[pc],
                    Cx = cx,
                    Cy = cy,
                    Cz = cz,
                    Radius = (radius * (1.0 + 1e-9)),
                    Na = m_normals[a],
                    Nb = m_normals[b],
                    Nc = m_normals[c],
                    Material = m_materials[triangle],
                    Triangle = triangle,
                };
                Bounds(candidate: ref m_measured[(count - 1)]);
            }

            return count;
        }
        // A box distance is a lower bound only. Leave a double-rounding margin and still use the exact measurement for
        // every candidate that can improve it; the three refusal checks and their recorded distances stay unchanged.
        private static bool Beyond(in Candidate candidate, double px, double py, double pz, double distance) {
            var dx = Math.Max(val1: 0.0, val2: Math.Max(val1: (candidate.LowX - px), val2: (px - candidate.HighX)));
            var dy = Math.Max(val1: 0.0, val2: Math.Max(val1: (candidate.LowY - py), val2: (py - candidate.HighY)));
            var dz = Math.Max(val1: 0.0, val2: Math.Max(val1: (candidate.LowZ - pz), val2: (pz - candidate.HighZ)));
            var margin = (1e-12 * (((1.0 + Math.Abs(value: px)) + Math.Abs(value: py)) + Math.Abs(value: pz)));
            var reach = (Math.Max(val1: 0.0, val2: distance) + margin);

            return ((((dx * dx) + (dy * dy)) + (dz * dz)) > (reach * reach));
        }
        private static void Bounds(ref Candidate candidate) {
            candidate.LowX = Math.Min(val1: candidate.Ax, val2: Math.Min(val1: candidate.Bx, val2: candidate.Ex));
            candidate.LowY = Math.Min(val1: candidate.Ay, val2: Math.Min(val1: candidate.By, val2: candidate.Ey));
            candidate.LowZ = Math.Min(val1: candidate.Az, val2: Math.Min(val1: candidate.Bz, val2: candidate.Ez));
            candidate.HighX = Math.Max(val1: candidate.Ax, val2: Math.Max(val1: candidate.Bx, val2: candidate.Ex));
            candidate.HighY = Math.Max(val1: candidate.Ay, val2: Math.Max(val1: candidate.By, val2: candidate.Ey));
            candidate.HighZ = Math.Max(val1: candidate.Az, val2: Math.Max(val1: candidate.Bz, val2: candidate.Ez));
        }
        // A sample's distance to a laid-out triangle, and the cosine between its own normal and the triangle's interpolated
        // normal at the nearest point.
        private static (double Distance, double Cosine) Nearest(in Candidate candidate, double px, double py, double pz, Vector3 own) {
            var (u, v, w) = Closest(ax: candidate.Ax, ay: candidate.Ay, az: candidate.Az, bx: candidate.Bx, by: candidate.By, bz: candidate.Bz, cx: candidate.Ex, cy: candidate.Ey, cz: candidate.Ez, px: px, py: py, pz: pz);
            var dx = ((((u * candidate.Ax) + (v * candidate.Bx)) + (w * candidate.Ex)) - px);
            var dy = ((((u * candidate.Ay) + (v * candidate.By)) + (w * candidate.Ey)) - py);
            var dz = ((((u * candidate.Az) + (v * candidate.Bz)) + (w * candidate.Ez)) - pz);

            var (na, nb, nc) = (candidate.Na, candidate.Nb, candidate.Nc);
            var nx = (((u * na.X) + (v * nb.X)) + (w * nc.X));
            var ny = (((u * na.Y) + (v * nb.Y)) + (w * nc.Y));
            var nz = (((u * na.Z) + (v * nb.Z)) + (w * nc.Z));
            var length = (Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz))) * Math.Sqrt(d: (((own.X * ((double)own.X)) + (own.Y * ((double)own.Y))) + (own.Z * ((double)own.Z)))));
            var cosine = ((length > 0.0) ? ((((nx * own.X) + (ny * own.Y)) + (nz * own.Z)) / length) : -1.0);

            return (Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz))), cosine);
        }
        // Takes a collapse: the edge's two triangles go, the rest about the place take the neighbour's vertices, every
        // measured sample is tracked to its chosen triangle, and the neighbour takes the place's planes.
        private void Commit(List<int> fan, int from, int to, int first, int second, int fromFirst, int toFirst, int toSecond) {
            foreach (var triangle in fan) {
                m_tracked[triangle] = -1;
            }

            m_alive[first] = false;
            m_alive[second] = false;
            m_aliveTriangles -= 2;
            m_touched[from] = m_pass;
            m_touched[to] = m_pass;
            foreach (var triangle in fan) {
                if (!m_alive[triangle]) { continue; }

                var corner = CornerAt(place: from, triangle: triangle);

                m_corners[corner] = Mapped(fromFirst: fromFirst, toFirst: toFirst, toSecond: toSecond, vertex: m_corners[corner]);

                var (_, next, after) = Around(place: to, triangle: triangle);

                m_changed[next] = m_pass;
                m_changed[after] = m_pass;
            }
            for (var index = 0; (index < m_samples.Count); index++) {
                var (triangle, distance) = m_choices[index];

                m_sampleDistance[m_samples[index]] = distance;
                Track(sample: m_samples[index], triangle: triangle);
            }
            for (var term = 0; (term < 10); term++) {
                m_quadrics[((10 * to) + term)] += m_quadrics[((10 * from) + term)];
            }
        }
        private void Track(int sample, int triangle) {
            m_sampleNext[sample] = m_tracked[triangle];
            m_tracked[triangle] = sample;
        }
        private (double, double, double, double, double, double, double, double, double) Positions(int triangle) {
            var a = m_placeOf[m_corners[(3 * triangle)]];
            var b = m_placeOf[m_corners[((3 * triangle) + 1)]];
            var c = m_placeOf[m_corners[((3 * triangle) + 2)]];

            return (m_x[a], m_y[a], m_z[a], m_x[b], m_y[b], m_z[b], m_x[c], m_y[c], m_z[c]);
        }
        private static (double X, double Y, double Z) Cross(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz) {
            var (ux, uy, uz) = ((bx - ax), (by - ay), (bz - az));
            var (vx, vy, vz) = ((cx - ax), (cy - ay), (cz - az));

            return (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        }
        // The cost of moving a place's planes to a neighbour: the summed squared distances of both places' planes there.
        private double Cost(int from, int to) {
            var (x, y, z) = (m_x[to], m_y[to], m_z[to]);

            return (Evaluate(place: from, x: x, y: y, z: z) + Evaluate(place: to, x: x, y: y, z: z));
        }
        private double Evaluate(int place, double x, double y, double z) {
            var q = m_quadrics.AsSpan(length: 10, start: (10 * place));

            return (((((((((((q[0] * x) * x) + (2.0 * ((q[1] * x) * y))) + (2.0 * ((q[2] * x) * z))) + (2.0 * (q[3] * x))) + ((q[4] * y) * y)) + (2.0 * ((q[5] * y) * z))) + (2.0 * (q[6] * y))) + ((q[7] * z) * z)) + (2.0 * (q[8] * z))) + q[9]);
        }
        // Adds a plane, through a point with a unit normal and a weight, to a place's quadric.
        private void AddPlane(int place, double nx, double ny, double nz, double px, double py, double pz, double weight) {
            var d = -(((nx * px) + (ny * py)) + (nz * pz));
            var q = m_quadrics.AsSpan(length: 10, start: (10 * place));

            q[0] += (weight * (nx * nx));
            q[1] += (weight * (nx * ny));
            q[2] += (weight * (nx * nz));
            q[3] += (weight * (nx * d));
            q[4] += (weight * (ny * ny));
            q[5] += (weight * (ny * nz));
            q[6] += (weight * (ny * d));
            q[7] += (weight * (nz * nz));
            q[8] += (weight * (nz * d));
            q[9] += (weight * (d * d));
        }
        // Adds a triangle's plane, weighted by its area, to each of its places.
        private void AddFace(int triangle) {
            var (ax, ay, az, bx, by, bz, cx, cy, cz) = Positions(triangle: triangle);
            var (nx, ny, nz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
            var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

            if (!(length > 0.0)) { return; }

            for (var corner = 0; (corner < 3); corner++) {
                AddPlane(nx: (nx / length), ny: (ny / length), nz: (nz / length), place: m_placeOf[m_corners[((3 * triangle) + corner)]], px: ax, py: ay, pz: az, weight: (0.5 * length));
            }
        }
        // Adds the plane through an edge standing on one of its triangles to one end's quadric, weighted heavier than a face
        // of the edge's length squared, so moving the end off the edge costs more than moving it across a face.
        private void AddEdge(int place, int other, int triangle) {
            var (ax, ay, az, bx, by, bz, cx, cy, cz) = Positions(triangle: triangle);
            var (fx, fy, fz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
            var (ex, ey, ez) = ((m_x[other] - m_x[place]), (m_y[other] - m_y[place]), (m_z[other] - m_z[place]));
            var (nx, ny, nz) = (((ey * fz) - (ez * fy)), ((ez * fx) - (ex * fz)), ((ex * fy) - (ey * fx)));
            var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

            if (!(length > 0.0)) { return; }

            AddPlane(nx: (nx / length), ny: (ny / length), nz: (nz / length), place: place, px: m_x[place], py: m_y[place], pz: m_z[place], weight: (FeatureWeight * (((ex * ex) + (ey * ey)) + (ez * ez))));
        }
        // The barycentric weights of the point of a triangle nearest a point (Ericson, Real-Time Collision Detection 5.1.5).
        private static (double U, double V, double W) Closest(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, double px, double py, double pz) {
            var (abx, aby, abz) = ((bx - ax), (by - ay), (bz - az));
            var (acx, acy, acz) = ((cx - ax), (cy - ay), (cz - az));
            var (apx, apy, apz) = ((px - ax), (py - ay), (pz - az));
            var d1 = (((abx * apx) + (aby * apy)) + (abz * apz));
            var d2 = (((acx * apx) + (acy * apy)) + (acz * apz));

            if ((d1 <= 0.0) && (d2 <= 0.0)) { return (1.0, 0.0, 0.0); }

            var (bpx, bpy, bpz) = ((px - bx), (py - by), (pz - bz));
            var d3 = (((abx * bpx) + (aby * bpy)) + (abz * bpz));
            var d4 = (((acx * bpx) + (acy * bpy)) + (acz * bpz));

            if ((d3 >= 0.0) && (d4 <= d3)) { return (0.0, 1.0, 0.0); }

            var vc = ((d1 * d4) - (d3 * d2));

            if ((vc <= 0.0) && (d1 >= 0.0) && (d3 <= 0.0)) {
                var v = (d1 / (d1 - d3));

                return ((1.0 - v), v, 0.0);
            }

            var (cpx, cpy, cpz) = ((px - cx), (py - cy), (pz - cz));
            var d5 = (((abx * cpx) + (aby * cpy)) + (abz * cpz));
            var d6 = (((acx * cpx) + (acy * cpy)) + (acz * cpz));

            if ((d6 >= 0.0) && (d5 <= d6)) { return (0.0, 0.0, 1.0); }

            var vb = ((d5 * d2) - (d1 * d6));

            if ((vb <= 0.0) && (d2 >= 0.0) && (d6 <= 0.0)) {
                var w = (d2 / (d2 - d6));

                return ((1.0 - w), 0.0, w);
            }

            var va = ((d3 * d6) - (d5 * d4));

            if ((va <= 0.0) && ((d4 - d3) >= 0.0) && ((d5 - d6) >= 0.0)) {
                var w = ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

                return (0.0, (1.0 - w), w);
            }

            var denominator = (1.0 / ((va + vb) + vc));
            var inner = (vb * denominator);
            var outer = (vc * denominator);

            return (((1.0 - inner) - outer), inner, outer);
        }

        // A triangle about a collapsing place as it will be: its corners, its centroid and the radius about it that holds
        // them, its corners' normals and its material.
        private struct Candidate {
            public double Ax, Ay, Az, Bx, By, Bz, Ex, Ey, Ez, Cx, Cy, Cz, Radius;
            public double LowX, LowY, LowZ, HighX, HighY, HighZ;
            public Vector3 Na, Nb, Nc;
            public uint Material;
            public int Triangle;
        }
    }
}
