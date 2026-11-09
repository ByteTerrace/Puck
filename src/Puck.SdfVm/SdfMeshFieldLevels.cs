using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>How a group's field levels are made and measured (<see cref="SdfMeshFieldLevels"/>).</summary>
public sealed record SdfMeshFieldLevelOptions {
    /// <summary>Gets the share of a level's cell the level is offset outside the surface by: its budget.</summary>
    public float OffsetShare { get; init; } = 0.5f;
    /// <summary>Gets the most samples one level's lattice may hold; a level past it is skipped and reported.</summary>
    public long MostSamples { get; init; } = 16_777_216L;
    /// <summary>Gets the most grid steps along a triangle's longest edge its witnesses are spaced at.</summary>
    public int GridMost { get; init; } = 32;
    /// <summary>Gets the share of the group's finest cell the oracle bisects a bracket to: its tolerance τ.</summary>
    public float ToleranceShare { get; init; } = 0.0625f;
    /// <summary>Gets a value indicating whether a level's offset is aimed by each sample's own slope rather than its
    /// lattice's median slope (<see cref="SdfMeshPathBake.ContourLevel"/>).</summary>
    public bool LocalAim { get; init; } = true;
    /// <summary>Gets the share of a level's cell a vertex of a near-coplanar region may move onto the region's plane so
    /// the planar merge takes the region (<see cref="SdfMeshPlanarMerge.Flatten"/>).</summary>
    public float FlattenShare { get; init; } = 0.0625f;
}

/// <summary>Runs of level-zero triangles a level misrepresents, connected over level zero's shared edges.</summary>
/// <param name="Count">The runs.</param>
/// <param name="Triangles">The level-zero triangles in them.</param>
/// <param name="Largest">The largest run's half box diagonal, in metres: the error it contributes.</param>
public readonly record struct SdfMeshLevelRuns(int Count, int Triangles, float Largest);

/// <summary>
/// One level measured by the error contract (<see cref="SdfMeshFieldLevels"/>): both geometric directions as bracketed
/// upper bounds in world units, the certified Lipschitz lower bound beside the mesh-to-surface one, the enclosure check,
/// material and normal runs, vanished components, and the error they record together.
/// </summary>
/// <param name="MeshToSurface">The upper bounds from the level's witnesses to the field's surface.</param>
/// <param name="LowerMax">The largest certified lower bound (|value| times the step scale) over the same witnesses.</param>
/// <param name="LowerOverUpper">The witnesses whose lower bound exceeds their upper bound: a bracket defect, zero when
/// the oracle is sound.</param>
/// <param name="Refused">The level's witnesses that found no crossing within twice the budget.</param>
/// <param name="Interior">The witnesses left out as lying on an interior surface, inside the group's field by more than
/// the tolerance.</param>
/// <param name="SurfaceToMesh">The distances from level zero's surface witnesses to the level, vanished components
/// left out.</param>
/// <param name="Outside">The surface witnesses outside the level by more than the tolerance.</param>
/// <param name="OutsideMax">The farthest of them outside, in metres.</param>
/// <param name="Material">The runs whose witnesses lie farther than the geometric error from a triangle of their own
/// material.</param>
/// <param name="Normal">The runs whose witnesses' normals turn past the widest collapse angle from the interpolated
/// normal of every level triangle within the geometric error, at its point nearest the witness.</param>
/// <param name="NormalNearest">The runs whose witnesses' normals turn past that angle from the nearest triangle's alone:
/// printed beside, recorded nowhere.</param>
/// <param name="Vanished">The runs of level zero the level leaves: components no level triangle lies near, and the parts
/// nearest a component the level vanished (<see cref="SdfMeshFieldLevels.Contour"/>), each run's extent its error.</param>
/// <param name="Recorded">The error recorded: the largest component, raised to the finer level's.</param>
public sealed record SdfMeshLevelError(
    SdfMeshAuditStatistic MeshToSurface,
    float LowerMax,
    int LowerOverUpper,
    int Refused,
    int Interior,
    SdfMeshAuditStatistic SurfaceToMesh,
    int Outside,
    float OutsideMax,
    SdfMeshLevelRuns Material,
    SdfMeshLevelRuns Normal,
    SdfMeshLevelRuns NormalNearest,
    SdfMeshLevelRuns Vanished,
    float Recorded
) {
    /// <summary>Gets the geometric error: the larger of the two directions' largest bounds.</summary>
    public float Geometric => MathF.Max(x: MeshToSurface.Max, y: SurfaceToMesh.Max);
    /// <summary>Gets each level-zero triangle marked where it lies in a vanished run: surface the level accounts for by
    /// the run's extent rather than by a triangle near it.</summary>
    public IReadOnlyList<bool> VanishedTriangles { get; init; } = [];
}

/// <summary>One contoured field level of a group (<see cref="SdfMeshFieldLevels.Contour"/>).</summary>
/// <param name="Level">The level, <c>k</c> for a cell of <c>2^k</c> times level zero's.</param>
/// <param name="Cell">The level's lattice cell, in metres.</param>
/// <param name="Offset">How far outside the surface the level is contoured, in metres: its budget.</param>
/// <param name="Mesh">The level's kept triangles (pieces joined), or <see langword="null"/> when its contour found no
/// surface, every component vanished, or it was skipped.</param>
/// <param name="Whole">The level as contoured, before components vanished, or <see langword="null"/>.</param>
/// <param name="Vanished">Each triangle of <paramref name="Whole"/> marked where it vanished, or <see langword="null"/>.</param>
/// <param name="VanishedComponents">The components that vanished.</param>
/// <param name="Members">Each vertex of <paramref name="Mesh"/>'s member, or <see langword="null"/> for a level of the
/// whole field.</param>
/// <param name="Skipped">Why the level was not contoured or not kept, or <see langword="null"/>.</param>
public sealed record SdfMeshFieldContour(int Level, float Cell, float Offset, SdfMesh? Mesh, SdfMesh? Whole, bool[]? Vanished, int VanishedComponents, byte[]? Members, string? Skipped) {
    /// <summary>Gets the level's kept triangles.</summary>
    public int Triangles => (Mesh?.TriangleCount ?? 0);
    /// <summary>Gets a value indicating whether the level ends the chain: its contour found no surface, or every
    /// component vanished.</summary>
    public bool Empty => ((Mesh is null) && (Skipped is null));
}

