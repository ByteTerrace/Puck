using System.Numerics;

using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>What a field answer of a field level serves: the level's contour, or the oracle measuring it.</summary>
public enum SdfMeshLevelWork {
    /// <summary>A sample, crossing, gradient, crease or material point of a level's contour.</summary>
    Contour,
    /// <summary>A bracket step or bisection of the oracle measuring a level (<see cref="SdfMeshFieldLevels"/>).</summary>
    Oracle,
}

/// <summary>
/// A group's field, answered offline for its field levels (<see cref="SdfMeshPathBake.ContourLevel"/>,
/// <see cref="SdfMeshFieldLevels"/>) as the bake kernel answers a lattice: a value and material a point, or a value and a
/// tetrahedral gradient (<see cref="SdfMeshBakeTaps"/>). An answer far outside every shape reads a large positive value.
/// </summary>
public interface ISdfMeshLevelField {
    /// <summary>Gets the factor a value is multiplied by to bound the distance to the field's surface from below: the
    /// reciprocal of the field's Lipschitz bound, zero where none is known.</summary>
    float StepScale { get; }

    /// <summary>Answers the field's value and material at each point.</summary>
    /// <param name="points">The points, in the group's frame, in metres.</param>
    /// <param name="values">The values, one per point, in field units; negative inside.</param>
    /// <param name="materials">The materials, one per point.</param>
    /// <param name="work">What the answers serve.</param>
    void Values(ReadOnlySpan<Vector3> points, Span<float> values, Span<int> materials, SdfMeshLevelWork work);
    /// <summary>Answers the field's value and gradient at each point.</summary>
    /// <param name="points">The points, in the group's frame, in metres.</param>
    /// <param name="spacing">The gradient taps' spacing from each point, in metres.</param>
    /// <param name="values">The values, one per point, in field units; negative inside.</param>
    /// <param name="gradients">The gradients, one per point, in field units per metre.</param>
    /// <param name="work">What the answers serve.</param>
    void Gradients(ReadOnlySpan<Vector3> points, float spacing, Span<float> values, Span<Vector3> gradients, SdfMeshLevelWork work);
}

/// <summary>A level's lattice samples, kept so the next finer level skips the samples their values certify far from the
/// surface.</summary>
/// <param name="Origin">The position of sample (0, 0, 0), in metres.</param>
/// <param name="Cell">The spacing along every axis, in metres.</param>
/// <param name="Dims">The samples along each axis.</param>
/// <param name="Values">The field's raw values, X fastest, in field units.</param>
/// <param name="Materials">The materials, one per sample.</param>
public sealed record SdfMeshLevelSamples(Vector3 Origin, float Cell, (int X, int Y, int Z) Dims, float[] Values, int[] Materials);

