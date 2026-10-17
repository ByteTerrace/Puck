using System.Numerics;

namespace Puck.SignedDistance.Baking;

/// <summary>A contoured surface in world space: a vertex per surface patch of each crossed cell, with its normal, and a
/// quad per sign-changing lattice edge, wound counter-clockwise seen from outside, with the material the field reported at
/// the edge's outside end, whose nearest shape is the one the edge crosses. A quad whose last two vertices are one vertex
/// is a triangle.</summary>
/// <param name="Positions">Three floats per vertex.</param>
/// <param name="Normals">Three floats per vertex: the unit outward normal.</param>
/// <param name="Quads">Four vertex indices per quad.</param>
/// <param name="QuadMaterials">One material per quad.</param>
public sealed record SdfContour(float[] Positions, float[] Normals, int[] Quads, int[] QuadMaterials) {
    /// <summary>Gets the quads.</summary>
    public int QuadCount => (Quads.Length / 4);
    /// <summary>Gets the vertices.</summary>
    public int VertexCount => (Positions.Length / 3);
}
/// <summary>
/// A field sampled on a lattice whose spacing may differ per axis, as a GPU bake reads it back: each sample's value and
/// the material the field reported there, then the gradients the contour reads. <see cref="Contour"/> extracts its
/// surface with the one manifold dual contouring the prototype baker uses, placing each vertex by the samples' gradients,
/// interpolated along each crossing edge, so a sharp edge between two faces stays sharp at any cell size.
/// <para>Where the samples cannot resolve the surface, the lattice refines (<see cref="Refine"/>): a cell crossed by a
/// sheet or gap thinner than a cell along an axis, whether a sample caught it or it passed between two, a cell whose
/// outside corners report different materials, and a cell two separate sheets cross are sampled again at a finer spacing
/// and contoured at it, with the cells around them contoured at the fine spacing from their coarse corners so the mesh
/// stays closed where its resolution changes. Every other cell contours whole.</para>
/// <para>The lattice is answered in stages: <see cref="Refine"/> names the fine samples to measure and
/// <see cref="AnswerRefinement"/> takes their values; <see cref="GradientPoints"/> names the samples the contour reads a
/// gradient at and <see cref="AnswerGradients"/> takes them. Unlike a prototype bake, a sampled lattice's values come
/// from the GPU interpreter in floats, so its surface is presentation data that may differ between devices; nothing in it
/// reaches simulation state.</para>
/// </summary>
public sealed class SdfSampledLattice {
    // A refinement that would measure more fine samples than this is not taken: the lattice contours at its own spacing.
    private const int MaxFineSamples = 4_000_000;

    private readonly Vector3 m_origin;
    private readonly Vector3 m_cell;
    private readonly int m_samplesX;
    private readonly int m_samplesY;
    private readonly int m_samplesZ;
    private readonly float[] m_values;
    private readonly int[] m_materials;
    private readonly Func<int, bool>? m_owns;
    private readonly Vector3[] m_gradients;

    private int m_factor = 1;
    private bool[] m_measured = [];
    private bool[] m_refined = [];
    private readonly Dictionary<int, int> m_fineSlots = [];
    private int[] m_fineKeys = [];
    private float[] m_fineValues = [];
    private int[] m_fineMaterials = [];
    private readonly Dictionary<int, Vector3> m_fineGradients = [];
    private int[] m_gradientKeys = [];

