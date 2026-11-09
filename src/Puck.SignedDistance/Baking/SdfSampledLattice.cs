using Puck.Abstractions.Memory;
using Puck.Maths;
using System.Buffers;
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
            for (var first = starts[vertex]; ((first < starts[(vertex + 1)]) && !creased[vertex]); first++) {
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
public sealed unsafe class SdfSampledLattice : IDisposable {
    // A refinement that would measure more fine samples than this is not taken: the lattice contours at its own spacing.
    /// <summary>The maximum measured fine samples of one lattice; also bounds its admission workspace.</summary>
    public const int MaxFineSamples = 4_000_000;

    // The share of a step across a line, at the line's own slope, the field may change by toward either neighbour before
    // a pair on the line lies beside a solid rather than either side of a sheet (SheetsBetween): a sheet tilted 45
    // degrees from the line changes the field by 0.71 of a step across it and stays a sheet; a line over a plate's top
    // falls by the whole step toward it, and a line a step out from a board's face rises by the step away from it.
    private const float AcrossChange = 0.75f;
    // How much less one diagonal's triangles must turn from their corners' normals than the other's for a quad to split
    // along it (SplitsAlongFirstDiagonal): below it, the two splits of a quad flat to within its normals' noise tie.
    private const double TurnTie = 1.0e-3;
    // A triangle whose height is under this share of its longest edge is a line to a split (SplitsAlongFirstDiagonal).
    private const double DegenerateSplit = 0.02;

    private readonly Vector3 m_origin;
    private readonly Vector3 m_cell;
    private readonly int m_samplesX;
    private readonly int m_samplesY;
    private readonly int m_samplesZ;
    // The caller's values and materials, pinned for the lattice's life, so the contour's per-corner reads index a pointer
    // rather than asking a memory manager for its span at every element.
    private readonly MemoryHandle m_valueHandle;
    private readonly MemoryHandle m_materialHandle;
    private readonly float* m_values;
    private readonly int* m_materials;
    private readonly int m_count;

    private ReadOnlySpan<float> Values => new(pointer: m_values, length: m_count);
    private ReadOnlySpan<int> Materials => new(pointer: m_materials, length: m_count);

    private readonly OwnedBuffer<Vector3> m_gradients;

    private int m_factor = 1;
    // Division by the fine lattice's row length, its rows per slab and m_factor (FineCoordinates, FineCell), set with
    // m_factor: the fine-sample scans divide every key they read.
    private ReciprocalDivisor32 m_byFineRow;
    private ReciprocalDivisor32 m_byFineColumn;
    private ReciprocalDivisor32 m_byFactor;
    private int m_fineRowLength;
    private int m_fineColumnLength;

    private readonly OwnedBuffer<bool> m_measured;
    // Per coarse cell, whether a sheet or gap thinner than a cell crosses an edge of it (Refine), the cells CloseSheets
    // closes sheets in.
    private readonly OwnedBuffer<bool> m_thin;
    private readonly OwnedBuffer<bool> m_refined;
    // Per coarse cell, the block of fine slots a measured cell holds (-1 for a cell not measured), and per block the slot
    // in m_fineValues of each of its (m_factor + 1)^3 fine samples, faces included (-1 at its coarse corners): FineSlot's
    // dense index of the measured fine samples.
    private readonly OwnedBuffer<int> m_cellBlocks;
    private readonly OwnedBuffer<int> m_blockSlots;
    private readonly OwnedBuffer<int> m_fineKeys;
    private readonly OwnedBuffer<float> m_fineValues;
    private readonly OwnedBuffer<int> m_fineMaterials;
    private readonly Dictionary<int, Vector3> m_fineGradients = [];
    private readonly OwnedBuffer<int> m_gradientKeys;
    private readonly OwnedBuffer<long> m_crossingKeys;
    // The fine key of the low corner of each crossed fine cell of a refined cell, in the order CrossingEdges reads the
    // cells, which GradientPoints reads again rather than measuring every fine cell's corners a second time.
    private readonly OwnedBuffer<SdfCrossedCell> m_crossedFineCells;
    // The round of points Refine, CrossingPoints or GradientPoints last returned, with weight one, in one owned block the
    // next of them rewrites: a round's answers all land before the caller asks for the next.
    private readonly OwnedBuffer<Vector4> m_points;

    private bool m_crossingsPrepared;
    private bool m_rootsPrepared;

    // Each searched crossing edge's root, in m_crossingKeys' order, and each such edge's index there by its CrossingKey.
    private CrossingRoot[] m_crossingRoots = [];
    private readonly Dictionary<long, int> m_crossingRootIndex = [];
    private readonly OwnedBuffer<long> m_pendingCrossings;

    // The indices in m_crossingRoots of m_pendingCrossings' edges, in its order.
    private int[] m_pendingRoots = [];

    // Per crossing edge (CrossingKey), the gradient measured at its crossing and the field's value there.
    private readonly Dictionary<long, Vector4> m_crossingGradients = [];
    // The measured fine samples a sheet thinner than the fine cell passed beside, and the value each reads (CloseSheets).
    // Per measured fine sample's slot, NaN for one CloseSheets left open; and how many it closed.
    private readonly OwnedBuffer<float> m_closedValues;

    private int m_closedCount;

    // Per fine key, whether FineInside has read the sample's sign, and that sign: one bit each.
    private readonly OwnedBuffer<ulong> m_signKnown;
    private readonly OwnedBuffer<ulong> m_signInside;

    /// <summary>Initializes a new instance of the <see cref="SdfSampledLattice"/> class.</summary>
    /// <param name="origin">The position of sample (0, 0, 0), in the frame the samples were taken in.</param>
    /// <param name="cell">The spacing between samples along each axis, in world units; each positive.</param>
    /// <param name="samplesX">The samples along X, at least two.</param>
    /// <param name="samplesY">The samples along Y, at least two.</param>
    /// <param name="samplesZ">The samples along Z, at least two.</param>
    /// <param name="values">One field value per sample, X fastest: negative inside. The lattice keeps the array.</param>
    /// <param name="materials">One material per sample. The lattice keeps the array.</param>
    /// <param name="allocator">Native workspace allocator, or null for offline managed storage.</param>
    /// <param name="observer">The owning job's byte account, or null for unmetered offline work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An axis has fewer than two samples, a spacing is not positive, or an array's
    /// length does not match the lattice.</exception>
    public SdfSampledLattice(Vector3 origin, Vector3 cell, int samplesX, int samplesY, int samplesZ, ReadOnlyMemory<float> values, ReadOnlyMemory<int> materials, IAllocator? allocator = null, IAllocationObserver? observer = null) {



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
        m_count = count;
        Divide(factor: 1);
        m_valueHandle = values.Pin();
        m_materialHandle = materials.Pin();
        m_values = ((float*)m_valueHandle.Pointer);
        m_materials = ((int*)m_materialHandle.Pointer);
        m_gradients = new OwnedBuffer<Vector3>(allocator: allocator, observer: observer);
        m_closedValues = new OwnedBuffer<float>(allocator: allocator, observer: observer);
        m_signKnown = new OwnedBuffer<ulong>(allocator: allocator, observer: observer);
        m_cellBlocks = new OwnedBuffer<int>(allocator: allocator, observer: observer);
        m_measured = new(allocator: allocator, observer: observer);
        m_thin = new(allocator: allocator, observer: observer);
        m_refined = new(allocator: allocator, observer: observer);
        m_fineKeys = new(allocator: allocator, observer: observer);
        m_gradientKeys = new(allocator: allocator, observer: observer);
        m_points = new(allocator: allocator, observer: observer);
        m_crossingKeys = new(allocator: allocator, observer: observer);
        m_crossedFineCells = new(allocator: allocator, observer: observer);
        m_pendingCrossings = new(allocator: allocator, observer: observer);
        m_fineValues = new OwnedBuffer<float>(allocator: allocator, observer: observer);
        m_blockSlots = new OwnedBuffer<int>(allocator: allocator, observer: observer);
        m_signInside = new OwnedBuffer<ulong>(allocator: allocator, observer: observer);
        m_fineMaterials = new OwnedBuffer<int>(allocator: allocator, observer: observer);
        m_gradients.Resize(count);
    }

    /// <summary>Releases the lattice's owned workspaces after its contour and point borrows end.</summary>
    public void Dispose() {
        m_valueHandle.Dispose(); m_materialHandle.Dispose();
        m_measured.Dispose(); m_thin.Dispose(); m_refined.Dispose(); m_fineKeys.Dispose();
        m_gradientKeys.Dispose(); m_points.Dispose(); m_crossingKeys.Dispose(); m_crossedFineCells.Dispose(); m_pendingCrossings.Dispose();
        m_gradients.Dispose();
        m_closedValues.Dispose();
        m_signKnown.Dispose();
        m_cellBlocks.Dispose();
        m_fineValues.Dispose();
        m_blockSlots.Dispose();
        m_signInside.Dispose();
        m_fineMaterials.Dispose();
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
    /// the samples put it: the parts of a union whose own detail is finer than the lattice. Empty for none. Where every
    /// region's cells would measure more fine samples than the refinement's budget, the regions covering the fewest cells
    /// are refined and the rest contour at the lattice's own spacing.</param>
    /// <returns>The fine samples to measure, in the lattice's frame, with weight one, in the order
    /// <see cref="AnswerRefinement"/> takes their answers; empty when no cell needs refining or even two fine cells a
    /// side would measure too many, and the lattice then contours at its own spacing. The memory is the lattice's, valid
    /// until the next round of points is asked for or the lattice is disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is less than two.</exception>
    /// <exception cref="InvalidOperationException">The lattice is already refined.</exception>
    public ReadOnlyMemory<Vector4> Refine(int factor, ReadOnlySpan<SdfLatticeRegion> regions = default) {
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
                        run = (((Values[Index(x: p[0], y: p[1], z: p[2])] < 0f) == inside) ? (run + 1) : 0);
                        if (run >= 2) {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        // Whether one of the four lattice lines beside sample `at`'s line along `axis` crosses the same sheet or gap
        // (whose samples are inside when `sheetInside`) thinly from `first` to `last` along it: a run of one sheet
        // sample between two of the other sign, or two samples of the other sign the field falls toward from both sides
        // and passes between (PassesBetween). A sheet or gap thinner than a step spans the lines beside the one that
        // caught it; a convex corner a line crosses near its tip is seen by that line alone, the lines beside it either
        // missing the solid or crossing its body, and refining its cells merges their fine vertices into collinear
        // slivers along the crease that the coarse contour's tangent planes keep sharp.
        bool ThinBeside(Span<int> at, int axis, bool sheetInside, int first, int last) {
            Span<int> p = stackalloc int[3];
            var h = spacing[axis];
            var n = samples[axis];

            for (var offset = 1; (offset < 3); offset++) {
                var across = ((axis + offset) % 3);

                if (!ThinAcross(across: across, at: at, p: p)) {
                    return false;
                }
            }

            return true;

            // Whether a line beside this one along `across`, on either side, crosses the sheet thinly: a sheet spans both
            // directions across the line that caught it, where a solid's edge spans one, its own.
            bool ThinAcross(Span<int> at, Span<int> p, int across) {
                for (var direction = -1; (direction <= 1); direction += 2) {
                    at.CopyTo(destination: p);
                    p[across] += direction;
                    if ((p[across] < 0) || (p[across] >= samples[across])) {
                        continue;
                    }

                    float Beside(Span<int> q, int i) {
                        q[axis] = i;

                        return Values[Index(x: q[0], y: q[1], z: q[2])];
                    }

                    for (var i = Math.Max(val1: 1, val2: first); (i <= Math.Min(val1: last, val2: (n - 2))); i++) {
                        var before = Beside(i: (i - 1), q: p);
                        var value = Beside(i: i, q: p);
                        var after = Beside(i: (i + 1), q: p);

                        if (((value < 0f) == sheetInside) && ((before < 0f) != sheetInside) && ((after < 0f) != sheetInside)) {
                            if (RunThickness(after: after, before: before, value: value) < 1f) {
                                return true;
                            }
                        }
                        if (((i + 2) < n) && ((before < 0f) != sheetInside) && ((value < 0f) != sheetInside) && ((after < 0f) != sheetInside)) {
                            var further = Beside(i: (i + 2), q: p);

                            if ((further < 0f) != sheetInside) {
                                p[axis] = i;

                                var slope = Slope(x: p[0], y: p[1], z: p[2]);

                                p[axis] = (i + 1);
                                slope = MathF.Max(x: slope, y: Slope(x: p[0], y: p[1], z: p[2]));
                                if (PassesBetween(after: MathF.Abs(x: after), before: MathF.Abs(x: before), further: MathF.Abs(x: further), reach: (h * slope), value: MathF.Abs(x: value))) {
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }
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
                        if (Values[Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))] < 0f) {
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

        // Each region's cells the surface can enter: a corner lies within half the cell's diagonal of it.
        var regionCells = new List<int>[regions.Length];

        if (!regions.IsEmpty) {
            var reach = (0.5f * m_cell.Length());

            for (var r = 0; (r < regions.Length); r++) {
                var region = regions[r];
                var cellsOf = new List<int>();
                var from = ((region.Low - m_origin) / m_cell);
                var to = ((region.High - m_origin) / m_cell);

                var (x0, x1) = (Math.Max(val1: 0, val2: (((int)MathF.Floor(x: from.X)) - 1)), Math.Min(val1: (cellsX - 1), val2: (((int)MathF.Floor(x: to.X)) + 1)));
                var (y0, y1) = (Math.Max(val1: 0, val2: (((int)MathF.Floor(x: from.Y)) - 1)), Math.Min(val1: (cellsY - 1), val2: (((int)MathF.Floor(x: to.Y)) + 1)));
                var (z0, z1) = (Math.Max(val1: 0, val2: (((int)MathF.Floor(x: from.Z)) - 1)), Math.Min(val1: (cellsZ - 1), val2: (((int)MathF.Floor(x: to.Z)) + 1)));

                for (var z = z0; (z <= z1); z++) {
                    for (var y = y0; (y <= y1); y++) {
                        for (var x = x0; (x <= x1); x++) {
                            var low = (m_origin + (new Vector3(x: x, y: y, z: z) * m_cell));
                            var high = (low + m_cell);

                            if (!((low.X <= region.High.X) && (high.X >= region.Low.X) &&
                                (low.Y <= region.High.Y) && (high.Y >= region.Low.Y) &&
                                (low.Z <= region.High.Z) && (high.Z >= region.Low.Z))) {
                                continue;
                            }

                            var nearest = float.PositiveInfinity;

                            for (var offset = 0; (offset < 8); offset++) {
                                nearest = MathF.Min(x: nearest, y: MathF.Abs(x: Values[Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))]));
                            }
                            if (nearest <= reach) {
                                cellsOf.Add(item: CellIndex(x: x, y: y, z: z));
                            }
                        }
                    }
                }
                regionCells[r] = cellsOf;
                any |= (cellsOf.Count > 0);
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

                        return Values[Index(x: p[0], y: p[1], z: p[2])];
                    }

                    for (var i = 1; ((i + 1) < n); i++) {
                        var before = ValueAt(i: (i - 1));
                        var value = ValueAt(i: i);
                        var after = ValueAt(i: (i + 1));
                        var inside = (value < 0f);

                        // A run of one sample between two of the other sign: a sheet or gap the lattice caught once. A
                        // solid's edge or corner the line crosses near its tip (an acute rim's, a tilted cut's) is as thin
                        // along the line, but thickens beside it (Thickens) or is seen by this line alone (ThinBeside),
                        // and the contour's tangent planes keep it sharp at the lattice's own spacing.
                        if (((before < 0f) != inside) && ((after < 0f) != inside)) {
                            var thickness = RunThickness(after: after, before: before, value: value);

                            at[axis] = i;

                            if ((thickness < 1f) && !Thickens(at: at, axis: axis, first: (i - 1), inside: inside, last: (i + 1)) && ThinBeside(at: at, axis: axis, first: (i - 1), last: (i + 1), sheetInside: inside)) {
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
                        slope = MathF.Max(x: slope, y: Slope(x: at[0], y: at[1], z: at[2]));
                        at[axis] = i;

                        // A solid's edge or corner the line grazes falls toward the pair from both sides as a sheet does,
                        // but the lines beside the pair cross its body a step thick or more (Thickens) or miss it
                        // (ThinBeside). Between two inside samples the falls must meet below the surface by half a fine
                        // cell, a gap the fine samples can open: where two solids touch (a stair's treads, a board on its
                        // table) the falls meet at the surface, and a fillet's notch at the seam's mouth only just below
                        // it. Neither is a gap the fine lattice draws, and measuring its cells leaves the ring around
                        // them, contoured from interpolated corners, to cut the crease above the seam.
                        var tolerance = (inside ? (-0.5f / factor) : 0.1f);

                        if (!Thickens(at: at, axis: axis, first: (i - 1), inside: !inside, last: (i + 2)) && PassesBetween(after: MathF.Abs(x: after), before: MathF.Abs(x: before), further: MathF.Abs(x: further), reach: (h * slope), tolerance: tolerance, value: MathF.Abs(x: value)) && ThinBeside(at: at, axis: axis, first: (i - 1), last: (i + 2), sheetInside: !inside)) {
                            MarkEdge(at: at, axis: axis);
                        }
                    }
                }
            }
        }

        if (!any) {
            return ReadOnlyMemory<Vector4>.Empty;
        }

        // The split and the regions the fine-sample budget serves. The cells the samples cannot resolve (a sheet, a gap, two
        // patches) are measured first, then each region's cells, the region the fewest cells cover first, a region whose
        // cells would carry the count past MaxFineSamples left to the coarse contour: a member whose region spans a whole
        // union (a slab's thin finish) costs as much as every other member together, and dropping all the regions for it
        // left the members that need refining, a strip under the slab, at the coarse cell. The finest split serving the
        // most regions is taken, splitting each cell into fewer fine cells rather than none: a sheet thinner than the
        // coarse cell is caught by any refinement and lost by the coarse contour.
        var essential = measured;
        var order = new List<int>();

        for (var r = 0; (r < regionCells.Length); r++) {
            if (regionCells[r] is { Count: > 0 }) { order.Add(item: r); }
        }
        order.Sort(comparison: (a, b) => ((regionCells[a].Count != regionCells[b].Count) ? regionCells[a].Count.CompareTo(value: regionCells[b].Count) : a.CompareTo(value: b)));

        bool[]? chosen = null;
        var chosenSplit = 0;
        var chosenServed = -1;
        // The cells whose fine samples the count holds, the samples each added that no cell counted before it held, and
        // their sum: the distinct fine samples of the counted cells.
        var countedCells = new bool[essential.Length];
        var cellSamples = new int[essential.Length];
        var counted = 0L;
        var addedCells = new List<int>();

        for (var split = factor; (split >= 2); split--) {
            var mask = ((bool[])essential.Clone());
            var fits = true;

            Array.Clear(array: countedCells);
            counted = 0L;
            for (var cell = 0; (fits && (cell < mask.Length)); cell++) {
                if (mask[cell]) { fits = Count(cell: cell, split: split); }
            }
            if (!fits) { continue; }

            var served = 0;

            foreach (var r in order) {
                addedCells.Clear();

                var within = true;

                foreach (var cell in regionCells[r]) {
                    if (mask[cell]) { continue; }
                    mask[cell] = true;
                    addedCells.Add(item: cell);
                    if (!Count(cell: cell, split: split)) {
                        within = false;
                        break;
                    }
                }
                if (within) {
                    served++;
                    continue;
                }
                foreach (var cell in addedCells) {
                    mask[cell] = false;
                    if (!countedCells[cell]) { continue; }
                    countedCells[cell] = false;
                    counted -= cellSamples[cell];
                }
            }
            if (served > chosenServed) {
                (chosen, chosenSplit, chosenServed) = (mask, split, served);
            }
            if (served == order.Count) { break; }
        }

        // Counts a cell's fine samples that are not coarse samples and that no counted cell holds (one sharing the face,
        // edge or corner they lie on); false once the count passes MaxFineSamples.
        bool Count(int cell, int split) {
            var x = (cell % cellsX);
            var y = ((cell / cellsX) % cellsY);
            var z = (cell / (cellsX * cellsY));
            var fresh = 0;

            // Ownership depends only on which cell faces a sample touches. Count each of the 27 boundary/interior
            // classes once; an interior axis represents exactly split - 1 samples with the same holders.
            for (var iz = 0; (iz < 3); iz++) {
                for (var iy = 0; (iy < 3); iy++) {
                    for (var ix = 0; (ix < 3); ix++) {
                        var faceX = (ix != 1);
                        var faceY = (iy != 1);
                        var faceZ = (iz != 1);
                        var lx = ((ix == 2) ? split : ix);
                        var ly = ((iy == 2) ? split : iy);
                        var lz = ((iz == 2) ? split : iz);

                        if (faceX && faceY && faceZ) { continue; }
                        if ((faceX || faceY || faceZ) && HeldByCounted(lx: lx, ly: ly, lz: lz, split: split, x: x, y: y, z: z)) { continue; }
                        fresh += (((faceX ? 1 : (split - 1)) * (faceY ? 1 : (split - 1))) * (faceZ ? 1 : (split - 1)));
                    }
                }
            }
            countedCells[cell] = true;
            cellSamples[cell] = fresh;
            counted += fresh;

            return (counted <= MaxFineSamples);
        }
        // Whether a counted cell besides cell (x, y, z) holds its fine sample (lx, ly, lz): a neighbour across each face the
        // sample lies on.
        bool HeldByCounted(int x, int y, int z, int lx, int ly, int lz, int split) {
            var (lowX, highX) = ((((lx == 0) && (x > 0)) ? -1 : 0), (((lx == split) && ((x + 1) < cellsX)) ? 1 : 0));
            var (lowY, highY) = ((((ly == 0) && (y > 0)) ? -1 : 0), (((ly == split) && ((y + 1) < cellsY)) ? 1 : 0));
            var (lowZ, highZ) = ((((lz == 0) && (z > 0)) ? -1 : 0), (((lz == split) && ((z + 1) < cellsZ)) ? 1 : 0));

            for (var dz = lowZ; (dz <= highZ); dz++) {
                for (var dy = lowY; (dy <= highY); dy++) {
                    for (var dx = lowX; (dx <= highX); dx++) {
                        if (((dx | dy | dz) != 0) && countedCells[CellIndex(x: (x + dx), y: (y + dy), z: (z + dz))]) { return true; }
                    }
                }
            }

            return false;
        }

        if (chosen is null) {
            return ReadOnlyMemory<Vector4>.Empty;
        }
        measured = chosen;
        factor = chosenSplit;

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

        FineKeys(split: factor);

        // Numbers the fine samples in the order the cells, then each cell's samples, first reach them, and indexes each
        // measured cell's block of (split + 1)^3 slots (FineSlot), -1 at its coarse corners. A sample on a cell's face, edge
        // or corner takes the slot of the earliest measured cell holding it: each holder past the cell along an axis where
        // the sample lies on the cell's face, at the sample's place in it.
        void FineKeys(int split) {
            var fineX = ((cellsX * split) + 1);
            var fineY = ((cellsY * split) + 1);
            var side = (split + 1);
            var block = ((side * side) * side);

            m_cellBlocks.Resize(length: measured.Length, clear: false, preserve: false);
            m_blockSlots.Resize(length: checked(measuredCount * block), clear: false, preserve: false);

            var cellSpan = m_cellBlocks.GetSpan();
            var slotSpan = m_blockSlots.GetSpan();
            var samples = 0;
            var blocks = 0;

            for (var z = 0; (z < cellsZ); z++) {
                for (var y = 0; (y < cellsY); y++) {
                    for (var x = 0; (x < cellsX); x++) {
                        var cell = CellIndex(x: x, y: y, z: z);

                        if (!measured[cell]) {
                            cellSpan[cell] = -1;
                            continue;
                        }
                        cellSpan[cell] = blocks;

                        var at = (blocks++ * block);

                        for (var lz = 0; (lz < side); lz++) {
                            for (var ly = 0; (ly < side); ly++) {
                                for (var lx = 0; (lx < side); lx++, at++) {
                                    if (((lx % split) == 0) && ((ly % split) == 0) && ((lz % split) == 0)) {
                                        slotSpan[at] = -1;
                                        continue;
                                    }

                                    var held = EarlierHolder(cellSpan: cellSpan, lx: lx, ly: ly, lz: lz, slotSpan: slotSpan, split: split, x: x, y: y, z: z);

                                    if (held < 0) {
                                        held = samples++;
                                    }
                                    slotSpan[at] = held;
                                }
                            }
                        }
                    }
                }
            }
            m_fineKeys.Resize(length: samples, clear: false, preserve: false);
            var keys = m_fineKeys.GetSpan();
            for (var z = 0; z < cellsZ; z++) {
                for (var y = 0; y < cellsY; y++) {
                    for (var x = 0; x < cellsX; x++) {
                        var cellBlock = cellSpan[CellIndex(x: x, y: y, z: z)];
                        if (cellBlock < 0) { continue; }
                        var at = cellBlock * block;
                        for (var lz = 0; lz < side; lz++) {
                            for (var ly = 0; ly < side; ly++) {
                                for (var lx = 0; lx < side; lx++, at++) {
                                    var slot = slotSpan[at];
                                    if (slot < 0) { continue; }
                                    keys[slot] = (((x * split) + lx) + (fineX * (((y * split) + ly) + (fineY * ((z * split) + lz)))));
                                }
                            }
                        }
                    }
                }
            }
        }
        // The slot an earlier measured cell gave a sample of cell (x, y, z) at (lx, ly, lz), or -1: a holder steps -1
        // along an axis where the sample lies on the cell's low face, +1 where on its high face, and comes earlier in
        // the cells' z, y, x order.
        int EarlierHolder(Span<int> cellSpan, Span<int> slotSpan, int split, int x, int y, int z, int lx, int ly, int lz) {
            var side = (split + 1);
            var block = ((side * side) * side);
            var (lowX, highX) = ((((lx == 0) && (x > 0)) ? -1 : 0), (((lx == split) && ((x + 1) < cellsX)) ? 1 : 0));
            var (lowY, highY) = ((((ly == 0) && (y > 0)) ? -1 : 0), (((ly == split) && ((y + 1) < cellsY)) ? 1 : 0));
            var (lowZ, highZ) = ((((lz == 0) && (z > 0)) ? -1 : 0), (((lz == split) && ((z + 1) < cellsZ)) ? 1 : 0));

            for (var dz = lowZ; (dz <= highZ); dz++) {
                for (var dy = lowY; (dy <= highY); dy++) {
                    for (var dx = lowX; (dx <= highX); dx++) {
                        if (!((dz < 0) || ((dz == 0) && ((dy < 0) || ((dy == 0) && (dx < 0)))))) { continue; }

                        var holder = cellSpan[CellIndex(x: (x + dx), y: (y + dy), z: (z + dz))];

                        if (holder >= 0) {
                            return slotSpan[((holder * block) + ((lx - (dx * split)) + (side * ((ly - (dy * split)) + (side * (lz - (dz * split)))))))];
                        }
                    }
                }
            }

            return -1;
        }

        m_factor = factor;
        Divide(factor: factor);
        m_measured.CopyFrom(source: measured);
        m_thin.CopyFrom(source: thin);
        m_refined.CopyFrom(source: refined);
        MeasuredCells = measuredCount;
        RefinedCells = refinedCount;
        var fineSamples = m_fineKeys.Length;
        m_fineValues.Resize(fineSamples);
        m_fineMaterials.Resize(fineSamples);

        m_points.Resize(length: fineSamples, clear: false, preserve: false);

        var points = m_points.GetSpan();
        var fine = ContourCell;

        for (var slot = 0; (slot < fineSamples); slot++) {
            var (fx, fy, fz) = FineCoordinates(key: m_fineKeys[slot]);

            points[slot] = new Vector4(value: (m_origin + (new Vector3(x: fx, y: fy, z: fz) * fine)), w: 1f);
        }

        return m_points.Memory;
    }

    /// <summary>Returns the most fine samples <see cref="Refine"/> measures in a lattice: every cell split
    /// <paramref name="factor"/> times along each axis, less its coarse corners, at most <see cref="MaxFineSamples"/>; zero
    /// for a factor that refines nothing.</summary>
    /// <param name="samplesX">The samples along X, at least two.</param>
    /// <param name="samplesY">The samples along Y, at least two.</param>
    /// <param name="samplesZ">The samples along Z, at least two.</param>
    /// <param name="factor">The fine cells along each axis of a coarse cell the refinement asks for.</param>
    /// <returns>The bound.</returns>
    public static int FineSampleBound(int samplesX, int samplesY, int samplesZ, int factor) {
        if (factor < 2) { return 0; }

        var cells = ((((long)Math.Max(val1: 1, val2: (samplesX - 1))) * Math.Max(val1: 1, val2: (samplesY - 1))) * Math.Max(val1: 1, val2: (samplesZ - 1)));
        var perCell = ((((long)factor + 1) * (factor + 1)) * (factor + 1)) - 8;

        return ((int)Math.Min(val1: MaxFineSamples, val2: (cells * perCell)));
    }

    /// <summary>Returns, per sample of a lattice, whether it is one of a pair along an axis that a sheet or gap thinner
    /// than the spacing passes between: the four samples along the line are of one sign, the field falls toward the pair
    /// from both sides and the falls, extended, meet strictly below the surface between them, as a sheet's do by half
    /// its thickness and a solid's edge a line grazes never does (<see cref="Refine"/> admits the grazed edge and tells
    /// it apart by the lines beside; this test reads one line alone). The field's change over a step is measured against
    /// its own steepest step among the four, since a bound's field changes by less than a step's length over a step. A
    /// plate lying between two sample rows of a lattice whose long axes are sampled coarsely is found this way where no
    /// edge changes sign.</summary>
    /// <param name="values">The lattice's values, x fastest.</param>
    /// <param name="dims">The samples along each axis.</param>
    /// <param name="cell">The spacing along each axis, in world units.</param>
    /// <param name="near">The field magnitude within which a sample is kept on its own: a pair with such a sample is
    /// left to it, since the sheet lies in that sample's cell already. Zero names every pair.</param>
    /// <returns>One mask per sample: bit 0, 1 or 2 set for each axis along which the sample is one of such a pair, so a
    /// sample in no pair reads zero.</returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> does not hold <paramref name="dims"/>'s
    /// samples.</exception>
    public static byte[] SheetsBetween(ReadOnlySpan<float> values, (int X, int Y, int Z) dims, Vector3 cell, float near = 0f) {
        if (values.Length != ((dims.X * dims.Y) * dims.Z)) {
            throw new ArgumentException(message: $"The lattice holds {values.Length} values for {dims.X}×{dims.Y}×{dims.Z} samples.", paramName: nameof(values));
        }

        var between = new byte[values.Length];
        int[] counts = [dims.X, dims.Y, dims.Z];
        int[] strides = [1, dims.X, (dims.X * dims.Y)];
        float[] spacing = [cell.X, cell.Y, cell.Z];

        for (var axis = 0; (axis < 3); axis++) {
            var (stride, n, h) = (strides[axis], counts[axis], spacing[axis]);

            if (n < 4) { continue; }
            for (var start = 0; (start < values.Length); start++) {
                if (((start / stride) % n) != 0) { continue; }
                for (var i = 1; ((i + 2) < n); i++) {
                    var (before, value, after, further) = (values[(start + ((i - 1) * stride))], values[(start + (i * stride))], values[(start + ((i + 1) * stride))], values[(start + ((i + 2) * stride))]);
                    var inside = (value < 0f);

                    if (((before < 0f) != inside) || ((after < 0f) != inside) || ((further < 0f) != inside)) { continue; }
                    if ((MathF.Abs(x: value) <= near) || (MathF.Abs(x: after) <= near)) { continue; }

                    var slope = (MathF.Max(x: MathF.Abs(x: (value - before)), y: MathF.Max(x: MathF.Abs(x: (after - value)), y: MathF.Abs(x: (further - after)))) / h);

                    if (!PassesBetween(after: MathF.Abs(x: after), before: MathF.Abs(x: before), further: MathF.Abs(x: further), reach: (h * slope), tolerance: 0f, value: MathF.Abs(x: value))) { continue; }

                    // The field at a pair a sheet lies between falls toward the sheet along the line and changes little
                    // across it; a pair that lies beside a solid (a line a step over a plate's top reads the plate's two
                    // ends as falls either side of a flat floor) changes across the line by the step instead.
                    var (first, second) = ((start + (i * stride)), (start + ((i + 1) * stride)));

                    if (ChangesAcross(axis: axis, sample: first, slope: slope, values: values) || ChangesAcross(axis: axis, sample: second, slope: slope, values: values)) { continue; }
                    between[first] |= ((byte)(1 << axis));
                    between[second] |= ((byte)(1 << axis));
                }
            }
        }

        return between;

        // Whether the field changes between a sample and a neighbour along an axis other than `axis` by more than
        // AcrossChange of that step at the line's own slope, either way: a sheet between the pair changes the field
        // across the line by its tilt alone, while a solid beside the line draws the field down toward it on one side
        // and lets it rise on the other.
        bool ChangesAcross(ReadOnlySpan<float> values, int axis, int sample, float slope) {
            var magnitude = MathF.Abs(x: values[sample]);

            for (var across = 0; (across < 3); across++) {
                if (across == axis) { continue; }

                var (strideAcross, countAcross, spacingAcross) = (strides[across], counts[across], spacing[across]);
                var at = ((sample / strideAcross) % countAcross);
                var limit = ((AcrossChange * spacingAcross) * slope);

                if (((at > 0) && (MathF.Abs(x: (magnitude - MathF.Abs(x: values[(sample - strideAcross)]))) > limit)) ||
                    (((at + 1) < countAcross) && (MathF.Abs(x: (magnitude - MathF.Abs(x: values[(sample + strideAcross)]))) > limit))) {
                    return true;
                }
            }

            return false;
        }
    }

    // Whether a sheet passed between two samples of one sign, `value` and `after`, whose neighbours `before` and `further`
    // lie a step out on either side, all as magnitudes: the field falls toward the pair from both sides by over half
    // `reach`, the most it changes over a step there, and the falls, extended, meet within `tolerance` of `reach` of the
    // surface. `reach` carries the field's own scale, since a field that is no true distance (a scope, a flare, a
    // non-uniform scale) changes by less, or more, than a step's length over a step. The refinement's tolerance of a
    // tenth admits a solid's edge or corner a line grazes, whose falls meet at or above the surface, and tells it apart
    // by the lines beside (Thickens, ThinBeside); a test with no lines to compare passes no tolerance, since a sheet's
    // falls meet below the surface by half its thickness and a grazed edge's never do. A gap between two inside samples
    // takes a negative tolerance, half a fine cell (Refine): solids that touch meet at the surface and are no gap.
    private static bool PassesBetween(float before, float value, float after, float further, float reach, float tolerance = 0.1f) {
        var fallA = (before - value);
        var fallB = (further - after);

        if (!(reach > 0f) || (fallA <= (0.5f * reach)) || (fallB <= (0.5f * reach))) {
            return false;
        }

        var t = Math.Clamp(max: 1f, min: 0f, value: (((value - after) + fallB) / (fallA + fallB)));

        return ((value - (fallA * t)) < (tolerance * reach));
    }
    // The most the field changes per world unit between a coarse sample and any of its six neighbours: the scale of the
    // field there, which a step's change is measured against (PassesBetween).
    private float Slope(int x, int y, int z) {
        var value = Values[Index(x: x, y: y, z: z)];
        var slope = 0f;

        if (x > 0) { slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - Values[Index(x: (x - 1), y: y, z: z)])) / m_cell.X)); }
        if ((x + 1) < m_samplesX) { slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - Values[Index(x: (x + 1), y: y, z: z)])) / m_cell.X)); }
        if (y > 0) { slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - Values[Index(x: x, y: (y - 1), z: z)])) / m_cell.Y)); }
        if ((y + 1) < m_samplesY) { slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - Values[Index(x: x, y: (y + 1), z: z)])) / m_cell.Y)); }
        if (z > 0) { slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - Values[Index(x: x, y: y, z: (z - 1))])) / m_cell.Z)); }
        if ((z + 1) < m_samplesZ) { slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - Values[Index(x: x, y: y, z: (z + 1))])) / m_cell.Z)); }

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
                    var value = Values[Index(x: x, y: y, z: z)];

                    if (((x + 1) < m_samplesX) && ((value < 0f) != (Values[Index(x: (x + 1), y: y, z: z)] < 0f))) { slopes.Add(item: (MathF.Abs(x: (value - Values[Index(x: (x + 1), y: y, z: z)])) / m_cell.X)); }
                    if (((y + 1) < m_samplesY) && ((value < 0f) != (Values[Index(x: x, y: (y + 1), z: z)] < 0f))) { slopes.Add(item: (MathF.Abs(x: (value - Values[Index(x: x, y: (y + 1), z: z)])) / m_cell.Y)); }
                    if (((z + 1) < m_samplesZ) && ((value < 0f) != (Values[Index(x: x, y: y, z: (z + 1))] < 0f))) { slopes.Add(item: (MathF.Abs(x: (value - Values[Index(x: x, y: y, z: (z + 1))])) / m_cell.Z)); }
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
        m_signKnown.Resize(0);
        m_signInside.Resize(0);
    }

    // An edge root stays bracketed by its original signs, and the search ends once the bracket is an 8,192th of the edge
    // wide, its root then the bracket's middle, within a 16,384th of the zero. Each round measures two trials a
    // tolerance apart about an estimate (the linear estimate, first, then the secant through the last round's trials, or
    // the false position between the bracket's ends where that secant leaves the bracket), so a root the
    // estimate all but hits closes the bracket that round; the bracket becomes the part between its ends and the trials
    // whose ends differ in sign. A bracket whose remaining thirdings fill its remaining rounds is cut in thirds instead,
    // so a field flat inside a corner cannot stall the search on the wrong face as an unguarded secant can, and no search
    // takes more than MostRounds readback round trips of its group's.
    private struct CrossingRoot {
        // The bracket width at which a search ends.
        public const float Tolerance = (1f / 8192f);
        // The rounds a search takes at most: one about the linear estimate, one about the next false position, and the
        // nine thirdings that close an edge's bracket.
        public const int MostRounds = 11;

        // Half the gap between a round's trials about an estimate: under half the tolerance, so a root between them
        // ends the search.
        private const float Straddle = (0.45f * Tolerance);

        public CrossingRoot(float first, float second) {
            LowValue = first;
            HighValue = second;
            Done = ((first == 0f) || (second == 0f));
            Position = (Done ? ((first == 0f) ? 0f : 1f) : 0.5f);
            if (!Done) { Straddling(estimate: (first / (first - second))); }
        }

        public bool Done { get; private set; }
        public float Position { get; private set; }
        // The round's two trials along the edge, nearer the low end first.
        public float First { get; private set; }
        public float Second { get; private set; }

        private int Answered { get; set; }
        private float FirstValue { get; set; }
        private float High { get; set; } = 1f;
        private float HighValue { get; set; }
        private float Low { get; set; }
        private float LowValue { get; set; }
        private int Rounds { get; set; }

        // Takes the field's value at one of the round's trials (0 the first, 1 the second); once both are answered,
        // narrows the bracket and chooses the next round's trials, or ends the search.
        public void Answer(int trial, float value) {
            if (Done) { return; }
            if (trial == 0) { FirstValue = value; }
            if (++Answered < 2) { return; }
            Answered = 0;
            Rounds++;

            var secondValue = value;

            if ((FirstValue == 0f) || (secondValue == 0f)) {
                Position = ((FirstValue == 0f) ? First : Second);
                Done = true;
                return;
            }
            // The first part of low, first, second, high whose ends differ in sign.
            var lowInside = (LowValue < 0f);

            if ((FirstValue < 0f) != lowInside) {
                (High, HighValue) = (First, FirstValue);
            } else if ((secondValue < 0f) != lowInside) {
                (Low, LowValue, High, HighValue) = (First, FirstValue, Second, secondValue);
            } else {
                (Low, LowValue) = (Second, secondValue);
            }

            var width = (High - Low);

            if ((width <= Tolerance) || (Rounds >= MostRounds)) {
                Position = ((Low + High) * 0.5f);
                Done = true;
                return;
            }
            // The thirdings the bracket still needs; once they fill the rounds left, every round cuts it in thirds.
            if (((int)MathF.Ceiling(x: (MathF.Log2(x: (width / Tolerance)) / MathF.Log2(x: 3f)))) >= (MostRounds - Rounds)) {
                First = (Low + (width / 3f));
                Second = (Low + ((2f * width) / 3f));
                return;
            }
            // The secant through the round's two trials, a tolerance apart, reads the field's slope where it was just
            // measured and lands far nearer a curved surface's root than the bracket's false position does; where it
            // leaves the bracket, the false position stands.
            var secant = (First - ((FirstValue * (Second - First)) / (secondValue - FirstValue)));

            Straddling(estimate: ((float.IsFinite(f: secant) && (secant > Low) && (secant < High)) ? secant : (((Low * HighValue) - (High * LowValue)) / (HighValue - LowValue))));
        }

        // Places the round's trials a tolerance apart about an estimate inside the bracket.
        private void Straddling(float estimate) {
            var inset = (0.125f * Tolerance);
            var centre = (float.IsFinite(f: estimate) ? Math.Clamp(value: estimate, min: (Low + inset), max: (High - inset)) : ((Low + High) * 0.5f));

            First = MathF.Max(x: (centre - Straddle), y: (Low + inset));
            Second = MathF.Min(x: (centre + Straddle), y: (High - inset));
        }
    }

    // Closing sheets changes their edge crossings deliberately; both crossing searches and gradient sampling use the
    // resulting lattice, prepared once after the refinement answers arrive.
    private void PrepareCrossings() {
        if (m_crossingsPrepared) { return; }
        CloseSheets();
        m_crossingKeys.CopyFrom(source: CrossingEdges());
        m_crossingsPrepared = true;
    }
    private int EdgeEnd(int start, int axis, int length) {
        var fineX = (((m_samplesX - 1) * m_factor) + 1);
        var fineY = (((m_samplesY - 1) * m_factor) + 1);

        return (start + ((axis == 0) ? length : ((axis == 1) ? (length * fineX) : ((length * fineX) * fineY))));
    }
    private Vector3 CrossingPoint(long key, float fraction) {
        var (start, axis, length) = CrossingEdge(key: key);
        var (x, y, z) = FineCoordinates(key: start);
        var along = (fraction * length);

        return (m_origin + (new Vector3(x: (x + ((axis == 0) ? along : 0f)), y: (y + ((axis == 1) ? along : 0f)), z: (z + ((axis == 2) ? along : 0f))) * ContourCell));
    }

    /// <summary>Returns the next points for locating each sign-changing edge's zero along that edge. After refinement,
    /// repeatedly answer these points with <see cref="AnswerCrossings"/> until this returns no points, then measure
    /// <see cref="GradientPoints"/>. Closed thin sheets and gaps keep their lattice crossings.</summary>
    /// <returns>The next round's points, with weight one, in the order <see cref="AnswerCrossings"/> takes their values.
    /// The memory is the lattice's, valid until the next round of points is asked for or the lattice is disposed.</returns>
    public ReadOnlyMemory<Vector4> CrossingPoints() {
        PrepareCrossings();
        if (!m_rootsPrepared) {
            var roots = new List<CrossingRoot>(capacity: m_crossingKeys.Length);
            var keys = new List<long>(capacity: m_crossingKeys.Length);

            _ = m_crossingRootIndex.EnsureCapacity(capacity: m_crossingKeys.Length);
            foreach (var key in m_crossingKeys) {
                var (start, axis, length) = CrossingEdge(key: key);
                var end = EdgeEnd(axis: axis, length: length, start: start);

                // A transition edge's unmeasured end reads the value its coarse corners interpolate, whose sign the
                // contour's topology keeps; its root is searched along the edge as a measured one is. Inside a union the
                // interpolated value is no distance (a layer thinner than a coarse cell measures to the face it shares
                // with its neighbour), so its linear estimate lands off the surface, by up to that layer's thickness.
                if (Closed(key: start) || Closed(key: end)) { continue; }
                var (x, y, z) = FineCoordinates(key: start);
                var (ex, ey, ez) = FineCoordinates(key: end);

                m_crossingRootIndex.Add(key: key, value: roots.Count);
                roots.Add(item: new CrossingRoot(first: FineValue(x: x, y: y, z: z), second: FineValue(x: ex, y: ey, z: ez)));
                keys.Add(item: key);
            }
            m_rootsPrepared = true;
            // The first round's roots, in the order of their edges; each later round keeps those still searching.
            m_crossingRoots = [.. roots];
            m_pendingCrossings.CopyFrom(source: CollectionsMarshal.AsSpan(list: keys));
            m_pendingRoots = new int[m_crossingRoots.Length];
            for (var index = 0; (index < m_pendingRoots.Length); index++) { m_pendingRoots[index] = index; }
        }

        var kept = 0;

        for (var index = 0; (index < m_pendingRoots.Length); index++) {
            if (m_crossingRoots[m_pendingRoots[index]].Done) { continue; }
            m_pendingCrossings[kept] = m_pendingCrossings[index];
            m_pendingRoots[kept] = m_pendingRoots[index];
            kept++;
        }
        m_pendingCrossings.Resize(kept);
        Array.Resize(array: ref m_pendingRoots, newSize: kept);

        // Two trials a root, the root's first then its second.
        m_points.Resize(length: (2 * kept), clear: false, preserve: false);

        var points = m_points.GetSpan();

        for (var index = 0; (index < kept); index++) {
            ref readonly var root = ref m_crossingRoots[m_pendingRoots[index]];

            points[(2 * index)] = new Vector4(value: CrossingPoint(key: m_pendingCrossings[index], fraction: root.First), w: 1f);
            points[((2 * index) + 1)] = new Vector4(value: CrossingPoint(key: m_pendingCrossings[index], fraction: root.Second), w: 1f);
        }

        return m_points.Memory;
    }
    /// <summary>Answers a chunk of the current <see cref="CrossingPoints"/> round with field values. Every root remains
    /// on its own edge, bracketed, and its search ends once the bracket is an 8,192th of the edge wide, the root within an
    /// 16,384th of the zero, in at most eleven rounds.</summary>
    /// <param name="first">The first point answered in the current round.</param>
    /// <param name="values">One field value per point.</param>
    /// <exception cref="ArgumentException">The chunk reaches past the current round or holds a non-finite value.</exception>
    public void AnswerCrossings(int first, ReadOnlySpan<float> values) {
        if ((first < 0) || ((first + values.Length) > (2 * m_pendingCrossings.Length))) {
            throw new ArgumentException(message: "A crossing answer must name points in the current crossing round.");
        }
        for (var index = 0; (index < values.Length); index++) {
            var value = values[index];

            if (!float.IsFinite(f: value)) { throw new ArgumentException(message: "A crossing answer must be finite."); }
            m_crossingRoots[m_pendingRoots[((first + index) / 2)]].Answer(trial: ((first + index) % 2), value: value);
        }
    }
    /// <summary>Returns the samples the contour reads a gradient at: the corners of every crossed cell that contours whole,
    /// the coarse corners of every refined cell, whose gradients its unmeasured fine samples interpolate, and the measured
    /// corners of every crossed fine cell, which its vertex normals interpolate; then the crossing of every sign-changing
    /// edge a vertex is placed by. Call it once the refinement, if any, is answered.</summary>
    /// <returns>The samples, in the lattice's frame, with weight one, in the order <see cref="AnswerGradients"/> takes
    /// their answers. The memory is the lattice's, valid until the next round of points is asked for or the lattice is
    /// disposed.</returns>
    public ReadOnlyMemory<Vector4> GradientPoints() {
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

        var crossed = 0;

        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    var refined = ((factor > 1) && m_refined[(x + (cellsX * (y + (cellsY * z))))]);
                    var mask = 0;

                    for (var offset = 0; (offset < 8); offset++) {
                        mask |= ((Values[Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))] < 0f) ? (1 << offset) : 0);
                    }
                    if (refined || ((mask != 0) && (mask != 0xFF))) {
                        for (var offset = 0; (offset < 8); offset++) {
                            Add(fx: ((x + (offset & 1)) * factor), fy: ((y + ((offset >> 1) & 1)) * factor), fz: ((z + ((offset >> 2) & 1)) * factor));
                        }
                    }
                    if (!refined) {
                        continue;
                    }

                    // This refined cell's crossed fine cells, which CrossingEdges listed in this order.
                    for (; (crossed < m_crossedFineCells.Length); crossed++) {
                        var (fx, fy, fz) = FineCoordinates(key: m_crossedFineCells[crossed].Corner);

                        if (((fx / factor) != x) || ((fy / factor) != y) || ((fz / factor) != z)) { break; }
                        for (var offset = 0; (offset < 8); offset++) {
                            var cx = (fx + (offset & 1));
                            var cy = (fy + ((offset >> 1) & 1));
                            var cz = (fz + ((offset >> 2) & 1));

                            if (FineSlot(x: cx, y: cy, z: cz) >= 0) {
                                Add(fx: cx, fy: cy, fz: cz);
                            }
                        }
                    }
                }
            }
        }

        m_gradientKeys.CopyFrom(source: CollectionsMarshal.AsSpan(list: keys));
        // Every answer lands in one of the two: size both once rather than growing them answer by answer.
        _ = m_fineGradients.EnsureCapacity(capacity: keys.Count);
        _ = m_crossingGradients.EnsureCapacity(capacity: m_crossingKeys.Length);

        m_points.Resize(length: (keys.Count + m_crossingKeys.Length), clear: false, preserve: false);

        var points = m_points.GetSpan();
        var fine = ContourCell;

        for (var index = 0; (index < keys.Count); index++) {
            var (fx, fy, fz) = FineCoordinates(key: keys[index]);

            points[index] = new Vector4(value: (m_origin + (new Vector3(x: fx, y: fy, z: fz) * fine)), w: 1f);
        }
        for (var index = 0; (index < m_crossingKeys.Length); index++) {
            var (start, axis, length) = CrossingEdge(key: m_crossingKeys[index]);
            var (fx, fy, fz) = FineCoordinates(key: start);
            var position = new Vector3(x: fx, y: fy, z: fz);
            var step = ((axis == 0) ? Vector3.UnitX : ((axis == 1) ? Vector3.UnitY : Vector3.UnitZ));

            points[(keys.Count + index)] = new Vector4(value: (m_origin + ((position + (step * (Crossing(axis: axis, length: length, x: fx, y: fy, z: fz) * length))) * fine)), w: 1f);
        }

        return m_points.Memory;
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
        var step = MathF.Min(x: cell.X, y: MathF.Min(x: cell.Y, y: cell.Z));
        var closing = new Dictionary<int, float>();
        Span<int> p = stackalloc int[3];

        int Key(Span<int> at) => (at[0] + (fine[0] * (at[1] + (fine[1] * at[2]))));
        bool Coarse(Span<int> at) => (((at[0] % factor) == 0) && ((at[1] % factor) == 0) && ((at[2] % factor) == 0));
        bool Within(Span<int> at) => ((at[0] >= 0) && (at[1] >= 0) && (at[2] >= 0) && (at[0] < fine[0]) && (at[1] < fine[1]) && (at[2] < fine[2]));
        bool Real(Span<int> at) => (Within(at: at) && (Coarse(at: at) || (FineSlot(x: at[0], y: at[1], z: at[2]) >= 0)));
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
                        slope = MathF.Max(x: slope, y: (MathF.Abs(x: (value - FineValue(x: neighbour[0], y: neighbour[1], z: neighbour[2]))) / spacing[axis]));
                    }
                }
            }

            return slope;
        }
        void Close(int key, float slope, float depth) => closing.TryAdd(key: key, value: -MathF.Max(x: depth, y: MathF.Max(x: ((0.25f * step) * slope), y: 1.0e-7f)));
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
                    deepest = (Within(at: around) ? MathF.Min(x: deepest, y: FineValue(x: around[0], y: around[1], z: around[2])) : deepest);
                }
            }

            return deepest;
        }
        var side = new int[3];
        var beside = new int[3];
        var clear = ((0.1f * step) * SurfaceSlope());
        // Whether a fine sample lies inside a sheet: inside, outside a step to either side along some axis by more than a
        // tenth of the field's typical change over a step, which a surface grazing the line within its samples' noise is
        // not, and no neighbour deeper inside than those two lie outside, as a solid's edge or corner, which a line near it
        // also crosses twice within a step, would hold, nor a line beside it inside for two steps (Thickens), as a wedge
        // too acute for its neighbours to reach deep would; with `thinner`, a sheet whose crossings on that line lie less
        // than a step apart.
        bool Sheet(Span<int> at, bool thinner) {
            if (!FineInside(x: at[0], y: at[1], z: at[2])) {
                return false;
            }

            var value = FineValue(x: at[0], y: at[1], z: at[2]);

            var deepest = float.NaN;

            for (var axis = 0; (axis < 3); axis++) {
                side[0] = at[0];
                side[1] = at[1];
                side[2] = at[2];
                side[axis]--;
                var low = (Within(at: side) ? FineValue(x: side[0], y: side[1], z: side[2]) : -1f);

                if (!(low > clear)) { continue; }
                side[axis] += 2;
                var high = (Within(at: side) ? FineValue(x: side[0], y: side[1], z: side[2]) : -1f);

                if (!(high > clear)) { continue; }
                if (float.IsNaN(f: deepest)) { deepest = Deepest(at: at); }

                if ((deepest > -MathF.Max(x: low, y: high)) && (!thinner || (((-value / (low - value)) + (-value / (high - value))) < 1f)) &&
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
                        run = (FineInside(x: beside[0], y: beside[1], z: beside[2]) ? (run + 1) : 0);
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
            var (cx, cy, cz, _, _, _) = FineCell(x: x, y: y, z: z);

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
            // Only an inside sample lies in a sheet, and only an outside one in a line that steps over one: either
            // pair below holds the sample itself among the line's four outside samples.
            if (FineInside(x: x, y: y, z: z)) {
                if (Sheet(at: p, thinner: true)) {
                    Close(depth: ((0.5f * step) * FineSlope(at: p)), key: key, slope: 0f);
                }
                continue;
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
                    // Every sample of the line lies outside, read before the slopes and depths about the pair.
                    if (!Within(at: p) || FineInside(x: p[0], y: p[1], z: p[2])) { continue; }
                    p[axis]++;
                    if (!Real(at: p) || FineInside(x: p[0], y: p[1], z: p[2])) { continue; }
                    p[axis]++;
                    if (!Real(at: p) || FineInside(x: p[0], y: p[1], z: p[2])) { continue; }
                    p[axis]++;
                    if (!Within(at: p) || FineInside(x: p[0], y: p[1], z: p[2])) { continue; }
                    var further = FineValue(x: p[0], y: p[1], z: p[2]);

                    p[axis]--;
                    var second = FineValue(x: p[0], y: p[1], z: p[2]);
                    var secondKey = (Coarse(at: p) ? -1 : Key(at: p));

                    p[axis]--;
                    var first = FineValue(x: p[0], y: p[1], z: p[2]);
                    var firstKey = (Coarse(at: p) ? -1 : Key(at: p));

                    p[axis]--;
                    var before = FineValue(x: p[0], y: p[1], z: p[2]);

                    // PassesBetween needs the field to fall toward the pair from both sides; where it does not, the line
                    // holds no sheet whatever the slopes and depths about the pair read, so neither is measured.
                    if (!(before > first) || !(further > second)) { continue; }
                    p[axis]++;
                    var firstSlope = FineSlope(at: p);
                    var deepest = Deepest(at: p);

                    p[axis]++;
                    var secondSlope = FineSlope(at: p);

                    deepest = MathF.Min(x: deepest, y: Deepest(at: p));
                    p[axis]++;

                    // A solid's edge the line grazes falls toward the pair from both sides as a sheet does, but holds
                    // a neighbour of the pair deeper inside than half a step, which a sheet thinner than a step cannot,
                    // or, an acute wedge's, a line beside the pair inside for two steps (Thickens).
                    if ((deepest < ((-0.5f * spacing[axis]) * MathF.Max(x: firstSlope, y: secondSlope))) ||
                        !PassesBetween(after: second, before: before, further: further, reach: (spacing[axis] * MathF.Max(x: firstSlope, y: secondSlope)), value: first) ||
                        Thickens(at: p, axis: axis, first: (p[axis] - 3), last: p[axis])) {
                        continue;
                    }

                    var nearer = ((((first <= second) && (firstKey >= 0)) || (secondKey < 0)) ? (Key: firstKey, Value: first, Slope: firstSlope) : (Key: secondKey, Value: second, Slope: secondSlope));

                    // A sheet that passed between the pair lies within half a step of the nearer: one farther off is a
                    // surface the samples beyond the pair, interpolated across a coarse cell, misread as falling.
                    // It reads as far inside as it lay outside, which puts the sheet's face toward the pair where the
                    // sheet passed and moves a surface a grazed edge misread by no more than the sample's own distance.
                    if ((nearer.Key >= 0) && (nearer.Value <= ((0.5f * spacing[axis]) * nearer.Slope))) { Close(depth: nearer.Value, key: nearer.Key, slope: nearer.Slope); }
                }
            }
        }

        foreach (var (key, value) in closing) {
            CloseSample(key: key, value: value);
        }

        // A sheet one sample thick that steps across the lattice diagonally holds its inside samples at the opposite
        // corners of a face, which the contour cuts apart (an ambiguous face separates its inside corners), leaving a slit
        // along every step. Where both inside corners lie in a sheet (outside on either side along some axis), the face's
        // outside corner nearer the sheet closes, so the steps join along a face.
        Span<int> corner = stackalloc int[3];
        Span<int> chosenAt = stackalloc int[3];
        Span<int> keys = stackalloc int[4];
        Span<float> values = stackalloc float[4];
        Span<bool> signs = stackalloc bool[4];

        for (var pass = 0; (pass < 2); pass++) {
            closing.Clear();
            foreach (var key in m_fineKeys) {
                var (x, y, z) = FineCoordinates(key: key);
                // The sample's six neighbours whose sign differs from its own, a bit each at 2 * axis + (1 for the step
                // up): a face alternates in sign only where both of the sample's neighbours on it differ from the
                // sample, so a face missing either is passed before any of its corners is read.
                var inside = FineInside(key: key);
                var differs = 0;

                if ((x > 0) && (FineInside(key: (key - 1)) != inside)) { differs |= 1; }
                if (((x + 1) < fine[0]) && (FineInside(key: (key + 1)) != inside)) { differs |= 2; }
                if ((y > 0) && (FineInside(key: (key - fine[0])) != inside)) { differs |= 4; }
                if (((y + 1) < fine[1]) && (FineInside(key: (key + fine[0])) != inside)) { differs |= 8; }
                if ((z > 0) && (FineInside(key: (key - (fine[0] * fine[1]))) != inside)) { differs |= 16; }
                if (((z + 1) < fine[2]) && (FineInside(key: (key + (fine[0] * fine[1]))) != inside)) { differs |= 32; }
                // An alternating face needs differing neighbours on two distinct axes.
                if (((differs & 3) == 0) ? (((differs & 12) == 0) || ((differs & 48) == 0)) : ((differs & 60) == 0)) { continue; }

                // Each face the sample is a corner of, as the face's lowest corner shifted by (du, dv) on its two axes.
                for (var normal = 0; (normal < 3); normal++) {
                    var u = ((normal + 1) % 3);
                    var v = ((normal + 2) % 3);

                    for (var du = -1; (du <= 0); du++) {
                        for (var dv = -1; (dv <= 0); dv++) {
                            // The sample's neighbours on the face: a step up the axis where the face starts at the sample.
                            if (((differs & (1 << ((2 * u) + du + 1))) == 0) || ((differs & (1 << ((2 * v) + dv + 1))) == 0)) { continue; }
                            corner[0] = x;
                            corner[1] = y;
                            corner[2] = z;
                            corner[u] += du;
                            corner[v] += dv;

                            // The corners' signs alone, from FineInside, settle most faces: one whose signs cannot
                            // alternate around it is passed before any of its slots is looked up.
                            var alternates = true;

                            for (var at = 0; ((at < 4) && alternates); at++) {
                                p[0] = corner[0];
                                p[1] = corner[1];
                                p[2] = corner[2];
                                p[u] += at & 1;
                                p[v] += (at >> 1);
                                if (!Within(at: p)) {
                                    alternates = false;
                                    break;
                                }
                                signs[at] = FineInside(x: p[0], y: p[1], z: p[2]);
                                alternates = (at switch {
                                    1 => (signs[1] != signs[0]),
                                    2 => (signs[2] == signs[1]),
                                    3 => (signs[3] == signs[0]),
                                    _ => true,
                                });
                            }
                            if (!alternates) { continue; }

                            // The face's corners in order: (0, 0), (1, 0), (0, 1), (1, 1) along (u, v). A face is read
                            // only while its corners can still alternate in sign around it: the first two differ, the
                            // third matches the second and the fourth the first. Every measured corner visits the face
                            // with one outcome, so only the first measured corner in that order reads it.
                            var real = true;
                            var visitor = true;

                            for (var at = 0; ((at < 4) && real); at++) {
                                p[0] = corner[0];
                                p[1] = corner[1];
                                p[2] = corner[2];
                                p[u] += at & 1;
                                p[v] += (at >> 1);
                                if (!Within(at: p)) {
                                    real = false;
                                    break;
                                }
                                if (Coarse(at: p)) {
                                    keys[at] = -1;
                                    values[at] = Values[Index(x: (p[0] / factor), y: (p[1] / factor), z: (p[2] / factor))];
                                } else {
                                    var (cx, cy, cz, lx, ly, lz) = FineCell(x: p[0], y: p[1], z: p[2]);
                                    var slot = FineSlot(cx: cx, cy: cy, cz: cz, lx: lx, ly: ly, lz: lz);

                                    if (slot < 0) {
                                        real = false;
                                        break;
                                    }
                                    keys[at] = Key(at: p);
                                    if (visitor) {
                                        visitor = false;
                                        if (keys[at] != key) {
                                            real = false;
                                            break;
                                        }
                                    }
                                    values[at] = MeasuredValue(slot: slot);
                                }
                                real &= (at switch {
                                    1 => ((values[1] < 0f) != (values[0] < 0f)),
                                    2 => ((values[2] < 0f) == (values[1] < 0f)),
                                    3 => ((values[3] < 0f) == (values[0] < 0f)),
                                    _ => true,
                                });
                            }
                            if (!real) { continue; }

                            // Inside on one diagonal, outside on the other.
                            var diagonal = (((values[0] < 0f) && (values[3] < 0f) && (values[1] >= 0f) && (values[2] >= 0f)) ? 0 :
                                (((values[1] < 0f) && (values[2] < 0f) && (values[0] >= 0f) && (values[3] >= 0f)) ? 1 : -1));

                            if (diagonal < 0) { continue; }

                            var (inA, inB, outA, outB) = ((diagonal == 0) ? (0, 3, 1, 2) : (1, 2, 0, 3));
                            var sheet = true;

                            foreach (var inner in ((ReadOnlySpan<int>)[inA, inB])) {
                                p[0] = corner[0];
                                p[1] = corner[1];
                                p[2] = corner[2];
                                p[u] += inner & 1;
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
                            chosenAt[u] += chosen & 1;
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
                CloseSample(key: key, value: value);
            }
        }
    }
    // Whether the sample a fine key names reads the value of a sheet closed at the fine spacing (CloseSheets).
    private bool Closed(int key) {
        if (m_closedCount == 0) { return false; }

        var (x, y, z) = FineCoordinates(key: key);

        return ((FineSlot(x: x, y: y, z: z) is var slot and >= 0) && !float.IsNaN(f: m_closedValues[slot]));
    }
    // Closes a measured fine sample at a value, unless it is closed already.
    private void CloseSample(int key, float value) {
        var (x, y, z) = FineCoordinates(key: key);
        var slot = FineSlot(x: x, y: y, z: z);

        if (m_closedValues.Length != m_fineValues.Length) {
            m_closedValues.Resize(m_fineValues.Length);
            m_closedValues.GetSpan().Fill(value: float.NaN);
        }
        if (!float.IsNaN(f: m_closedValues[slot])) { return; }
        m_closedValues[slot] = value;
        m_closedCount++;
        KeepSign(key: key, value: value);
    }
    // The thickness, in steps, of the run of one sample `value` between `before` and `after` of the other sign along a
    // line, as the line's values interpolate its two crossings.
    private static float RunThickness(float before, float value, float after) {
        var middle = MathF.Abs(x: value);

        return ((middle / (MathF.Abs(x: before) + middle)) + (middle / (middle + MathF.Abs(x: after))));
    }
    // Every sign-changing lattice edge the contour places a vertex by: the edges of each crossed cell that contours whole,
    // a coarse cell long, and the fine edges of each crossed fine cell of a refined cell. A crossing's gradient is measured
    // where the edge crosses the surface, since its corners' gradients, inside a solid near a sharp edge, point to the
    // nearer face beyond the edge and tilt the vertex's tangent planes into a chamfer.
    private long[] CrossingEdges() {
        var edges = new List<long>();
        var seen = new HashSet<long>();
        var crossedFine = new List<SdfCrossedCell>();
        var cellsX = (m_samplesX - 1);
        var cellsY = (m_samplesY - 1);
        var cellsZ = (m_samplesZ - 1);
        var factor = m_factor;
        var fineX = ((cellsX * factor) + 1);
        var fineY = ((cellsY * factor) + 1);

        // The four signs on an X face, in the low-X bits of a corner mask. Adjacent fine cells in a row share
        // this exact face. Signs are fixed after CloseSheets, and the row keeps the face only until its next cell.
        int Face(int x, int y, int z, int stride) {
            var key = x + (fineX * (y + (fineY * z)));
            var dy = fineX * stride;
            var dz = fineX * fineY * stride;
            return (FineInside(key) ? 1 : 0) | (FineInside(key + dy) ? 4 : 0) |
                (FineInside(key + dz) ? 16 : 0) | (FineInside(key + dy + dz) ? 64 : 0);
        }
        // Adds a cell's sign-changing edges in the original cell/axis/corner order.
        void AddCell(int x, int y, int z, int stride, int mask) {
            if ((mask == 0) || (mask == 0xFF)) {
                return;
            }

            for (var axis = 0; (axis < 3); axis++) {
                for (var u = 0; (u < 2); u++) {
                    for (var v = 0; (v < 2); v++) {
                        // The edge along `axis` at offsets (u, v) on the two axes after it, as corner bits a (start) and b.
                        var a = (((u << ((axis + 1) % 3)) | (v << ((axis + 2) % 3))));
                        var b = a | (1 << axis);

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
                        var mask = Face(x * factor, y * factor, z * factor, factor) |
                            (Face((x + 1) * factor, y * factor, z * factor, factor) << 1);
                        AddCell(stride: factor, x: (x * factor), y: (y * factor), z: (z * factor), mask: mask);

                        continue;
                    }

                    for (var fz = (z * factor); (fz < ((z + 1) * factor)); fz++) {
                        for (var fy = (y * factor); (fy < ((y + 1) * factor)); fy++) {
                            var left = Face(x * factor, fy, fz, 1);
                            for (var fx = (x * factor); (fx < ((x + 1) * factor)); fx++) {
                                var right = Face(fx + 1, fy, fz, 1);
                                var mask = left | (right << 1);
                                left = right;
                                AddCell(stride: 1, x: fx, y: fy, z: fz, mask: mask);

                                if ((mask != 0) && (mask != 0xFF)) { crossedFine.Add(item: new SdfCrossedCell(Corner: (fx + (fineX * (fy + (fineY * fz)))), Mask: (byte)mask)); }
                            }
                        }
                    }
                }
            }
        }
        m_crossedFineCells.CopyFrom(source: CollectionsMarshal.AsSpan(list: crossedFine));

        return [.. edges];
    }
    // A crossing edge's key: its start sample's fine key, its axis and whether it is a coarse cell long.
    private long CrossingKey(int start, int axis, int length) => ((((((long)start) * 3L) + axis) * 2L) + (((length > 1) && (m_factor > 1)) ? 1L : 0L));
    private (int Start, int Axis, int Length) CrossingEdge(long key) => (((int)((key / 2L) / 3L)), ((int)((key / 2L) % 3L)), (((key & 1L) == 1L) ? m_factor : 1));
    // The refined zero along the edge `length` fine cells long from (x, y, z), in [0, 1]. Synthetic samples retain the
    // linear crossing of the lattice the contour must connect, rather than the underlying field it intentionally replaces.
    private float Crossing(int x, int y, int z, int axis, int length) {
        var fineX = (((m_samplesX - 1) * m_factor) + 1);
        var fineY = (((m_samplesY - 1) * m_factor) + 1);
        var start = (x + (fineX * (y + (fineY * z))));

        if (m_crossingRootIndex.TryGetValue(key: CrossingKey(axis: axis, length: length, start: start), value: out var root)) { return m_crossingRoots[root].Position; }
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
        PrepareCrossings();
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
            Beside = surface.Beside,
            Features = surface.Features,
            Solved = surface.Solved,
        };

        contour.SelectDiagonals();

        return contour;
    }

    internal static void SelectDiagonals(SdfContour contour) {
        var quads = contour.Quads;

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var at = (4 * quad);

            if ((quads[(at + 2)] != quads[(at + 3)]) && !SplitsAlongFirstDiagonal(features: contour.Features, normals: contour.Normals, positions: contour.Positions, quad: quads.AsSpan(length: 4, start: at))) {
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
        // A split whose triangle is a line (three vertices on one crease, as a diagonal crease through three of the four
        // cells around a lattice edge places them) loses to the other: that triangle's facing is rounding noise, and the
        // other split's two triangles both have area.
        var thinFirst = Math.Min(val1: Thinness(a: v0, b: v1, c: v2), val2: Thinness(a: v0, b: v2, c: v3));
        var thinSecond = Math.Min(val1: Thinness(a: v0, b: v1, c: v3), val2: Thinness(a: v1, b: v2, c: v3));

        if ((thinFirst < DegenerateSplit) != (thinSecond < DegenerateSplit)) {
            return (thinFirst >= DegenerateSplit);
        }

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

        foreach (var corner in ((ReadOnlySpan<int>)[a, b, c])) {
            if ((corner < features.Length) && features[corner]) {
                continue;
            }

            turn += (1.0 - ((((nx * normals[(3 * corner)]) + (ny * normals[((3 * corner) + 1)])) + (nz * normals[((3 * corner) + 2)])) / length));
        }

        return turn;
    }
    private static Vector3 Vertex(float[] values, int vertex) => new(x: values[(3 * vertex)], y: values[((3 * vertex) + 1)], z: values[((3 * vertex) + 2)]);
    // Twice triangle (a, b, c)'s area over its longest edge squared: its height as a share of that edge, zero for a line.
    private static double Thinness(Vector3 a, Vector3 b, Vector3 c) {
        var longest = Math.Max(val1: DistanceSquared(a: a, b: b), val2: Math.Max(val1: DistanceSquared(a: b, b: c), val2: DistanceSquared(a: c, b: a)));

        return ((longest > 0.0) ? (((double)Vector3.Cross(vector1: (b - a), vector2: (c - a)).Length()) / longest) : 0.0);
    }
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
        var row = ((int)m_byFineRow.Divide(value: ((uint)key)));
        var z = ((int)m_byFineColumn.Divide(value: ((uint)row)));

        return ((key - (row * m_fineRowLength)), (row - (z * m_fineColumnLength)), z);
    }
    private void Divide(int factor) {
        m_fineRowLength = (((m_samplesX - 1) * factor) + 1);
        m_fineColumnLength = (((m_samplesY - 1) * factor) + 1);
        m_byFineRow = new ReciprocalDivisor32(divisor: checked((uint)m_fineRowLength));
        m_byFineColumn = new ReciprocalDivisor32(divisor: checked((uint)m_fineColumnLength));
        m_byFactor = new ReciprocalDivisor32(divisor: checked((uint)factor));
    }
    // The coarse cell a fine sample interpolates in and its position there, each in [0, 1].
    private (int X, int Y, int Z, float U, float V, float W) Interpolant(int x, int y, int z) {
        var (cx, cy, cz, lx, ly, lz) = FineCell(x: x, y: y, z: z);

        return (cx, cy, cz, (((float)lx) / m_factor), (((float)ly) / m_factor), (((float)lz) / m_factor));
    }
    // The slot in m_fineValues of a measured fine sample, or -1 for a coarse sample or one no measured cell holds. A
    // sample on a cell's face, edge or corner is held by every cell about it; any measured one answers it.
    private int FineSlot(int x, int y, int z) {
        var (cx, cy, cz, lx, ly, lz) = FineCell(x: x, y: y, z: z);

        return FineSlot(cx: cx, cy: cy, cz: cz, lx: lx, ly: ly, lz: lz);
    }
    // The coarse cell a fine sample interpolates in (the last cell along an axis for a sample on the lattice's far face)
    // and the sample's place in it, 0 through m_factor along each axis.
    private (int X, int Y, int Z, int LX, int LY, int LZ) FineCell(int x, int y, int z) {
        var factor = m_factor;
        var cx = Math.Min(val1: ((int)m_byFactor.Divide(value: ((uint)x))), val2: (m_samplesX - 2));
        var cy = Math.Min(val1: ((int)m_byFactor.Divide(value: ((uint)y))), val2: (m_samplesY - 2));
        var cz = Math.Min(val1: ((int)m_byFactor.Divide(value: ((uint)z))), val2: (m_samplesZ - 2));

        return (cx, cy, cz, (x - (cx * factor)), (y - (cy * factor)), (z - (cz * factor)));
    }
    // FineSlot by the sample's cell and its place in the cell (FineCell).
    private int FineSlot(int cx, int cy, int cz, int lx, int ly, int lz) {
        if (m_cellBlocks.Length == 0) { return -1; }

        var factor = m_factor;
        var side = (factor + 1);
        var cellsX = (m_samplesX - 1);
        var cellsY = (m_samplesY - 1);
        var primary = m_cellBlocks[(cx + (cellsX * (cy + (cellsY * cz))))];

        if (primary >= 0) { return m_blockSlots[((((primary * side) * side) * side) + (lx + (side * (ly + (side * lz)))))]; }
        if ((lx != 0) && (ly != 0) && (lz != 0)) { return -1; }

        for (var shift = 1; (shift < 8); shift++) {
            var (ax, bx) = (((shift & 1) == 0) ? (cx, lx) : (((lx == 0) && (cx > 0)) ? ((cx - 1), factor) : (-1, 0)));
            var (ay, by) = (((shift & 2) == 0) ? (cy, ly) : (((ly == 0) && (cy > 0)) ? ((cy - 1), factor) : (-1, 0)));
            var (az, bz) = (((shift & 4) == 0) ? (cz, lz) : (((lz == 0) && (cz > 0)) ? ((cz - 1), factor) : (-1, 0)));

            if ((ax < 0) || (ay < 0) || (az < 0)) { continue; }

            var block = m_cellBlocks[(ax + (cellsX * (ay + (cellsY * az))))];

            if (block >= 0) { return m_blockSlots[((((block * side) * side) * side) + (bx + (side * (by + (side * bz)))))]; }
        }

        return -1;
    }
    // A measured fine sample's value by its slot: the value CloseSheets closed it at, or the one measured.
    private float MeasuredValue(int slot) {
        if (m_closedCount == 0) { return m_fineValues[slot]; }

        var closed = m_closedValues[slot];

        return (float.IsNaN(f: closed) ? m_fineValues[slot] : closed);
    }
    // A fine sample's value: the coarse sample's at a coarse position, the measured one in a measured cell, and otherwise
    // the trilinear interpolation of its coarse cell's corners.
    private float FineValue(int x, int y, int z) {
        var factor = m_factor;

        var (cx, cy, cz, lx, ly, lz) = FineCell(x: x, y: y, z: z);

        // A coarse position: every place in the cell is 0 or the far face's m_factor.
        if (((lx == 0) || (lx == factor)) && ((ly == 0) || (ly == factor)) && ((lz == 0) || (lz == factor))) {
            return Values[Index(x: (x / factor), y: (y / factor), z: (z / factor))];
        }

        if (FineSlot(cx: cx, cy: cy, cz: cz, lx: lx, ly: ly, lz: lz) is var slot and >= 0) { return MeasuredValue(slot: slot); }

        var (_, _, _, u, v, w) = Interpolant(x: x, y: y, z: z);
        var sum = 0f;

        for (var corner = 0; (corner < 8); corner++) {
            var dx = corner & 1;
            var dy = (corner >> 1) & 1;
            var dz = (corner >> 2) & 1;
            var weight = ((((dx == 1) ? u : (1f - u)) * ((dy == 1) ? v : (1f - v))) * ((dz == 1) ? w : (1f - w)));

            sum += (weight * Values[Index(x: (cx + dx), y: (cy + dy), z: (cz + dz))]);
        }

        return sum;
    }
    // Whether a fine sample lies inside: FineValue's sign, kept per fine key once read, since the contour's corner masks
    // and CloseSheets' line scans read each sample's sign many times. AnswerRefinement clears the signs and CloseSample
    // keeps a closed sample's.
    private bool FineInside(int x, int y, int z) => FineInside(key: (x + ((((m_samplesX - 1) * m_factor) + 1) * (y + ((((m_samplesY - 1) * m_factor) + 1) * z)))));
    private bool FineInside(int key) {
        if (m_factor == 1) { return (Values[key] < 0f); }
        if (m_signKnown.Length == 0) {
            var count = ((((long)(((m_samplesX - 1) * m_factor) + 1)) * (((m_samplesY - 1) * m_factor) + 1)) * (((m_samplesZ - 1) * m_factor) + 1));
            var words = ((int)((count + 63L) >> 6));

            m_signKnown.Resize(words);
            m_signInside.Resize(words);
        }

        var word = (key >> 6);
        var bit = (1UL << (key & 63));

        if ((m_signKnown[word] & bit) != 0UL) { return ((m_signInside[word] & bit) != 0UL); }

        var (x, y, z) = FineCoordinates(key: key);
        var inside = (FineValue(x: x, y: y, z: z) < 0f);

        m_signKnown[word] |= bit;
        if (inside) { m_signInside[word] |= bit; }
        return inside;
    }
    // Records a fine sample's new value's sign where FineInside has read its sign.
    private void KeepSign(int key, float value) {
        if (m_signKnown.Length == 0) { return; }

        var word = (key >> 6);
        var bit = (1UL << (key & 63));

        m_signKnown[word] |= bit;
        m_signInside[word] = ((value < 0f) ? m_signInside[word] | bit : m_signInside[word] & ~bit);
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
            var dx = corner & 1;
            var dy = (corner >> 1) & 1;
            var dz = (corner >> 2) & 1;
            var weight = ((((dx == 1) ? u : (1f - u)) * ((dy == 1) ? v : (1f - v))) * ((dz == 1) ? w : (1f - w)));

            sum += (m_gradients[Index(x: (cx + dx), y: (cy + dy), z: (cz + dz))] * weight);
        }

        return sum;
    }
    // A fine sample's material: the sample's own where it was measured, else its nearest coarse sample's.
    private int MaterialAt(int key) {
        var factor = m_factor;

        if (factor == 1) {
            return Materials[key];
        }
        var (fx, fy, fz) = FineCoordinates(key: key);

        if (FineSlot(x: fx, y: fy, z: fz) is var slot and >= 0) {
            return m_fineMaterials[slot];
        }

        return Materials[Index(
            x: (((fx * 2) + factor) / (2 * factor)),
            y: (((fy * 2) + factor) / (2 * factor)),
            z: (((fz * 2) + factor) / (2 * factor))
        )];
    }

    // The lattice as the contouring reads it, at the fine spacing once refined: corner values in world units of field,
    // cell coordinates scaled per axis, and a normal trilinearly interpolated from the gradients of the eight samples
    // around a point.
    private readonly struct Lattice(SdfSampledLattice owner) : ISdfRefinedContourLattice {
        public double Crossing(int x, int y, int z, int axis, int length) => owner.Crossing(axis: axis, length: length, x: x, y: y, z: z);

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
        public ReadOnlySpan<SdfCrossedCell> CrossedFineCells => owner.m_crossedFineCells.GetSpan();

        public bool Refined(int x, int y, int z) => owner.m_refined[(x + ((owner.m_samplesX - 1) * (y + ((owner.m_samplesY - 1) * z))))];
        public bool Measured(int x, int y, int z) => owner.m_measured[(x + ((owner.m_samplesX - 1) * (y + ((owner.m_samplesY - 1) * z))))];
        public bool CoarseInside(int x, int y, int z) => (owner.Values[owner.Index(x: x, y: y, z: z)] < 0f);
        public int Corner(int x, int y, int z) => (x + ((CellsX + 1) * (y + ((CellsY + 1) * z))));
        public bool Inside(int corner) => owner.FineInside(key: corner);
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
            var end = (start + ((axis == 0) ? length : ((axis == 1) ? (length * (CellsX + 1)) : ((length * (CellsX + 1)) * (CellsY + 1)))));
            // A crossing beside a closed sample lies where the samples put it, a cell's thickness from the sheet's other
            // face (CloseSheets); moved onto the sheet it would cross it.
            var closed = (owner.Closed(key: start) || owner.Closed(key: end));

            if (owner.m_crossingGradients.TryGetValue(key: owner.CrossingKey(axis: axis, length: length, start: start), value: out var measured)) {
                var gradient = new Vector3(x: measured.X, y: measured.Y, z: measured.Z);
                var magnitude = gradient.Length();

                if (magnitude > 1.0e-12f) {
                    var (ex, ey, ez) = (CellExtent ?? (CellSize, CellSize, CellSize));
                    var reach = (length * Math.Max(val1: ex, val2: Math.Max(val1: ey, val2: ez)));

                    gradient /= magnitude;
                    (nx, ny, nz) = (gradient.X, gradient.Y, gradient.Z);
                    // A searched root stays on its edge, and a closed sheet retains its deliberately displaced crossing;
                    // any other crossing's plane moves by its measured value.
                    offset = ((closed || owner.m_crossingRootIndex.ContainsKey(key: owner.CrossingKey(axis: axis, length: length, start: start)))
                        ? 0.0
                        : Math.Clamp(max: reach, min: -reach, value: (measured.W / magnitude)));

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
            var lx = Math.Clamp(max: cellsX, min: 0.0, value: ((x - owner.m_origin.X) / cell.X));
            var ly = Math.Clamp(max: cellsY, min: 0.0, value: ((y - owner.m_origin.Y) / cell.Y));
            var lz = Math.Clamp(max: cellsZ, min: 0.0, value: ((z - owner.m_origin.Z) / cell.Z));
            var x0 = Math.Min(val1: ((int)lx), val2: (cellsX - 1));
            var y0 = Math.Min(val1: ((int)ly), val2: (cellsY - 1));
            var z0 = Math.Min(val1: ((int)lz), val2: (cellsZ - 1));
            var fx = ((float)(lx - x0));
            var fy = ((float)(ly - y0));
            var fz = ((float)(lz - z0));
            var sum = Vector3.Zero;

            for (var corner = 0; (corner < 8); corner++) {
                var dx = corner & 1;
                var dy = (corner >> 1) & 1;
                var dz = (corner >> 2) & 1;
                var weight = ((((dx == 1) ? fx : (1f - fx)) * ((dy == 1) ? fy : (1f - fy))) * ((dz == 1) ? fz : (1f - fz)));

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
