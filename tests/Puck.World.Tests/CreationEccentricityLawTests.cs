using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: an eccentric creation shape (a non-uniformly scaled sphere, baked as an ellipsoid) never taxes the whole
/// program's step scale. Every stamper that emits creation shapes wraps it in a field scope of its own, or rides the
/// caller's, so its Lipschitz factor clamps its own candidate at the pop and <see cref="SdfProgram.StepScale"/> stays
/// exactly 1. Each arm pairs the eccentric document with a control differing only in the sphere's scale.
/// </summary>
public sealed class CreationEccentricityLawTests {
    private const string PrototypeId = "squash";

    private static readonly Vector3 EccentricScale = new(
        x: 0.16f,
        y: 0.1f,
        z: 0.125f
    );

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
    private static SdfProgram EmitStatic(Vector3 scale, bool inScope) {
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
            document: Document(scale: scale),
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
    // The dynamic emission path a body-stamped creation renders through (the shipped avatars' path).
    private static SdfProgram EmitPool(Vector3 scale) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: Document(scale: scale),
            source: PrototypeId
        );
        var creation = new WorldPrototype(
            Id: PrototypeId,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [
                new WorldLook(
                    Name: "rig",
                    Source: new WorldLookSource.Creation(PrototypeId: PrototypeId),
                    Scale: 1f,
                    Motion: WorldLookMotion.Default
                ),
            ],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [
                new WorldStampPool.BodyStamp(
                    BodyIndex: 0,
                    Creation: creation,
                    Scale: 1f,
                    Motion: WorldLookMotion.Default
                ),
            ]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            definition: definition,
            probeWorstCase: false,
            maxPlacementScale: 1f,
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false);
    }
    private static int ScopeCount(SdfProgram program) =>
        program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PushField));

    [Fact]
    public void TheStaticStampScopesEachEccentricShape() {
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
        Assert.Null(eccentric.StepScaleBinder);
        // Two eccentric shapes, two scopes; the round control opens none.
        Assert.Equal(
            expected: 2,
            actual: ScopeCount(program: eccentric)
        );
        Assert.Equal(
            expected: 0,
            actual: ScopeCount(program: round)
        );
    }
    [Fact]
    public void InsideTheCallersScopeTheStampOpensNoneOfItsOwn() {
        var program = EmitStatic(
            inScope: true,
            scale: EccentricScale
        );

        // The caller's one scope, and its pop carries the clamp: the global step scale is still 1.
        Assert.Equal(
            expected: 1,
            actual: ScopeCount(program: program)
        );
        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
    }
    [Fact]
    public void TheDynamicPoolScopesTheUngroupedShapeAndTheGroup() {
        var eccentric = EmitPool(scale: EccentricScale);
        var round = EmitPool(scale: Vector3.One);

        Assert.Equal(
            expected: 1f,
            actual: eccentric.StepScale
        );
        Assert.Null(eccentric.StepScaleBinder);
        // Pass 1 scopes the ungrouped eccentric shape; pass 2 scopes the group once for both members.
        Assert.Equal(
            expected: (ScopeCount(program: round) + 2),
            actual: ScopeCount(program: eccentric)
        );
        Assert.Equal(
            expected: 1f,
            actual: round.StepScale
        );
    }
}
