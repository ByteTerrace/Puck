using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.SdfVm;

/// <summary>The thresholds a mesh audit (<see cref="SdfMeshAudit"/>) flags a sample past and how it groups them.</summary>
public sealed record SdfMeshAuditOptions {
    /// <summary>Gets the distance off the field's surface, in the mesh's cells, past which a vertex, triangle centroid or
    /// edge midpoint is flagged.</summary>
    public float ErrorCells { get; init; } = 0.25f;
    /// <summary>Gets the angle, in degrees, between a triangle's normal and the field's gradient past which the triangle
    /// is flagged.</summary>
    public float NormalDegrees { get; init; } = 30f;
    /// <summary>Gets the samples along the longest axis of the lattice the field's zero set is found on.</summary>
    public int MissingSamples { get; init; } = 48;
    /// <summary>Gets the most clusters a report keeps, worst first.</summary>
    public int Clusters { get; init; } = 5;
    /// <summary>Gets the shapes a cluster names, nearest first.</summary>
    public int Shapes { get; init; } = 3;
    /// <summary>Gets the probe step, in metres, of the field gradient a triangle's normal is measured against.</summary>
    public float GradientStep { get; init; } = 0.001f;
    /// <summary>Gets the length of the field's gradient at a triangle's centroid below which the field there is a loose
    /// bound rather than a distance: the bake's lattice reads values the contour cannot place a surface from, such as a
    /// stretched ellipsoid's or a non-uniform scale's lower bound. A distance's gradient has length one; a smooth blend's
    /// dips toward cos(θ/2) at a crease of angle θ.</summary>
    public float LooseSlope { get; init; } = 0.6f;
}
/// <summary>One metric of an audited mesh over its samples: how many, its median, 99th percentile and largest value,
/// and how many samples passed its threshold.</summary>
/// <param name="Count">The samples measured.</param>
/// <param name="P50">The median, in the metric's unit.</param>
/// <param name="P99">The 99th percentile.</param>
/// <param name="Max">The largest value.</param>
/// <param name="Over">The samples past the metric's threshold.</param>
public readonly record struct SdfMeshAuditStatistic(int Count, float P50, float P99, float Max, int Over);
/// <summary>A shape of an audited group and its own distance from a cluster's center, in metres: negative inside it.</summary>
/// <param name="Name">The shape's name.</param>
/// <param name="Distance">The shape's own field at the cluster's center.</param>
public readonly record struct SdfMeshAuditShape(string Name, float Distance);
/// <summary>A shape of an audited group and how many of a mesh's samples its own surface lies nearest.</summary>
/// <param name="Name">The shape's name.</param>
/// <param name="Count">The samples.</param>
public readonly record struct SdfMeshAuditShapeCount(string Name, int Count);
/// <summary>A cluster of a mesh's flagged samples: where it lies in the group's frame, how far it spans, how bad it is and
/// which shapes' surfaces lie nearest it.</summary>
/// <param name="Center">The mean of its samples, in the group's frame.</param>
/// <param name="Extent">The size of the box its samples span, in metres.</param>
/// <param name="Score">The sum of its samples' severities: each a distance off the surface in cells, a normal's error
/// over a right angle, or one for a fold.</param>
/// <param name="Samples">The flagged samples it holds.</param>
/// <param name="Off">The vertices, centroids and midpoints off the surface among them.</param>
/// <param name="Turned">The triangles whose normals stray from the gradient among them.</param>
/// <param name="Shaded">The triangles whose corners' shading normals stray from the gradient among them.</param>
/// <param name="Folded">The folded edges among them.</param>
/// <param name="Missing">The surface points no triangle covers among them.</param>
/// <param name="WorstError">The largest distance off the surface among them, in metres.</param>
/// <param name="WorstNormal">The largest normal error among them, in degrees.</param>
/// <param name="Shapes">The shapes whose own surfaces lie nearest its center, nearest first.</param>
public sealed record SdfMeshAuditCluster(Vector3 Center, Vector3 Extent, float Score, int Samples, int Off, int Turned, int Shaded, int Folded, int Missing, float WorstError, float WorstNormal, IReadOnlyList<SdfMeshAuditShape> Shapes) {
    /// <summary>Gets the position of its most severe sample, in the group's frame.</summary>
    public Vector3 Worst { get; init; }
    /// <summary>Gets the unit direction its samples spread along most: the line an edge defect runs along.</summary>
    public Vector3 Axis { get; init; } = Vector3.UnitX;
    /// <summary>Gets positions of up to sixteen of its samples, spread over them, at which its shapes are named.</summary>
    public IReadOnlyList<Vector3> Probes { get; init; } = [];
}
/// <summary>A baked mesh measured against the field it was baked from (<see cref="SdfMeshAudit.Audit"/>).</summary>
/// <param name="Triangles">The mesh's triangles.</param>
/// <param name="Vertices">The mesh's vertices.</param>
/// <param name="Cell">The mesh's cell, in metres: the median length of its edges, the spacing its contour placed one
/// vertex a cell at.</param>
/// <param name="VertexError">The field's magnitude at each vertex, in metres.</param>
/// <param name="CentroidError">The field's magnitude at each triangle's centroid, in metres.</param>
/// <param name="MidpointError">The field's magnitude at each edge's midpoint, in metres.</param>
/// <param name="NormalError">The angle between each triangle's normal and the field's gradient at its centroid, in
/// degrees.</param>
/// <param name="ShadingError">The largest angle between a triangle's corners' shading normals and the field's gradient at
/// its centroid, in degrees: what the normals debug view draws.</param>
/// <param name="SlopeDeficit">How far the length of the field's gradient at each triangle's centroid falls short of a
/// distance's one: past one less <see cref="SdfMeshAuditOptions.LooseSlope"/>, the field there is a loose bound.</param>
/// <param name="LooseShapes">The shapes that make the field loose over its triangles, with how many triangles each,
/// most first: at each loose centroid, the loosest own field among the shapes whose surfaces lie within two cells.</param>
/// <param name="Folds">The edges whose two triangles turn by more than 120 degrees.</param>
/// <param name="Flipped">The triangles whose normals face against the field's gradient.</param>
/// <param name="Slivers">The triangles narrower than a twentieth of their longest edge.</param>
/// <param name="OpenEdges">The edges one triangle alone holds.</param>
/// <param name="NonManifoldEdges">The edges more than two triangles hold.</param>
/// <param name="Crossings">The points of the field's zero set the audit found on its lattice.</param>
/// <param name="MissingCrossings">The crossings farther than a cell from every triangle.</param>
/// <param name="Clusters">The worst clusters of flagged samples, worst first.</param>
/// <param name="Score">The sum of every flagged sample's severity.</param>
public sealed record SdfMeshAuditReport(
    int Triangles,
    int Vertices,
    float Cell,
    SdfMeshAuditStatistic VertexError,
    SdfMeshAuditStatistic CentroidError,
    SdfMeshAuditStatistic MidpointError,
    SdfMeshAuditStatistic NormalError,
    SdfMeshAuditStatistic ShadingError,
    SdfMeshAuditStatistic SlopeDeficit,
    IReadOnlyList<SdfMeshAuditShapeCount> LooseShapes,
    int Folds,
    int Flipped,
    int Slivers,
    int OpenEdges,
    int NonManifoldEdges,
    int Crossings,
    int MissingCrossings,
    IReadOnlyList<SdfMeshAuditCluster> Clusters,
    float Score
);

