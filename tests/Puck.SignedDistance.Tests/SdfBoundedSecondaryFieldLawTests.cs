using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: the shadow and ambient-occlusion walks read a bounded instance (<see cref="SdfProgram.InstanceBoundsSecondaryField"/>)
/// as max(field, gap to its packed influence sphere). That never steps a march past a surface because the solid those
/// walks see lies inside the sphere: outside it the gap is at most the true distance, and inside it the floor is negative,
/// so every surface and interior keeps its sign. An instance is bounded only where the root is a hard union of
/// independent operands and nothing those walks drop can widen its solid past the sphere: an optional clipper (the
/// intersection family, a xor, a morph) and a lane erosion keep the instance's own field. The fixed-point evaluator is
/// the oracle for containment over the warp-free kinds it interprets; warps and dynamic bodies rest on the same
/// influence contract the tile and group masks cull by.
/// </summary>
public sealed class SdfBoundedSecondaryFieldLawTests {
    private const int Samples = 20;
    private const float Tolerance = 0.001f;

    // Each kind with the geometric radius an emitter states for it; the packer widens that by the instance's blend and
    // field-op reach, which is the sphere the walks read.
    private static readonly Dictionary<string, (float Radius, Action<SdfProgramBuilder> Emit)> Kinds = new(comparer: StringComparer.Ordinal) {
        ["sphere"] = (0.5f, builder => builder.Sphere(material: 0, radius: 0.5f)),
        ["rounded box"] = (0.6f, builder => builder.Box(halfExtents: new Vector3(x: 0.4f, y: 0.2f, z: 0.3f), material: 0, round: 0.05f)),
        ["smooth union of gauges"] = ((0.3f + new Vector3(x: 0.3f, y: 0.04f, z: 0.05f).Length()), builder => builder
            .PushField()
            .Superellipsoid(exponent: 2.1f, material: 0, radii: new Vector3(x: 0.3f, y: 0.04f, z: 0.05f))
            .ResetPoint().Translate(offset: new Vector3(x: 0.25f, y: 0.05f, z: 0f))
            .Superellipsoid(blend: SdfBlendOp.SmoothUnion, exponent: 2.1f, material: 0, radii: new Vector3(x: 0.3f, y: 0.04f, z: 0.05f), smooth: 0.06f)
            .PopField()),
        ["dilated scope"] = (0.3f, builder => builder.PushField().Sphere(material: 0, radius: 0.3f).Dilate(radius: 0.15f).PopField()),
        ["onion scope"] = (0.4f, builder => builder.PushField().Sphere(material: 0, radius: 0.4f).Onion(thickness: 0.05f).PopField()),
        ["carved subject with an optional union"] = (0.5f, builder => builder
            .PushField()
            .Sphere(material: 0, radius: 0.5f)
            .ResetPoint().Translate(offset: new Vector3(x: 0.3f, y: 0f, z: 0f)).Sphere(detail: true, material: 0, radius: 0.1f)
            .ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.4f, z: 0f)).Sphere(blend: SdfBlendOp.Subtraction, material: 0, radius: 0.3f)
            .PopField()),
        ["required clipper"] = (0.5f, builder => builder
            .PushField()
            .Sphere(material: 0, radius: 0.5f)
            .ResetPoint().Box(blend: SdfBlendOp.Intersection, halfExtents: new Vector3(x: 0.6f, y: 0.2f, z: 0.6f), material: 0, round: 0f)
            .PopField()),
    };

    public static TheoryData<string> BoundedKinds => new(values: Kinds.Keys);

    [MemberData(nameof(BoundedKinds))]
    [Theory]
    public void ABoundedInstancesSolidLiesInsideItsPackedSphereSoTheFloorKeepsEverySign(string kind) {
        var (stated, emit) = Kinds[kind];
        var program = Build(emit: emit, radius: stated);
        var radius = program.InspectInstance(index: 0).BoundRadius;
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.True(condition: program.InstanceBoundsSecondaryField(index: 0), userMessage: $"The {kind} is bounded.");
        for (var x = 0; (x < Samples); x++) {
            for (var y = 0; (y < Samples); y++) {
                for (var z = 0; (z < Samples); z++) {
                    var point = (((new Vector3(x: x, y: y, z: z) * (2f / (Samples - 1))) - Vector3.One) * (1.25f * radius));
                    var gap = (point.Length() - radius);

                    Assert.True(condition: evaluator.TryDistance(FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point)), out var raw, out _));
                    var field = ((float)raw);

                    if (field <= 0f) {
                        Assert.True(condition: (gap <= Tolerance),
                            userMessage: $"The {kind}'s solid reaches {point}, {gap} outside its packed sphere of radius {radius}.");
                    }
                    Assert.Equal(expected: MathF.Sign(x: field), actual: MathF.Sign(x: MathF.Max(x: field, y: gap)));
                }
            }
        }
    }
    [Fact]
    public void AnInstanceIsBoundedOnlyWhereNothingTheWalksDropCanWidenItsSolid() {
        Assert.True(condition: Bounded(emit: builder => builder.PushField().RotatePlane(driver: SdfAxis.Y, origin: 0f, plane: SdfPlane.XZ, rate: 2f)
            .Box(halfExtents: new Vector3(x: 0.4f, y: 0.4f, z: 0.1f), material: 0, round: 0f).PopField()), userMessage: "A warp's reach is in its bound.");
        Assert.True(condition: Bounded(emit: builder => builder.PushField().Scale(scale: new Vector3(x: 4f, y: 1f, z: 2f))
            .Sphere(material: 0, radius: 0.1f).PopField()), userMessage: "A non-uniform scale's reach is in its bound.");
        foreach (var blend in ((ReadOnlySpan<SdfBlendOp>)[SdfBlendOp.Intersection, SdfBlendOp.SmoothIntersection, SdfBlendOp.ChamferIntersection, SdfBlendOp.Xor])) {
            Assert.False(condition: Bounded(emit: builder => builder.PushField().Sphere(material: 0, radius: 0.5f)
                .ResetPoint().Sphere(blend: blend, detail: true, material: 0, radius: 0.2f, smooth: 0.05f).PopField()),
                userMessage: $"A {blend} the secondary walks drop can widen the solid past the bound.");
        }
        Assert.False(condition: Bounded(emit: builder => builder.PushField().LaneErode(from: 0f, lane: 0, noiseScale: 4f, reach: 0.5f, to: 1f)
            .Sphere(material: 0, radius: 0.5f).PopField()), userMessage: "A lane erosion keeps its own field.");
        Assert.False(condition: Bounded(emit: builder => builder.PushField().Sphere(material: 0, radius: 0.5f).PopField(),
            root: builder => builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: 0, radius: 0.5f, smooth: 0.1f)),
            userMessage: "Under a root that is not a hard union, an instance's field feeds the accumulator's later operations.");
    }

    private static bool Bounded(Action<SdfProgramBuilder> emit, Action<SdfProgramBuilder>? root = null) =>
        Build(emit: emit, radius: 0.5f, root: root).InstanceBoundsSecondaryField(index: 0);
    private static SdfProgram Build(Action<SdfProgramBuilder> emit, float radius, Action<SdfProgramBuilder>? root = null) {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: radius).ResetPoint();
        emit(obj: builder);
        _ = builder.EndInstance();
        if (root is not null) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: 3f, y: 0f, z: 0f));
            root(obj: builder);
        }
        return builder.Build(buildInstanceGrid: false);
    }
}
