using System.Numerics;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// Laws over <see cref="SdfSampledLattice"/>'s refinement: a disc thinner than a lattice cell, which the lattice's own
/// samples miss in places, refines and contours as one closed two-manifold, with no crack where the contour's resolution
/// changes, and its mesh lies within half a lattice cell of the field.
/// </summary>
public sealed class SdfSampledLatticeLawTests {
    private const float Cell = 0.015f;
    private const int Samples = 33;
    private const int Factor = 3;
    private static readonly Vector3 Origin = new(value: -0.24f);
    private static readonly Vector3 Normal = Vector3.Normalize(value: new Vector3(x: 0.3f, y: 1f, z: 0.2f));

    // A tilted disc 6 mm thick and 18 cm across: thinner than the 15 mm lattice cell, so its inside samples are sparse.
    private static float Field(Vector3 point) {
        var slab = (MathF.Abs(Vector3.Dot(vector1: point, vector2: Normal)) - 0.003f);
        var sphere = (point.Length() - 0.18f);

        return MathF.Max(slab, sphere);
    }
    // A box 10 cm a side whose faces lie off the lattice's planes, turned about y by `turn` radians.
    private static float Box(Vector3 point, float turn) {
        var turned = Vector3.Transform(value: point, rotation: Quaternion.CreateFromAxisAngle(angle: turn, axis: Vector3.UnitY));
        var q = (Vector3.Abs(value: (turned - new Vector3(x: 0.004f, y: 0.007f, z: -0.003f))) - new Vector3(value: 0.05f));

        return (Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f));
    }
    // The turned box with a sphere carved from one corner: concave creases where the carve meets the faces.
    private static float Carved(Vector3 point) => MathF.Max(Box(point: point, turn: 0.3f), -(Vector3.Distance(value1: point, value2: new Vector3(value: 0.03f)) - 0.05f));
    private static Vector3 Position(SdfContour contour, int vertex) =>
        new(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)]);
    // The bake kernel's gradient (sdf-mesh-bake.comp.hlsl): four tetrahedral taps a sixty-fourth of a cell out, summed
    // into the field's derivative.
    private static Vector3 Gradient(Func<Vector3, float> field, Vector3 point) {
        const float Spacing = (Cell / 64f);
        ReadOnlySpan<Vector3> taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];
        var gradient = Vector3.Zero;

        foreach (var tap in taps) {
            gradient += (tap * field(arg: (point + (tap * Spacing))));
        }

        return (gradient * (0.25f / Spacing));
    }
    private static SdfContour Bake(bool refine) => Bake(field: Field, refine: refine);
    private static SdfContour Bake(Func<Vector3, float> field, bool refine) => Bake(field: field, material: null, refine: refine, regions: []);
    // The lattice's samples of `field` and, when given, `material`; refined `Factor` to a cell when asked.
    private static SdfContour Bake(Func<Vector3, float> field, Func<Vector3, int>? material, bool refine, SdfLatticeRegion[] regions) {
        var count = ((Samples * Samples) * Samples);
        var values = new float[count];
        var materials = new int[count];

        for (var index = 0; (index < count); index++) {
            var at = new Vector3(x: (index % Samples), y: ((index / Samples) % Samples), z: (index / (Samples * Samples)));

            values[index] = field(arg: (Origin + (at * Cell)));
            materials[index] = (material?.Invoke(arg: (Origin + (at * Cell))) ?? 0);
        }

        var lattice = new SdfSampledLattice(cell: new Vector3(value: Cell), materials: materials, origin: Origin, samplesX: Samples, samplesY: Samples, samplesZ: Samples, values: values);

        if (refine) {
            var fine = lattice.Refine(factor: Factor, regions: regions);

            Assert.NotEmpty(collection: fine);
            lattice.AnswerRefinement(first: 0, materials: [.. fine.Select(selector: point => (material?.Invoke(arg: point) ?? 0))], values: [.. fine.Select(selector: field)]);
        }

        for (var crossings = lattice.CrossingPoints(); (crossings.Length > 0); crossings = lattice.CrossingPoints()) {
            lattice.AnswerCrossings(first: 0, values: [.. crossings.Select(selector: field)]);
        }

        var points = lattice.GradientPoints();
        var gradients = new float[(3 * points.Length)];

        for (var index = 0; (index < points.Length); index++) {
            var gradient = Gradient(field: field, point: points[index]);

            gradients[(3 * index)] = gradient.X;
            gradients[((3 * index) + 1)] = gradient.Y;
            gradients[((3 * index) + 2)] = gradient.Z;
        }
        lattice.AnswerGradients(first: 0, gradients: gradients, values: [.. points.Select(selector: field)]);

        return lattice.Contour();
    }
    private static List<(int A, int B, int C)> Triangles(SdfContour contour) {
        var triangles = new List<(int A, int B, int C)>();

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var (a, b, c, d) = (contour.Quads[(4 * quad)], contour.Quads[((4 * quad) + 1)], contour.Quads[((4 * quad) + 2)], contour.Quads[((4 * quad) + 3)]);

            triangles.Add(item: (a, b, c));
            if (c != d) {
                triangles.Add(item: (a, c, d));
            }
        }

        return triangles;
    }

    [Fact]
    public void ThinDiscRefinesIntoAClosedTwoManifold() {
        var contour = Bake(refine: true);
        var uses = new Dictionary<(int, int), int>();

        foreach (var (a, b, c) in Triangles(contour: contour)) {
            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) }) {
                var key = ((from < to) ? (from, to) : (to, from));

                uses[key] = (uses.GetValueOrDefault(key: key) + 1);
            }
        }

        Assert.NotEmpty(collection: uses);
        Assert.All(collection: uses, action: static use => Assert.Equal(expected: 2, actual: use.Value));
    }

    [Fact]
    public void RefinedMeshLiesWithinHalfALatticeCellOfTheField() {
        var contour = Bake(refine: true);

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            var point = new Vector3(x: contour.Positions[(3 * vertex)], y: contour.Positions[((3 * vertex) + 1)], z: contour.Positions[((3 * vertex) + 2)]);

            Assert.True(condition: (MathF.Abs(Field(point: point)) <= (0.5f * Cell)), userMessage: $"vertex {vertex} at {point} lies {Field(point: point)} from the field");
        }
    }

    // A box with its edges along the lattice's axes and one turned off them: every vertex lies on the box, its edges
    // included. A turned box's corners are left out: a corner cell's planes come from crossings whose gradients the
    // corner's radial field tilts.
    [InlineData(0f)]
    [InlineData(0.3f)]
    [Theory]
    public void BoxEdgesStaySharp(float turn) {
        var contour = Bake(field: point => Box(point: point, turn: turn), refine: false);
        var worst = 0f;

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            var point = Position(contour: contour, vertex: vertex);
            var local = Vector3.Abs(value: (Vector3.Transform(value: point, rotation: Quaternion.CreateFromAxisAngle(angle: turn, axis: Vector3.UnitY)) - new Vector3(x: 0.004f, y: 0.007f, z: -0.003f)));
            var corner = ((turn != 0f) && (MathF.Min(local.X, MathF.Min(local.Y, local.Z)) > (0.05f - Cell)));

            worst = (corner ? worst : MathF.Max(worst, MathF.Abs(Box(point: point, turn: turn))));
        }

        Assert.True(condition: (worst <= (0.05f * Cell)), userMessage: $"a vertex lies {worst} from the box, {(worst / Cell)} cells");
    }

    [Fact]
    public void CarvedBoxFacesOut() {
        var contour = Bake(field: Carved, refine: false);
        var inward = 0;

        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var outward = Vector3.Zero;

            for (var corner = 0; (corner < 4); corner++) {
                var vertex = contour.Quads[((4 * quad) + corner)];

                outward += new Vector3(x: contour.Normals[(3 * vertex)], y: contour.Normals[((3 * vertex) + 1)], z: contour.Normals[((3 * vertex) + 2)]);
            }

            var (a, b, c, d) = (Position(contour: contour, vertex: contour.Quads[(4 * quad)]), Position(contour: contour, vertex: contour.Quads[((4 * quad) + 1)]), Position(contour: contour, vertex: contour.Quads[((4 * quad) + 2)]), Position(contour: contour, vertex: contour.Quads[((4 * quad) + 3)]));

            inward += ((Vector3.Dot(vector1: Vector3.Cross(vector1: (b - a), vector2: (c - a)), vector2: outward) < 0f) ? 1 : 0);
            inward += ((Vector3.Dot(vector1: Vector3.Cross(vector1: (c - a), vector2: (d - a)), vector2: outward) < 0f) ? 1 : 0);
        }

        Assert.Equal(expected: 0, actual: inward);
    }

    [Fact]
    public void RefinementKeepsTheDiscWholeWhereTheLatticeAloneBreaksIt() {
        Assert.True(condition: (Pieces(contour: Bake(refine: false)) > 1));
        Assert.Equal(expected: 1, actual: Pieces(contour: Bake(refine: true)));
    }

    // A turned slab with a block standing on it: a concave crease where the block meets the slab's top, running across
    // the lattice's cells. Every vertex within a cell of the crease lies on it, where the mean-point bias once drew it
    // off by a tenth of a cell from cell to cell, a sawtooth along the crease.
    [Fact]
    public void AConcaveCreaseKeepsItsVerticesOnIt() {
        static float Slab(Vector3 point) => MathF.Abs(point.Y + 0.1f) - 0.03f;
        static float Block(Vector3 point) => Box(point: (point + new Vector3(x: 0f, y: 0.0f, z: 0f)), turn: 0.3f);
        static float Field(Vector3 point) => MathF.Max(MathF.Min(Slab(point: point), Block(point: point)), (point.Length() - 0.2f));

        var contour = Bake(field: Field, refine: false);
        var worst = 0f;

        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            var point = Position(contour: contour, vertex: vertex);

            // The crease: on the slab's top face (y = -0.07) and the block's side, away from the block's corners.
            if ((MathF.Abs(point.Y + 0.07f) > Cell) || (MathF.Abs(Block(point: new Vector3(x: point.X, y: 0f, z: point.Z))) > Cell) || (point.Length() > 0.15f)) {
                continue;
            }

            var local = Vector3.Abs(value: (Vector3.Transform(value: point, rotation: Quaternion.CreateFromAxisAngle(angle: 0.3f, axis: Vector3.UnitY)) - new Vector3(x: 0.004f, y: 0.007f, z: -0.003f)));

            if ((local.X > (0.05f - Cell)) && (local.Z > (0.05f - Cell))) {
                continue;
            }
            worst = MathF.Max(worst, MathF.Abs(Field(point: point)));
        }

        Assert.True(condition: (worst <= (0.02f * Cell)), userMessage: $"a crease vertex lies {worst} from the field, {(worst / Cell)} cells");
    }

    // A turned box splits at its edges: each face's quads shade with the face's own normal up to the edge, where its
    // vertex normals had blended the two faces across a band a cell wide. A sphere, which has no crease, keeps every vertex.
    [Fact]
    public void SplittingAtCreasesShadesEachFaceOfABoxAsItselfAndLeavesASphereWhole() {
        var contour = Bake(field: point => Box(point: point, turn: 0.3f), refine: false);
        var split = contour.SplitAtCreases();
        var (blended, area) = Bent(contour: contour);
        var (bent, _) = Bent(contour: split);

        Assert.True(condition: (split.VertexCount > contour.VertexCount));
        Assert.True(condition: ((bent < (0.03f * area)) && (bent < (0.5f * blended))), userMessage: $"{bent / area:P2} of the box shades more than 10 degrees off its faces, {blended / area:P2} before the split");

        var sphere = Bake(field: static point => (point.Length() - 0.15f), refine: false);

        Assert.Equal(expected: sphere.VertexCount, actual: sphere.SplitAtCreases().VertexCount);
    }
    // The area of the turned box's triangles, two cells or more from its corners, whose vertices' normals shade more than
    // ten degrees off the face they lie on, and the area of them all.
    private static (float Bent, float Area) Bent(SdfContour contour) {
        var rotation = Quaternion.CreateFromAxisAngle(angle: 0.3f, axis: Vector3.UnitY);
        var area = 0f;
        var bent = 0f;

        foreach (var (a, b, c) in Triangles(contour: contour)) {
            var centre = (((Position(contour: contour, vertex: a) + Position(contour: contour, vertex: b)) + Position(contour: contour, vertex: c)) / 3f);
            var local = (Vector3.Transform(value: centre, rotation: rotation) - new Vector3(x: 0.004f, y: 0.007f, z: -0.003f));
            var face = Vector3.Abs(value: local);

            if ((face.X > (0.05f - (2f * Cell))) && (face.Y > (0.05f - (2f * Cell))) && (face.Z > (0.05f - (2f * Cell)))) {
                continue;
            }

            var truth = ((face.X >= MathF.Max(face.Y, face.Z))
                ? new Vector3(x: MathF.Sign(local.X), y: 0f, z: 0f)
                : ((face.Y >= face.Z) ? new Vector3(x: 0f, y: MathF.Sign(local.Y), z: 0f) : new Vector3(x: 0f, y: 0f, z: MathF.Sign(local.Z))));
            var normal = Vector3.Transform(value: Vector3.Normalize(value: ((NormalOf(contour: contour, vertex: a) + NormalOf(contour: contour, vertex: b)) + NormalOf(contour: contour, vertex: c))), rotation: rotation);
            var size = (0.5f * Vector3.Cross(vector1: (Position(contour: contour, vertex: b) - Position(contour: contour, vertex: a)), vector2: (Position(contour: contour, vertex: c) - Position(contour: contour, vertex: a))).Length());

            area += size;
            bent += ((Vector3.Dot(vector1: normal, vector2: truth) < MathF.Cos(x: (10f * (MathF.PI / 180f)))) ? size : 0f);
        }

        return (bent, area);
    }
    // A turned box of two materials, split across the middle: the cells the seam crosses refine and the cells around them
    // merge their fine vertices, and the box's edges stay sharp through both, where a merged vertex at its fine vertices'
    // mean once cut a notch into every edge it crossed.
    // A seam between two materials refines no cell by itself: the material cut places the boundary on the mesh's own
    // edges, and refining the seam's cells merged them into collinear vertices whose quads folded.
    [Fact]
    public void ASeamBetweenTwoMaterialsRefinesNoCell() {
        var count = ((Samples * Samples) * Samples);
        var values = new float[count];
        var materials = new int[count];

        for (var index = 0; (index < count); index++) {
            var point = (Origin + (new Vector3(x: (index % Samples), y: ((index / Samples) % Samples), z: (index / (Samples * Samples))) * Cell));

            values[index] = Box(point: point, turn: 0f);
            materials[index] = ((point.X < 0f) ? 1 : 2);
        }

        var lattice = new SdfSampledLattice(cell: new Vector3(value: Cell), materials: materials, origin: Origin, samplesX: Samples, samplesY: Samples, samplesZ: Samples, values: values);

        Assert.Empty(collection: lattice.Refine(factor: Factor));
    }

    // A tilted disc thinner than the fine cell, which the fine samples alone catch in places and step over in others,
    // contours as one closed two-manifold, and no vertex strays from the disc by more than a fine cell. A field that
    // reads at a fifth of the distance (a scoped or flared shape's bound) contours the same, since the lattice measures
    // a step's change against the field's own slope.
    [InlineData(1f)]
    [InlineData(0.2f)]
    [Theory]
    public void ASheetThinnerThanTheFineCellContoursWhole(float scale) {
        static float Sheet(Vector3 point) => MathF.Max((MathF.Abs(Vector3.Dot(vector1: point, vector2: Normal)) - 0.0008f), (point.Length() - 0.18f));

        var contour = Bake(field: point => (scale * Sheet(point: point)), material: null, refine: true, regions: []);
        var uses = new Dictionary<(int, int), int>();

        foreach (var (a, b, c) in Triangles(contour: contour)) {
            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) }) {
                var key = ((from < to) ? (from, to) : (to, from));

                uses[key] = (uses.GetValueOrDefault(key: key) + 1);
            }
        }

        Assert.Equal(expected: 1, actual: Pieces(contour: contour));
        Assert.All(collection: uses, action: static use => Assert.Equal(expected: 2, actual: use.Value));
        for (var vertex = 0; (vertex < contour.VertexCount); vertex++) {
            Assert.True(condition: (MathF.Abs(Sheet(point: Position(contour: contour, vertex: vertex))) <= (Cell / Factor)), userMessage: $"vertex {vertex} lies {Sheet(point: Position(contour: contour, vertex: vertex))} from the sheet");
        }
    }

    // A region refines the surface it holds though no sample asks it to: a sphere refines where a region names it.
    [Fact]
    public void ARegionRefinesTheSurfaceItHolds() {
        var values = new float[((Samples * Samples) * Samples)];

        for (var index = 0; (index < values.Length); index++) {
            values[index] = ((Origin + (new Vector3(x: (index % Samples), y: ((index / Samples) % Samples), z: (index / (Samples * Samples))) * Cell)).Length() - 0.15f);
        }

        SdfSampledLattice Lattice() => new(cell: new Vector3(value: Cell), materials: new int[values.Length], origin: Origin, samplesX: Samples, samplesY: Samples, samplesZ: Samples, values: [.. values]);

        var plain = Lattice();
        var refined = Lattice();

        _ = plain.Refine(factor: Factor);
        Assert.NotEmpty(collection: refined.Refine(factor: Factor, regions: [new SdfLatticeRegion(High: new Vector3(value: 0.2f), Low: new Vector3(x: 0.1f, y: -0.2f, z: -0.2f))]));
        Assert.True(condition: (refined.MeasuredCells > (plain.MeasuredCells + 50)), userMessage: $"{refined.MeasuredCells} cells measured with the region, {plain.MeasuredCells} without");
    }

    // A speck apart from the surface, as a sheet thinning to a knife edge leaves, is dropped; a surface that small on its
    // own is the whole part and stays.
    [Fact]
    public void ASpeckApartFromTheSurfaceIsDroppedAndASmallSurfaceAloneStays() {
        static float Speck(Vector3 point) => ((point - new Vector3(x: 0.17f, y: 0.17f, z: 0.17f)).Length() - (0.8f * Cell));

        var withSpeck = Bake(field: static point => MathF.Min((point.Length() - 0.12f), Speck(point: point)), refine: false);
        var alone = Bake(field: Speck, refine: false);

        Assert.Equal(expected: 1, actual: Pieces(contour: withSpeck));
        Assert.True(condition: (alone.QuadCount > 0));
    }

    private static Vector3 NormalOf(SdfContour contour, int vertex) =>
        new(x: contour.Normals[(3 * vertex)], y: contour.Normals[((3 * vertex) + 1)], z: contour.Normals[((3 * vertex) + 2)]);

    // The connected pieces of a contour's triangles.
    private static int Pieces(SdfContour contour) {
        var parent = Enumerable.Range(start: 0, count: contour.VertexCount).ToArray();

        int Root(int vertex) {
            while (parent[vertex] != vertex) {
                vertex = parent[vertex] = parent[parent[vertex]];
            }

            return vertex;
        }

        var used = new HashSet<int>();

        foreach (var (a, b, c) in Triangles(contour: contour)) {
            parent[Root(vertex: b)] = Root(vertex: a);
            parent[Root(vertex: c)] = Root(vertex: a);
            _ = used.Add(item: a);
            _ = used.Add(item: b);
            _ = used.Add(item: c);
        }

        return used.Select(selector: Root).Distinct().Count();
    }
}