/// <summary>
/// Measures a baked mesh against the field it was baked from: how far its vertices, centroids and edge midpoints lie off
/// the field's surface, how far its triangles' normals stray from the field's gradient, where its triangles fold, flip,
/// thin to slivers or leave edges open, and where the field's zero set has no triangle near it. It reads a group's field
/// on the CPU through <see cref="SdfFieldEvaluator"/> (<see cref="Field"/>, the instances the bake sampled posed as it
/// posed them), and names each cluster of flagged samples by the shapes whose own fields (<see cref="ShapeFields"/>) lie
/// nearest it, so a bake defect is found, located and attributed without looking at a frame.
/// <para>Presentation alone: the audit reads stored meshes and evaluates fields, and its report is a pure function of
/// the mesh, the fields and the options, so a later run over the same inputs prints the same report.</para>
/// </summary>
public static class SdfMeshAudit {
    // A crossing's position is refined by this many halvings of its lattice edge.
    private const int Bisections = 10;
    // Two triangles sharing an edge fold when their normals' dot product falls below this: a turn past 120 degrees.
    private const float FoldDot = -0.5f;
    // A triangle is a sliver when twice its area over its longest edge squared falls below this: a height under a
    // twentieth of that edge.
    private const float SliverRatio = 0.05f;
    // The samples of a cluster its shapes are named at.
    private const int ProbeCount = 16;

