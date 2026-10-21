using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance.Baking;

/// <summary>A contoured surface in world space: a vertex per surface patch of each crossed cell, with its normal, and a
/// quad per sign-changing lattice edge, wound counter-clockwise seen from outside, with the material the field reported at
/// the edge's outside end, whose nearest shape is the one the edge crosses. A quad whose last two vertices are one vertex
/// is a triangle; any other splits into two triangles along its first diagonal, its first vertex to its third: the
/// diagonal whose triangles face out where only one does, else the one whose triangles turn least from the normals of
/// their corners that lie on no crease.</summary>
/// <param name="Positions">Three floats per vertex.</param>
/// <param name="Normals">Three floats per vertex: the unit outward normal.</param>
/// <param name="Quads">Four vertex indices per quad.</param>
/// <param name="QuadMaterials">One material per quad.</param>
public sealed record SdfContour(float[] Positions, float[] Normals, int[] Quads, int[] QuadMaterials) {
    /// <summary>Gets the quads.</summary>
    public int QuadCount => (Quads.Length / 4);
    /// <summary>Gets the vertices.</summary>
    public int VertexCount => (Positions.Length / 3);
    /// <summary>Gets, per vertex, whether it lies on a crease or corner of the surface, a point where crossings that turn
    /// apart by more than thirty degrees meet; empty when the contour records none. A vertex
    /// <see cref="SplitAtCreases"/> appends records none.</summary>
    public bool[] Features { get; init; } = [];
    /// <summary>Gets, per vertex, whether the contour placed it where no crossing pins it to the surface: drawn by its
    /// crossings' tangent planes away from their mean (to a crease, or to the point the planes of a face rounded within the
    /// cell share, proud of the rounding), or merged from a refined cell's fine vertices, which interpolate the cell's
    /// coarse corners. Empty when the contour records none. A vertex <see cref="SplitAtCreases"/> appends records
    /// none.</summary>
    public bool[] Solved { get; init; } = [];
    /// <summary>Gets, per vertex, whether the contour placed it inside its cell beside a curved convex crease that lies
    /// past the cell, where it may lie inside the solid off both faces and its quads turn across the crease: moved onto
    /// the surface, it stays a crease vertex (<see cref="Features"/>) so <see cref="SplitAtCreases"/> shades each face's
    /// quads as that face. Empty when the contour records none. A vertex <see cref="SplitAtCreases"/> appends records
    /// none.</summary>
    public bool[] Beside { get; init; } = [];

    // A lifted chamfer's corner meets its neighboring bevel at 31.4 degrees, although the bevel meets the main face at
    // 45. Leave room below that smaller turn for sampled triangle normals; using 32 blended the corner across its rim.
    private const double CreaseDegrees = 25.0;

    /// <summary>Selects each quad's outward-facing diagonal from its current vertex positions and normals. Call again
    /// after changing positions, before <see cref="SplitAtCreases"/> freezes the per-face normals.</summary>
    public void SelectDiagonals() => SdfSampledLattice.SelectDiagonals(contour: this);

    /// <summary>Returns the contour with its crease vertices split: a vertex two of whose quads face
    /// apart by more than 25 degrees gives each quad corner a vertex of its own at the same place, whose normal is the
    /// area-weighted facing of the vertex's quads within 25 degrees of that quad's, so a sharp edge shades as the two faces
    /// meeting at it rather than as a band of blended normals. Any other vertex gives such a vertex to the corners of the
    /// quads that face more than 25 degrees from its own normal: a vertex on a rounding narrower than its cell carries the
    /// rounding's normal, which the faces beyond it would otherwise blend across a cell. A quad folded against its own
    /// vertices' normals neither splits nor lends its facing.</summary>
    /// <returns>The contour with the split vertices appended and its quads renumbered to them; this contour when nothing
    /// splits.</returns>
    public SdfContour SplitAtCreases() {
        var positions = Positions;
        var normals = Normals;
        var vertexCount = (positions.Length / 3);
        var cosine = Math.Cos(d: (CreaseDegrees * (Math.PI / 180.0)));

        Vector3 At(float[] values, int vertex) => new(x: values[(3 * vertex)], y: values[((3 * vertex) + 1)], z: values[((3 * vertex) + 2)]);

        // A quad at a crease vertex folded across the crease, its two triangles facing apart by more than the crease
        // angle (one on each face, as where a crease is shorter than a cell), shades as its two triangles: as one quad it
        // takes one side's normal at every corner, and its other triangle shades as a spike of that side on this one.
        var quadList = new List<int>(capacity: Quads.Length);
        var materialList = new List<int>(capacity: QuadMaterials.Length);

        for (var quad = 0; (quad < QuadMaterials.Length); quad++) {
            var (ia, ib, ic, id) = (Quads[(4 * quad)], Quads[((4 * quad) + 1)], Quads[((4 * quad) + 2)], Quads[((4 * quad) + 3)]);
            if (ic != id) {
                var a = At(values: positions, vertex: ia);
                var first = Vector3.Cross(vector1: (At(values: positions, vertex: ib) - a), vector2: (At(values: positions, vertex: ic) - a));
                var second = Vector3.Cross(vector1: (At(values: positions, vertex: ic) - a), vector2: (At(values: positions, vertex: id) - a));

                if ((first.LengthSquared() > 0f) && (second.LengthSquared() > 0f) && (Vector3.Dot(vector1: Vector3.Normalize(value: first), vector2: Vector3.Normalize(value: second)) < cosine)) {
                    quadList.AddRange(collection: [ia, ib, ic, ic, ia, ic, id, id]);
                    materialList.Add(item: QuadMaterials[quad]);
                    materialList.Add(item: QuadMaterials[quad]);
                    continue;
                }
            }
            quadList.AddRange(collection: [ia, ib, ic, id]);
            materialList.Add(item: QuadMaterials[quad]);
        }

        int[] quads = [.. quadList];
        var quadCount = (quads.Length / 4);
        var folded = (quadCount != QuadMaterials.Length);

        // Each quad's area-weighted facing: its two triangles along its first diagonal.
        var facings = new Vector3[quadCount];

        for (var quad = 0; (quad < quadCount); quad++) {
            var a = At(values: positions, vertex: quads[(4 * quad)]);
            var b = At(values: positions, vertex: quads[((4 * quad) + 1)]);
            var c = At(values: positions, vertex: quads[((4 * quad) + 2)]);
            var d = At(values: positions, vertex: quads[((4 * quad) + 3)]);

            var facing = (Vector3.Cross(vector1: (b - a), vector2: (c - a)) + Vector3.Cross(vector1: (c - a), vector2: (d - a)));
            var outward = (((At(values: normals, vertex: quads[(4 * quad)]) + At(values: normals, vertex: quads[((4 * quad) + 1)])) +
                At(values: normals, vertex: quads[((4 * quad) + 2)])) + At(values: normals, vertex: quads[((4 * quad) + 3)]));

            // A quad folded against its own vertices' normals has no facing to shade by: it keeps their normals and
            // lends its facing to no split.
            facings[quad] = ((Vector3.Dot(vector1: facing, vector2: outward) > 0f) ? facing : Vector3.Zero);
        }

        // The quads around each vertex, as compressed rows.
        var starts = new int[(vertexCount + 1)];

        for (var corner = 0; (corner < quads.Length); corner++) {
            if (((corner & 3) == 3) && (quads[corner] == quads[(corner - 1)])) { continue; }
            starts[(quads[corner] + 1)]++;
        }
        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            starts[(vertex + 1)] += starts[vertex];
        }

        var around = new int[starts[vertexCount]];
        var filled = new int[vertexCount];

        for (var corner = 0; (corner < quads.Length); corner++) {
            if (((corner & 3) == 3) && (quads[corner] == quads[(corner - 1)])) { continue; }
            var vertex = quads[corner];

            around[(starts[vertex] + filled[vertex]++)] = (corner >> 2);
        }