    /// <summary>Initializes a new instance of the <see cref="SdfSampledLattice"/> class.</summary>
    /// <param name="origin">The position of sample (0, 0, 0), in the frame the samples were taken in.</param>
    /// <param name="cell">The spacing between samples along each axis, in world units; each positive.</param>
    /// <param name="samplesX">The samples along X, at least two.</param>
    /// <param name="samplesY">The samples along Y, at least two.</param>
    /// <param name="samplesZ">The samples along Z, at least two.</param>
    /// <param name="values">One field value per sample, X fastest: negative inside. The lattice keeps the array.</param>
    /// <param name="materials">One material per sample. The lattice keeps the array.</param>
    /// <param name="owns">Whether a material belongs to the surface being contoured, or <see langword="null"/> when every
    /// material does: a sample reporting a material the surface does not own reads as outside, and a quad whose outside
    /// sample reports one takes its inside sample's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An axis has fewer than two samples, a spacing is not positive, or an array's
    /// length does not match the lattice.</exception>
    public SdfSampledLattice(Vector3 origin, Vector3 cell, int samplesX, int samplesY, int samplesZ, float[] values, int[] materials, Func<int, bool>? owns = null) {
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
        m_owns = owns;
        m_gradients = new Vector3[count];

        for (var index = 0; (index < count); index++) {
            m_values[index] = Owned(material: materials[index], value: values[index]);
        }
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
    /// values fall steeply toward each other from both sides and meet below the surface), a crossed cell whose outside
    /// corners report different owned materials, and a crossed cell whose corner signs hold more than one surface patch.
    /// </summary>
    /// <param name="factor">The fine cells along each axis of a coarse cell, at least two.</param>
    /// <returns>The fine samples to measure, in the lattice's frame, in the order <see cref="AnswerRefinement"/> takes
    /// their answers; empty when no cell needs refining or the refinement would measure too many, and the lattice then
    /// contours at its own spacing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is less than two.</exception>
    /// <exception cref="InvalidOperationException">The lattice is already refined.</exception>
    public Vector3[] Refine(int factor) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: factor, other: 2);

        if (m_factor != 1) {
            throw new InvalidOperationException(message: "The lattice is already refined.");
        }

        var cellsX = (m_samplesX - 1);
        var cellsY = (m_samplesY - 1);
        var cellsZ = (m_samplesZ - 1);
        var measured = new bool[((cellsX * cellsY) * cellsZ)];
        int[] samples = [m_samplesX, m_samplesY, m_samplesZ];
        int[] cells = [cellsX, cellsY, cellsZ];
        float[] spacing = [m_cell.X, m_cell.Y, m_cell.Z];
        var at = new int[3];
        var any = false;

