using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: an eccentric creation shape (a non-uniformly scaled sphere) bakes its radii into the exact exponent-2
/// superellipsoid gauge, which is 1-Lipschitz, so it neither taxes the whole program's step scale nor opens a field
/// scope of its own: <see cref="SdfProgram.StepScale"/> stays exactly 1 and every stamper emits exactly the scopes the
/// round control does. Each arm pairs the eccentric document with a control differing only in the sphere's scale.
/// </summary>
public sealed class CreationEccentricityLawTests {
    private const string PrototypeId = "squash";

    private static readonly Vector3 EccentricScale = new(
        x: 0.16f,
        y: 0.1f,
        z: 0.125f
    );

    private static CreationDocument Document(Vector3 scale) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            // An ungrouped shape (its own pool slot / its own static instance) and an all-Union group of two, so both
            // emission passes of the dynamic pool and both static forms see an eccentric member.
            Shapes: [
                Sphere(
                    id: 0,
                    scale: scale
                ),
                Sphere(
                    group: 1,
                    id: 1,
                    scale: scale
                ),
                Sphere(
                    group: 1,
                    id: 2,
                    scale: Vector3.One
                ),
            ],
            Frames: null,
            Noise: null
        );
    // The dynamic emission path a body-stamped creation renders through (the shipped avatars' path).
    private static SdfProgram EmitPool(Vector3 scale, CreationDocument? document = null) => CreationFixtures.EmitPool(
        bodyScale: 1f,
        creation: CreationFixtures.Prototype(
            document: (document ?? Document(scale: scale)),
            id: PrototypeId
        )
    );
    private static SdfProgram EmitStatic(Vector3 scale, bool inScope, CreationDocument? document = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstance(
            boundCenter: Vector3.Zero,
            boundRadius: 8f
        );

        if (inScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        CreationStampEmitter.Emit(
            builder: builder,
            document: (document ?? Document(scale: scale)),
            inScope: inScope,
            materialFor: _ => material,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: 1f,
                ReflectionNormal: null
            )
        );

        if (inScope) {
            _ = builder.PopField();
        }

        _ = builder.EndInstance();

        return builder.Build(buildInstanceGrid: false);
    }
    private static void AssertEmitsTheGauge(SdfProgram program) =>
        Assert.Contains(
            collection: program.Instructions,
            filter: instruction => (
                (instruction.Op == SdfOp.ShapeBlend) &&
                (instruction.Shape == ((uint)SdfShapeType.Superellipsoid)) &&
                (instruction.Data0.W == SdfProgramBuilder.MinSuperellipsoidExponent)
            )
        );
    private static int ScopeCount(SdfProgram program) =>
        program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PushField));
    private static ShapeDocument Sphere(int id, Vector3 scale, int group = 0) =>
        new(
            Id: id,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: new Vector3(
                x: (0.5f * id),
                y: 0f,
                z: 0f
            ),
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: group
        );

    [Fact]
    public void InsideTheCallersScopeTheStampOpensNoneOfItsOwn() {
        var program = EmitStatic(
            inScope: true,
            scale: EccentricScale
        );

        // The caller's one scope and nothing else; the global step scale is still 1.
        Assert.Equal(
            expected: 1,
            actual: ScopeCount(program: program)
        );
        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SharedFlareClampIsVisibleWhenTheGlobalScaleIsOne(bool pooled) {
        var plain = Document(scale: Vector3.One);
        var document = plain with {
            Shapes = [
                plain.Shapes![0] with { Group = 1, Flare = new ShapeFlareDocument(
                2f,
                0f,
                1f
            ) },
                plain.Shapes[1] with { Group = 1 },
                plain.Shapes[2] with { Group = 1, Blend = SdfBlendOp.Subtraction },
            ],
        };

        Assert.True(condition: (CreationCanonicalizer.Validate(document: document).Count == 0));
        var program = (pooled
            ? EmitPool(
                Vector3.One,
                document
            )
            : EmitStatic(
                Vector3.One,
                inScope: true,
                document
            )
        );

        Assert.Equal(
            1f,
            program.StepScale
        );
        Assert.Null(value: program.StepScaleBinder);
        var clamp = Assert.Single(collection: program.FieldScopeClamps);

        Assert.Equal(
            3,
            clamp.ShapeCount
        );
        Assert.InRange(
            clamp.StepScale,
            float.Epsilon,
            .99f
        );
        Assert.Equal(
            program.Instructions[clamp.PopInstructionIndex].Data1.Y,
            clamp.StepScale
        );
    }
    [Fact]
    public void TheDynamicPoolOpensNoScopeForEccentricity() {
        var eccentric = EmitPool(scale: EccentricScale);
        var round = EmitPool(scale: Vector3.One);

        Assert.Equal(
            expected: 1f,
            actual: eccentric.StepScale
        );
        Assert.Null(value: eccentric.StepScaleBinder);
        Assert.Equal(
            expected: ScopeCount(program: round),
            actual: ScopeCount(program: eccentric)
        );
        AssertEmitsTheGauge(program: eccentric);
        Assert.Equal(
            expected: 1f,
            actual: round.StepScale
        );
    }
    [Fact]
    public void TheStaticStampOpensNoScopeForEccentricity() {
        var eccentric = EmitStatic(
            inScope: false,
            scale: EccentricScale
        );
        var round = EmitStatic(
            inScope: false,
            scale: Vector3.One
        );

        Assert.Equal(
            expected: 1f,
            actual: eccentric.StepScale
        );
        Assert.Null(value: eccentric.StepScaleBinder);
        Assert.Equal(
            expected: 0,
            actual: ScopeCount(program: eccentric)
        );
        AssertEmitsTheGauge(program: eccentric);
        Assert.Equal(
            expected: 0,
            actual: ScopeCount(program: round)
        );
    }
}
