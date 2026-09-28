using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws that a mirrored creation's contact field is its original's mirror image: the copy stamped with a
/// reflection normal reads, at every sampled point, what the unmirrored copy reads at that point's reflection.</summary>
public sealed class MirroredCreationReflectionLawTests {
    // The field agrees to within rounding of the fixed-point rotations the copies carry; a copy that drops the mirror
    // of an asymmetric shape misses by more than a unit.
    private static readonly FixedQ4816 Tolerance = FixedQ4816.FromDouble(value: 0.002);

    public static TheoryData<string, float, float, float> Cases() => new() {
        { "box", 0f, 0f, 1f },
        { "box", 1f, 2f, -3f },
        { "triangle", 0f, 0f, 1f },
        { "triangle", 1f, 0f, 0f },
        { "triangle", 1f, 2f, -3f },
    };

    private static ShapeDocument Shape(string kind) => new ShapeDocument(
        Id: 0,
        Name: null,
        Type: ((kind == "box") ? SdfSolidPrimitive.Box : SdfSolidPrimitive.Prism),
        Position: new Vector3(
            x: 0.75f,
            y: -0.25f,
            z: 0.5f
        ),
        Rotation: Quaternion.Normalize(value: new Quaternion(
            w: 0.8f,
            x: 0.2f,
            y: -0.3f,
            z: 0.4f
        )),
        Scale: Vector3.One,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    ) with {
        // A right triangle in local XY, clockwise, with no symmetry under x → −x.
        Profile = ((kind == "box")
            ? null
            : new SdfPrismProfile(
                Kind: SdfPrismProfileKind.Convex,
                Vertices: [
                    new Vector2(
                        x: -1f,
                        y: 1f
                    ),
                    new Vector2(
                        x: 1f,
                        y: -1f
                    ),
                    new Vector2(
                        x: -1f,
                        y: -1f
                    ),
                ]
            )),
    };
    private static SdfFieldEvaluator Field(ShapeDocument shape, FixedVector3? normal) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitFixed(
            builder: builder,
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "mirrored-reflection-law",
                Palette: null,
                Shapes: [shape],
                Frames: null
            ),
            transform: new FixedCreationStampTransform(
                Origin: FixedVector3.Zero,
                Rotation: FixedQuaternion.Identity,
                Scale: FixedQ4816.One,
                ReflectionNormal: normal
            ),
            materialFor: _ => material
        );

        return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
    }
    private static FixedVector3 Reflect(FixedVector3 point, FixedVector3 normal) {
        var projection = FixedVector3.Dot(
            left: point,
            right: normal
        );

        return (point - (normal * (projection + projection)));
    }

    // A symmetry plane's mirrored copy is a true mirror too: the contact field of an asymmetric shape folded across a
    // plane reads the same at every point and its reflection across that plane.
    [Fact]
    public void ASymmetryPlanesContactFieldIsSymmetricAboutItsPlane() {
        var planeNormal = Vector3.Normalize(value: new Vector3(
            x: 1f,
            y: 0.5f,
            z: 0f
        ));
        var normal = FixedVector3.FromVector3(value: planeNormal).Normalize();
        var field = Field(
            normal: null,
            shape: Shape(kind: "triangle") with {
                Domain = [new ShapeDomainOp.Symmetry(Normal: planeNormal)],
            }
        );
        var worst = FixedQ4816.Zero;

        for (var x = -6; (x <= 6); x++) {
            for (var y = -6; (y <= 6); y++) {
                for (var z = -6; (z <= 6); z++) {
                    var point = FixedVector3.FromVector3(value: new Vector3(
                        x: (x * 0.25f),
                        y: (y * 0.25f),
                        z: (z * 0.25f)
                    ));

                    Assert.True(condition: field.TryDistance(
                        distance: out var near,
                        material: out _,
                        position: FixedPosition.FromLocal(local: point)
                    ));
                    Assert.True(condition: field.TryDistance(
                        distance: out var far,
                        material: out _,
                        position: FixedPosition.FromLocal(local: Reflect(
                            normal: normal,
                            point: point
                        ))
                    ));

                    worst = FixedQ4816.Max(
                        x: worst,
                        y: FixedQ4816.Abs(value: (near - far))
                    );
                }
            }
        }

        Assert.True(
            condition: (worst <= Tolerance),
            userMessage: $"the folded triangle's field differs from its reflection by {((double)worst)}"
        );
    }
    [MemberData(memberName: nameof(Cases))]
    [Theory]
    public void AMirroredCopyReadsTheOriginalsFieldAtTheReflectedPoint(string kind, float normalX, float normalY, float normalZ) {
        var shape = Shape(kind: kind);
        var normal = FixedVector3.FromVector3(value: Vector3.Normalize(value: new Vector3(
            x: normalX,
            y: normalY,
            z: normalZ
        ))).Normalize();
        var original = Field(
            normal: null,
            shape: shape
        );
        var mirrored = Field(
            normal: normal,
            shape: shape
        );
        var worst = FixedQ4816.Zero;
        var worstPoint = FixedVector3.Zero;

        for (var x = -6; (x <= 6); x++) {
            for (var y = -6; (y <= 6); y++) {
                for (var z = -6; (z <= 6); z++) {
                    var point = FixedVector3.FromVector3(value: new Vector3(
                        x: (x * 0.25f),
                        y: (y * 0.25f),
                        z: (z * 0.25f)
                    ));

                    Assert.True(condition: mirrored.TryDistance(
                        distance: out var copy,
                        material: out _,
                        position: FixedPosition.FromLocal(local: point)
                    ));
                    Assert.True(condition: original.TryDistance(
                        distance: out var source,
                        material: out _,
                        position: FixedPosition.FromLocal(local: Reflect(
                            normal: normal,
                            point: point
                        ))
                    ));

                    var difference = FixedQ4816.Abs(value: (copy - source));

                    if (difference > worst) {
                        worst = difference;
                        worstPoint = point;
                    }
                }
            }
        }

        Assert.True(
            condition: (worst <= Tolerance),
            userMessage: $"the {kind} mirrored across {normal.ToVector3()} differs from its reflection by {((double)worst)} at {worstPoint.ToVector3()}"
        );
    }
}