/// <summary>
/// The field levels of one group that follow its collapse levels: each contoured again from the group's field on a
/// lattice of twice the previous cell (<see cref="SdfMeshPathBake.ContourLevel"/>), offset outward by its budget, per
/// member for a group whose shapes ride several slots, its near-coplanar faces merged and its components that would flip
/// or open an edge vanished (<see cref="Contour"/>), from the coarsest level down to the first holding more than the
/// kept share of the level it follows; and the oracle that measures any level, a collapse level included, by the error
/// contract. Offline and load-independent: it reads fields and meshes and writes nothing.
/// <para>The invariant the oracle measures: a level's recorded error bounds, in world units, the distance from each of
/// its witnesses to the field's surface and from each surface witness to the level, encloses the surface, covers every
/// material or normal region and every part it loses, and is at least the finer level's. <see cref="Measure"/> is the
/// one place it is computed; the guarantee stops at the witness spacing, the bracket tolerance and features thinner
/// than level zero's own sampling.</para>
/// </summary>
public sealed class SdfMeshFieldLevels {
    // The probe step a level's facing is judged at: the audit's, so a component the level keeps flips no more for the
    // audit than for this.
    private static readonly float FacingStep = new SdfMeshAuditOptions().GradientStep;

    private readonly ISdfMeshLevelField m_field;
    private readonly SdfMesh m_reference;
    private readonly float m_cell;
    private readonly float m_tolerance;
    private readonly float m_normalCosine;
    private readonly SdfMeshFieldLevelOptions m_options;
    // Level zero's surface witnesses, bracketed onto the field's surface once: the point, the level-zero triangle it
    // belongs to, its material and its unit normal.
    private readonly Vector3[] m_surface;
    private readonly int[] m_owner;
    private readonly int[] m_material;
    private readonly Vector3[] m_normal;
    // Level zero's triangle adjacency over welded positions, and each triangle's component.
    private readonly int[][] m_neighbours;
    private readonly int[] m_component;
    private readonly int m_components;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshFieldLevels"/> class for one group: level zero's
    /// surface witnesses (its vertices, its centroids and a grid over each triangle at twice its cell) bracketed onto the
    /// field's surface, and its adjacency.</summary>
    /// <param name="field">The group's whole field.</param>
    /// <param name="reference">Level zero: the group's merged mesh, joined when chunked.</param>
    /// <param name="cell">Level zero's contour cell, in metres; positive.</param>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/>, <paramref name="reference"/> or
    /// <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> is not positive.</exception>
    public SdfMeshFieldLevels(ISdfMeshLevelField field, SdfMesh reference, float cell, SdfMeshFieldLevelOptions options) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentNullException.ThrowIfNull(argument: reference);
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: cell);

        m_field = field;
        m_reference = reference;
        m_cell = cell;
        m_options = options;
        m_tolerance = (options.ToleranceShare * cell);
        m_normalCosine = MathF.Cos(x: float.DegreesToRadians(degrees: SdfMeshCollapse.NormalDegrees[^1]));

        var positions = reference.Positions.Span;
        var indices = reference.Indices.Span;
        var normals = reference.Normals.Span;
        var triangles = reference.TriangleCount;
        var points = new List<Vector3>();
        var owners = new List<int>();
        var witnessNormals = new List<Vector3>();
        var seen = new bool[positions.Length];

        for (var triangle = 0; (triangle < triangles); triangle++) {
            var (a, b, c) = Corners(indices: indices, triangle: triangle);
            var (pa, pb, pc) = (positions[a], positions[b], positions[c]);
            var (na, nb, nc) = (normals.IsEmpty ? (Face(pa, pb, pc), Face(pa, pb, pc), Face(pa, pb, pc)) : (normals[a], normals[b], normals[c]));

            foreach (var vertex in (ReadOnlySpan<int>)[a, b, c]) {
                if (seen[vertex]) { continue; }
                seen[vertex] = true;
                points.Add(item: positions[vertex]);
                owners.Add(item: triangle);
                witnessNormals.Add(item: (normals.IsEmpty ? Face(pa, pb, pc) : normals[vertex]));
            }
            foreach (var (u, v) in Grid(a: pa, b: pb, c: pc, spacing: (2f * cell), most: options.GridMost, centroid: true)) {
                points.Add(item: ((pa + ((pb - pa) * u)) + ((pc - pa) * v)));
                owners.Add(item: triangle);
                witnessNormals.Add(item: (((na * ((1f - u) - v)) + (nb * u)) + (nc * v)));
            }
        }

        var bracket = Bracket(field: field, normals: [.. witnessNormals.Select(selector: Unit)], points: [.. points], reach: (4f * cell), step: cell);
        var kept = new List<int>();
        var ratio = 0f;
        var ratioWitness = (At: Vector3.Zero, Value: 0f, Upper: 0f);

        for (var witness = 0; (witness < points.Count); witness++) {
            if (!bracket.Found[witness]) { continue; }
            kept.Add(item: witness);

            var lowerOverUpper = ((bracket.Upper[witness] > 0f) ? ((MathF.Abs(x: bracket.Value[witness]) * field.StepScale) / bracket.Upper[witness]) : 0f);

            if (lowerOverUpper > ratio) { (ratio, ratioWitness) = (lowerOverUpper, (points[witness], bracket.Value[witness], bracket.Upper[witness])); }
        }
        SurfaceRefused = (points.Count - kept.Count);
        LowerOverUpper = ratio;
        LowerOverUpperWitness = ratioWitness;
        Certified = ((field.StepScale > 0f) && (ratio <= 1f));
        m_surface = [.. kept.Select(selector: witness => bracket.Surface[witness])];
        m_owner = [.. kept.Select(selector: witness => owners[witness])];
        m_material = [.. m_owner.Select(selector: triangle => (reference.TriangleMaterials.IsEmpty ? 0 : (int)reference.TriangleMaterials.Span[triangle]))];
        m_normal = [.. kept.Select(selector: witness => Unit(vector: witnessNormals[witness]))];

        (m_neighbours, m_component, m_components, _) = Adjacency(positions: positions, indices: indices, triangles: triangles);
        HalfDiagonal = (0.5f * Vector3.Distance(value1: reference.PositionLow, value2: reference.PositionHigh));
    }

    /// <summary>Gets the group's half box diagonal, in metres: the terminal empty level's error.</summary>
    public float HalfDiagonal { get; }
    /// <summary>Gets the level-zero surface witnesses that found no crossing within four cells and are left out.</summary>
    public int SurfaceRefused { get; }
    /// <summary>Gets the surface witnesses measured.</summary>
    public int SurfaceWitnesses => m_surface.Length;
    /// <summary>Gets the largest ratio, over level zero's bracketed witnesses, of the Lipschitz lower bound (|value| times
    /// the field's step scale) to the bracketed upper bound: above one, the step scale certifies nothing for this
    /// field.</summary>
    public float LowerOverUpper { get; }
    /// <summary>Gets the level-zero witness where <see cref="LowerOverUpper"/> was read, so a failing step scale is traced
    /// to the shape there: its position in the group's rest frame, the field's value there and the bracketed upper bound
    /// on its distance to the surface, all in metres.</summary>
    public (Vector3 At, float Value, float Upper) LowerOverUpperWitness { get; }
    /// <summary>Gets a value indicating whether the field's step scale held as a lower bound at every level-zero witness,
    /// so a closed group's finer levels may skip the samples a coarser one certifies far from the surface; otherwise every
    /// sample is answered.</summary>
    public bool Certified { get; }

    /// <summary>Contours a group's field levels below the level they follow: levels of cell <c>2^k</c> times level
    /// zero's while the cell is no wider than the group's longest side, coarsest first (so each finer level skips what
    /// the coarser one certifies far from its surface), each contoured from <paramref name="pieces"/> (or the whole field
    /// when there are none) and joined, its near-coplanar faces flattened and merged, and its components that would flip
    /// or open an edge vanished (<see cref="Vanish"/>). The first level holding more than <paramref name="most"/>
    /// triangles ends the contouring, since every finer level holds more: the levels a bake appends below the collapse's
    /// coarsest are the coarser ones.</summary>
    /// <param name="pieces">Each member's own field and the member its vertices ride, for a group whose shapes ride
    /// several slots; empty for a closed group, which contours its whole field.</param>
    /// <param name="preserveFeatures">Whether the group holds glyphs.</param>
    /// <param name="most">The most triangles a level may hold: the kept share of the level it follows.</param>
    /// <param name="allowance">The flipped triangles and open edges level zero holds, which a level may hold too.</param>
    /// <returns>Each level, finest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pieces"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SdfMeshFieldContour> Contour(IReadOnlyList<(ISdfMeshLevelField Field, byte Member)> pieces, bool preserveFeatures, int most, (int Flips, int OpenEdges) allowance) {
        ArgumentNullException.ThrowIfNull(argument: pieces);

        var low = m_reference.PositionLow;
        var high = m_reference.PositionHigh;
        var longest = MathF.Max(x: (high.X - low.X), y: MathF.Max(x: (high.Y - low.Y), y: (high.Z - low.Z)));
        var count = 0;

        while ((m_cell * MathF.Pow(x: 2f, y: (count + 1))) <= longest) { count++; }

        var fields = ((pieces.Count == 0) ? [(m_field, SdfMesh.NoMember)] : pieces);
        var made = new SdfMeshFieldContour[count];
        var coarser = new SdfMeshLevelSamples?[fields.Count];
        // A member's part reaches past the whole group's box where another member carved or covered it, so each part
        // takes its own box, read from its field on a lattice of the coarsest level's cell over the group's box widened by
        // half its longest side.
        var boxes = fields.Select(selector: piece => ((pieces.Count == 0) ? (low, high) : PieceBox(cell: (m_cell * MathF.Pow(x: 2f, y: Math.Max(val1: 1, val2: count))), field: piece.Field, high: (high + new Vector3(value: (0.5f * longest))), low: (low - new Vector3(value: (0.5f * longest)))))).ToArray();
        string? ended = null;

        for (var level = count; (level >= 1); level--) {
            var cell = (m_cell * MathF.Pow(x: 2f, y: level));
            var offset = (m_options.OffsetShare * cell);

            if (ended is not null) {
                made[(level - 1)] = new SdfMeshFieldContour(Cell: cell, Level: level, Members: null, Mesh: null, Offset: offset, Skipped: ended, Vanished: null, VanishedComponents: 0, Whole: null);
                continue;
            }

            var meshes = new List<(SdfMesh Mesh, byte Member)>();
            var skipped = false;

            for (var piece = 0; (piece < fields.Count); piece++) {
                // Only a closed group's whole field is checked against its step scale; a member's part is answered whole.
                var mesh = SdfMeshPathBake.ContourLevel(cell: cell, coarser: ((Certified && (pieces.Count == 0)) ? coarser[piece] : null), field: fields[piece].Field, flatten: (m_options.FlattenShare * cell), high: boxes[piece].Item2, localAim: m_options.LocalAim, low: boxes[piece].Item1, mostSamples: m_options.MostSamples, offset: offset, preserveFeatures: preserveFeatures, samples: out var samples);

                skipped |= (samples is null);
                coarser[piece] = samples;
                if (mesh is not null) { meshes.Add(item: (mesh, fields[piece].Member)); }
            }
            if (skipped) {
                made[(level - 1)] = new SdfMeshFieldContour(Cell: cell, Level: level, Members: null, Mesh: null, Offset: offset, Skipped: $"lattice past {m_options.MostSamples} samples", Vanished: null, VanishedComponents: 0, Whole: null);
                continue;
            }
            if (meshes.Count == 0) {
                made[(level - 1)] = new SdfMeshFieldContour(Cell: cell, Level: level, Members: null, Mesh: null, Offset: offset, Skipped: null, Vanished: null, VanishedComponents: 0, Whole: null);
                continue;
            }

            var (whole, members) = Join(members: (pieces.Count > 0), pieces: meshes);

            if (whole.TriangleCount > most) {
                made[(level - 1)] = new SdfMeshFieldContour(Cell: cell, Level: level, Members: null, Mesh: null, Offset: offset, Skipped: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{whole.TriangleCount} triangles keep more than {SdfMeshCollapse.KeptShare} of the level it follows"), Vanished: null, VanishedComponents: 0, Whole: null);
                ended = "finer than a level past the kept share, not contoured";
                continue;
            }

            var (kept, keptMembers, vanished, components) = Vanish(allowance: allowance, members: members, pieces: pieces, whole: whole);

            made[(level - 1)] = new SdfMeshFieldContour(Cell: cell, Level: level, Members: keptMembers, Mesh: kept, Offset: offset, Skipped: null, Vanished: vanished, VanishedComponents: components, Whole: whole);
        }

        return made;
    }
    /// <summary>Vanishes a contoured level's components that would flip or open an edge: a component holding a triangle
    /// that faces against its piece's field (<see cref="SdfMeshAudit.Flips"/>, judged at its centroid by the bake's tap
    /// stencil) or an edge one triangle alone holds, smallest box first, until the level holds no more flipped triangles
    /// and open edges than level zero does. A vanished component draws nothing, and the level's measurement records each
    /// part of level zero it leaves by that part's own extent (<see cref="Measure"/>).</summary>
    /// <param name="whole">The level as contoured.</param>
    /// <param name="members">Each vertex's member, or <see langword="null"/> for a level of the whole field.</param>
    /// <param name="pieces">Each member's own field; empty for a level of the whole field.</param>
    /// <param name="allowance">The flipped triangles and open edges level zero holds.</param>
    /// <returns>The level's kept triangles as a mesh of their own (<see langword="null"/> when every triangle vanished),
    /// each kept vertex's member, each triangle of <paramref name="whole"/> marked where it vanished, and the components
    /// vanished.</returns>
    private (SdfMesh? Kept, byte[]? Members, bool[] Vanished, int Components) Vanish(SdfMesh whole, byte[]? members, IReadOnlyList<(ISdfMeshLevelField Field, byte Member)> pieces, (int Flips, int OpenEdges) allowance) {
        var positions = whole.Positions.Span;
        var indices = whole.Indices.Span;
        var triangles = whole.TriangleCount;
        var faces = new Vector3[triangles];
        var gradients = new Vector3[triangles];
        var byPiece = new Dictionary<int, List<int>>();
        var pieceOf = new Dictionary<byte, int>();

        for (var index = 0; (index < pieces.Count); index++) { _ = pieceOf.TryAdd(key: pieces[index].Member, value: index); }
        for (var triangle = 0; (triangle < triangles); triangle++) {
            var (a, b, c) = Corners(indices: indices, triangle: triangle);
            var piece = (((members is not null) && pieceOf.TryGetValue(key: members[a], value: out var own)) ? own : 0);

            faces[triangle] = SdfMeshAudit.FaceNormal(a: positions[a], b: positions[b], c: positions[c]);
            if (!byPiece.TryGetValue(key: piece, value: out var list)) {
                list = [];
                byPiece.Add(key: piece, value: list);
            }
            list.Add(item: triangle);
        }
        foreach (var (piece, list) in byPiece) {
            var field = ((pieces.Count == 0) ? m_field : pieces[piece].Field);
            var centroids = list.Select(selector: triangle => {
                var (a, b, c) = Corners(indices: whole.Indices.Span, triangle: triangle);

                return (((whole.Positions.Span[a] + whole.Positions.Span[b]) + whole.Positions.Span[c]) / 3f);
            }).ToArray();
            var values = new float[centroids.Length];
            var slopes = new Vector3[centroids.Length];

            field.Gradients(gradients: slopes, points: centroids, spacing: FacingStep, values: values, work: SdfMeshLevelWork.Contour);
            for (var at = 0; (at < list.Count); at++) { gradients[list[at]] = Unit(vector: slopes[at]); }
        }

        var winding = SdfMeshAudit.Winding(faceNormals: faces, gradients: gradients);
        var (_, component, components, open) = Adjacency(indices: indices, positions: positions, triangles: triangles);
        var flips = new int[components];
        var opens = new int[components];
        var lows = Enumerable.Repeat(element: new Vector3(value: float.MaxValue), count: components).ToArray();
        var highs = Enumerable.Repeat(element: new Vector3(value: float.MinValue), count: components).ToArray();
        var flipped = 0;
        var opened = 0;

        for (var triangle = 0; (triangle < triangles); triangle++) {
            var (a, b, c) = Corners(indices: indices, triangle: triangle);
            var own = component[triangle];
            var flip = (SdfMeshAudit.Flips(faceNormal: faces[triangle], gradient: gradients[triangle], winding: winding) ? 1 : 0);

            flips[own] += flip;
            opens[own] += open[triangle];
            flipped += flip;
            opened += open[triangle];
            lows[own] = Vector3.Min(value1: lows[own], value2: Vector3.Min(value1: positions[a], value2: Vector3.Min(value1: positions[b], value2: positions[c])));
            highs[own] = Vector3.Max(value1: highs[own], value2: Vector3.Max(value1: positions[a], value2: Vector3.Max(value1: positions[b], value2: positions[c])));
        }

        var gone = new bool[components];
        var vanishedComponents = 0;

        while ((flipped > allowance.Flips) || (opened > allowance.OpenEdges)) {
            var pick = -1;

            for (var candidate = 0; (candidate < components); candidate++) {
                if (gone[candidate] || !(((flipped > allowance.Flips) && (flips[candidate] > 0)) || ((opened > allowance.OpenEdges) && (opens[candidate] > 0)))) { continue; }
                if ((pick < 0) || (Vector3.DistanceSquared(value1: lows[candidate], value2: highs[candidate]) < Vector3.DistanceSquared(value1: lows[pick], value2: highs[pick]))) { pick = candidate; }
            }
            if (pick < 0) { break; }
            gone[pick] = true;
            vanishedComponents++;
            flipped -= flips[pick];
            opened -= opens[pick];
        }

        var vanished = new bool[triangles];
        var keptIndices = new List<uint>();
        var keptMaterials = new List<uint>();
        var materials = whole.TriangleMaterials.Span;

        for (var triangle = 0; (triangle < triangles); triangle++) {
            vanished[triangle] = gone[component[triangle]];
            if (vanished[triangle]) { continue; }
            keptIndices.Add(item: indices[(3 * triangle)]);
            keptIndices.Add(item: indices[((3 * triangle) + 1)]);
            keptIndices.Add(item: indices[((3 * triangle) + 2)]);
            if (!materials.IsEmpty) { keptMaterials.Add(item: materials[triangle]); }
        }
        if (vanishedComponents == 0) { return (whole, members, vanished, 0); }
        if (keptIndices.Count == 0) { return (null, null, vanished, vanishedComponents); }

        var kept = SdfMeshAudit.Subset(indices: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: keptIndices), materials: keptMaterials.ToArray(), mesh: whole, sources: out var sources);

        return (kept, ((members is null) ? null : [.. sources.Select(selector: vertex => members[vertex])]), vanished, vanishedComponents);
    }

    /// <summary>Measures one level against the field and level zero by the error contract.</summary>
    /// <param name="level">The level's mesh, in the group's frame.</param>
    /// <param name="members">Each vertex's member (an index into <paramref name="pieces"/> by its member), or
    /// <see langword="null"/> for a level of the whole field.</param>
    /// <param name="pieces">Each member's own field, which its vertices' witnesses bracket onto; empty for a level of the
    /// whole field.</param>
    /// <param name="cell">The level's cell, in metres: its witness spacing.</param>
    /// <param name="budget">The level's budget, in metres: its offset, or a collapse level's own recorded error.</param>
    /// <param name="finer">The finer level's recorded error, in metres, which this level's is raised to.</param>
    /// <param name="whole">The level as contoured before <see cref="Vanish"/> took components from it, or
    /// <see langword="null"/> when none vanished.</param>
    /// <param name="vanished">Each triangle of <paramref name="whole"/> marked where it vanished, or
    /// <see langword="null"/>.</param>
    /// <returns>The measurement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> or <paramref name="pieces"/> is
    /// <see langword="null"/>.</exception>
    public SdfMeshLevelError Measure(SdfMesh level, byte[]? members, IReadOnlyList<(ISdfMeshLevelField Field, byte Member)> pieces, float cell, float budget, float finer, SdfMesh? whole = null, bool[]? vanished = null) {
        ArgumentNullException.ThrowIfNull(argument: level);
        ArgumentNullException.ThrowIfNull(argument: pieces);

        budget = MathF.Max(x: budget, y: m_tolerance);

        var (toSurface, lower, lowerOver, refused, interior) = MeshToSurface(budget: budget, cell: cell, level: level, members: members, pieces: pieces);
        using var scene = SdfMeshIrradianceScene.Build(draws: [new SdfMeshDraw(Identity: level, Material: 0, Mesh: level, ObjectToWorld: Matrix4x4.Identity)]);
        var vanishReach = ((2f * budget) + cell);
        var radius = (4f * vanishReach);
        var count = m_surface.Length;
        var distance = new float[count];
        var nearest = new Vector3[count];
        var held = new int[count];
        var excused = new bool[count];
        var positions = level.Positions.Span;
        var indices = level.Indices.Span;

        // A surface witness nearest a vanished triangle of the level as contoured lies on a part the level vanished: it
        // counts in that part's extent, not in a distance to whatever surface remains.
        if ((whole is not null) && (vanished is not null) && vanished.Contains(value: true)) {
            using var contoured = SdfMeshIrradianceScene.Build(draws: [new SdfMeshDraw(Identity: whole, Material: 0, Mesh: whole, ObjectToWorld: Matrix4x4.Identity)]);

            for (var witness = 0; (witness < count); witness++) {
                excused[witness] = (contoured.TryNearestSurface(accept: null, instance: out _, point: m_surface[witness], radius: radius, surface: out _, triangle: out var triangle) && vanished[triangle]);
            }
        }
        for (var witness = 0; (witness < count); witness++) {
            if (excused[witness] || !scene.TryNearestSurface(accept: null, instance: out _, point: m_surface[witness], radius: radius, surface: out var surface, triangle: out var triangle)) {
                distance[witness] = radius;
                held[witness] = -1;
                continue;
            }
            distance[witness] = Vector3.Distance(value1: m_surface[witness], value2: surface);
            nearest[witness] = surface;
            held[witness] = triangle;
        }

        // A component no witness of which lies within reach of the level has vanished; it counts its own extent.
        var covered = new bool[m_components];

        for (var witness = 0; (witness < count); witness++) {
            if (!excused[witness] && (distance[witness] <= vanishReach)) { covered[m_component[m_owner[witness]]] = true; }
        }

        var gone = new bool[m_reference.TriangleCount];

        for (var triangle = 0; (triangle < gone.Length); triangle++) { gone[triangle] = !covered[m_component[triangle]]; }
        for (var witness = 0; (witness < count); witness++) {
            if (excused[witness]) { gone[m_owner[witness]] = true; }
        }

        bool Counted(int witness) => (!excused[witness] && covered[m_component[m_owner[witness]]]);

        // The enclosure: a counted witness on the outer side of its nearest triangle by more than the tolerance.
        var outside = 0;
        var outsideMax = 0f;

        for (var witness = 0; (witness < count); witness++) {
            if (!Counted(witness: witness) || (held[witness] < 0) || !(distance[witness] > m_tolerance)) { continue; }

            var (a, b, c) = Corners(indices: indices, triangle: held[witness]);

            if (Vector3.Dot(vector1: Face(positions[a], positions[b], positions[c]), vector2: (m_surface[witness] - nearest[witness])) > 0f) {
                outside++;
                outsideMax = MathF.Max(x: outsideMax, y: distance[witness]);
            }
        }

        var vanishedRuns = Runs(marked: gone);
        var kept = Enumerable.Range(start: 0, count: count).Where(predicate: Counted).Select(selector: witness => distance[witness]).ToArray();
        var fromSurface = SdfMeshAudit.Statistic(over: budget, values: kept);
        var geometric = MathF.Max(x: toSurface.Max, y: fromSurface.Max);
        var allowed = MathF.Max(x: geometric, y: budget);
        var materialMarked = new bool[m_reference.TriangleCount];
        var normalMarked = new bool[m_reference.TriangleCount];
        var nearestMarked = new bool[m_reference.TriangleCount];
        var levelMaterials = level.TriangleMaterials;
        var levelNormals = level.Normals;
        var levelPositions = level.Positions;
        var levelIndices = level.Indices;

        Vector3 Interpolated(int triangle, Vector3 point) {
            var corners = levelIndices.Span;
            var at = levelPositions.Span;
            var (a, b, c) = Corners(indices: corners, triangle: triangle);
            var weights = Barycentric(a: at[a], b: at[b], c: at[c], point: point);

            return (levelNormals.IsEmpty ? Face(at[a], at[b], at[c]) : Unit(vector: (((levelNormals.Span[a] * weights.X) + (levelNormals.Span[b] * weights.Y)) + (levelNormals.Span[c] * weights.Z))));
        }

        for (var witness = 0; (witness < count); witness++) {
            var triangle = held[witness];

            if ((triangle < 0) || !Counted(witness: witness)) { continue; }

            var wanted = m_material[witness];
            var own = (levelMaterials.IsEmpty ? 0 : (int)levelMaterials.Span[triangle]);
            var materialDistance = distance[witness];

            if (own != wanted) {
                materialDistance = (scene.TryNearestSurface(accept: (_, other, _) => ((levelMaterials.IsEmpty ? 0 : (int)levelMaterials.Span[other]) == wanted), instance: out _, point: m_surface[witness], radius: radius, surface: out var same, triangle: out _) ? Vector3.Distance(value1: m_surface[witness], value2: same) : radius);
            }
            if (materialDistance > allowed) { materialMarked[m_owner[witness]] = true; }

            // The level carries a witness's normal where some triangle within its geometric error turns, at its point
            // nearest the witness, within the widest collapse angle of it; the nearest triangle alone (kept apart below)
            // misreads every crease the level moves by less than its error.
            var normal = m_normal[witness];

            if (Vector3.Dot(vector1: Interpolated(point: nearest[witness], triangle: triangle), vector2: normal) < m_normalCosine) {
                nearestMarked[m_owner[witness]] = true;
                if (!scene.TryNearestSurface(accept: (_, other, point) => (Vector3.Dot(vector1: Interpolated(point: point, triangle: other), vector2: normal) >= m_normalCosine), instance: out _, point: m_surface[witness], radius: allowed, surface: out _, triangle: out _)) {
                    normalMarked[m_owner[witness]] = true;
                }
            }
        }

        var material = Runs(marked: materialMarked);
        var normalRuns = Runs(marked: normalMarked);
        var recorded = MathF.Max(x: finer, y: MathF.Max(x: geometric, y: MathF.Max(x: material.Largest, y: MathF.Max(x: normalRuns.Largest, y: vanishedRuns.Largest))));

        return new SdfMeshLevelError(
            Interior: interior,
            LowerMax: lower,
            LowerOverUpper: lowerOver,
            Material: material,
            MeshToSurface: toSurface,
            Normal: normalRuns,
            NormalNearest: Runs(marked: nearestMarked),
            Outside: outside,
            OutsideMax: outsideMax,
            Recorded: recorded,
            Refused: refused,
            SurfaceToMesh: fromSurface,
            Vanished: vanishedRuns
        ) { VanishedTriangles = gone };
    }
    // The level's witnesses (vertices, centroids, edge midpoints and a grid at the level's cell over larger triangles),
    // each bracketed onto its piece's field: the upper bounds, the largest certified lower bound, the witnesses whose lower
    // bound passes their upper one, those refused, and those left out as interior.
    private (SdfMeshAuditStatistic Upper, float Lower, int LowerOver, int Refused, int Interior) MeshToSurface(SdfMesh level, byte[]? members, IReadOnlyList<(ISdfMeshLevelField Field, byte Member)> pieces, float cell, float budget) {
        var positions = level.Positions.Span;
        var indices = level.Indices.Span;
        var byPiece = new Dictionary<int, List<(Vector3 Point, Vector3 Normal)>>();
        var pieceOf = new Dictionary<byte, int>();

        for (var index = 0; (index < pieces.Count); index++) { _ = pieceOf.TryAdd(key: pieces[index].Member, value: index); }

        void Add(Vector3 point, Vector3 normal, int vertex) {
            var piece = (((members is not null) && pieceOf.TryGetValue(key: members[vertex], value: out var own)) ? own : 0);

            if (!byPiece.TryGetValue(key: piece, value: out var list)) {
                list = [];
                byPiece.Add(key: piece, value: list);
            }
            list.Add(item: (point, Unit(vector: normal)));
        }

        var vertexNormals = level.Normals;

        Vector3 NormalOf(int vertex, Vector3 face) => (vertexNormals.IsEmpty ? face : vertexNormals.Span[vertex]);

        var edges = new HashSet<(int, int)>();
        var seen = new bool[positions.Length];

        for (var triangle = 0; (triangle < level.TriangleCount); triangle++) {
            var (a, b, c) = Corners(indices: indices, triangle: triangle);
            var face = Face(positions[a], positions[b], positions[c]);
            var (na, nb, nc) = (NormalOf(face: face, vertex: a), NormalOf(face: face, vertex: b), NormalOf(face: face, vertex: c));

            foreach (var vertex in (ReadOnlySpan<int>)[a, b, c]) {
                if (seen[vertex]) { continue; }
                seen[vertex] = true;
                Add(normal: NormalOf(face: face, vertex: vertex), point: positions[vertex], vertex: vertex);
            }
            foreach (var (first, second) in (ReadOnlySpan<(int, int)>)[(a, b), (b, c), (c, a)]) {
                if (edges.Add(item: ((first < second) ? (first, second) : (second, first)))) {
                    Add(normal: (NormalOf(face: face, vertex: first) + NormalOf(face: face, vertex: second)), point: ((positions[first] + positions[second]) * 0.5f), vertex: first);
                }
            }
            foreach (var (u, v) in Grid(a: positions[a], b: positions[b], c: positions[c], spacing: cell, most: m_options.GridMost, centroid: true)) {
                Add(normal: (((na * ((1f - u) - v)) + (nb * u)) + (nc * v)), point: ((positions[a] + ((positions[b] - positions[a]) * u)) + ((positions[c] - positions[a]) * v)), vertex: a);
            }
        }

        var upper = new List<float>();
        var lowerMax = 0f;
        var lowerOver = 0;
        var refused = 0;
        var interior = 0;

        foreach (var (piece, list) in byPiece) {
            var field = ((pieces.Count == 0) ? m_field : pieces[piece].Field);
            var points = list.Select(selector: static entry => entry.Point).ToArray();
            var pointNormals = list.Select(selector: static entry => entry.Normal).ToArray();
            var skip = new bool[points.Length];

            if (pieces.Count > 0) {
                // A piece's face inside another member is an interior surface: the whole field certifies it inside by
                // more than the tolerance.
                var whole = new float[points.Length];

                m_field.Values(materials: new int[points.Length], points: points, values: whole, work: SdfMeshLevelWork.Oracle);
                for (var index = 0; (index < points.Length); index++) {
                    skip[index] = ((-whole[index] * m_field.StepScale) > m_tolerance);
                    interior += (skip[index] ? 1 : 0);
                }
            }

            var bracket = Bracket(field: field, normals: pointNormals, points: points, reach: (2f * budget), step: MathF.Max(x: (0.5f * budget), y: m_tolerance), skip: skip);

            for (var index = 0; (index < points.Length); index++) {
                if (skip[index]) { continue; }
                if (!bracket.Found[index]) {
                    refused++;
                    continue;
                }

                var bound = (MathF.Abs(x: bracket.Value[index]) * field.StepScale);

                upper.Add(item: bracket.Upper[index]);
                lowerMax = MathF.Max(x: lowerMax, y: bound);
                lowerOver += ((bound > (bracket.Upper[index] + 1e-6f)) ? 1 : 0);
            }
        }

        return (SdfMeshAudit.Statistic(over: budget, values: [.. upper]), lowerMax, lowerOver, refused, interior);
    }

    // Each point walked toward the field's surface along its gradient in steps of at most `step`, re-reading the gradient
    // at each, until the field changes sign within `reach`, then bisected to the tolerance; a point whose gradient walk
    // finds no crossing walks again along its mesh normal, inward from outside and outward from inside. The surface lies
    // in the final bracket, so the farther bracket end's distance is an upper bound on the point's distance to the surface
    // whatever path found it and whatever the field's slope. A point is answered in lockstep rounds with the others, one
    // field call a round.
    private Brackets Bracket(ISdfMeshLevelField field, Vector3[] points, Vector3[] normals, float reach, float step, bool[]? skip = null) {
        var count = points.Length;
        var result = new Brackets(Found: new bool[count], Surface: new Vector3[count], Upper: new float[count], Value: new float[count]);
        var inside = new bool[count];
        var current = new Vector3[count];
        var direction = new Vector3[count];
        var walked = new float[count];
        var active = new List<int>();
        var near = new Vector3[count];
        var far = new Vector3[count];
        var bracketed = new List<int>();
        var unresolved = new List<int>();

        var (values, gradients) = GradientsAt(field: field, points: points);

        for (var index = 0; (index < count); index++) {
            result.Value[index] = values[index];
            if ((skip is not null) && skip[index]) { continue; }
            if (values[index] == 0f) {
                result.Found[index] = true;
                result.Surface[index] = points[index];
                continue;
            }
            inside[index] = (values[index] < 0f);
            current[index] = points[index];

            var length = gradients[index].Length();

            if (!(length > 0f) || !float.IsFinite(f: length)) {
                unresolved.Add(item: index);
                continue;
            }
            direction[index] = ((inside[index] ? gradients[index] : -gradients[index]) / length);
            active.Add(item: index);
        }

        void Walk(List<int> walking, bool alongNormal) {
            while (walking.Count > 0) {
                var next = walking.Select(selector: index => (current[index] + (direction[index] * step))).ToArray();
                var (nextValues, nextGradients) = (alongNormal ? (ValuesAt(field: field, points: next), new Vector3[next.Length]) : GradientsAt(field: field, points: next));
                var still = new List<int>();

                for (var at = 0; (at < walking.Count); at++) {
                    var index = walking[at];

                    walked[index] += step;
                    if ((nextValues[at] < 0f) != inside[index]) {
                        near[index] = current[index];
                        far[index] = next[at];
                        bracketed.Add(item: index);
                        continue;
                    }

                    var length = nextGradients[at].Length();

                    if ((walked[index] >= reach) || (!alongNormal && (!(length > 0f) || !float.IsFinite(f: length)))) {
                        if (!alongNormal) { unresolved.Add(item: index); }
                        continue;
                    }
                    current[index] = next[at];
                    if (!alongNormal) { direction[index] = ((inside[index] ? nextGradients[at] : -nextGradients[at]) / length); }
                    still.Add(item: index);
                }
                walking = still;
            }
        }

        Walk(alongNormal: false, walking: active);

        var retry = new List<int>();

        foreach (var index in unresolved) {
            if (!(normals[index].LengthSquared() > 0f)) { continue; }
            current[index] = points[index];
            walked[index] = 0f;
            direction[index] = (inside[index] ? normals[index] : -normals[index]);
            retry.Add(item: index);
        }
        Walk(alongNormal: true, walking: retry);

        // Halve each bracket until it is no longer than the tolerance; `near` keeps the starting side's sign.
        var open = bracketed.Where(predicate: index => (Vector3.Distance(value1: near[index], value2: far[index]) > m_tolerance)).ToList();

        while (open.Count > 0) {
            var middles = open.Select(selector: index => ((near[index] + far[index]) * 0.5f)).ToArray();
            var middleValues = new float[middles.Length];

            field.Values(materials: new int[middles.Length], points: middles, values: middleValues, work: SdfMeshLevelWork.Oracle);
            var still = new List<int>();

            for (var at = 0; (at < open.Count); at++) {
                var index = open[at];

                if ((middleValues[at] < 0f) == inside[index]) { near[index] = middles[at]; } else { far[index] = middles[at]; }
                if (Vector3.Distance(value1: near[index], value2: far[index]) > m_tolerance) { still.Add(item: index); }
            }
            open = still;
        }
        foreach (var index in bracketed) {
            result.Found[index] = true;
            result.Surface[index] = ((near[index] + far[index]) * 0.5f);
            result.Upper[index] = MathF.Max(x: Vector3.Distance(value1: points[index], value2: near[index]), y: Vector3.Distance(value1: points[index], value2: far[index]));
        }

        return result;
    }
    private static float[] ValuesAt(ISdfMeshLevelField field, Vector3[] points) {
        var values = new float[points.Length];

        field.Values(materials: new int[points.Length], points: points, values: values, work: SdfMeshLevelWork.Oracle);

        return values;
    }
    private (float[] Values, Vector3[] Gradients) GradientsAt(ISdfMeshLevelField field, Vector3[] points) {
        var values = new float[points.Length];
        var gradients = new Vector3[points.Length];

        field.Gradients(gradients: gradients, points: points, spacing: MathF.Max(x: m_tolerance, y: (SdfMeshBakeTaps.SpacingShare * m_cell)), values: values, work: SdfMeshLevelWork.Oracle);

        return (values, gradients);
    }
    // The marked level-zero triangles joined over shared edges into runs, each run's half box diagonal its error.
    private SdfMeshLevelRuns Runs(bool[] marked) {
        var positions = m_reference.Positions.Span;
        var indices = m_reference.Indices.Span;
        var visited = new bool[marked.Length];
        var runs = 0;
        var triangles = 0;
        var largest = 0f;
        var stack = new Stack<int>();

        for (var start = 0; (start < marked.Length); start++) {
            if (!marked[start] || visited[start]) { continue; }

            var low = new Vector3(value: float.MaxValue);
            var high = new Vector3(value: float.MinValue);

            runs++;
            visited[start] = true;
            stack.Push(item: start);
            while (stack.Count > 0) {
                var triangle = stack.Pop();
                var (a, b, c) = Corners(indices: indices, triangle: triangle);

                triangles++;
                low = Vector3.Min(value1: low, value2: Vector3.Min(value1: positions[a], value2: Vector3.Min(value1: positions[b], value2: positions[c])));
                high = Vector3.Max(value1: high, value2: Vector3.Max(value1: positions[a], value2: Vector3.Max(value1: positions[b], value2: positions[c])));
                foreach (var neighbour in m_neighbours[triangle]) {
                    if (marked[neighbour] && !visited[neighbour]) {
                        visited[neighbour] = true;
                        stack.Push(item: neighbour);
                    }
                }
            }
            largest = MathF.Max(x: largest, y: (0.5f * Vector3.Distance(value1: low, value2: high)));
        }

        return new SdfMeshLevelRuns(Count: runs, Largest: largest, Triangles: triangles);
    }

    // The box of a field's samples within a cell of its surface (or inside it) on a lattice of the cell over a box, or the
    // box itself where none is.
    private static (Vector3 Low, Vector3 High) PieceBox(ISdfMeshLevelField field, Vector3 low, Vector3 high, float cell) {
        var extent = (high - low);
        var dims = (X: Math.Max(val1: 2, val2: ((int)MathF.Ceiling(x: (extent.X / cell)) + 1)), Y: Math.Max(val1: 2, val2: ((int)MathF.Ceiling(x: (extent.Y / cell)) + 1)), Z: Math.Max(val1: 2, val2: ((int)MathF.Ceiling(x: (extent.Z / cell)) + 1)));
        var points = new Vector3[((dims.X * dims.Y) * dims.Z)];

        for (var index = 0; (index < points.Length); index++) {
            points[index] = (low + (new Vector3(x: (index % dims.X), y: ((index / dims.X) % dims.Y), z: (index / (dims.X * dims.Y))) * cell));
        }

        var values = new float[points.Length];

        field.Values(materials: new int[points.Length], points: points, values: values, work: SdfMeshLevelWork.Contour);

        var found = (Low: new Vector3(value: float.MaxValue), High: new Vector3(value: float.MinValue));

        for (var index = 0; (index < points.Length); index++) {
            if (values[index] > cell) { continue; }
            found = (Vector3.Min(value1: found.Low, value2: points[index]), Vector3.Max(value1: found.High, value2: points[index]));
        }

        return ((found.Low.X <= found.High.X) ? found : (low, high));
    }
    /// <summary>Joins pieces' meshes into one level, each vertex riding its piece's member.</summary>
    /// <param name="pieces">The pieces' meshes and members, at least one.</param>
    /// <param name="members">Whether to return each vertex's member.</param>
    /// <returns>The joined mesh and, when asked, each vertex's member.</returns>
    private static (SdfMesh Mesh, byte[]? Members) Join(List<(SdfMesh Mesh, byte Member)> pieces, bool members) {
        if (pieces.Count == 1) {
            return (pieces[0].Mesh, (members ? Enumerable.Repeat(element: pieces[0].Member, count: pieces[0].Mesh.Positions.Length).ToArray() : null));
        }

        var joined = SdfMeshChunks.Join(chunks: [.. pieces.Select(selector: static piece => piece.Mesh)]);
        var vertexMembers = new List<byte>();

        foreach (var (mesh, member) in pieces) { vertexMembers.AddRange(collection: Enumerable.Repeat(element: member, count: mesh.Positions.Length)); }

        return (new SdfMesh(indices: joined.Indices, normals: joined.Normals, positions: joined.Positions, triangleMaterials: joined.TriangleMaterials) { BakeCell = pieces[0].Mesh.BakeCell }, (members ? [.. vertexMembers] : null));
    }
    // A mesh's triangle neighbours across welded edges, each triangle's connected component, how many there are, and each
    // triangle's open edges (welded edges it alone holds, as the audit counts them).
    private static (int[][] Neighbours, int[] Component, int Components, int[] Open) Adjacency(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, int triangles) {
        var weld = new Dictionary<Vector3, int>(capacity: positions.Length);
        var welded = new int[positions.Length];

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            welded[vertex] = (weld.TryAdd(key: positions[vertex], value: vertex) ? vertex : weld[positions[vertex]]);
        }

        var edges = new Dictionary<(int, int), List<int>>();

        for (var triangle = 0; (triangle < triangles); triangle++) {
            var (a, b, c) = Corners(indices: indices, triangle: triangle);

            foreach (var (first, second) in (ReadOnlySpan<(int, int)>)[(welded[a], welded[b]), (welded[b], welded[c]), (welded[c], welded[a])]) {
                if (first == second) { continue; }

                var key = ((first < second) ? (first, second) : (second, first));

                if (!edges.TryGetValue(key: key, value: out var held)) {
                    held = [];
                    edges.Add(key: key, value: held);
                }
                held.Add(item: triangle);
            }
        }

        var neighbours = new List<int>[triangles];
        var open = new int[triangles];

        for (var triangle = 0; (triangle < triangles); triangle++) { neighbours[triangle] = []; }
        foreach (var held in edges.Values) {
            if (held.Count == 1) { open[held[0]]++; }
            foreach (var first in held) {
                foreach (var second in held) {
                    if (first != second) { neighbours[first].Add(item: second); }
                }
            }
        }

        var sets = new SdfDisjointSet(count: triangles);

        for (var triangle = 0; (triangle < triangles); triangle++) {
            foreach (var neighbour in neighbours[triangle]) { sets.Union(first: triangle, second: neighbour); }
        }

        var component = new int[triangles];
        var numbering = new Dictionary<int, int>();

        for (var triangle = 0; (triangle < triangles); triangle++) {
            var root = sets.Find(element: triangle);

            if (!numbering.TryGetValue(key: root, value: out var number)) {
                number = numbering.Count;
                numbering.Add(key: root, value: number);
            }
            component[triangle] = number;
        }

        return ([.. neighbours.Select(selector: static list => list.ToArray())], component, numbering.Count, open);
    }
    // The barycentric steps (u, v) of a grid over a triangle at a spacing, corners left out, its centroid included when
    // asked: none beyond the centroid for a triangle no longer than the spacing.
    private static IEnumerable<(float U, float V)> Grid(Vector3 a, Vector3 b, Vector3 c, float spacing, int most, bool centroid) {
        var longest = MathF.Sqrt(x: MathF.Max(x: Vector3.DistanceSquared(value1: a, value2: b), y: MathF.Max(x: Vector3.DistanceSquared(value1: b, value2: c), y: Vector3.DistanceSquared(value1: c, value2: a))));

        if (centroid) { yield return ((1f / 3f), (1f / 3f)); }
        if (!(longest > spacing)) { yield break; }

        var steps = Math.Clamp(value: (int)MathF.Ceiling(x: (longest / spacing)), min: 2, max: most);

        for (var i = 0; (i <= steps); i++) {
            for (var j = 0; ((i + j) <= steps); j++) {
                if (((i == 0) && (j == 0)) || (i == steps) || (j == steps)) { continue; }
                yield return ((i / (float)steps), (j / (float)steps));
            }
        }
    }
    private static (int A, int B, int C) Corners(ReadOnlySpan<uint> indices, int triangle) =>
        ((int)indices[(3 * triangle)], (int)indices[((3 * triangle) + 1)], (int)indices[((3 * triangle) + 2)]);
    private static Vector3 Face(Vector3 a, Vector3 b, Vector3 c) => Unit(vector: Vector3.Cross(vector1: (b - a), vector2: (c - a)));
    private static Vector3 Unit(Vector3 vector) => VectorFunctions.NormalizeOr(fallback: Vector3.Zero, value: vector);
    // The barycentric weights of a point on a triangle (its closest point), each clamped to the triangle.
    private static Vector3 Barycentric(Vector3 a, Vector3 b, Vector3 c, Vector3 point) {
        var v0 = (b - a);
        var v1 = (c - a);
        var v2 = (point - a);
        var d00 = Vector3.Dot(vector1: v0, vector2: v0);
        var d01 = Vector3.Dot(vector1: v0, vector2: v1);
        var d11 = Vector3.Dot(vector1: v1, vector2: v1);
        var d20 = Vector3.Dot(vector1: v2, vector2: v0);
        var d21 = Vector3.Dot(vector1: v2, vector2: v1);
        var denominator = ((d00 * d11) - (d01 * d01));

        if (!(MathF.Abs(x: denominator) > 0f)) { return new Vector3(value: (1f / 3f)); }

        var v = Math.Clamp(value: (((d11 * d20) - (d01 * d21)) / denominator), min: 0f, max: 1f);
        var w = Math.Clamp(value: (((d00 * d21) - (d01 * d20)) / denominator), min: 0f, max: 1f);

        return new Vector3(x: MathF.Max(x: 0f, y: ((1f - v) - w)), y: v, z: w);
    }

    private sealed record Brackets(bool[] Found, Vector3[] Surface, float[] Upper, float[] Value);
}