        int CellIndex(int x, int y, int z) => (x + (cellsX * (y + (cellsY * z))));
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
                    any = true;
                }
            }
        }

        // Crossed cells: more than one surface patch, or outside corners that disagree on the owned material.
        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    var mask = 0;
                    var material = int.MinValue;
                    var mixed = false;

                    for (var offset = 0; (offset < 8); offset++) {
                        var index = Index(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)));

                        if (m_values[index] < 0f) {
                            mask |= (1 << offset);
                        } else if (Owns(material: m_materials[index])) {
                            mixed |= ((material != int.MinValue) && (material != m_materials[index]));
                            material = m_materials[index];
                        }
                    }

                    if ((mask == 0) || (mask == 0xFF)) {
                        continue;
                    }
                    if (mixed || (SdfDualContouring.PatchesOf(mask: mask) > 1)) {
                        measured[CellIndex(x: x, y: y, z: z)] = true;
                        any = true;
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

                        // A run of one sample between two of the other sign: a sheet or gap the lattice caught once.
                        if (((before < 0f) != inside) && ((after < 0f) != inside)) {
                            var middle = MathF.Abs(value);
                            var thickness = ((middle / (MathF.Abs(before) + middle)) + (middle / (middle + MathF.Abs(after))));

                            if (thickness < 1f) {
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

                        var a = MathF.Abs(value);
                        var b = MathF.Abs(after);
                        var fallA = (MathF.Abs(before) - a);
                        var fallB = (MathF.Abs(further) - b);

                        if ((fallA <= (0.5f * h)) || (fallB <= (0.5f * h))) {
                            continue;
                        }

                        var t = Math.Clamp(value: (((a - b) + fallB) / (fallA + fallB)), min: 0f, max: 1f);

                        if ((a - (fallA * t)) < (0.1f * h)) {
                            at[axis] = i;
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

        // The fine samples of every measured cell, its faces included, that are not coarse samples.
        var fineX = ((cellsX * factor) + 1);
        var fineY = ((cellsY * factor) + 1);
        var keys = new List<int>();
        var slots = new Dictionary<int, int>();

        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
                    if (!measured[CellIndex(x: x, y: y, z: z)]) {
                        continue;
                    }
                    for (var fz = (z * factor); (fz <= ((z + 1) * factor)); fz++) {
                        for (var fy = (y * factor); (fy <= ((y + 1) * factor)); fy++) {
                            for (var fx = (x * factor); (fx <= ((x + 1) * factor)); fx++) {
                                if (((fx % factor) == 0) && ((fy % factor) == 0) && ((fz % factor) == 0)) {
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
                        return [];
                    }
                }
            }
        }

        m_factor = factor;
        m_measured = measured;
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
            m_fineValues[(first + index)] = Owned(material: materials[index], value: values[index]);
            m_fineMaterials[(first + index)] = materials[index];
        }
    }
    /// <summary>Returns the samples the contour reads a gradient at: the corners of every crossed cell that contours whole,
    /// the coarse corners of every refined cell, whose gradients its unmeasured fine samples interpolate, and the measured
    /// corners of every crossed fine cell. Call it once the refinement, if any, is answered.</summary>
    /// <returns>The samples, in the lattice's frame, in the order <see cref="AnswerGradients"/> takes their
    /// answers.</returns>
    public Vector3[] GradientPoints() {
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

        var points = new Vector3[keys.Count];
        var fine = ContourCell;

        for (var index = 0; (index < keys.Count); index++) {
            var (fx, fy, fz) = FineCoordinates(key: keys[index]);

            points[index] = (m_origin + (new Vector3(x: fx, y: fy, z: fz) * fine));
        }

        return points;
    }
    /// <summary>Answers a run of the samples <see cref="GradientPoints"/> named with their gradients.</summary>
    /// <param name="first">The first sample the run answers, in <see cref="GradientPoints"/>'s order.</param>
    /// <param name="gradients">Three components per sample, in any scale consistent across the lattice.</param>
    /// <exception cref="ArgumentException">The run is not whole samples or reaches past the samples.</exception>
    public void AnswerGradients(int first, ReadOnlySpan<float> gradients) {
        if (((gradients.Length % 3) != 0) || (first < 0) || ((first + (gradients.Length / 3)) > m_gradientKeys.Length)) {
            throw new ArgumentException(message: "A gradient answer holds three components per sample it names.");
        }

        var factor = m_factor;
        var fineX = (((m_samplesX - 1) * factor) + 1);
        var fineY = (((m_samplesY - 1) * factor) + 1);

        for (var index = 0; (index < (gradients.Length / 3)); index++) {
            var key = m_gradientKeys[(first + index)];
            var gradient = new Vector3(x: gradients[(3 * index)], y: gradients[((3 * index) + 1)], z: gradients[((3 * index) + 2)]);
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
    /// the lattice's own everywhere else.</summary>
    /// <returns>The surface; empty when no lattice edge changes sign.</returns>
    public SdfContour Contour() {
        var surface = ((m_factor > 1)
            ? SdfDualContouring.ExtractRefined(grid: new Lattice(owner: this))
            : SdfDualContouring.Extract(grid: new Lattice(owner: this)));
        var materials = new int[surface.QuadCount];

        for (var quad = 0; (quad < materials.Length); quad++) {
            var outside = MaterialAt(key: surface.QuadCorners[quad]);

            materials[quad] = (Owns(material: outside) ? outside : MaterialAt(key: surface.QuadInsideCorners[quad]));
        }

        return new SdfContour(
            Normals: [.. surface.Normals.Select(selector: static value => ((float)value))],
            Positions: [.. surface.Positions.Select(selector: static value => ((float)value))],
            QuadMaterials: materials,
            Quads: surface.Quads
        );
    }

    private bool Owns(int material) => ((m_owns is null) || m_owns(arg: material));
    // A sample whose material the surface does not own reads as outside.
    private float Owned(int material, float value) => (Owns(material: material) ? value : MathF.Abs(value));
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
            return m_fineValues[slot];
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