    /// <summary>Returns the field a bake group samples, as one program: its instructions in order, each transform into a
    /// dynamic slot replaced by the translation and rotation the group's geometry posed that slot at
    /// (<see cref="SdfMeshGeometryBuilder.Poses"/>), so the field lies in the group's own frame as its mesh does, and every
    /// detail shape kept, as the bake keeps it.</summary>
    /// <param name="program">The program the group was planned from.</param>
    /// <param name="group">The group.</param>
    /// <returns>The group's field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="group"/> is
    /// <see langword="null"/>.</exception>
    public static SdfProgram Field(SdfProgram program, SdfMeshBakeGroup group) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: group);

        return program.Excerpt(indices: group.InstructionIndices(program: program), rewrite: (_, instruction) => Posed(group: group, instruction: instruction));
    }
    /// <summary>Returns each shape of a bake group alone: for every shape instruction, the point transforms of its chain
    /// since the chain's reset, posed as <see cref="Field"/> poses them, then the shape as a union and the field edits that
    /// follow it, so its field is that shape's own surface in the group's frame.</summary>
    /// <param name="program">The program the group was planned from.</param>
    /// <param name="group">The group.</param>
    /// <returns>Each shape instruction's index in <paramref name="program"/> and its own field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="group"/> is
    /// <see langword="null"/>.</exception>
    public static IReadOnlyList<(int Instruction, SdfProgram Field)> ShapeFields(SdfProgram program, SdfMeshBakeGroup group) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: group);

        var indices = group.InstructionIndices(program: program);
        var instructions = program.Instructions;
        var shapes = new List<(int Instruction, SdfProgram Field)>();
        var chain = new List<int>();

        for (var at = 0; (at < indices.Count); at++) {
            var index = indices[at];
            var op = instructions[index].Op;

            if (op == SdfOp.ResetPoint) {
                chain.Clear();
                chain.Add(item: index);
                continue;
            }
            if (op != SdfOp.ShapeBlend) {
                if (SdfOpRoles.Of(op: op) is SdfOpRole.Point or SdfOpRole.Lattice) { chain.Add(item: index); }
                continue;
            }

            var own = new List<int>(collection: chain) { index };

            for (var next = (at + 1); ((next < indices.Count) && (SdfOpRoles.Of(op: instructions[indices[next]].Op) == SdfOpRole.Field)); next++) {
                own.Add(item: indices[next]);
            }
            shapes.Add(item: (index, program.Excerpt(indices: own, rewrite: (source, instruction) => ((source == index)
                ? [instruction with { Blend = (uint)SdfBlendOp.Union, Detail = false }]
                : Posed(group: group, instruction: instruction)))));
        }

        return shapes;
    }
    /// <summary>Measures a baked mesh against its group's field and names its worst clusters by its shapes' own
    /// fields.</summary>
    /// <param name="mesh">The mesh, in the group's frame.</param>
    /// <param name="field">The group's field (<see cref="Field"/>).</param>
    /// <param name="shapes">Each shape's name and own field (<see cref="ShapeFields"/>); shapes sharing a name answer
    /// as the nearest of them.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/>, <paramref name="field"/>,
    /// <paramref name="shapes"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static SdfMeshAuditReport Audit(SdfMesh mesh, SdfFieldEvaluator field, IReadOnlyList<(string Name, SdfFieldEvaluator Field)> shapes, SdfMeshAuditOptions options) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentNullException.ThrowIfNull(argument: shapes);
        ArgumentNullException.ThrowIfNull(argument: options);

        var positions = mesh.Positions.Span;
        var indices = mesh.Indices.Span;
        var triangles = mesh.TriangleCount;
        var welded = Weld(positions: positions);
        var edges = Edges(indices: indices, welded: welded);
        var cell = MedianEdge(edges: edges, positions: positions);
        var threshold = (options.ErrorCells * cell);
        var flagged = new List<Sample>();

        // Vertices, centroids and edge midpoints, each a distance off the surface.
        var vertexErrors = new float[positions.Length];

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            vertexErrors[vertex] = Off(field: field, flagged: flagged, cell: cell, point: positions[vertex], threshold: threshold);
        }

        var centroidErrors = new float[triangles];
        var normalErrors = new float[triangles];
        var faceNormals = new Vector3[triangles];
        var gradients = new Vector3[triangles];
        var deficits = new float[triangles];
        var loose = new List<Vector3>();
        var slivers = 0;
        var agree = 0L;

        for (var triangle = 0; (triangle < triangles); triangle++) {
            var (a, b, c) = Corners(indices: indices, positions: positions, triangle: triangle);
            var cross = Vector3.Cross(vector1: (b - a), vector2: (c - a));
            var longest = MathF.Max(Vector3.DistanceSquared(value1: a, value2: b), MathF.Max(Vector3.DistanceSquared(value1: b, value2: c), Vector3.DistanceSquared(value1: c, value2: a)));
            var centroid = ((a + b + c) / 3f);

            slivers += (((longest <= 0f) || ((cross.Length() / longest) < SliverRatio)) ? 1 : 0);
            faceNormals[triangle] = ((cross.LengthSquared() > 0f) ? Vector3.Normalize(value: cross) : Vector3.Zero);
            centroidErrors[triangle] = Off(field: field, flagged: flagged, cell: cell, point: centroid, threshold: threshold);
            var slope = Slope(field: field, point: centroid, step: options.GradientStep);
            var steepness = slope.Length();

            gradients[triangle] = ((steepness > 0f) ? (slope / steepness) : Vector3.Zero);
            deficits[triangle] = ((steepness > 0f) ? (1f - steepness) : float.NaN);
            if ((steepness > 0f) && (steepness < options.LooseSlope)) {
                loose.Add(item: centroid);
            }
            agree += Math.Sign(value: Vector3.Dot(vector1: faceNormals[triangle], vector2: gradients[triangle]));
        }

        // The mesh winds one way; a triangle is judged against the way most of them wind.
        var winding = ((agree < 0) ? -1f : 1f);
        var flipped = 0;
        var normals = mesh.Normals.Span;
        var shadingErrors = new float[(normals.IsEmpty ? 0 : triangles)];

        for (var triangle = 0; (triangle < triangles); triangle++) {
            var (a, b, c) = Corners(indices: indices, positions: positions, triangle: triangle);

            if (gradients[triangle] == Vector3.Zero) {
                normalErrors[triangle] = float.NaN;
                if (!normals.IsEmpty) { shadingErrors[triangle] = float.NaN; }
                continue;
            }

            // The shading normal each corner carries, against the gradient at the centroid: the face's side of a crease,
            // which a crease vertex's split copy on this triangle answers for.
            if (!normals.IsEmpty) {
                var worst = 0f;

                for (var corner = 0; (corner < 3); corner++) {
                    var normal = normals[(int)indices[(3 * triangle) + corner]];

                    worst = MathF.Max(worst, Angle(first: ((normal.LengthSquared() > 0f) ? Vector3.Normalize(value: normal) : Vector3.Zero), second: gradients[triangle]));
                }
                shadingErrors[triangle] = worst;
                if (worst > options.NormalDegrees) {
                    flagged.Add(item: new Sample(Kind: SampleKind.Shaded, Position: ((a + b + c) / 3f), Severity: (worst / 90f), Value: worst));
                }
            }
            if (faceNormals[triangle] == Vector3.Zero) {
                normalErrors[triangle] = float.NaN;
                continue;
            }

            var degrees = Angle(first: (winding * faceNormals[triangle]), second: gradients[triangle]);

            normalErrors[triangle] = degrees;
            flipped += ((degrees > 90f) ? 1 : 0);
            if (degrees > options.NormalDegrees) {
                flagged.Add(item: new Sample(Kind: SampleKind.Turned, Position: ((a + b + c) / 3f), Severity: (degrees / 90f), Value: degrees));
            }
        }

        // Edges: midpoints off the surface, folds, open and non-manifold edges.
        var midpointErrors = new float[edges.Count];
        var folds = 0;
        var open = 0;
        var nonManifold = 0;
        var edge = 0;

        foreach (var ((first, second), held) in edges) {
            var midpoint = ((positions[first] + positions[second]) * 0.5f);

            midpointErrors[edge++] = Off(field: field, flagged: flagged, cell: cell, point: midpoint, threshold: threshold);
            if (held.Count == 1) {
                open++;
            } else if (held.Count > 2) {
                nonManifold++;
                flagged.Add(item: new Sample(Kind: SampleKind.Folded, Position: midpoint, Severity: 1f, Value: 0f));
            } else if (Vector3.Dot(vector1: faceNormals[held.First], vector2: faceNormals[held.Second]) < FoldDot) {
                folds++;
                flagged.Add(item: new Sample(Kind: SampleKind.Folded, Position: midpoint, Severity: 1f, Value: 0f));
            }
        }

        var (crossings, missing) = Missing(cell: cell, field: field, flagged: flagged, indices: indices, options: options, positions: positions);
        var clusters = Cluster(cell: cell, flagged: flagged)
            .Take(count: options.Clusters)
            .Select(selector: cluster => cluster with { Shapes = Nearest(count: options.Shapes, probes: cluster.Probes, shapes: shapes) })
            .ToArray();

        return new SdfMeshAuditReport(
            Cell: cell,
            CentroidError: Statistic(over: threshold, values: centroidErrors),
            Clusters: clusters,
            Crossings: crossings,
            Flipped: flipped,
            Folds: folds,
            SlopeDeficit: Statistic(over: (1f - options.LooseSlope), values: deficits),
            LooseShapes: Tally(count: options.Shapes, points: loose, reach: (2f * cell), shapes: shapes, step: options.GradientStep),
            MidpointError: Statistic(over: threshold, values: midpointErrors),
            MissingCrossings: missing,
            NonManifoldEdges: nonManifold,
            NormalError: Statistic(over: options.NormalDegrees, values: normalErrors),
            ShadingError: Statistic(over: options.NormalDegrees, values: shadingErrors),
            OpenEdges: open,
            Score: flagged.Sum(selector: static sample => sample.Severity),
            Slivers: slivers,
            Triangles: triangles,
            VertexError: Statistic(over: threshold, values: vertexErrors),
            Vertices: positions.Length
        );
    }
    /// <summary>Builds the evaluator that reads a field as the renderer samples it (<see cref="SdfFieldReading.Render"/>),
    /// or says why none reads it: a program the evaluator refuses by name, or one whose arithmetic faults, which is an
    /// evaluator defect reported against the field rather than thrown through an audit of many.</summary>
    /// <param name="program">The field (<see cref="Field"/>, <see cref="ShapeFields"/>).</param>
    /// <param name="field">The evaluator, when this returns <see langword="true"/>.</param>
    /// <param name="refusal">Why no evaluator reads the field, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the evaluator reads the field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public static bool TryEvaluator(SdfProgram program, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfFieldEvaluator? field, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: false)] out string? refusal) {
        ArgumentNullException.ThrowIfNull(argument: program);

        try {
            field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);
            refusal = null;

            return true;
        } catch (Exception exception) when (exception is ArgumentException or ArithmeticException) {
            var message = exception.Message;
            var end = message.IndexOf(value: ". ", comparisonType: StringComparison.Ordinal);

            field = null;
            refusal = $"{((exception is ArgumentException) ? "refused" : "failed")}: {((end < 0) ? message : message[..(end + 1)])}";

            return false;
        }
    }
    /// <summary>Reads a field at a point in its frame.</summary>
    /// <param name="field">The field.</param>
    /// <param name="point">The point, in metres.</param>
    /// <returns>The field's value, in metres, or <see cref="float.NaN"/> where the field answers nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static float Distance(SdfFieldEvaluator field, Vector3 point) {
        ArgumentNullException.ThrowIfNull(argument: field);

        return (field.TryDistance(distance: out var distance, material: out _, position: Fixed(point: point)) ? (float)(double)distance : float.NaN);
    }

    // A dynamic transform as the translation and rotation the group posed its slot at; a shape as itself, detail or not.
    private static IEnumerable<SdfInstruction> Posed(SdfMeshBakeGroup group, SdfInstruction instruction) {
        if (instruction.Op == SdfOp.ShapeBlend) { return [instruction with { Detail = false }]; }
        if (instruction.Op != SdfOp.TransformDynamic) { return [instruction]; }
        if (!group.Geometry.Poses.TryGetValue(key: (int)instruction.Data0.X, value: out var pose)) { return []; }

        var turn = Quaternion.Normalize(value: pose.Orientation);

        return [
            new SdfInstruction(Blend: 0u, Data0: new Vector4(value: pose.Position, w: 0f), Data1: Vector4.Zero, Material: 0u, Op: SdfOp.Translate, Shape: 0u),
            new SdfInstruction(Blend: 0u, Data0: new Vector4(x: turn.X, y: turn.Y, z: turn.Z, w: turn.W), Data1: Vector4.Zero, Material: 0u, Op: SdfOp.Rotate, Shape: 0u),
        ];
    }
    // The angle between two unit vectors, in degrees; a right angle where either is zero.
    private static float Angle(Vector3 first, Vector3 second) =>
        (MathF.Acos(x: Math.Clamp(value: Vector3.Dot(vector1: first, vector2: second), min: -1f, max: 1f)) * (180f / MathF.PI));
    private static FixedPosition Fixed(Vector3 point) => FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point));
    // The field's magnitude at a point, flagged past the threshold; NaN where the field answers nothing.
    private static float Off(SdfFieldEvaluator field, List<Sample> flagged, float cell, Vector3 point, float threshold) {
        var error = MathF.Abs(x: Distance(field: field, point: point));

        if (error > threshold) {
            flagged.Add(item: new Sample(Kind: SampleKind.Off, Position: point, Severity: (error / cell), Value: error));
        }

        return error;
    }
    // The field's gradient at a point by central differences a step either way along each axis, unnormalized: a distance
    // field's has length one, a loose bound's less.
    private static Vector3 Slope(SdfFieldEvaluator field, Vector3 point, float step) {
        var x = (Distance(field: field, point: (point + new Vector3(x: step, y: 0f, z: 0f))) - Distance(field: field, point: (point - new Vector3(x: step, y: 0f, z: 0f))));
        var y = (Distance(field: field, point: (point + new Vector3(x: 0f, y: step, z: 0f))) - Distance(field: field, point: (point - new Vector3(x: 0f, y: step, z: 0f))));
        var z = (Distance(field: field, point: (point + new Vector3(x: 0f, y: 0f, z: step))) - Distance(field: field, point: (point - new Vector3(x: 0f, y: 0f, z: step))));
        var slope = (new Vector3(x: x, y: y, z: z) / (2f * step));

        return (float.IsFinite(f: slope.X) && float.IsFinite(f: slope.Y) && float.IsFinite(f: slope.Z)) ? slope : Vector3.Zero;
    }
    /// <summary>Reads a field's unit gradient at a point in its frame.</summary>
    /// <param name="field">The field.</param>
    /// <param name="point">The point, in metres.</param>
    /// <param name="step">The probe step, in metres.</param>
    /// <returns>The unit gradient, or zero where the field answers none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static Vector3 Gradient(SdfFieldEvaluator field, Vector3 point, float step) {
        ArgumentNullException.ThrowIfNull(argument: field);

        return (field.TryFieldGradient(epsilon: FixedQ4816.FromDouble(value: step), gradient: out var gradient, position: Fixed(point: point)) ? gradient.ToVector3() : Vector3.Zero);
    }
    private static (Vector3 A, Vector3 B, Vector3 C) Corners(ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions, int triangle) =>
        (positions[(int)indices[(3 * triangle)]], positions[(int)indices[((3 * triangle) + 1)]], positions[(int)indices[((3 * triangle) + 2)]]);
    // Each vertex's first vertex at the same position: the contour splits a vertex at creases and material boundaries
    // into copies that share it.
    private static int[] Weld(ReadOnlySpan<Vector3> positions) {
        var first = new Dictionary<Vector3, int>(capacity: positions.Length);
        var welded = new int[positions.Length];

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            if (!first.TryGetValue(key: positions[vertex], value: out var at)) {
                at = vertex;
                first.Add(key: positions[vertex], value: vertex);
            }
            welded[vertex] = at;
        }

        return welded;
    }
    // Every welded edge, ordered by its first appearance, with the triangles that hold it.
    private static Dictionary<(int First, int Second), Held> Edges(ReadOnlySpan<uint> indices, int[] welded) {
        var edges = new Dictionary<(int First, int Second), Held>();

        for (var triangle = 0; (triangle < (indices.Length / 3)); triangle++) {
            for (var corner = 0; (corner < 3); corner++) {
                var a = welded[(int)indices[((3 * triangle) + corner)]];
                var b = welded[(int)indices[((3 * triangle) + ((corner + 1) % 3))]];

                if (a == b) { continue; }

                var key = ((a < b) ? (a, b) : (b, a));

                edges[key] = (edges.TryGetValue(key: key, value: out var held) ? held.With(triangle: triangle) : new Held(Count: 1, First: triangle, Second: -1));
            }
        }

        return edges;
    }
    private static float MedianEdge(Dictionary<(int First, int Second), Held> edges, ReadOnlySpan<Vector3> positions) {
        if (edges.Count == 0) { return 0f; }

        var lengths = new float[edges.Count];
        var at = 0;

        foreach (var (first, second) in edges.Keys) {
            lengths[at++] = Vector3.Distance(value1: positions[first], value2: positions[second]);
        }
        Array.Sort(array: lengths);

        return lengths[(lengths.Length - 1) / 2];
    }
    private static SdfMeshAuditStatistic Statistic(float[] values, float over) {
        var finite = values.Where(predicate: static value => float.IsFinite(f: value)).ToArray();

        if (finite.Length == 0) { return default; }
        Array.Sort(array: finite);

        return new SdfMeshAuditStatistic(
            Count: finite.Length,
            Max: finite[^1],
            Over: finite.Count(predicate: value => (value > over)),
            P50: finite[(int)((finite.Length - 1) * 0.5)],
            P99: finite[(int)((finite.Length - 1) * 0.99)]
        );
    }
    // The field's zero set on a lattice over the mesh's box: each lattice edge whose ends differ in sign holds a crossing,
    // refined by bisection and flagged where no triangle lies within a cell of it.
    private static (int Crossings, int Missing) Missing(SdfFieldEvaluator field, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float cell, SdfMeshAuditOptions options, List<Sample> flagged) {
        if ((positions.Length == 0) || !(cell > 0f)) { return (0, 0); }

        var low = new Vector3(value: float.MaxValue);
        var high = new Vector3(value: float.MinValue);

        foreach (var position in positions) {
            low = Vector3.Min(value1: low, value2: position);
            high = Vector3.Max(value1: high, value2: position);
        }
        low -= new Vector3(value: (3f * cell));
        high += new Vector3(value: (3f * cell));

        var extent = (high - low);
        var spacing = MathF.Max((MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / (Math.Max(options.MissingSamples, 2) - 1)), (1.5f * cell));
        var dims = (X: ((int)MathF.Ceiling(extent.X / spacing) + 1), Y: ((int)MathF.Ceiling(extent.Y / spacing) + 1), Z: ((int)MathF.Ceiling(extent.Z / spacing) + 1));
        var values = new float[(dims.X * dims.Y) * dims.Z];

        Vector3 PointAt(int x, int y, int z) => (low + (new Vector3(x: x, y: y, z: z) * spacing));
        int At(int x, int y, int z) => (((z * dims.Y) + y) * dims.X) + x;

        for (var z = 0; (z < dims.Z); z++) {
            for (var y = 0; (y < dims.Y); y++) {
                for (var x = 0; (x < dims.X); x++) {
                    values[At(x: x, y: y, z: z)] = Distance(field: field, point: PointAt(x: x, y: y, z: z));
                }
            }
        }

        var triangles = new TriangleGrid(cellSize: (2f * cell), indices: indices, positions: positions);
        var crossings = 0;
        var missing = 0;

        void Edge(int x0, int y0, int z0, int x1, int y1, int z1, ReadOnlySpan<Vector3> mesh, ReadOnlySpan<uint> corners) {
            var v0 = values[At(x: x0, y: y0, z: z0)];
            var v1 = values[At(x: x1, y: y1, z: z1)];

            if (!float.IsFinite(f: v0) || !float.IsFinite(f: v1) || ((v0 < 0f) == (v1 < 0f))) { return; }

            var inside = ((v0 < 0f) ? PointAt(x: x0, y: y0, z: z0) : PointAt(x: x1, y: y1, z: z1));
            var outside = ((v0 < 0f) ? PointAt(x: x1, y: y1, z: z1) : PointAt(x: x0, y: y0, z: z0));

            for (var step = 0; (step < Bisections); step++) {
                var middle = ((inside + outside) * 0.5f);

                if (Distance(field: field, point: middle) < 0f) { inside = middle; } else { outside = middle; }
            }

            var crossing = ((inside + outside) * 0.5f);
            var nearest = triangles.Nearest(indices: corners, point: crossing, positions: mesh, reach: (4f * cell));

            crossings++;
            if (nearest > cell) {
                missing++;
                flagged.Add(item: new Sample(Kind: SampleKind.Missing, Position: crossing, Severity: (nearest / cell), Value: nearest));
            }
        }

        for (var z = 0; (z < dims.Z); z++) {
            for (var y = 0; (y < dims.Y); y++) {
                for (var x = 0; (x < dims.X); x++) {
                    if ((x + 1) < dims.X) { Edge(x0: x, y0: y, z0: z, x1: (x + 1), y1: y, z1: z, mesh: positions, corners: indices); }
                    if ((y + 1) < dims.Y) { Edge(x0: x, y0: y, z0: z, x1: x, y1: (y + 1), z1: z, mesh: positions, corners: indices); }
                    if ((z + 1) < dims.Z) { Edge(x0: x, y0: y, z0: z, x1: x, y1: y, z1: (z + 1), mesh: positions, corners: indices); }
                }
            }
        }

        return (crossings, missing);
    }
    // The flagged samples joined into clusters, each sample with every other within a cell and a half (five millimetres
    // at the least), worst first; ties fall to the cluster nearest the frame's origin along x, then y, then z.
    private static List<SdfMeshAuditCluster> Cluster(List<Sample> flagged, float cell) {
        var reach = MathF.Max((1.5f * cell), 0.005f);
        var parents = new int[flagged.Count];
        var buckets = new Dictionary<(int, int, int), List<int>>();

        (int, int, int) Bucket(Vector3 point) => ((int)MathF.Floor(point.X / reach), (int)MathF.Floor(point.Y / reach), (int)MathF.Floor(point.Z / reach));
        int Root(int sample) {
            while (parents[sample] != sample) {
                parents[sample] = parents[parents[sample]];
                sample = parents[sample];
            }
            return sample;
        }

        for (var sample = 0; (sample < flagged.Count); sample++) {
            parents[sample] = sample;

            var (bx, by, bz) = Bucket(point: flagged[sample].Position);

            for (var dz = -1; (dz <= 1); dz++) {
                for (var dy = -1; (dy <= 1); dy++) {
                    for (var dx = -1; (dx <= 1); dx++) {
                        if (!buckets.TryGetValue(key: ((bx + dx), (by + dy), (bz + dz)), value: out var near)) { continue; }
                        foreach (var other in near) {
                            if (Vector3.Distance(value1: flagged[sample].Position, value2: flagged[other].Position) <= reach) {
                                parents[Root(sample: sample)] = Root(sample: other);
                            }
                        }
                    }
                }
            }
            if (!buckets.TryGetValue(key: (bx, by, bz), value: out var own)) {
                own = [];
                buckets.Add(key: (bx, by, bz), value: own);
            }
            own.Add(item: sample);
        }

        return [.. Enumerable.Range(start: 0, count: flagged.Count)
            .GroupBy(keySelector: Root)
            .Select(selector: members => {
                var samples = members.Select(selector: member => flagged[member]).ToArray();
                var low = samples.Aggregate(seed: new Vector3(value: float.MaxValue), func: static (bound, sample) => Vector3.Min(value1: bound, value2: sample.Position));
                var high = samples.Aggregate(seed: new Vector3(value: float.MinValue), func: static (bound, sample) => Vector3.Max(value1: bound, value2: sample.Position));
                var center = (samples.Aggregate(seed: Vector3.Zero, func: static (sum, sample) => (sum + sample.Position)) / samples.Length);

                return new SdfMeshAuditCluster(
                    Center: center,
                    Extent: (high - low),
                    Folded: samples.Count(predicate: static sample => (sample.Kind == SampleKind.Folded)),
                    Missing: samples.Count(predicate: static sample => (sample.Kind == SampleKind.Missing)),
                    Off: samples.Count(predicate: static sample => (sample.Kind == SampleKind.Off)),
                    Samples: samples.Length,
                    Score: samples.Sum(selector: static sample => sample.Severity),
                    Shapes: [],
                    Shaded: samples.Count(predicate: static sample => (sample.Kind == SampleKind.Shaded)),
                    Turned: samples.Count(predicate: static sample => (sample.Kind == SampleKind.Turned)),
                    WorstError: samples.Where(predicate: static sample => (sample.Kind is SampleKind.Off or SampleKind.Missing)).Select(selector: static sample => sample.Value).DefaultIfEmpty(defaultValue: 0f).Max(),
                    WorstNormal: samples.Where(predicate: static sample => (sample.Kind is SampleKind.Turned or SampleKind.Shaded)).Select(selector: static sample => sample.Value).DefaultIfEmpty(defaultValue: 0f).Max()
                ) {
                    Axis = Spread(center: center, samples: samples),
                    Probes = [.. Enumerable.Range(start: 0, count: Math.Min(samples.Length, ProbeCount)).Select(selector: probe => samples[(probe * samples.Length) / Math.Min(samples.Length, ProbeCount)].Position)],
                    Worst = samples.MaxBy(keySelector: static sample => sample.Severity).Position,
                };
            })
            .OrderByDescending(keySelector: static cluster => cluster.Score)
            .ThenBy(keySelector: static cluster => cluster.Center.X)
            .ThenBy(keySelector: static cluster => cluster.Center.Y)
            .ThenBy(keySelector: static cluster => cluster.Center.Z)];
    }
    // The direction samples spread along most: the principal axis of their scatter about the center, by power iteration
    // from a fixed start.
    private static Vector3 Spread(Vector3 center, Sample[] samples) {
        var (xx, xy, xz, yy, yz, zz) = (0f, 0f, 0f, 0f, 0f, 0f);

        foreach (var sample in samples) {
            var d = (sample.Position - center);

            (xx, xy, xz, yy, yz, zz) = ((xx + (d.X * d.X)), (xy + (d.X * d.Y)), (xz + (d.X * d.Z)), (yy + (d.Y * d.Y)), (yz + (d.Y * d.Z)), (zz + (d.Z * d.Z)));
        }

        var axis = Vector3.Normalize(value: new Vector3(x: 1f, y: 0.7f, z: 0.4f));

        for (var step = 0; (step < 32); step++) {
            var next = new Vector3(
                x: (((xx * axis.X) + (xy * axis.Y)) + (xz * axis.Z)),
                y: (((xy * axis.X) + (yy * axis.Y)) + (yz * axis.Z)),
                z: (((xz * axis.X) + (yz * axis.Y)) + (zz * axis.Z))
            );

            if (!(next.LengthSquared() > 0f)) { return axis; }
            axis = Vector3.Normalize(value: next);
        }

        return axis;
    }
    // Which shape makes the field loose at each point, tallied one entry a name, most first: of the shapes whose own
    // surfaces lie within reach of the point, the one whose own field is loosest there, or of them all when none is
    // within reach.
    private static SdfMeshAuditShapeCount[] Tally(List<Vector3> points, IReadOnlyList<(string Name, SdfFieldEvaluator Field)> shapes, float reach, float step, int count) {
        if (shapes.Count == 0) { return []; }

        var tally = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var point in points) {
            var near = shapes
                .Select(selector: shape => (shape.Name, shape.Field, Distance: MathF.Abs(x: Distance(field: shape.Field, point: point))))
                .Where(predicate: static shape => float.IsFinite(f: shape.Distance))
                .ToArray();

            if (near.Length == 0) { continue; }

            var within = near.Where(predicate: shape => (shape.Distance <= reach)).ToArray();
            var named = ((within.Length > 0) ? within : near).MinBy(keySelector: shape => Slope(field: shape.Field, point: point, step: step).Length()).Name;

            tally[named] = (tally.GetValueOrDefault(key: named) + 1);
        }

        return [.. tally
            .OrderByDescending(keySelector: static entry => entry.Value)
            .ThenBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)
            .Take(count: count)
            .Select(selector: static entry => new SdfMeshAuditShapeCount(Count: entry.Value, Name: entry.Key))];
    }
    // The shapes whose own surfaces lie nearest a cluster, one entry a name: each shape's field at every probe, a name's
    // nearest shape there, and the name's median over the probes, so a cluster strung along an edge names the shape whose
    // surface the edge follows rather than whatever lies nearest its centroid.
    private static SdfMeshAuditShape[] Nearest(IReadOnlyList<Vector3> probes, IReadOnlyList<(string Name, SdfFieldEvaluator Field)> shapes, int count) {
        if (probes.Count == 0) { return []; }

        return [.. shapes
            .GroupBy(keySelector: static shape => shape.Name, comparer: StringComparer.Ordinal)
            .Select(selector: named => {
                var distances = probes
                    .Select(selector: probe => named.Select(selector: shape => Distance(field: shape.Field, point: probe)).Where(predicate: static distance => float.IsFinite(f: distance)).DefaultIfEmpty(defaultValue: float.NaN).MinBy(keySelector: MathF.Abs))
                    .Where(predicate: static distance => float.IsFinite(f: distance))
                    .OrderBy(keySelector: MathF.Abs)
                    .ToArray();

                return new SdfMeshAuditShape(Distance: ((distances.Length == 0) ? float.NaN : distances[(distances.Length - 1) / 2]), Name: named.Key);
            })
            .Where(predicate: static shape => float.IsFinite(f: shape.Distance))
            .OrderBy(keySelector: static shape => MathF.Abs(x: shape.Distance))
            .ThenBy(keySelector: static shape => shape.Name, comparer: StringComparer.Ordinal)
            .Take(count: count)];
    }

    private enum SampleKind {
        Off,
        Turned,
        Shaded,
        Folded,
        Missing,
    }
    private readonly record struct Sample(SampleKind Kind, Vector3 Position, float Severity, float Value);
    // The triangles that hold an edge: how many, and the first two.
    private readonly record struct Held(int Count, int First, int Second) {
        public Held With(int triangle) => new(Count: (Count + 1), First: First, Second: ((Count == 1) ? triangle : Second));
    }
    // The mesh's triangles bucketed by the cells of a uniform grid their boxes overlap, which answers the distance from a
    // point to the nearest triangle within a reach.
    private sealed class TriangleGrid {
        private readonly float m_cellSize;
        private readonly Dictionary<(int, int, int), List<int>> m_cells = [];

        public TriangleGrid(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float cellSize) {
            m_cellSize = cellSize;
            for (var triangle = 0; (triangle < (indices.Length / 3)); triangle++) {
                var (a, b, c) = Corners(indices: indices, positions: positions, triangle: triangle);
                var low = Cell(point: Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c)));
                var high = Cell(point: Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c)));

                for (var z = low.Z; (z <= high.Z); z++) {
                    for (var y = low.Y; (y <= high.Y); y++) {
                        for (var x = low.X; (x <= high.X); x++) {
                            if (!m_cells.TryGetValue(key: (x, y, z), value: out var held)) {
                                held = [];
                                m_cells.Add(key: (x, y, z), value: held);
                            }
                            held.Add(item: triangle);
                        }
                    }
                }
            }
        }

        // The distance to the nearest triangle within the reach, or the reach when none lies within it.
        public float Nearest(Vector3 point, float reach, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) {
            var low = Cell(point: (point - new Vector3(value: reach)));
            var high = Cell(point: (point + new Vector3(value: reach)));
            var nearest = reach;

            for (var z = low.Z; (z <= high.Z); z++) {
                for (var y = low.Y; (y <= high.Y); y++) {
                    for (var x = low.X; (x <= high.X); x++) {
                        if (!m_cells.TryGetValue(key: (x, y, z), value: out var held)) { continue; }
                        foreach (var triangle in held) {
                            var (a, b, c) = Corners(indices: indices, positions: positions, triangle: triangle);

                            nearest = MathF.Min(nearest, Vector3.Distance(value1: point, value2: ClosestOnTriangle(a: a, b: b, c: c, p: point)));
                        }
                    }
                }
            }

            return nearest;
        }

        private (int X, int Y, int Z) Cell(Vector3 point) => ((int)MathF.Floor(point.X / m_cellSize), (int)MathF.Floor(point.Y / m_cellSize), (int)MathF.Floor(point.Z / m_cellSize));

        // The point of a triangle nearest p, by the triangle's Voronoi regions.
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
            var ab = (b - a);
            var ac = (c - a);
            var ap = (p - a);
            var d1 = Vector3.Dot(vector1: ab, vector2: ap);
            var d2 = Vector3.Dot(vector1: ac, vector2: ap);

            if ((d1 <= 0f) && (d2 <= 0f)) { return a; }

            var bp = (p - b);
            var d3 = Vector3.Dot(vector1: ab, vector2: bp);
            var d4 = Vector3.Dot(vector1: ac, vector2: bp);

            if ((d3 >= 0f) && (d4 <= d3)) { return b; }

            var vc = ((d1 * d4) - (d3 * d2));

            if ((vc <= 0f) && (d1 >= 0f) && (d3 <= 0f)) { return (a + (ab * (d1 / (d1 - d3)))); }

            var cp = (p - c);
            var d5 = Vector3.Dot(vector1: ab, vector2: cp);
            var d6 = Vector3.Dot(vector1: ac, vector2: cp);

            if ((d6 >= 0f) && (d5 <= d6)) { return c; }

            var vb = ((d5 * d2) - (d1 * d6));

            if ((vb <= 0f) && (d2 >= 0f) && (d6 <= 0f)) { return (a + (ac * (d2 / (d2 - d6)))); }

            var va = ((d3 * d6) - (d5 * d4));

            if ((va <= 0f) && ((d4 - d3) >= 0f) && ((d5 - d6) >= 0f)) { return (b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))); }

            var denominator = (1f / ((va + vb) + vc));

            return ((a + (ab * (vb * denominator))) + (ac * (vc * denominator)));
        }
    }
}