        // Incident faces define the shading crease even when field correction cleared the QEF's feature flag. A normal
        // halfway between a chamfer's faces is less than the split angle from either face and cannot detect that crease.
        var creased = new bool[vertexCount];

        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            for (var first = starts[vertex]; (first < starts[(vertex + 1)]) && !creased[vertex]; first++) {
                if (!(facings[around[first]].LengthSquared() > 0f)) { continue; }
                var one = Vector3.Normalize(value: facings[around[first]]);

                for (var second = (first + 1); (second < starts[(vertex + 1)]); second++) {
                    if ((facings[around[second]].LengthSquared() > 0f) && (Vector3.Dot(vector1: one, vector2: Vector3.Normalize(value: facings[around[second]])) < cosine)) {
                        creased[vertex] = true;
                        break;
                    }
                }
            }
        }

        var addedPositions = new List<float>();
        var addedNormals = new List<float>();
        // A lowest quad only narrows the search: overlapping normal neighborhoods can share that quad without sharing
        // their other members, so reuse a split only when its complete neighborhood agrees.
        var splits = new Dictionary<(int Vertex, int Group), List<(int[] Members, int Vertex)>>();
        var members = new List<int>();

        for (var quad = 0; (quad < quadCount); quad++) {
            var facing = facings[quad];

            if (!(facing.LengthSquared() > 0f)) { continue; }
            facing = Vector3.Normalize(value: facing);

            // A triangle lists its last vertex twice; both corners take one vertex.
            var triangle = (quads[((4 * quad) + 3)] == quads[((4 * quad) + 2)]);

            for (var corner = 0; (corner < 4); corner++) {
                var at = ((4 * quad) + corner);

                if ((corner == 3) && triangle) {
                    quads[at] = quads[(at - 1)];
                    continue;
                }

                var vertex = quads[at];

                // Any other vertex splits off the corners of the quads that face more than the crease angle from its own
                // normal: it lies on a rounding narrower than its cell, where the field's normal turns, and those quads on
                // a face beyond the rounding, which would otherwise shade as a band of the rounding's normal a cell wide.
                if (!creased[vertex] && (Feature(vertex: vertex) || (Vector3.Dot(vector1: facing, vector2: At(values: normals, vertex: vertex)) >= cosine))) { continue; }

                var sum = Vector3.Zero;
                var group = int.MaxValue;
                members.Clear();

                for (var index = starts[vertex]; (index < starts[(vertex + 1)]); index++) {
                    var other = around[index];
                    var otherFacing = facings[other];

                    if (!(otherFacing.LengthSquared() > 0f) || (Vector3.Dot(vector1: Vector3.Normalize(value: otherFacing), vector2: facing) < cosine)) { continue; }
                    sum += otherFacing;
                    group = Math.Min(val1: group, val2: other);
                    members.Add(item: other);
                }

                if (!splits.TryGetValue(key: (vertex, group), value: out var candidates)) {
                    candidates = [];
                    splits.Add(key: (vertex, group), value: candidates);
                }
                var split = -1;

                foreach (var candidate in candidates) {
                    if (CollectionsMarshal.AsSpan(list: members).SequenceEqual(other: candidate.Members)) {
                        split = candidate.Vertex;
                        break;
                    }
                }
                if (split < 0) {
                    var normal = Vector3.Normalize(value: sum);

                    split = (vertexCount + (addedPositions.Count / 3));
                    candidates.Add(item: ([.. members], split));
                    addedPositions.Add(item: positions[(3 * vertex)]);
                    addedPositions.Add(item: positions[((3 * vertex) + 1)]);
                    addedPositions.Add(item: positions[((3 * vertex) + 2)]);
                    addedNormals.Add(item: normal.X);
                    addedNormals.Add(item: normal.Y);
                    addedNormals.Add(item: normal.Z);
                }
                quads[at] = split;
            }
        }

        return (((addedPositions.Count == 0) && !folded)
            ? this
            : (this with { Normals = [.. normals, .. addedNormals], Positions = [.. positions, .. addedPositions], QuadMaterials = [.. materialList], Quads = quads }));

        bool Feature(int vertex) => ((vertex < Features.Length) && Features[vertex]);
    }
}
/// <summary>An axis-aligned box of a sampled lattice's frame whose surface <see cref="SdfSampledLattice.Refine"/> contours
/// at the fine spacing.</summary>
/// <param name="Low">The box's lowest corner.</param>
/// <param name="High">The box's highest corner.</param>
public readonly record struct SdfLatticeRegion(Vector3 Low, Vector3 High);
/// <summary>
/// A field sampled on a lattice whose spacing may differ per axis, as a GPU bake reads it back: each sample's value and
/// the material the field reported there, then the gradients the contour reads. <see cref="Contour"/> extracts its
/// surface by manifold dual contouring, placing each vertex by the tangent planes of
/// its crossings: each crossing is located along its edge before its gradient is measured, so its plane belongs to the
/// face that edge crosses rather than a different face nearer a linear estimate inside the solid. A sharp edge stays sharp at any cell
/// size. Where the edge lies up to half a cell past the cell that sees both its faces, in a neighbour that sees one, the
/// vertex of a straight edge reaches past its cell to it, and a curved convex edge moves onto the neighbour's vertex.
/// <para>Where the samples cannot resolve the surface, the lattice refines (<see cref="Refine"/>): a cell crossed by a
/// sheet or gap thinner than a cell along an axis, whether a sample caught it or it passed between two, and a cell two
/// separate sheets cross are sampled again at a finer spacing
/// and contoured at it, with the cells around them contoured at the fine spacing from their coarse corners so the mesh
/// stays closed where its resolution changes. Every other cell contours whole.</para>
/// <para>The lattice is answered in stages: <see cref="Refine"/> names the fine samples to measure and
/// <see cref="AnswerRefinement"/> takes their values; <see cref="CrossingPoints"/> and <see cref="AnswerCrossings"/>
/// bracket the edge roots; <see cref="GradientPoints"/> names the samples the contour reads a
/// gradient at, the corners its vertex normals interpolate and the crossings its tangent planes pass through, and
/// <see cref="AnswerGradients"/> takes them. A sampled lattice's values come
/// from the GPU interpreter in floats, so its surface is presentation data that may differ between devices; nothing in it
/// reaches simulation state.</para>
/// </summary>
public sealed class SdfSampledLattice {
    // A refinement that would measure more fine samples than this is not taken: the lattice contours at its own spacing.
    private const int MaxFineSamples = 4_000_000;
    // How much less one diagonal's triangles must turn from their corners' normals than the other's for a quad to split
    // along it (SplitsAlongFirstDiagonal): below it, the two splits of a quad flat to within its normals' noise tie.
    private const double TurnTie = 1.0e-3;

    private readonly Vector3 m_origin;
    private readonly Vector3 m_cell;
    private readonly int m_samplesX;
    private readonly int m_samplesY;
    private readonly int m_samplesZ;
    private readonly float[] m_values;
    private readonly int[] m_materials;
    private readonly Vector3[] m_gradients;

    private int m_factor = 1;
    private bool[] m_measured = [];
    // Per coarse cell, whether a sheet or gap thinner than a cell crosses an edge of it (Refine), the cells CloseSheets
    // closes sheets in.
    private bool[] m_thin = [];
    private bool[] m_refined = [];
    private readonly Dictionary<int, int> m_fineSlots = [];
    private int[] m_fineKeys = [];
    private float[] m_fineValues = [];
    private int[] m_fineMaterials = [];
    private readonly Dictionary<int, Vector3> m_fineGradients = [];
    private int[] m_gradientKeys = [];
    private long[] m_crossingKeys = [];
    private bool m_crossingsPrepared;
    private bool m_rootsPrepared;
    private readonly Dictionary<long, CrossingRoot> m_crossingRoots = [];
    private long[] m_pendingCrossings = [];
    // Per crossing edge (CrossingKey), the gradient measured at its crossing and the field's value there.
    private readonly Dictionary<long, Vector4> m_crossingGradients = [];
    // The measured fine samples a sheet thinner than the fine cell passed beside, and the value each reads (CloseSheets).
    private readonly Dictionary<int, float> m_closed = [];

    /// <summary>Initializes a new instance of the <see cref="SdfSampledLattice"/> class.</summary>
    /// <param name="origin">The position of sample (0, 0, 0), in the frame the samples were taken in.</param>
    /// <param name="cell">The spacing between samples along each axis, in world units; each positive.</param>
    /// <param name="samplesX">The samples along X, at least two.</param>
    /// <param name="samplesY">The samples along Y, at least two.</param>
    /// <param name="samplesZ">The samples along Z, at least two.</param>
    /// <param name="values">One field value per sample, X fastest: negative inside. The lattice keeps the array.</param>
    /// <param name="materials">One material per sample. The lattice keeps the array.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An axis has fewer than two samples, a spacing is not positive, or an array's
    /// length does not match the lattice.</exception>
    public SdfSampledLattice(Vector3 origin, Vector3 cell, int samplesX, int samplesY, int samplesZ, float[] values, int[] materials) {
        ArgumentNullException.ThrowIfNull(argument: values);
        ArgumentNullException.ThrowIfNull(argument: materials);

        if ((samplesX < 2) || (samplesY < 2) || (samplesZ < 2)) {
            throw new ArgumentException(message: "A lattice holds at least two samples along each axis.");
        }
        if (!((cell.X > 0f) && (cell.Y > 0f) && (cell.Z > 0f))) {
            throw new ArgumentException(message: "A lattice's spacing is positive along each axis.", paramName: nameof(cell));
        }

        var count = ((samplesX * samplesY) * samplesZ);

        if ((values.Length != count) || (materials.Length != count)) {
            throw new ArgumentException(message: $"A lattice of {count} samples holds one value and material per sample.");
        }

        m_origin = origin;
        m_cell = cell;
        m_samplesX = samplesX;
        m_samplesY = samplesY;
        m_samplesZ = samplesZ;
        m_values = values;
        m_materials = materials;
        m_gradients = new Vector3[count];
    }

    /// <summary>Gets the fine cells along each axis of a coarse cell: one until <see cref="Refine"/> refines the
    /// lattice.</summary>
    public int Refinement => m_factor;
    /// <summary>Gets the coarse cells <see cref="Refine"/> measured at the fine spacing.</summary>
    public int MeasuredCells { get; private set; }
    /// <summary>Gets the coarse cells contoured at the fine spacing: the measured cells and every cell touching one.</summary>
    public int RefinedCells { get; private set; }
    /// <summary>Gets the spacing the contour's finest cells have along each axis, in world units.</summary>
    public Vector3 ContourCell => (m_cell / m_factor);