public sealed partial class SdfMeshPathBake {
    /// <summary>
    /// Contours one field level of a group: the field sampled on a cubic lattice of the level's cell over a box, at the
    /// iso-value that offsets the surface outward by <paramref name="offset"/> (the lattice's measured slope,
    /// <see cref="SdfSampledLattice.SurfaceSlope"/>, or each sample's own with <paramref name="localAim"/>, converts it
    /// to field units only to aim the contour; the oracle measures where it landed), then the bake's
    /// own stages after the lattice: crossings bracketed on their edges, gradients, the contour, the field check of the
    /// vertices no crossing pins, the crease split, the material cut and the planar merge, with no coarser level of its
    /// own. A sample whose nearest sample in <paramref name="coarser"/> certifies its side of the offset surface by the
    /// field's Lipschitz bound (<see cref="ISdfMeshLevelField.StepScale"/>) takes the certified value without an answer.
    /// The one contouring (<see cref="SdfSampledLattice"/>, <see cref="SdfDualContouring"/>), on a coarser lattice at
    /// another iso-value: no second mesher. Offline: nothing here reaches a residency or the store.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="low">The box's low corner, in the group's frame, in metres.</param>
    /// <param name="high">The box's high corner.</param>
    /// <param name="cell">The level's cell, in metres; positive.</param>
    /// <param name="offset">How far outside the surface the level is contoured, in metres; at least zero.</param>
    /// <param name="preserveFeatures">Whether the group holds glyphs, which the planar merge keeps as the bake does.</param>
    /// <param name="localAim">Whether the offset converts to field units by each sample's own slope (its central
    /// differences, trilinear between samples) rather than the lattice's median slope: a field that is no true distance
    /// changes by several times more or less than a metre a metre from place to place.</param>
    /// <param name="coarser">The next coarser level's samples, or <see langword="null"/> to answer every sample.</param>
    /// <param name="mostSamples">The most samples the lattice may hold.</param>
    /// <param name="samples">The lattice's samples, raw values, for the next finer level; <see langword="null"/> when the
    /// lattice would hold more than <paramref name="mostSamples"/>.</param>
    /// <returns>The level's mesh, its bake cell the level's cell; <see langword="null"/> when the contour finds no surface
    /// or the lattice is past <paramref name="mostSamples"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> is not positive or
    /// <paramref name="offset"/> is negative.</exception>
    public static SdfMesh? ContourLevel(ISdfMeshLevelField field, Vector3 low, Vector3 high, float cell, float offset, bool preserveFeatures, bool localAim, SdfMeshLevelSamples? coarser, long mostSamples, out SdfMeshLevelSamples? samples) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: cell);
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);

        var margin = new Vector3(value: (offset + (2f * cell)));
        var origin = (low - margin);
        var extent = ((high + margin) - origin);
        var dims = (X: Math.Max(val1: 2, val2: ((int)MathF.Ceiling(x: (extent.X / cell)) + 1)), Y: Math.Max(val1: 2, val2: ((int)MathF.Ceiling(x: (extent.Y / cell)) + 1)), Z: Math.Max(val1: 2, val2: ((int)MathF.Ceiling(x: (extent.Z / cell)) + 1)));
        var count = (((long)dims.X * dims.Y) * dims.Z);

        samples = null;
        if (count > mostSamples) { return null; }

        var values = new float[count];
        var materials = new int[count];
        var asked = new List<int>();
        var scale = field.StepScale;

        for (var index = 0; (index < count); index++) {
            var at = PointOf(dims: dims, index: index, origin: origin, cell: cell);

            // The offset any aim can put in field units at a sample is at most AimSpread times the median slope, which is no
            // steeper than the Lipschitz bound.
            if ((coarser is null) || !(scale > 0f) || !TryCertified(at: at, coarser: coarser, material: out materials[index], offset: (AimSpread * offset), scale: scale, value: out values[index])) {
                asked.Add(item: index);
            }
        }

        var points = new Vector3[asked.Count];
        var answers = new float[asked.Count];
        var answered = new int[asked.Count];

        for (var index = 0; (index < points.Length); index++) {
            points[index] = PointOf(dims: dims, index: asked[index], origin: origin, cell: cell);
        }
        field.Values(materials: answered, points: points, values: answers, work: SdfMeshLevelWork.Contour);
        for (var index = 0; (index < points.Length); index++) {
            values[asked[index]] = answers[index];
            materials[asked[index]] = answered[index];
        }
        samples = new SdfMeshLevelSamples(Cell: cell, Dims: dims, Materials: materials, Origin: origin, Values: (float[])values.Clone());

        using var lattice = new SdfSampledLattice(cell: new Vector3(value: cell), materials: materials, origin: origin, samplesX: dims.X, samplesY: dims.Y, samplesZ: dims.Z, values: values);
        // The lattice reads the array it pins, so the offset applies in place once the raw slope is read.
        var median = lattice.SurfaceSlope();
        var slopes = (localAim ? LocalSlopes(cell: cell, dims: dims, median: median, values: samples.Values) : null);

        float IsoAt(Vector3 point) => (offset * ((slopes is null) ? median : Trilinear(cell: cell, dims: dims, origin: origin, point: point, values: slopes)));

        for (var index = 0; (index < count); index++) { values[index] -= (offset * ((slopes is null) ? median : slopes[index])); }

        // The cells the level's samples cannot resolve (two patches in a cell, a sheet or gap thinner than it) refine as
        // the bake's do, at two fine cells a coarse one and with no detail region, so the level stays one closed surface.
        var fine = Xyz(points: lattice.Refine(factor: 2).Span);

        if (fine.Length > 0) {
            var fineValues = new float[fine.Length];
            var fineMaterials = new int[fine.Length];

            field.Values(materials: fineMaterials, points: fine, values: fineValues, work: SdfMeshLevelWork.Contour);
            for (var index = 0; (index < fine.Length); index++) { fineValues[index] -= IsoAt(point: fine[index]); }
            lattice.AnswerRefinement(first: 0, materials: fineMaterials, values: fineValues);
        }

        for (var crossings = lattice.CrossingPoints(); (crossings.Length > 0); crossings = lattice.CrossingPoints()) {
            lattice.AnswerCrossings(first: 0, values: LevelValues(field: field, iso: IsoAt, points: crossings.Span));
        }

        var spacing = SdfMeshBakeTaps.Spacing(cell: lattice.ContourCell);
        var gradientPoints = Xyz(points: lattice.GradientPoints().Span);
        var (gradientValues, gradients) = LevelGradients(field: field, iso: IsoAt, points: gradientPoints, spacing: spacing);

        lattice.AnswerGradients(first: 0, gradients: gradients, values: gradientValues);

        SdfContour contour;

        try {
            contour = lattice.Contour();
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            return null;
        }
        if (contour.QuadCount == 0) { return null; }

        var creases = CreaseVertices(contour: contour);

        if (creases.Length > 0) {
            var projection = new ContourProjection(contour: contour);
            var creaseCell = lattice.ContourCell;
            var tolerance = (CreaseTolerance * MathF.Min(x: creaseCell.X, y: MathF.Min(x: creaseCell.Y, y: creaseCell.Z)));
            var slope = lattice.SurfaceSlope();
            var at = new Vector3[creases.Length];

            for (var index = 0; (index < creases.Length); index++) {
                at[index] = new Vector3(x: contour.Positions[(3 * creases[index])], y: contour.Positions[((3 * creases[index]) + 1)], z: contour.Positions[((3 * creases[index]) + 2)]);
            }

            var (creaseValues, creaseGradients) = LevelGradients(field: field, iso: IsoAt, points: at, spacing: SdfMeshBakeTaps.Spacing(cell: creaseCell));

            for (var index = 0; (index < creases.Length); index++) {
                _ = ProjectCrease(contour: contour, gradient: new Vector3(x: creaseGradients[(3 * index)], y: creaseGradients[((3 * index) + 1)], z: creaseGradients[((3 * index) + 2)]), projection: projection, slope: slope, tolerance: tolerance, value: creaseValues[index], vertex: creases[index]);
            }
        }

        contour.SelectDiagonals();
        var cut = new SdfMeshMaterialCut(contour: contour.SplitAtCreases());

        for (var round = cut.Points(); (round.Length > 0); round = cut.Points()) {
            var at = Xyz(points: round);
            var ignored = new float[at.Length];
            var found = new int[at.Length];

            field.Values(materials: found, points: at, values: ignored, work: SdfMeshLevelWork.Contour);
            cut.Answer(materials: found);
        }

        SdfMesh? mesh;

        try {
            mesh = cut.Build();
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            return null;
        }

        return ((mesh is null) ? null : SdfMeshChunks.Merge(levels: false, mesh: (mesh with { BakeCell = lattice.ContourCell }), preserveFeatures: preserveFeatures));
    }

    // How far a sample's local slope may stray from the lattice's median before it is clamped: a field that is no true
    // distance reads a few times over or under it, and a sample beside the evaluator's empty answer reads a jump.
    private const float AimSpread = 8f;

    // Each sample's slope, the magnitude of the field's central differences over its neighbours (one-sided at the
    // lattice's faces), clamped to within AimSpread of the median: the field units a metre of offset is there.
    private static float[] LocalSlopes(float[] values, (int X, int Y, int Z) dims, float cell, float median) {
        var slopes = new float[values.Length];
        var low = (median / AimSpread);
        var high = (median * AimSpread);

        float Along(int index, int coordinate, int length, int stride) {
            var before = ((coordinate > 0) ? values[(index - stride)] : values[index]);
            var after = (((coordinate + 1) < length) ? values[(index + stride)] : values[index]);
            var span = (((coordinate > 0) ? 1 : 0) + (((coordinate + 1) < length) ? 1 : 0));

            return ((after - before) / (span * cell));
        }

        for (var z = 0; (z < dims.Z); z++) {
            for (var y = 0; (y < dims.Y); y++) {
                for (var x = 0; (x < dims.X); x++) {
                    var index = (x + (dims.X * (y + (dims.Y * z))));
                    var gradient = new Vector3(x: Along(coordinate: x, index: index, length: dims.X, stride: 1), y: Along(coordinate: y, index: index, length: dims.Y, stride: dims.X), z: Along(coordinate: z, index: index, length: dims.Z, stride: (dims.X * dims.Y)));
                    var length = gradient.Length();

                    slopes[index] = (float.IsFinite(f: length) ? Math.Clamp(value: length, min: low, max: high) : median);
                }
            }
        }

        return slopes;
    }
    // A per-sample quantity at any point, trilinear between the eight samples about it, clamped to the lattice.
    private static float Trilinear(float[] values, (int X, int Y, int Z) dims, Vector3 origin, float cell, Vector3 point) {
        var local = Vector3.Clamp(value1: ((point - origin) / cell), min: Vector3.Zero, max: new Vector3(x: (dims.X - 1), y: (dims.Y - 1), z: (dims.Z - 1)));
        var x = Math.Min(val1: (int)local.X, val2: (dims.X - 2));
        var y = Math.Min(val1: (int)local.Y, val2: (dims.Y - 2));
        var z = Math.Min(val1: (int)local.Z, val2: (dims.Z - 2));
        var (fx, fy, fz) = ((local.X - x), (local.Y - y), (local.Z - z));
        float At(int dx, int dy, int dz) => values[((x + dx) + (dims.X * ((y + dy) + (dims.Y * (z + dz)))))];
        var c00 = float.Lerp(At(0, 0, 0), At(1, 0, 0), fx);
        var c10 = float.Lerp(At(0, 1, 0), At(1, 1, 0), fx);
        var c01 = float.Lerp(At(0, 0, 1), At(1, 0, 1), fx);
        var c11 = float.Lerp(At(0, 1, 1), At(1, 1, 1), fx);

        return float.Lerp(float.Lerp(c00, c10, fy), float.Lerp(c01, c11, fy), fz);
    }
    private static Vector3 PointOf((int X, int Y, int Z) dims, int index, Vector3 origin, float cell) =>
        (origin + (new Vector3(x: (index % dims.X), y: ((index / dims.X) % dims.Y), z: (index / (dims.X * dims.Y))) * cell));
    // A sample's raw value and material from the coarser level's nearest sample, when the field's Lipschitz bound puts the
    // sample on that sample's side of the offset surface: outside it by more than the offset, or inside the surface.
    private static bool TryCertified(Vector3 at, SdfMeshLevelSamples coarser, float offset, float scale, out float value, out int material) {
        var local = ((at - coarser.Origin) / coarser.Cell);
        var x = Math.Clamp(value: (int)MathF.Round(x: local.X), min: 0, max: (coarser.Dims.X - 1));
        var y = Math.Clamp(value: (int)MathF.Round(x: local.Y), min: 0, max: (coarser.Dims.Y - 1));
        var z = Math.Clamp(value: (int)MathF.Round(x: local.Z), min: 0, max: (coarser.Dims.Z - 1));
        var index = (x + (coarser.Dims.X * (y + (coarser.Dims.Y * z))));
        var near = coarser.Values[index];
        // The field changes by at most the distance over the step scale between the two samples.
        var reach = (Vector3.Distance(value1: at, value2: (coarser.Origin + (new Vector3(x: x, y: y, z: z) * coarser.Cell))) / scale);

        material = coarser.Materials[index];
        value = ((near > 0f) ? (near - reach) : (near + reach));

        return (float.IsFinite(f: near) && ((near > 0f) ? ((value * scale) > offset) : (value < 0f)));
    }
    private static Vector3[] Xyz(ReadOnlySpan<Vector4> points) {
        var xyz = new Vector3[points.Length];

        for (var index = 0; (index < xyz.Length); index++) { xyz[index] = new Vector3(x: points[index].X, y: points[index].Y, z: points[index].Z); }

        return xyz;
    }
    private static float[] LevelValues(ISdfMeshLevelField field, Func<Vector3, float> iso, ReadOnlySpan<Vector4> points) {
        var at = Xyz(points: points);
        var values = new float[at.Length];

        field.Values(materials: new int[at.Length], points: at, values: values, work: SdfMeshLevelWork.Contour);
        for (var index = 0; (index < values.Length); index++) { values[index] -= iso(arg: at[index]); }

        return values;
    }
    // Values less the iso-value and gradients, three floats a point, as the lattice and the field check take them.
    private static (float[] Values, float[] Gradients) LevelGradients(ISdfMeshLevelField field, Func<Vector3, float> iso, Vector3[] points, float spacing) {
        var values = new float[points.Length];
        var gradients = new Vector3[points.Length];
        var flat = new float[(3 * points.Length)];

        field.Gradients(gradients: gradients, points: points, spacing: spacing, values: values, work: SdfMeshLevelWork.Contour);
        for (var index = 0; (index < points.Length); index++) {
            values[index] -= iso(arg: points[index]);
            flat[(3 * index)] = gradients[index].X;
            flat[((3 * index) + 1)] = gradients[index].Y;
            flat[((3 * index) + 2)] = gradients[index].Z;
        }

        return (values, flat);
    }
}
