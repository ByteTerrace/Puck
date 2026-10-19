using System.Numerics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws over <see cref="SdfMeshAudit"/>, which measures a baked mesh against the field its group was sampled from. A mesh
/// contoured from the field audits clean; a vertex pushed off the surface, a patch the mesh drops and a field that is only
/// a loose bound are each found where they lie and named by the shape there; a dynamic group's field lies in its slot's
/// frame, posed as its geometry hashed; and each shape's own field answers that shape alone.
/// </summary>
public sealed class SdfMeshAuditLawTests {
    private const float Radius = 0.3f;
    private const int Samples = 41;
    private static readonly Vector3 Low = new(value: -0.5f);
    private static readonly Vector3 High = new(value: 0.5f);

    // One static instance whose chains the emit writes after its reset.
    private static SdfProgram Program(Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return emit(arg1: builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1f).ResetPoint(), arg2: material).EndInstance().Build();
    }
    private static SdfProgram Sphere() => Program(emit: static (builder, material) => builder.Sphere(material: material, radius: Radius));
    private static (SdfMeshBakeGroup Group, SdfFieldEvaluator Field, List<(string Name, SdfFieldEvaluator Field)> Shapes) Fields(SdfProgram program, IReadOnlyList<DynamicTransform>? transforms = null) {
        var group = Assert.Single(collection: SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: 40f, program: program, transforms: (transforms ?? [])));
        var field = new SdfFieldEvaluator(program: SdfMeshAudit.Field(group: group, program: program), reading: SdfFieldReading.Render);
        var shapes = SdfMeshAudit.ShapeFields(group: group, program: program).Select(selector: static shape => ($"shape {shape.Instruction}", new SdfFieldEvaluator(program: shape.Field, reading: SdfFieldReading.Render))).ToList();

        return (group, field, shapes);
    }
    private static SdfMeshAuditReport Audit(SdfMesh mesh, SdfFieldEvaluator field, List<(string Name, SdfFieldEvaluator Field)> shapes) =>
        SdfMeshAudit.Audit(field: field, mesh: mesh, options: new SdfMeshAuditOptions(), shapes: shapes);
    private static SdfMesh With(SdfMesh mesh, Vector3[]? positions = null, uint[]? indices = null) =>
        new(indices: (indices ?? mesh.Indices.ToArray()), normals: mesh.Normals, positions: (positions ?? mesh.Positions.ToArray()), triangleMaterials: new uint[(indices ?? mesh.Indices.ToArray()).Length / 3]);

    [Fact]
    public void AMeshContouredFromItsFieldAuditsClean() {
        var (_, field, shapes) = Fields(program: Sphere());
        var report = Audit(field: field, mesh: SdfMeshContour.Bake(field: field, high: High, low: Low, samples: Samples), shapes: shapes);

        Assert.True(condition: (report.Triangles > 0));
        Assert.InRange(actual: report.Cell, low: 0.015f, high: 0.035f);
        Assert.Equal(expected: 0, actual: report.VertexError.Over);
        Assert.Equal(expected: 0, actual: report.MissingCrossings);
        Assert.True(condition: (report.Crossings > 0));
        Assert.Equal(expected: 0, actual: (report.Folds + report.Flipped + report.OpenEdges + report.NonManifoldEdges));
        Assert.Equal(expected: 0, actual: report.SlopeDeficit.Over);
        Assert.Empty(collection: report.LooseShapes);
        Assert.All(collection: report.Clusters, action: static cluster => Assert.Equal(expected: 0, actual: ((cluster.Off + cluster.Missing) + cluster.Folded)));
    }
    [Fact]
    public void AVertexOffTheSurfaceIsFoundWhereItLiesAndNamedByItsShape() {
        var (_, field, shapes) = Fields(program: Sphere());
        var mesh = SdfMeshContour.Bake(field: field, high: High, low: Low, samples: Samples);
        var positions = mesh.Positions.ToArray();
        var top = Array.IndexOf(array: positions, value: positions.MaxBy(keySelector: static position => position.Y));

        positions[top] += new Vector3(x: 0f, y: 0.06f, z: 0f);

        var report = Audit(field: field, mesh: With(mesh: mesh, positions: positions), shapes: shapes);
        var worst = report.Clusters[0];

        Assert.True(condition: (report.VertexError.Max > 0.05f));
        Assert.True(condition: (worst.Off > 0));
        Assert.True(condition: (Vector3.Distance(value1: worst.Worst, value2: positions[top]) < 0.03f), userMessage: $"the worst sample {worst.Worst} lies far from the moved vertex {positions[top]}");
        Assert.Equal(expected: shapes[0].Name, actual: worst.Shapes[0].Name);
        Assert.InRange(actual: worst.Shapes[0].Distance, low: 0f, high: 0.06f);
    }
    [Fact]
    public void ASurfaceTheMeshDropsIsUncovered() {
        var (_, field, shapes) = Fields(program: Sphere());
        var mesh = SdfMeshContour.Bake(field: field, high: High, low: Low, samples: Samples);
        var positions = mesh.Positions.ToArray();
        var indices = mesh.Indices.ToArray();
        var kept = Enumerable.Range(start: 0, count: (indices.Length / 3))
            .Where(predicate: triangle => ((((positions[indices[3 * triangle]].Y + positions[indices[(3 * triangle) + 1]].Y) + positions[indices[(3 * triangle) + 2]].Y) / 3f) < 0.15f))
            .SelectMany(selector: triangle => new[] { indices[3 * triangle], indices[(3 * triangle) + 1], indices[(3 * triangle) + 2] })
            .ToArray();
        var report = Audit(field: field, mesh: With(indices: kept, mesh: mesh), shapes: shapes);
        var worst = report.Clusters[0];

        Assert.True(condition: (report.MissingCrossings > 0));
        Assert.True(condition: (report.OpenEdges > 0));
        Assert.True(condition: (worst.Missing > 0));
        Assert.True(condition: (worst.Center.Y > 0.15f), userMessage: $"the uncovered surface centres at {worst.Center}, below the dropped cap");
        Assert.True(condition: (worst.WorstError > report.Cell));
    }
    [Fact]
    public void ALooseFieldIsMeasuredAndNamedByTheShapeThatLoosensIt() {
        var program = Program(emit: static (builder, material) => builder.Scale(scale: new Vector3(x: 1f, y: 2.5f, z: 1f)).Sphere(material: material, radius: (Radius / 2.5f)));
        var (_, field, shapes) = Fields(program: program);
        var report = Audit(field: field, mesh: SdfMeshContour.Bake(field: field, high: High, low: Low, samples: Samples), shapes: shapes);

        _ = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: SdfMeshAudit.Field(group: Fields(program: program).Group, program: program)));
        // The scale stretches the sphere along y alone, so its field is loose toward its poles.
        Assert.True(condition: (report.SlopeDeficit.Over > 0));
        Assert.InRange(actual: report.SlopeDeficit.Max, low: 0.5f, high: 0.65f);
        Assert.Equal(expected: shapes[0].Name, actual: Assert.Single(collection: report.LooseShapes).Name);
    }
    [Fact]
    public void ADynamicGroupsFieldLiesInItsSlotsFrame() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder
            .DynamicInstance(boundOffset: Vector3.Zero, boundRadius: 1f, slot: 0, emit: instance => instance.ResetPoint().TransformDynamic(slot: 0).Sphere(material: material, radius: Radius))
            .Build();
        var transforms = new[] { new DynamicTransform(Lanes: Vector4.Zero, Orientation: Quaternion.CreateFromYawPitchRoll(yaw: 0.7f, pitch: 0.2f, roll: 0f), Position: new Vector3(x: 5f, y: 1f, z: -3f)) };
        var (group, field, shapes) = Fields(program: program, transforms: transforms);

        Assert.True(condition: group.Dynamic);
        Assert.Equal(expected: 0, actual: group.Slot);
        Assert.Equal(expected: 0f, actual: SdfMeshAudit.Distance(field: field, point: new Vector3(x: Radius, y: 0f, z: 0f)), tolerance: 1e-4f);
        Assert.Equal(expected: -Radius, actual: SdfMeshAudit.Distance(field: Assert.Single(collection: shapes).Field, point: Vector3.Zero), tolerance: 1e-4f);
        Assert.Equal(expected: Vector3.UnitY, actual: SdfMeshAudit.Gradient(field: field, point: new Vector3(x: 0f, y: Radius, z: 0f), step: 0.001f), comparer: new Near());
    }
    [Fact]
    public void EachShapesOwnFieldAnswersThatShapeAlone() {
        var program = Program(emit: static (builder, material) => builder
            .Translate(offset: new Vector3(x: -0.2f, y: 0f, z: 0f)).Sphere(material: material, radius: 0.15f)
            .ResetPoint().Translate(offset: new Vector3(x: 0.2f, y: 0f, z: 0f)).Box(halfExtents: new Vector3(value: 0.1f), material: material, round: 0f, blend: SdfBlendOp.SmoothUnion, smooth: 0.05f).Dilate(radius: 0.01f));
        var (group, field, shapes) = Fields(program: program);
        var instructions = group.InstructionIndices(program: program);

        Assert.Equal(expected: program.Instances[0].End - program.Instances[0].First, actual: instructions.Count);
        Assert.Equal(expected: 2, actual: shapes.Count);
        Assert.Equal(expected: -0.15f, actual: SdfMeshAudit.Distance(field: shapes[0].Field, point: new Vector3(x: -0.2f, y: 0f, z: 0f)), tolerance: 1e-4f);
        Assert.Equal(expected: -0.11f, actual: SdfMeshAudit.Distance(field: shapes[1].Field, point: new Vector3(x: 0.2f, y: 0f, z: 0f)), tolerance: 1e-4f);
        Assert.True(condition: (SdfMeshAudit.Distance(field: field, point: Vector3.Zero) < SdfMeshAudit.Distance(field: shapes[1].Field, point: Vector3.Zero)));
    }

    private sealed class Near : IEqualityComparer<Vector3> {
        public bool Equals(Vector3 x, Vector3 y) => (Vector3.Distance(value1: x, value2: y) < 1e-3f);
        public int GetHashCode(Vector3 obj) => 0;
    }
}