    /// <summary>
    /// Chooses the cells the samples cannot resolve and returns the fine samples that resolve them, <paramref name="factor"/>
    /// to a cell along each axis: a cell next to a lattice edge a sheet or gap thinner than a cell crosses (a run of one
    /// sample between two of the other sign whose crossings lie less than a cell apart, or two samples of one sign whose
    /// values fall steeply toward each other from both sides and meet below the surface, where no line beside it crosses
    /// the solid or gap two samples thick, as beside a solid's edge or corner the line crosses near its tip or grazes,
    /// which the contour keeps sharp at the lattice's spacing), a crossed cell whose corner signs hold more than one
    /// surface patch, and
    /// every cell the surface can enter (a corner within half the cell's diagonal of it) that overlaps one of
    /// <paramref name="regions"/>.
    /// </summary>
    /// <param name="factor">The fine cells along each axis of a coarse cell, at least two; fewer when that many would
    /// measure more fine samples than the refinement's budget (<see cref="Refinement"/> reports the split taken).</param>
    /// <param name="regions">The boxes, in the lattice's frame, whose surface is contoured at the fine spacing wherever
    /// the samples put it: the parts of a union whose own detail is finer than the lattice. Empty for none.</param>
    /// <returns>The fine samples to measure, in the lattice's frame, in the order <see cref="AnswerRefinement"/> takes
    /// their answers; empty when no cell needs refining or even two fine cells a side would measure too many, and the
    /// lattice then contours at its own spacing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is less than two.</exception>
    /// <exception cref="InvalidOperationException">The lattice is already refined.</exception>
    public Vector3[] Refine(int factor, ReadOnlySpan<SdfLatticeRegion> regions = default) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: factor, other: 2);

        if (m_factor != 1) {
            throw new InvalidOperationException(message: "The lattice is already refined.");
        }

        var cellsX = (m_samplesX - 1);
        var cellsY = (m_samplesY - 1);
        var cellsZ = (m_samplesZ - 1);
        var measured = new bool[((cellsX * cellsY) * cellsZ)];
        var thin = new bool[measured.Length];
        int[] samples = [m_samplesX, m_samplesY, m_samplesZ];
        int[] cells = [cellsX, cellsY, cellsZ];
        float[] spacing = [m_cell.X, m_cell.Y, m_cell.Z];
        var at = new int[3];
        var any = false;

        int CellIndex(int x, int y, int z) => (x + (cellsX * (y + (cellsY * z))));
        // Whether one of the four lattice lines beside sample `at`'s line along `axis` holds two neighbouring samples
        // inside (`inside`), or outside, from `first` to `last` along it: the solid or gap there is a step thick or
        // more. A sheet or gap thinner than a step, crossed along a line within a step, is as thin along the lines beside
        // it, while a solid's edge or corner a line crosses near its tip, or grazes, thickens beside it into the body.
        bool Thickens(Span<int> at, int axis, bool inside, int first, int last) {
            Span<int> p = stackalloc int[3];

            for (var offset = 1; (offset < 3); offset++) {
                var across = ((axis + offset) % 3);

                for (var direction = -1; (direction <= 1); direction += 2) {
                    at.CopyTo(destination: p);
                    p[across] += direction;
                    if ((p[across] < 0) || (p[across] >= samples[across])) {
                        continue;
                    }

                    var run = 0;

                    for (var step = Math.Max(val1: 0, val2: first); (step <= Math.Min(val1: last, val2: (samples[axis] - 1))); step++) {
                        p[axis] = step;
                        run = (((m_values[Index(x: p[0], y: p[1], z: p[2])] < 0f) == inside) ? (run + 1) : 0);
                        if (run >= 2) {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        // Marks the cells around the lattice edge from sample `at` along `axis`.
        void MarkEdge(Span<int> at, int axis) {
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);
            Span<int> cell = stackalloc int[3];

            cell[axis] = at[axis];
            for (var du = -1; (du <= 0); du++) {
                for (var dv = -1; (dv <= 0); dv++) {
                    cell[u] = (at[u] + du);
                    cell[v] = (at[v] + dv);
                    if ((cell[u] < 0) || (cell[u] >= cells[u]) || (cell[v] < 0) || (cell[v] >= cells[v]) || (cell[axis] >= cells[axis])) {
                        continue;
                    }
                    measured[CellIndex(x: cell[0], y: cell[1], z: cell[2])] = true;
                    thin[CellIndex(x: cell[0], y: cell[1], z: cell[2])] = true;
                    any = true;
                }
            }
        }

        // Crossed cells holding more than one surface patch. A cell whose outside corners disagree on the material is not
        // refined for that: the material cut (SdfMeshMaterialCut) searches each boundary on the mesh's own edges, and fine
        // cells along a concave seam between two materials merge into collinear vertices whose quads fold.
        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    var mask = 0;

                    for (var offset = 0; (offset < 8); offset++) {
                        if (m_values[Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))] < 0f) {
                            mask |= (1 << offset);
                        }
                    }

                    if ((mask == 0) || (mask == 0xFF)) {
                        continue;
                    }
                    if (SdfDualContouring.PatchesOf(mask: mask) > 1) {
                        measured[CellIndex(x: x, y: y, z: z)] = true;
                        any = true;
                    }
                }
            }
        }

        // Cells of a region the surface can enter: a corner lies within half the cell's diagonal of it.
        if (!regions.IsEmpty) {
            var reach = (0.5f * m_cell.Length());

            for (var z = 0; (z < cellsZ); z++) {
                for (var y = 0; (y < cellsY); y++) {
                    for (var x = 0; (x < cellsX); x++) {
                        var low = (m_origin + (new Vector3(x: x, y: y, z: z) * m_cell));
                        var high = (low + m_cell);
                        var overlaps = false;

                        foreach (var region in regions) {
                            overlaps |= ((low.X <= region.High.X) && (high.X >= region.Low.X) &&
                                (low.Y <= region.High.Y) && (high.Y >= region.Low.Y) &&
                                (low.Z <= region.High.Z) && (high.Z >= region.Low.Z));
                        }
                        if (!overlaps) {
                            continue;
                        }

                        var nearest = float.PositiveInfinity;

                        for (var offset = 0; (offset < 8); offset++) {
                            nearest = MathF.Min(nearest, MathF.Abs(m_values[Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))]));
                        }
                        if (nearest <= reach) {
                            measured[CellIndex(x: x, y: y, z: z)] = true;
                            any = true;
                        }
                    }
                }
            }
        }

        // Thin sheets and gaps along each axis.
        for (var axis = 0; (axis < 3); axis++) {
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);
            var h = spacing[axis];
            var n = samples[axis];

            for (var k = 0; (k < samples[v]); k++) {
                for (var j = 0; (j < samples[u]); j++) {
                    at[u] = j;
                    at[v] = k;

                    float ValueAt(int i) {
                        Span<int> p = [at[0], at[1], at[2]];

                        p[axis] = i;

                        return m_values[Index(x: p[0], y: p[1], z: p[2])];
                    }

                    for (var i = 1; ((i + 1) < n); i++) {
                        var before = ValueAt(i: (i - 1));
                        var value = ValueAt(i: i);
                        var after = ValueAt(i: (i + 1));
                        var inside = (value < 0f);

                        // A run of one sample between two of the other sign: a sheet or gap the lattice caught once. A
                        // solid's edge or corner the line crosses near its tip (an acute rim's, a tilted cut's) is as thin
                        // along the line, but thickens beside it (Thickens), and the contour's tangent planes keep it
                        // sharp at the lattice's own spacing.
                        if (((before < 0f) != inside) && ((after < 0f) != inside)) {
                            var middle = MathF.Abs(value);
                            var thickness = ((middle / (MathF.Abs(before) + middle)) + (middle / (middle + MathF.Abs(after))));

                            at[axis] = i;

                            if ((thickness < 1f) && !Thickens(at: at, axis: axis, first: (i - 1), inside: inside, last: (i + 1))) {
                                at[axis] = (i - 1);
                                MarkEdge(at: at, axis: axis);
                                at[axis] = i;
                                MarkEdge(at: at, axis: axis);
                            }
                        }
                        if ((i + 2) >= n) {
                            continue;
                        }

                        // Two samples of one sign, the field falling steeply toward them from both sides: a sheet or gap
                        // that passed between them when the falls meet below the surface.
                        var further = ValueAt(i: (i + 2));

                        if (((before < 0f) != inside) || ((after < 0f) != inside) || ((further < 0f) != inside)) {
                            continue;
                        }

                        at[axis] = i;

                        var slope = Slope(x: at[0], y: at[1], z: at[2]);

                        at[axis] = (i + 1);
                        slope = MathF.Max(slope, Slope(x: at[0], y: at[1], z: at[2]));
                        at[axis] = i;

                        // A solid's edge or corner the line grazes falls toward the pair from both sides as a sheet does,
                        // but the lines beside the pair cross its body a step thick or more (Thickens).
                        if (!Thickens(at: at, axis: axis, first: (i - 1), inside: !inside, last: (i + 2)) && PassesBetween(after: MathF.Abs(after), before: MathF.Abs(before), further: MathF.Abs(further), reach: (h * slope), value: MathF.Abs(value))) {
                            MarkEdge(at: at, axis: axis);
                        }
                    }
                }
            }
        }

        if (!any) {
            return [];
        }

        // Every cell touching a measured cell contours at the fine spacing too, from its coarse corners, so no cell that
        // contours whole meets a measured fine sample.
        var refined = new bool[measured.Length];
        var measuredCount = 0;
        var refinedCount = 0;

        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    if (!measured[CellIndex(x: x, y: y, z: z)]) {
                        continue;
                    }
                    measuredCount++;
                    for (var dz = Math.Max(val1: 0, val2: (z - 1)); (dz <= Math.Min(val1: (cellsZ - 1), val2: (z + 1))); dz++) {
                        for (var dy = Math.Max(val1: 0, val2: (y - 1)); (dy <= Math.Min(val1: (cellsY - 1), val2: (y + 1))); dy++) {
                            for (var dx = Math.Max(val1: 0, val2: (x - 1)); (dx <= Math.Min(val1: (cellsX - 1), val2: (x + 1))); dx++) {
                                refined[CellIndex(x: dx, y: dy, z: dz)] = true;
                            }
                        }
                    }
                }
            }
        }
        foreach (var cell in refined) {
            refinedCount += (cell ? 1 : 0);
        }

        // The fine samples of every measured cell, its faces included, that are not coarse samples. A refinement that would
        // measure more than MaxFineSamples splits each cell into fewer fine cells rather than none: a sheet thinner than
        // the coarse cell is caught by any refinement and lost by the coarse contour.
        var keys = new List<int>();
        var slots = new Dictionary<int, int>();

        while (!FineKeys(split: factor)) {
            if (--factor < 2) {
                return [];
            }
        }

        bool FineKeys(int split) {
            var fineX = ((cellsX * split) + 1);
            var fineY = ((cellsY * split) + 1);

            keys.Clear();
            slots.Clear();
            for (var z = 0; (z < cellsZ); z++) {
                for (var y = 0; (y < cellsY); y++) {
                    for (var x = 0; (x < cellsX); x++) {
                        if (!measured[CellIndex(x: x, y: y, z: z)]) {
                            continue;
                        }
                        for (var fz = (z * split); (fz <= ((z + 1) * split)); fz++) {
                            for (var fy = (y * split); (fy <= ((y + 1) * split)); fy++) {
                                for (var fx = (x * split); (fx <= ((x + 1) * split)); fx++) {
                                    if (((fx % split) == 0) && ((fy % split) == 0) && ((fz % split) == 0)) {
                                        continue;
                                    }

                                    var key = (fx + (fineX * (fy + (fineY * fz))));

                                    if (slots.TryAdd(key: key, value: keys.Count)) {
                                        keys.Add(item: key);
                                    }
                                }
                            }
                        }
                        if (keys.Count > MaxFineSamples) {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        m_factor = factor;
        m_measured = measured;
        m_thin = thin;
        m_refined = refined;
        MeasuredCells = measuredCount;
        RefinedCells = refinedCount;
        m_fineKeys = [.. keys];
        m_fineValues = new float[keys.Count];
        m_fineMaterials = new int[keys.Count];
        foreach (var (key, slot) in slots) {
            m_fineSlots.Add(key: key, value: slot);
        }

        var points = new Vector3[keys.Count];
        var fine = ContourCell;

        for (var slot = 0; (slot < keys.Count); slot++) {
            var (fx, fy, fz) = FineCoordinates(key: keys[slot]);

            points[slot] = (m_origin + (new Vector3(x: fx, y: fy, z: fz) * fine));
        }

        return points;
    }
    // Whether a sheet passed between two samples of one sign, `value` and `after`, whose neighbours `before` and `further`
    // lie a step out on either side, all as magnitudes: the field falls toward the pair from both sides by over half
    // `reach`, the most it changes over a step there, and the falls, extended, meet within a tenth of `reach` of the
    // surface. `reach` carries the field's own scale, since a field that is no true distance (a scope, a flare, a
    // non-uniform scale) changes by less, or more, than a step's length over a step.
    private static bool PassesBetween(float before, float value, float after, float further, float reach) {
        var fallA = (before - value);
        var fallB = (further - after);

        if (!(reach > 0f) || (fallA <= (0.5f * reach)) || (fallB <= (0.5f * reach))) {
            return false;
        }

        var t = Math.Clamp(value: (((value - after) + fallB) / (fallA + fallB)), min: 0f, max: 1f);

        return ((value - (fallA * t)) < (0.1f * reach));
    }
    // The most the field changes per world unit between a coarse sample and any of its six neighbours: the scale of the
    // field there, which a step's change is measured against (PassesBetween).
    private float Slope(int x, int y, int z) {
        var value = m_values[Index(x: x, y: y, z: z)];
        var slope = 0f;

        if (x > 0) { slope = MathF.Max(slope, (MathF.Abs(value - m_values[Index(x: (x - 1), y: y, z: z)]) / m_cell.X)); }
        if ((x + 1) < m_samplesX) { slope = MathF.Max(slope, (MathF.Abs(value - m_values[Index(x: (x + 1), y: y, z: z)]) / m_cell.X)); }
        if (y > 0) { slope = MathF.Max(slope, (MathF.Abs(value - m_values[Index(x: x, y: (y - 1), z: z)]) / m_cell.Y)); }
        if ((y + 1) < m_samplesY) { slope = MathF.Max(slope, (MathF.Abs(value - m_values[Index(x: x, y: (y + 1), z: z)]) / m_cell.Y)); }
        if (z > 0) { slope = MathF.Max(slope, (MathF.Abs(value - m_values[Index(x: x, y: y, z: (z - 1))]) / m_cell.Z)); }
        if ((z + 1) < m_samplesZ) { slope = MathF.Max(slope, (MathF.Abs(value - m_values[Index(x: x, y: y, z: (z + 1))]) / m_cell.Z)); }

        return slope;
    }
    /// <summary>Returns the field's typical change per world unit across its surface: the median, over the coarse lattice
    /// edges that change sign, of the change along the edge over its length. A field that is no true distance changes by
    /// less, or more, than one.</summary>
    /// <returns>The median slope, in field units per world unit; one where no edge changes sign.</returns>
    public float SurfaceSlope() {
        var slopes = new List<float>();

        for (var z = 0; (z < m_samplesZ); z++) {
            for (var y = 0; (y < m_samplesY); y++) {
                for (var x = 0; (x < m_samplesX); x++) {
                    var value = m_values[Index(x: x, y: y, z: z)];

                    if (((x + 1) < m_samplesX) && ((value < 0f) != (m_values[Index(x: (x + 1), y: y, z: z)] < 0f))) { slopes.Add(item: (MathF.Abs(value - m_values[Index(x: (x + 1), y: y, z: z)]) / m_cell.X)); }
                    if (((y + 1) < m_samplesY) && ((value < 0f) != (m_values[Index(x: x, y: (y + 1), z: z)] < 0f))) { slopes.Add(item: (MathF.Abs(value - m_values[Index(x: x, y: (y + 1), z: z)]) / m_cell.Y)); }
                    if (((z + 1) < m_samplesZ) && ((value < 0f) != (m_values[Index(x: x, y: y, z: (z + 1))] < 0f))) { slopes.Add(item: (MathF.Abs(value - m_values[Index(x: x, y: y, z: (z + 1))]) / m_cell.Z)); }
                }
            }
        }
        slopes.Sort();

        return ((slopes.Count > 0) ? slopes[(slopes.Count / 2)] : 1f);
    }
    /// <summary>Answers a run of the fine samples <see cref="Refine"/> named.</summary>
    /// <param name="first">The first sample the run answers, in <see cref="Refine"/>'s order.</param>
    /// <param name="values">The run's field values.</param>
    /// <param name="materials">The run's materials, one per value.</param>
    /// <exception cref="ArgumentException">The run's arrays differ in length or reach past the fine samples.</exception>
    public void AnswerRefinement(int first, ReadOnlySpan<float> values, ReadOnlySpan<int> materials) {
        if ((values.Length != materials.Length) || (first < 0) || ((first + values.Length) > m_fineValues.Length)) {
            throw new ArgumentException(message: "A refinement answer holds one value and material per fine sample it names.");
        }

        for (var index = 0; (index < values.Length); index++) {
            m_fineValues[(first + index)] = values[index];
            m_fineMaterials[(first + index)] = materials[index];
        }
    }
    // An edge root stays bracketed by its original signs. The first trial is the linear estimate; subsequent trials
    // bisect, so a field flat inside a corner cannot stall the search on the wrong face as an unguarded secant can.
    private sealed class CrossingRoot(float first, float second) {
        public float Low { get; set; }
        public float High { get; set; } = 1f;
        public bool LowInside { get; } = (first < 0f);
        public float Position { get; set; } = Math.Clamp(value: (first / (first - second)), min: 0f, max: 1f);
        public int Rounds { get; set; }
        public bool Done { get; set; } = ((first == 0f) || (second == 0f));
    }
    // Closing sheets changes their edge crossings deliberately; both crossing searches and gradient sampling use the
    // resulting lattice, prepared once after the refinement answers arrive.
    private void PrepareCrossings() {
        if (m_crossingsPrepared) { return; }
        CloseSheets();
        m_crossingKeys = CrossingEdges();
        m_crossingsPrepared = true;
    }
    private int EdgeEnd(int start, int axis, int length) {
        var fineX = (((m_samplesX - 1) * m_factor) + 1);
        var fineY = (((m_samplesY - 1) * m_factor) + 1);

        return (start + ((axis == 0) ? length : ((axis == 1) ? (length * fineX) : (length * fineX * fineY))));
    }
    private bool MeasuredSample(int key) {
        var (x, y, z) = FineCoordinates(key: key);

        return (((x % m_factor) == 0) && ((y % m_factor) == 0) && ((z % m_factor) == 0)) || m_fineSlots.ContainsKey(key: key);
    }
    private Vector3 CrossingPoint(long key, float fraction) {
        var (start, axis, length) = CrossingEdge(key: key);
        var (x, y, z) = FineCoordinates(key: start);
        var along = (fraction * length);

        return (m_origin + (new Vector3(x: (x + ((axis == 0) ? along : 0f)), y: (y + ((axis == 1) ? along : 0f)), z: (z + ((axis == 2) ? along : 0f))) * ContourCell));
    }
    /// <summary>Returns the next points for locating each sign-changing edge's zero along that edge. After refinement,
    /// repeatedly answer these points with <see cref="AnswerCrossings"/> until this returns no points, then measure
    /// <see cref="GradientPoints"/>. Artificial transition samples and closed thin sheets keep their lattice crossings.</summary>
    /// <returns>The next round's points in the order <see cref="AnswerCrossings"/> takes their values.</returns>
    public Vector3[] CrossingPoints() {
        PrepareCrossings();
        if (!m_rootsPrepared) {
            foreach (var key in m_crossingKeys) {
                var (start, axis, length) = CrossingEdge(key: key);
                var end = EdgeEnd(start: start, axis: axis, length: length);

                if (m_closed.ContainsKey(key: start) || m_closed.ContainsKey(key: end) || !MeasuredSample(key: start) || !MeasuredSample(key: end)) { continue; }
                var (x, y, z) = FineCoordinates(key: start);
                var (ex, ey, ez) = FineCoordinates(key: end);

                m_crossingRoots.Add(key: key, value: new CrossingRoot(first: FineValue(x: x, y: y, z: z), second: FineValue(x: ex, y: ey, z: ez)));
            }
            m_rootsPrepared = true;
        }

        var pending = new List<long>();
        var points = new List<Vector3>();

        foreach (var key in m_crossingKeys) {
            if (!m_crossingRoots.TryGetValue(key: key, value: out var root) || root.Done) { continue; }
            pending.Add(item: key);
            points.Add(item: CrossingPoint(key: key, fraction: root.Position));
        }
        m_pendingCrossings = [.. pending];

        return [.. points];
    }
    /// <summary>Answers a chunk of the current <see cref="CrossingPoints"/> round with field values. Every root remains
    /// on its own edge; at most twelve bisections after the initial estimate bound its error below one 4,096th of an edge.</summary>
    /// <param name="first">The first point answered in the current round.</param>
    /// <param name="values">One field value per point.</param>
    /// <exception cref="ArgumentException">The chunk reaches past the current round or holds a non-finite value.</exception>
    public void AnswerCrossings(int first, ReadOnlySpan<float> values) {
        if ((first < 0) || ((first + values.Length) > m_pendingCrossings.Length)) {
            throw new ArgumentException(message: "A crossing answer must name points in the current crossing round.");
        }
        for (var index = 0; (index < values.Length); index++) {
            var value = values[index];

            if (!float.IsFinite(value)) { throw new ArgumentException(message: "A crossing answer must be finite."); }
            var root = m_crossingRoots[m_pendingCrossings[(first + index)]];

            if (value == 0f) {
                root.Done = true;
                continue;
            }
            if ((value < 0f) == root.LowInside) { root.Low = root.Position; }
            else { root.High = root.Position; }
            root.Rounds++;
            root.Position = ((root.Low + root.High) * 0.5f);
            root.Done = ((root.Rounds >= 13) || ((root.High - root.Low) <= 0.0001f));
        }
    }
    /// <summary>Returns the samples the contour reads a gradient at: the corners of every crossed cell that contours whole,
    /// the coarse corners of every refined cell, whose gradients its unmeasured fine samples interpolate, and the measured
    /// corners of every crossed fine cell, which its vertex normals interpolate; then the crossing of every sign-changing
    /// edge a vertex is placed by. Call it once the refinement, if any, is answered.</summary>
    /// <returns>The samples, in the lattice's frame, in the order <see cref="AnswerGradients"/> takes their
    /// answers.</returns>
    public Vector3[] GradientPoints() {
        PrepareCrossings();

        var keys = new List<int>();
        var seen = new HashSet<int>();
        var cellsX = (m_samplesX - 1);
        var cellsY = (m_samplesY - 1);
        var cellsZ = (m_samplesZ - 1);
        var factor = m_factor;
        var fineX = ((cellsX * factor) + 1);
        var fineY = ((cellsY * factor) + 1);

        void Add(int fx, int fy, int fz) {
            var key = (fx + (fineX * (fy + (fineY * fz))));

            if (seen.Add(item: key)) {
                keys.Add(item: key);
            }
        }

        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    var refined = ((factor > 1) && m_refined[(x + (cellsX * (y + (cellsY * z))))]);
                    var mask = 0;

                    for (var offset = 0; (offset < 8); offset++) {
                        mask |= ((m_values[Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))] < 0f) ? (1 << offset) : 0);
                    }
                    if (refined || ((mask != 0) && (mask != 0xFF))) {
                        for (var offset = 0; (offset < 8); offset++) {
                            Add(fx: ((x + (offset & 1)) * factor), fy: ((y + ((offset >> 1) & 1)) * factor), fz: ((z + ((offset >> 2) & 1)) * factor));
                        }
                    }
                    if (!refined) {
                        continue;
                    }

                    for (var fz = (z * factor); (fz < ((z + 1) * factor)); fz++) {
                        for (var fy = (y * factor); (fy < ((y + 1) * factor)); fy++) {
                            for (var fx = (x * factor); (fx < ((x + 1) * factor)); fx++) {
                                var fineMask = 0;

                                for (var offset = 0; (offset < 8); offset++) {
                                    fineMask |= ((FineValue(x: (fx + (offset & 1)), y: (fy + ((offset >> 1) & 1)), z: (fz + ((offset >> 2) & 1))) < 0f) ? (1 << offset) : 0);
                                }
                                if ((fineMask == 0) || (fineMask == 0xFF)) {
                                    continue;
                                }
                                for (var offset = 0; (offset < 8); offset++) {
                                    var cx = (fx + (offset & 1));
                                    var cy = (fy + ((offset >> 1) & 1));
                                    var cz = (fz + ((offset >> 2) & 1));

                                    if (m_fineSlots.ContainsKey(key: (cx + (fineX * (cy + (fineY * cz)))))) {
                                        Add(fx: cx, fy: cy, fz: cz);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        m_gradientKeys = [.. keys];

        var points = new Vector3[(keys.Count + m_crossingKeys.Length)];
        var fine = ContourCell;

        for (var index = 0; (index < keys.Count); index++) {
            var (fx, fy, fz) = FineCoordinates(key: keys[index]);

            points[index] = (m_origin + (new Vector3(x: fx, y: fy, z: fz) * fine));
        }
        for (var index = 0; (index < m_crossingKeys.Length); index++) {
            var (start, axis, length) = CrossingEdge(key: m_crossingKeys[index]);
            var (fx, fy, fz) = FineCoordinates(key: start);
            var position = new Vector3(x: fx, y: fy, z: fz);
            var step = ((axis == 0) ? Vector3.UnitX : ((axis == 1) ? Vector3.UnitY : Vector3.UnitZ));

            points[(keys.Count + index)] = (m_origin + ((position + (step * (Crossing(axis: axis, length: length, x: fx, y: fy, z: fz) * length))) * fine));
        }

        return points;
    }
    // Closes the sheets thinner than the fine cell in the cells Refine saw one cross, which the fine samples alone leave
    // holed, fray into slivers where they thin to a knife edge, and contour as two faces closer than a vertex's placement
    // can tell apart. A measured fine sample inside such a sheet reads as inside by half the field's change over a fine
    // step, so the sheet contours one fine cell thick about it; where one passed between two measured fine samples of a
    // line, both outside, the nearer reads as far inside as it lay outside (a quarter step at least), so the sheet's face
    // stays where it passed; and a sheet one sample thick stepping diagonally across the lattice is joined along the faces
    // its steps share. Lattice.TryCrossingNormal keeps the crossings beside a closed sample where the values put them.
    private void CloseSheets() {
        if (m_factor < 2) {
            return;
        }

        var factor = m_factor;
        int[] fine = [(((m_samplesX - 1) * factor) + 1), (((m_samplesY - 1) * factor) + 1), (((m_samplesZ - 1) * factor) + 1)];
        var cell = ContourCell;
        float[] spacing = [cell.X, cell.Y, cell.Z];
        var step = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));
        var closing = new Dictionary<int, float>();
        Span<int> p = stackalloc int[3];

        int Key(Span<int> at) => (at[0] + (fine[0] * (at[1] + (fine[1] * at[2]))));
        bool Coarse(Span<int> at) => (((at[0] % factor) == 0) && ((at[1] % factor) == 0) && ((at[2] % factor) == 0));
        bool Within(Span<int> at) => ((at[0] >= 0) && (at[1] >= 0) && (at[2] >= 0) && (at[0] < fine[0]) && (at[1] < fine[1]) && (at[2] < fine[2]));
        bool Real(Span<int> at) => (Within(at: at) && (Coarse(at: at) || m_fineSlots.ContainsKey(key: Key(at: at))));
        var neighbour = new int[3];
        // The field's scale at a fine sample: the most it changes per world unit to any of its six neighbours that was
        // measured (Slope's fine counterpart).
        float FineSlope(Span<int> at) {
            var value = FineValue(x: at[0], y: at[1], z: at[2]);
            var slope = 0f;

            for (var axis = 0; (axis < 3); axis++) {
                for (var direction = -1; (direction <= 1); direction += 2) {
                    neighbour[0] = at[0];
                    neighbour[1] = at[1];
                    neighbour[2] = at[2];
                    neighbour[axis] += direction;
                    if (Real(at: neighbour)) {
                        slope = MathF.Max(slope, (MathF.Abs(value - FineValue(x: neighbour[0], y: neighbour[1], z: neighbour[2])) / spacing[axis]));
                    }
                }
            }

            return slope;
        }
        void Close(int key, float slope, float depth) => closing.TryAdd(key: key, value: -MathF.Max(depth, MathF.Max((0.25f * step * slope), 1.0e-7f)));
        var around = new int[3];
        // The deepest inside value among a fine sample's six neighbours, zero for none inside.
        float Deepest(Span<int> at) {
            var deepest = 0f;

            for (var axis = 0; (axis < 3); axis++) {
                for (var direction = -1; (direction <= 1); direction += 2) {
                    around[0] = at[0];
                    around[1] = at[1];
                    around[2] = at[2];
                    around[axis] += direction;
                    deepest = (Within(at: around) ? MathF.Min(deepest, FineValue(x: around[0], y: around[1], z: around[2])) : deepest);
                }
            }

            return deepest;
        }
        var side = new int[3];
        var beside = new int[3];
        var clear = (0.1f * step * SurfaceSlope());
        // Whether a fine sample lies inside a sheet: inside, outside a step to either side along some axis by more than a
        // tenth of the field's typical change over a step, which a surface grazing the line within its samples' noise is
        // not, and no neighbour deeper inside than those two lie outside, as a solid's edge or corner, which a line near it
        // also crosses twice within a step, would hold, nor a line beside it inside for two steps (Thickens), as a wedge
        // too acute for its neighbours to reach deep would; with `thinner`, a sheet whose crossings on that line lie less
        // than a step apart.
        bool Sheet(Span<int> at, bool thinner) {
            var value = FineValue(x: at[0], y: at[1], z: at[2]);

            if (!(value < 0f)) {
                return false;
            }

            var deepest = Deepest(at: at);

            for (var axis = 0; (axis < 3); axis++) {
                side[0] = at[0];
                side[1] = at[1];
                side[2] = at[2];
                side[axis]--;
                var low = (Within(at: side) ? FineValue(x: side[0], y: side[1], z: side[2]) : -1f);
                side[axis] += 2;
                var high = (Within(at: side) ? FineValue(x: side[0], y: side[1], z: side[2]) : -1f);

                if ((low > clear) && (high > clear) && (deepest > -MathF.Max(low, high)) && (!thinner || (((-value / (low - value)) + (-value / (high - value))) < 1f)) &&
                    !Thickens(at: at, axis: axis, first: (at[axis] - 1), last: (at[axis] + 1))) {
                    return true;
                }
            }

            return false;
        }
        // Whether a fine line parallel to fine sample `at`'s line along `axis`, up to Reach fine steps from it across or
        // diagonally, holds two neighbouring samples inside from `first` to `last` along it, widened by its distance
        // (Refine's Thickens at the fine spacing): a solid's acute edge or wedge, which a line crosses near its tip or
        // grazes, thickens within a few fine steps beside it into the body, where a sheet thinner than a fine step does
        // not.
        bool Thickens(Span<int> at, int axis, int first, int last) {
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);
            const int Reach = 3;

            for (var distance = 1; (distance <= Reach); distance++) {
                for (var direction = 0; (direction < 8); direction++) {
                    var (du, dv) = (direction switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), 3 => (0, -1), 4 => (1, 1), 5 => (1, -1), 6 => (-1, 1), _ => (-1, -1) });

                    var run = 0;

                    for (var step = (first - distance); (step <= (last + distance)); step++) {
                        beside[0] = at[0];
                        beside[1] = at[1];
                        beside[2] = at[2];
                        beside[u] += (du * distance);
                        beside[v] += (dv * distance);
                        beside[axis] = step;
                        if (!Within(at: beside)) {
                            run = 0;
                            continue;
                        }
                        run = ((FineValue(x: beside[0], y: beside[1], z: beside[2]) < 0f) ? (run + 1) : 0);
                        if (run >= 2) {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        foreach (var key in m_fineKeys) {
            var (x, y, z) = FineCoordinates(key: key);
            var (cx, cy, cz, _, _, _) = Interpolant(x: x, y: y, z: z);

            // Only where the coarse samples saw a sheet or gap thinner than a cell: elsewhere a line grazing a solid's
            // edge falls toward its samples as a sheet's does.
            if (!m_thin[(cx + ((m_samplesX - 1) * (cy + ((m_samplesY - 1) * cz))))]) {
                continue;
            }

            p[0] = x;
            p[1] = y;
            p[2] = z;

            // A sheet a sample caught, thinner than the fine cell along a line: its two faces, a fraction of a cell
            // apart, cross where the tangent planes misplace a vertex by as much, so it too contours a cell thick.
            if (Sheet(at: p, thinner: true)) {
                Close(depth: (0.5f * step * FineSlope(at: p)), key: key, slope: 0f);
            }

            for (var axis = 0; (axis < 3); axis++) {
                // The measured sample as the first of the pair, then as the second.
                for (var shift = 0; (shift > -2); shift--) {
                    p[0] = x;
                    p[1] = y;
                    p[2] = z;
                    p[axis] += (shift - 1);
                    // The pair's outer neighbours may lie in a cell that interpolates its coarse corners, beside a sheet
                    // at the measured cells' edge.
                    if (!Within(at: p)) { continue; }
                    var before = FineValue(x: p[0], y: p[1], z: p[2]);
                    p[axis]++;
                    if (!Real(at: p)) { continue; }
                    var first = FineValue(x: p[0], y: p[1], z: p[2]);
                    var firstKey = (Coarse(at: p) ? -1 : Key(at: p));
                    var firstSlope = FineSlope(at: p);
                    var deepest = Deepest(at: p);
                    p[axis]++;
                    if (!Real(at: p)) { continue; }
                    var second = FineValue(x: p[0], y: p[1], z: p[2]);
                    var secondKey = (Coarse(at: p) ? -1 : Key(at: p));
                    var secondSlope = FineSlope(at: p);
                    deepest = MathF.Min(deepest, Deepest(at: p));
                    p[axis]++;
                    if (!Within(at: p)) { continue; }
                    var further = FineValue(x: p[0], y: p[1], z: p[2]);

                    // A solid's edge the line grazes falls toward the pair from both sides as a sheet does, but holds
                    // a neighbour of the pair deeper inside than half a step, which a sheet thinner than a step cannot,
                    // or, an acute wedge's, a line beside the pair inside for two steps (Thickens).
                    if ((before < 0f) || (first < 0f) || (second < 0f) || (further < 0f) ||
                        (deepest < (-0.5f * spacing[axis] * MathF.Max(firstSlope, secondSlope))) ||
                        Thickens(at: p, axis: axis, first: (p[axis] - 3), last: p[axis]) ||
                        !PassesBetween(after: second, before: before, further: further, reach: (spacing[axis] * MathF.Max(firstSlope, secondSlope)), value: first)) {
                        continue;
                    }

                    var nearer = ((((first <= second) && (firstKey >= 0)) || (secondKey < 0)) ? (Key: firstKey, Value: first, Slope: firstSlope) : (Key: secondKey, Value: second, Slope: secondSlope));

                    // A sheet that passed between the pair lies within half a step of the nearer: one farther off is a
                    // surface the samples beyond the pair, interpolated across a coarse cell, misread as falling.
                    // It reads as far inside as it lay outside, which puts the sheet's face toward the pair where the
                    // sheet passed and moves a surface a grazed edge misread by no more than the sample's own distance.
                    if ((nearer.Key >= 0) && (nearer.Value <= (0.5f * spacing[axis] * nearer.Slope))) { Close(depth: nearer.Value, key: nearer.Key, slope: nearer.Slope); }
                }
            }
        }

        foreach (var (key, value) in closing) {
            m_closed.TryAdd(key: key, value: value);
        }

        // A sheet one sample thick that steps across the lattice diagonally holds its inside samples at the opposite
        // corners of a face, which the contour cuts apart (an ambiguous face separates its inside corners), leaving a slit
        // along every step. Where both inside corners lie in a sheet (outside on either side along some axis), the face's
        // outside corner nearer the sheet closes, so the steps join along a face.
        Span<int> corner = stackalloc int[3];
        Span<int> chosenAt = stackalloc int[3];
        Span<int> keys = stackalloc int[4];
        Span<float> values = stackalloc float[4];

        for (var pass = 0; (pass < 2); pass++) {
            closing.Clear();
            foreach (var key in m_fineKeys) {
                var (x, y, z) = FineCoordinates(key: key);

                // Each face the sample is a corner of, as the face's lowest corner shifted by (du, dv) on its two axes.
                for (var normal = 0; (normal < 3); normal++) {
                    var u = ((normal + 1) % 3);
                    var v = ((normal + 2) % 3);

                    for (var du = -1; (du <= 0); du++) {
                        for (var dv = -1; (dv <= 0); dv++) {
                            corner[0] = x;
                            corner[1] = y;
                            corner[2] = z;
                            corner[u] += du;
                            corner[v] += dv;

                            // The face's corners in order: (0, 0), (1, 0), (0, 1), (1, 1) along (u, v).
                            var real = true;

                            for (var at = 0; (at < 4) && real; at++) {
                                p[0] = corner[0];
                                p[1] = corner[1];
                                p[2] = corner[2];
                                p[u] += (at & 1);
                                p[v] += (at >> 1);
                                real = Real(at: p);
                                keys[at] = (real ? (Coarse(at: p) ? -1 : Key(at: p)) : -1);
                                values[at] = (real ? FineValue(x: p[0], y: p[1], z: p[2]) : 0f);
                            }
                            if (!real) { continue; }

                            // Inside on one diagonal, outside on the other.
                            var diagonal = (((values[0] < 0f) && (values[3] < 0f) && (values[1] >= 0f) && (values[2] >= 0f)) ? 0 :
                                (((values[1] < 0f) && (values[2] < 0f) && (values[0] >= 0f) && (values[3] >= 0f)) ? 1 : -1));

                            if (diagonal < 0) { continue; }

                            var (inA, inB, outA, outB) = ((diagonal == 0) ? (0, 3, 1, 2) : (1, 2, 0, 3));
                            var sheet = true;

                            foreach (var inner in (ReadOnlySpan<int>)[inA, inB]) {
                                p[0] = corner[0];
                                p[1] = corner[1];
                                p[2] = corner[2];
                                p[u] += (inner & 1);
                                p[v] += (inner >> 1);
                                sheet &= Sheet(at: p, thinner: false);
                            }
                            if (!sheet) { continue; }

                            var (near, far) = ((values[outA] <= values[outB]) ? (outA, outB) : (outB, outA));
                            var chosen = ((keys[near] >= 0) ? near : far);

                            if (keys[chosen] < 0) { continue; }
                            chosenAt[0] = corner[0];
                            chosenAt[1] = corner[1];
                            chosenAt[2] = corner[2];
                            chosenAt[u] += (chosen & 1);
                            chosenAt[v] += (chosen >> 1);
                            Close(depth: values[chosen], key: keys[chosen], slope: FineSlope(at: chosenAt));
                        }
                    }
                }
            }
            if (closing.Count == 0) {
                break;
            }
            foreach (var (key, value) in closing) {
                m_closed.TryAdd(key: key, value: value);
            }
        }
    }
    // Every sign-changing lattice edge the contour places a vertex by: the edges of each crossed cell that contours whole,
    // a coarse cell long, and the fine edges of each crossed fine cell of a refined cell. A crossing's gradient is measured
    // where the edge crosses the surface, since its corners' gradients, inside a solid near a sharp edge, point to the
    // nearer face beyond the edge and tilt the vertex's tangent planes into a chamfer.
    private long[] CrossingEdges() {
        var edges = new List<long>();
        var seen = new HashSet<long>();
        var cellsX = (m_samplesX - 1);
        var cellsY = (m_samplesY - 1);
        var cellsZ = (m_samplesZ - 1);
        var factor = m_factor;
        var fineX = ((cellsX * factor) + 1);
        var fineY = ((cellsY * factor) + 1);

        void AddCell(int x, int y, int z, int stride) {
            var mask = 0;

            for (var offset = 0; (offset < 8); offset++) {
                mask |= ((FineValue(x: (x + ((offset & 1) * stride)), y: (y + (((offset >> 1) & 1) * stride)), z: (z + (((offset >> 2) & 1) * stride))) < 0f) ? (1 << offset) : 0);
            }
            if ((mask == 0) || (mask == 0xFF)) {
                return;
            }

            for (var axis = 0; (axis < 3); axis++) {
                for (var u = 0; (u < 2); u++) {
                    for (var v = 0; (v < 2); v++) {
                        // The edge along `axis` at offsets (u, v) on the two axes after it, as corner bits a (start) and b.
                        var a = (((u << ((axis + 1) % 3)) | (v << ((axis + 2) % 3))));
                        var b = (a | (1 << axis));

                        if ((((mask >> a) ^ (mask >> b)) & 1) == 0) {
                            continue;
                        }

                        var start = ((x + ((a & 1) * stride)) + (fineX * ((y + (((a >> 1) & 1) * stride)) + (fineY * (z + (((a >> 2) & 1) * stride))))));
                        var key = CrossingKey(axis: axis, length: stride, start: start);

                        if (seen.Add(item: key)) {
                            edges.Add(item: key);
                        }
                    }
                }
            }
        }

        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    if (!((factor > 1) && m_refined[(x + (cellsX * (y + (cellsY * z))))])) {
                        AddCell(stride: factor, x: (x * factor), y: (y * factor), z: (z * factor));

                        continue;
                    }

                    for (var fz = (z * factor); (fz < ((z + 1) * factor)); fz++) {
                        for (var fy = (y * factor); (fy < ((y + 1) * factor)); fy++) {
                            for (var fx = (x * factor); (fx < ((x + 1) * factor)); fx++) {
                                AddCell(stride: 1, x: fx, y: fy, z: fz);
                            }
                        }
                    }
                }
            }
        }

        return [.. edges];
    }
    // A crossing edge's key: its start sample's fine key, its axis and whether it is a coarse cell long.
    private long CrossingKey(int start, int axis, int length) => (((((long)start) * 3L) + axis) * 2L) + (((length > 1) && (m_factor > 1)) ? 1L : 0L);
    private (int Start, int Axis, int Length) CrossingEdge(long key) => (((int)((key / 2L) / 3L)), ((int)((key / 2L) % 3L)), (((key & 1L) == 1L) ? m_factor : 1));
    // The refined zero along the edge `length` fine cells long from (x, y, z), in [0, 1]. Synthetic samples retain the
    // linear crossing of the lattice the contour must connect, rather than the underlying field it intentionally replaces.
    private float Crossing(int x, int y, int z, int axis, int length) {
        var fineX = (((m_samplesX - 1) * m_factor) + 1);
        var fineY = (((m_samplesY - 1) * m_factor) + 1);
        var start = (x + (fineX * (y + (fineY * z))));

        if (m_crossingRoots.TryGetValue(key: CrossingKey(start: start, axis: axis, length: length), value: out var root)) { return root.Position; }
        var va = FineValue(x: x, y: y, z: z);
        var vb = FineValue(x: (x + ((axis == 0) ? length : 0)), y: (y + ((axis == 1) ? length : 0)), z: (z + ((axis == 2) ? length : 0)));
        var denominator = (va - vb);

        return ((denominator == 0f) ? 0.5f : Math.Clamp(max: 1f, min: 0f, value: (va / denominator)));
    }
    /// <summary>Answers a run of the samples <see cref="GradientPoints"/> named with the field's value and gradient
    /// there.</summary>
    /// <param name="first">The first sample the run answers, in <see cref="GradientPoints"/>'s order.</param>
    /// <param name="values">The field's value at each sample: negative inside.</param>
    /// <param name="gradients">Three components per sample: the field's derivative, in field units per world unit. A
    /// field need not be a true distance (a scope, a flare or a non-uniform scale bounds it below the distance, a
    /// superellipsoid only approximates one), so the contour reads a value as a distance only through the gradient's
    /// magnitude.</param>
    /// <exception cref="ArgumentException">The run is not whole samples, its values and gradients name different samples,
    /// or it reaches past the samples.</exception>
    public void AnswerGradients(int first, ReadOnlySpan<float> values, ReadOnlySpan<float> gradients) {
        if (((gradients.Length % 3) != 0) || (values.Length != (gradients.Length / 3)) || (first < 0) || ((first + values.Length) > (m_gradientKeys.Length + m_crossingKeys.Length))) {
            throw new ArgumentException(message: "A gradient answer holds one value and three gradient components per sample it names.");
        }

        var factor = m_factor;
        var fineX = (((m_samplesX - 1) * factor) + 1);
        var fineY = (((m_samplesY - 1) * factor) + 1);

        for (var index = 0; (index < values.Length); index++) {
            var gradient = new Vector3(x: gradients[(3 * index)], y: gradients[((3 * index) + 1)], z: gradients[((3 * index) + 2)]);

            if ((first + index) >= m_gradientKeys.Length) {
                m_crossingGradients[m_crossingKeys[((first + index) - m_gradientKeys.Length)]] = new Vector4(value: gradient, w: values[index]);

                continue;
            }

            var key = m_gradientKeys[(first + index)];
            var fx = (key % fineX);
            var fy = ((key / fineX) % fineY);
            var fz = (key / (fineX * fineY));

            if (((fx % factor) == 0) && ((fy % factor) == 0) && ((fz % factor) == 0)) {
                m_gradients[Index(x: (fx / factor), y: (fy / factor), z: (fz / factor))] = gradient;
            } else {
                m_fineGradients[key] = gradient;
            }
        }
    }

    /// <summary>Extracts the lattice's surface: at the fine spacing in the cells <see cref="Refine"/> refined, and at
    /// the lattice's own everywhere else, with its feature vertices marked (<see cref="SdfContour.Features"/>). A piece
    /// of the surface apart from the rest, under two lattice cells across and a hundredth of the largest piece's quads,
    /// is dropped: the specks a sheet leaves where it thins below the fine spacing.</summary>
    /// <returns>The surface; empty when no lattice edge changes sign.</returns>
    public SdfContour Contour() {
        var surface = ((m_factor > 1)
            ? SdfDualContouring.ExtractRefined(grid: new Lattice(owner: this))
            : SdfDualContouring.Extract(grid: new Lattice(owner: this)));
        var materials = new int[surface.QuadCount];

        for (var quad = 0; (quad < materials.Length); quad++) {
            materials[quad] = MaterialAt(key: surface.QuadCorners[quad]);
        }

        float[] normals = [.. surface.Normals.Select(selector: static value => ((float)value))];
        float[] positions = [.. surface.Positions.Select(selector: static value => ((float)value))];
        var quads = surface.Quads;

        (quads, materials) = WithoutSpecks(materials: materials, positions: positions, quads: quads);

        var contour = new SdfContour(
            Normals: normals,
            Positions: positions,
            QuadMaterials: materials,
            Quads: quads
        ) {
            Features = surface.Features,
            Beside = surface.Beside,
            Solved = surface.Solved,
        };
        contour.SelectDiagonals();

        return contour;
    }

    internal static void SelectDiagonals(SdfContour contour) {
        var quads = contour.Quads;

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var at = (4 * quad);

            if ((quads[(at + 2)] != quads[(at + 3)]) && !SplitsAlongFirstDiagonal(features: contour.Features, normals: contour.Normals, positions: contour.Positions, quad: quads.AsSpan(start: at, length: 4))) {
                (quads[at], quads[(at + 1)], quads[(at + 2)], quads[(at + 3)]) = (quads[(at + 1)], quads[(at + 2)], quads[(at + 3)], quads[at]);
            }
        }
    }

    // Whether a quad of four vertex indices splits along its first diagonal, its first corner to its third, rather than
    // its second: the diagonal whose two triangles both face the way the corner normals point, where only one's do; else
    // the one whose triangles turn least from the normals of their corners that lie on no crease (Turn); else the
    // shorter. A quad around an edge beside a crease that steps across the lattice holds a crease vertex, a vertex of
    // each face and a fourth: split one way, a triangle joins the two faces' vertices across the crease and cuts its
    // corner; split the other, the crease vertex is a corner of both triangles and each lies on one face. Every quad of a
    // contour splits by this one rule.
    private static bool SplitsAlongFirstDiagonal(float[] positions, float[] normals, bool[] features, ReadOnlySpan<int> quad) {
        var (v0, v1, v2, v3) = (Vertex(values: positions, vertex: quad[0]), Vertex(values: positions, vertex: quad[1]), Vertex(values: positions, vertex: quad[2]), Vertex(values: positions, vertex: quad[3]));
        var (n0, n1, n2, n3) = (Vertex(values: normals, vertex: quad[0]), Vertex(values: normals, vertex: quad[1]), Vertex(values: normals, vertex: quad[2]), Vertex(values: normals, vertex: quad[3]));
        var outward = (
            X: (((((double)n0.X) + n1.X) + n2.X) + n3.X),
            Y: (((((double)n0.Y) + n1.Y) + n2.Y) + n3.Y),
            Z: (((((double)n0.Z) + n1.Z) + n2.Z) + n3.Z)
        );
        var alongFirst = (Faces(a: v0, b: v1, c: v2, outward: outward) && Faces(a: v0, b: v2, c: v3, outward: outward));
        var alongSecond = (Faces(a: v0, b: v1, c: v3, outward: outward) && Faces(a: v1, b: v2, c: v3, outward: outward));

        if (alongFirst != alongSecond) {
            return alongFirst;
        }

        var turnFirst = (Turn(a: quad[0], b: quad[1], c: quad[2], features: features, normals: normals, positions: positions) + Turn(a: quad[0], b: quad[2], c: quad[3], features: features, normals: normals, positions: positions));
        var turnSecond = (Turn(a: quad[0], b: quad[1], c: quad[3], features: features, normals: normals, positions: positions) + Turn(a: quad[1], b: quad[2], c: quad[3], features: features, normals: normals, positions: positions));

        return ((Math.Abs(value: (turnFirst - turnSecond)) > TurnTie)
            ? (turnFirst < turnSecond)
            : (DistanceSquared(a: v0, b: v2) <= DistanceSquared(a: v1, b: v3)));
    }
    // How far triangle (a, b, c), wound counter-clockwise, turns from the normals of its corners that lie on no crease:
    // one minus the cosine between its facing and each such corner's normal, summed. A crease vertex's normal blends
    // the faces meeting there and says nothing of which one a triangle lies on.
    private static double Turn(int a, int b, int c, float[] positions, float[] normals, bool[] features) {
        var (ax, ay, az) = (((double)positions[(3 * a)]), ((double)positions[((3 * a) + 1)]), ((double)positions[((3 * a) + 2)]));
        var (ux, uy, uz) = ((positions[(3 * b)] - ax), (positions[((3 * b) + 1)] - ay), (positions[((3 * b) + 2)] - az));
        var (vx, vy, vz) = ((positions[(3 * c)] - ax), (positions[((3 * c) + 1)] - ay), (positions[((3 * c) + 2)] - az));
        var (nx, ny, nz) = (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

        if (!(length > 0.0)) {
            return 0.0;
        }

        var turn = 0.0;

        foreach (var corner in (ReadOnlySpan<int>)[a, b, c]) {
            if ((corner < features.Length) && features[corner]) {
                continue;
            }

            turn += (1.0 - ((((nx * normals[(3 * corner)]) + (ny * normals[((3 * corner) + 1)])) + (nz * normals[((3 * corner) + 2)])) / length));
        }

        return turn;
    }
    private static Vector3 Vertex(float[] values, int vertex) => new(x: values[(3 * vertex)], y: values[((3 * vertex) + 1)], z: values[((3 * vertex) + 2)]);
    // Whether triangle (a, b, c), wound counter-clockwise, faces the way `outward` points: (b - a) x (c - a) . outward.
    private static bool Faces(Vector3 a, Vector3 b, Vector3 c, (double X, double Y, double Z) outward) {
        var ux = (((double)b.X) - a.X);
        var uy = (((double)b.Y) - a.Y);
        var uz = (((double)b.Z) - a.Z);
        var vx = (((double)c.X) - a.X);
        var vy = (((double)c.Y) - a.Y);
        var vz = (((double)c.Z) - a.Z);
        var nx = ((uy * vz) - (uz * vy));
        var ny = ((uz * vx) - (ux * vz));
        var nz = ((ux * vy) - (uy * vx));

        return ((((nx * outward.X) + (ny * outward.Y)) + (nz * outward.Z)) >= 0.0);
    }
    private static double DistanceSquared(Vector3 a, Vector3 b) {
        var dx = (((double)b.X) - a.X);
        var dy = (((double)b.Y) - a.Y);
        var dz = (((double)b.Z) - a.Z);

        return (((dx * dx) + (dy * dy)) + (dz * dz));
    }

    // Drops the specks a sheet thinner than the finest cell leaves where it thins to a knife edge (the top of a shell cut
    // where its inside meets its outside): pieces of the surface apart from the rest, each smaller than two lattice cells
    // across and holding under a hundredth of the largest piece's quads. A whole surface that small, a part's own, stays.
    private (int[] Quads, int[] Materials) WithoutSpecks(int[] quads, int[] materials, float[] positions) {
        var quadCount = materials.Length;
        var vertexCount = (positions.Length / 3);
        var pieceSets = new SdfDisjointSet(count: vertexCount);

        int Root(int vertex) => pieceSets.Find(element: vertex);

        for (var quad = 0; (quad < quadCount); quad++) {
            for (var corner = 1; (corner < 4); corner++) {
                pieceSets.Union(first: quads[(4 * quad)], second: quads[((4 * quad) + corner)]);
            }
        }

        // Each piece's quads and bounds.
        var pieces = new Dictionary<int, (int Quads, Vector3 Low, Vector3 High)>();

        for (var quad = 0; (quad < quadCount); quad++) {
            var root = Root(vertex: quads[(4 * quad)]);
            (int Quads, Vector3 Low, Vector3 High) piece = (pieces.TryGetValue(key: root, value: out var found) ? found : (0, new Vector3(value: float.PositiveInfinity), new Vector3(value: float.NegativeInfinity)));

            for (var corner = 0; (corner < 4); corner++) {
                var vertex = quads[((4 * quad) + corner)];
                var point = new Vector3(x: positions[(3 * vertex)], y: positions[((3 * vertex) + 1)], z: positions[((3 * vertex) + 2)]);

                piece = (piece.Quads, Vector3.Min(value1: piece.Low, value2: point), Vector3.Max(value1: piece.High, value2: point));
            }
            pieces[root] = ((piece.Quads + 1), piece.Low, piece.High);
        }
        if (pieces.Count < 2) {
            return (quads, materials);
        }

        var largest = pieces.Values.Max(selector: static piece => piece.Quads);
        var reach = (2f * m_cell.Length());
        var specks = new HashSet<int>();

        foreach (var (root, piece) in pieces) {
            if (((100 * piece.Quads) < largest) && ((piece.High - piece.Low).Length() < reach)) { _ = specks.Add(item: root); }
        }
        if (specks.Count == 0) {
            return (quads, materials);
        }

        var keptQuads = new List<int>(capacity: quads.Length);
        var keptMaterials = new List<int>(capacity: quadCount);

        for (var quad = 0; (quad < quadCount); quad++) {
            if (specks.Contains(item: Root(vertex: quads[(4 * quad)]))) { continue; }
            for (var corner = 0; (corner < 4); corner++) {
                keptQuads.Add(item: quads[((4 * quad) + corner)]);
            }
            keptMaterials.Add(item: materials[quad]);
        }

        return ([.. keptQuads], [.. keptMaterials]);
    }

    private int Index(int x, int y, int z) => (x + (m_samplesX * (y + (m_samplesY * z))));
    private (int X, int Y, int Z) FineCoordinates(int key) {
        var fineX = (((m_samplesX - 1) * m_factor) + 1);
        var fineY = (((m_samplesY - 1) * m_factor) + 1);

        return ((key % fineX), ((key / fineX) % fineY), (key / (fineX * fineY)));
    }
    // The coarse cell a fine sample interpolates in and its position there, each in [0, 1].
    private (int X, int Y, int Z, float U, float V, float W) Interpolant(int x, int y, int z) {
        var factor = m_factor;
        var cx = Math.Min(val1: (x / factor), val2: (m_samplesX - 2));
        var cy = Math.Min(val1: (y / factor), val2: (m_samplesY - 2));
        var cz = Math.Min(val1: (z / factor), val2: (m_samplesZ - 2));

        return (cx, cy, cz, (((float)(x - (cx * factor))) / factor), (((float)(y - (cy * factor))) / factor), (((float)(z - (cz * factor))) / factor));
    }
    // A fine sample's value: the coarse sample's at a coarse position, the measured one in a measured cell, and otherwise
    // the trilinear interpolation of its coarse cell's corners.
    private float FineValue(int x, int y, int z) {
        var factor = m_factor;

        if (((x % factor) == 0) && ((y % factor) == 0) && ((z % factor) == 0)) {
            return m_values[Index(x: (x / factor), y: (y / factor), z: (z / factor))];
        }

        var fineX = (((m_samplesX - 1) * factor) + 1);
        var fineY = (((m_samplesY - 1) * factor) + 1);

        if (m_fineSlots.TryGetValue(key: (x + (fineX * (y + (fineY * z)))), value: out var slot)) {
            return (m_closed.TryGetValue(key: (x + (fineX * (y + (fineY * z)))), value: out var closed) ? closed : m_fineValues[slot]);
        }

        var (cx, cy, cz, u, v, w) = Interpolant(x: x, y: y, z: z);
        var sum = 0f;

        for (var corner = 0; (corner < 8); corner++) {
            var dx = (corner & 1);
            var dy = ((corner >> 1) & 1);
            var dz = ((corner >> 2) & 1);
            var weight = ((((dx == 1) ? u : (1f - u)) * ((dy == 1) ? v : (1f - v))) * ((dz == 1) ? w : (1f - w)));

            sum += (weight * m_values[Index(x: (cx + dx), y: (cy + dy), z: (cz + dz))]);
        }

        return sum;
    }
    // A fine sample's gradient: the one measured there, or else the trilinear interpolation of its coarse cell's corners'.
    private Vector3 FineGradient(int x, int y, int z) {
        var factor = m_factor;

        if (((x % factor) == 0) && ((y % factor) == 0) && ((z % factor) == 0)) {
            return m_gradients[Index(x: (x / factor), y: (y / factor), z: (z / factor))];
        }

        var fineX = (((m_samplesX - 1) * factor) + 1);
        var fineY = (((m_samplesY - 1) * factor) + 1);

        if (m_fineGradients.TryGetValue(key: (x + (fineX * (y + (fineY * z)))), value: out var measured)) {
            return measured;
        }

        var (cx, cy, cz, u, v, w) = Interpolant(x: x, y: y, z: z);
        var sum = Vector3.Zero;

        for (var corner = 0; (corner < 8); corner++) {
            var dx = (corner & 1);
            var dy = ((corner >> 1) & 1);
            var dz = ((corner >> 2) & 1);
            var weight = ((((dx == 1) ? u : (1f - u)) * ((dy == 1) ? v : (1f - v))) * ((dz == 1) ? w : (1f - w)));

            sum += (m_gradients[Index(x: (cx + dx), y: (cy + dy), z: (cz + dz))] * weight);
        }

        return sum;
    }
    // A fine sample's material: the sample's own where it was measured, else its nearest coarse sample's.
    private int MaterialAt(int key) {
        var factor = m_factor;

        if (factor == 1) {
            return m_materials[key];
        }
        if (m_fineSlots.TryGetValue(key: key, value: out var slot)) {
            return m_fineMaterials[slot];
        }

        var (fx, fy, fz) = FineCoordinates(key: key);

        return m_materials[Index(
            x: (((fx * 2) + factor) / (2 * factor)),
            y: (((fy * 2) + factor) / (2 * factor)),
            z: (((fz * 2) + factor) / (2 * factor))
        )];
    }

    // The lattice as the contouring reads it, at the fine spacing once refined: corner values in world units of field,
    // cell coordinates scaled per axis, and a normal trilinearly interpolated from the gradients of the eight samples
    // around a point.
    private readonly struct Lattice(SdfSampledLattice owner) : ISdfRefinedContourLattice {
        public double Crossing(int x, int y, int z, int axis, int length) => owner.Crossing(x: x, y: y, z: z, axis: axis, length: length);
        public int CellsX => ((owner.m_samplesX - 1) * owner.m_factor);
        public int CellsY => ((owner.m_samplesY - 1) * owner.m_factor);
        public int CellsZ => ((owner.m_samplesZ - 1) * owner.m_factor);
        public double CellSize {
            get {
                var cell = owner.ContourCell;

                return Math.Max(val1: cell.X, val2: Math.Max(val1: cell.Y, val2: cell.Z));
            }
        }
        public (double X, double Y, double Z)? CellExtent {
            get {
                var cell = owner.ContourCell;

                return (cell.X, cell.Y, cell.Z);
            }
        }
        public int Refinement => owner.m_factor;

        public bool Refined(int x, int y, int z) => owner.m_refined[(x + ((owner.m_samplesX - 1) * (y + ((owner.m_samplesY - 1) * z))))];
        public bool Measured(int x, int y, int z) => owner.m_measured[(x + ((owner.m_samplesX - 1) * (y + ((owner.m_samplesY - 1) * z))))];
        public bool CoarseInside(int x, int y, int z) => (owner.m_values[owner.Index(x: x, y: y, z: z)] < 0f);
        public int Corner(int x, int y, int z) => (x + ((CellsX + 1) * (y + ((CellsY + 1) * z))));
        public bool Inside(int corner) {
            var (x, y, z) = owner.FineCoordinates(key: corner);

            return (owner.FineValue(x: x, y: y, z: z) < 0f);
        }
        public double Value(int corner, int x, int y, int z) => owner.FineValue(x: x, y: y, z: z);
        public (double X, double Y, double Z) World(double x, double y, double z) {
            var cell = owner.ContourCell;

            return (
                (owner.m_origin.X + (x * cell.X)),
                (owner.m_origin.Y + (y * cell.Y)),
                (owner.m_origin.Z + (z * cell.Z))
            );
        }
        public double FeatureReach => 0.5;
        public bool CrossingNormals => true;
        // A crossing's own measured gradient and value, or the gradient its edge's samples interpolate, on the surface.
        public bool TryCrossingNormal(int x, int y, int z, int axis, int length, double wx, double wy, double wz, out double nx, out double ny, out double nz, out double offset) {
            var start = (x + ((CellsX + 1) * (y + ((CellsY + 1) * z))));
            var end = (start + ((axis == 0) ? length : ((axis == 1) ? (length * (CellsX + 1)) : (length * (CellsX + 1) * (CellsY + 1)))));
            // A crossing beside a closed sample lies where the samples put it, a cell's thickness from the sheet's other
            // face (CloseSheets); moved onto the sheet it would cross it.
            var closed = ((owner.m_closed.Count > 0) && (owner.m_closed.ContainsKey(key: start) || owner.m_closed.ContainsKey(key: end)));

            if (owner.m_crossingGradients.TryGetValue(key: owner.CrossingKey(axis: axis, length: length, start: start), value: out var measured)) {
                var gradient = new Vector3(x: measured.X, y: measured.Y, z: measured.Z);
                var magnitude = gradient.Length();

                if (magnitude > 1.0e-12f) {
                    var (ex, ey, ez) = (CellExtent ?? (CellSize, CellSize, CellSize));
                    var reach = (length * Math.Max(val1: ex, val2: Math.Max(val1: ey, val2: ez)));

                    gradient /= magnitude;
                    (nx, ny, nz) = (gradient.X, gradient.Y, gradient.Z);
                    // A measured root stays on its edge. Only an interpolated transition sample keeps the previous
                    // first-order plane correction; a closed sheet retains its deliberately displaced crossing.
                    offset = ((closed || owner.m_crossingRoots.ContainsKey(key: owner.CrossingKey(axis: axis, length: length, start: start)))
                        ? 0.0
                        : Math.Clamp(value: (measured.W / magnitude), min: -reach, max: reach));

                    return true;
                }
            }

            offset = 0.0;

            return TryNormal(nx: out nx, ny: out ny, nz: out nz, x: wx, y: wy, z: wz);
        }
        public bool TryNormal(double x, double y, double z, out double nx, out double ny, out double nz) {
            var cell = owner.ContourCell;
            var cellsX = CellsX;
            var cellsY = CellsY;
            var cellsZ = CellsZ;
            var lx = Math.Clamp(value: ((x - owner.m_origin.X) / cell.X), min: 0.0, max: cellsX);
            var ly = Math.Clamp(value: ((y - owner.m_origin.Y) / cell.Y), min: 0.0, max: cellsY);
            var lz = Math.Clamp(value: ((z - owner.m_origin.Z) / cell.Z), min: 0.0, max: cellsZ);
            var x0 = Math.Min(val1: ((int)lx), val2: (cellsX - 1));
            var y0 = Math.Min(val1: ((int)ly), val2: (cellsY - 1));
            var z0 = Math.Min(val1: ((int)lz), val2: (cellsZ - 1));
            var fx = (float)(lx - x0);
            var fy = (float)(ly - y0);
            var fz = (float)(lz - z0);
            var sum = Vector3.Zero;

            for (var corner = 0; (corner < 8); corner++) {
                var dx = (corner & 1);
                var dy = ((corner >> 1) & 1);
                var dz = ((corner >> 2) & 1);
                var weight = (((dx == 1) ? fx : (1f - fx)) * ((dy == 1) ? fy : (1f - fy))) * ((dz == 1) ? fz : (1f - fz));

                if (weight != 0f) {
                    sum += (owner.FineGradient(x: (x0 + dx), y: (y0 + dy), z: (z0 + dz)) * weight);
                }
            }

            var length = sum.Length();

            if (!(length > 1.0e-12f)) {
                (nx, ny, nz) = (0.0, 0.0, 0.0);

                return false;
            }

            sum /= length;
            (nx, ny, nz) = (sum.X, sum.Y, sum.Z);

            return true;
        }
    }
}
