using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeDocument.Shear"/> and <see cref="ShapeDocument.Bumps"/> (<see cref="ShapeBumpDocument"/>)
/// are admitted on every primitive, refused by name against non-finite values, equal shear target and driver axes, a bump
/// list past <see cref="ShapeBumpDocument.MaxBumps"/>, or negative Radii; both warps emit inside their own field
/// scope on both emission paths (their Lipschitz factor would otherwise fold into the whole program's step scale);
/// and both grow a shape's cull bound (mirroring <c>ShapeFlareLawTests</c>'s convention).
/// </summary>
public sealed class ShapeWarpLawTests {
    private const string PrototypeId = "warped";

    private static readonly ShapeShearDocument Shear = new(
        Linear: 0.4f,
        Quadratic: 0.6f
    );
    private static readonly ShapeBumpDocument[] Bumps = [new(
            Center: new(
                x: 0f,
                y: 0f,
                z: 0.3f
            ),
            Radii: new(
                x: 0.2f,
                y: 0.2f,
                z: 0.2f
            ),
            Push: new(
                x: 0f,
                y: 0f,
                z: 0.15f
            )
        )];

    private static void AssertWarpIsScoped(SdfProgram program) {
        var instructions = program.Instructions.ToList();
        var shapeIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.ShapeBlend));
        var pushIndex = instructions.FindLastIndex(
            startIndex: shapeIndex,
            match: static instruction => (instruction.Op == SdfOp.PushField)
        );
        var popIndex = instructions.FindIndex(
            startIndex: shapeIndex,
            match: static instruction => (instruction.Op == SdfOp.PopField)
        );

        Assert.True(
            condition: ((pushIndex >= 0) && (pushIndex < shapeIndex)),
            userMessage: "the warped shape has no PushField before it."
        );
        Assert.True(
            condition: (popIndex > shapeIndex),
            userMessage: "the warped shape has no PopField after it."
        );
    }
    private static CreationDocument Document(params ShapeDocument[] shapes) => CreationFixtures.Document(
        name: PrototypeId,
        shapes: shapes
    );
    // --- Pool emission path (WorldStampPool.EmitShape's warp prefix) ---

    private static SdfProgram EmitPool(ShapeDocument shape, float bodyScale) => CreationFixtures.EmitPool(
        bodyScale: bodyScale,
        name: PrototypeId,
        shapes: [shape]
    );
    // --- Static emission path (CreationStampEmitter.EmitShapeChain's BuildTransformChain) ---

    private static SdfProgram EmitStatic(ShapeDocument shape, float stampScale) => CreationFixtures.EmitStatic(
        document: Document(shape),
        stampScale: stampScale
    );
    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, ShapeShearDocument? shear = null, IReadOnlyList<ShapeBumpDocument>? bumps = null, int id = 0, IReadOnlyList<ShapeDomainOp>? domain = null) =>
        new(
            Id: id,
            Name: null,
            Type: type,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Domain: domain,
            Shear: shear,
            Bumps: bumps
        );

    [Fact]
    public void ANegativeBumpRadiusIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                bumps: [Bumps[0] with { Radii = new DocumentVector3(
                        x: -0.1f,
                        y: 0.2f,
                        z: 0.2f
                    ) }]
            )),
            needle: "radii"
        );
    [Fact]
    public void ANonFiniteBumpPushIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                bumps: [Bumps[0] with { Push = new DocumentVector3(
                        x: float.NaN,
                        y: 0f,
                        z: 0f
                    ) }]
            )),
            needle: "push"
        );
    [Fact]
    public void ANonFiniteShearIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                shear: new ShapeShearDocument(Linear: float.NaN)
            )),
            needle: "shear"
        );
    [Fact]
    public void AShearWithTheSameTargetAndDriverIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                shear: new ShapeShearDocument(
                    Linear: 0.1f,
                    Target: 1,
                    Driver: 1
                )
            )),
            needle: "shear"
        );
    // THE LAW: a shear/bump is a warp whose Lipschitz factor would otherwise fold into the WHOLE program's step
    // scale, so both emitters give a warped shape its own field scope. The un-warped, uniformly scaled Box is the
    // control (no scope at all).
    [Fact]
    public void AWarpedShapeEmitsInsideItsOwnFieldScopeOnBothPaths() {
        AssertWarpIsScoped(program: EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Shear,
                Bumps
            ),
            bodyScale: 1f
        ));
        AssertWarpIsScoped(program: EmitStatic(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Shear,
                Bumps
            ),
            stampScale: 1f
        ));
        Assert.Equal(
            expected: 1f,
            actual: EmitPool(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    Shear,
                    Bumps
                ),
                bodyScale: 1f
            ).StepScale
        );
        Assert.Equal(
            expected: 1f,
            actual: EmitStatic(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    Shear,
                    Bumps
                ),
                stampScale: 1f
            ).StepScale
        );
    }
    [Fact]
    public void ComposedWarpBoundsCoverTheInverseImageAtEveryPlacementScale() {
        var shape = Shape(
            SdfSolidPrimitive.Sphere,
            Vector3.One,
            shear: new(
                Linear: 0f,
                Cubic: 1f,
                Target: 0,
                Driver: 1
            )
        ) with {
            Flare = new(
            Amount: 0f,
            Bulge: 0f,
            Span: 1f,
            StartScale: 3f
        ),
        };
        // The primitive point (0,1,0) maps back through shear to (-1,1,0), then profile to (-3,1,0).
        var surfaceReach = MathF.Sqrt(x: 10f);

        foreach (var scale in new[] { 0.5f, 1f, 2f }) {
            var bound = CreationStampEmitter.ShapeStampBound(
                document: Document(shape),
                shapeIndex: 0,
                transform: new CreationStampTransform(
                    Vector3.Zero,
                    Quaternion.Identity,
                    scale,
                    null
                )
            );

            Assert.True(condition: (bound.Radius >= (surfaceReach * scale)));
            var pool = EmitPool(
                bodyScale: scale,
                shape: shape
            );

            Assert.True(condition: (pool.Instances[0].Radius >= (surfaceReach * scale)));
        }
    }
    // Sweep is not a closed solid: its curve facet refuses the warp facets by name (ShapeCurveLawTests), so this
    // every-primitive admission law ranges over the closed set only.
    public static TheoryData<SdfSolidPrimitive> EveryPrimitive() => CreationFixtures.EveryPrimitiveExcept(excluded: SdfSolidPrimitive.Sweep);
    [MemberData(memberName: nameof(EveryPrimitive))]
    [Theory]
    public void ShearAndBumpsAreAdmittedOnEveryPrimitive(SdfSolidPrimitive type) =>
        CreationFixtures.AssertAccepts(document: Document(Shape(
            type,
            Vector3.One,
            Shear,
            Bumps
        )));
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void BothPathsEmitAShearInstructionCarryingTheAuthoredCoefficientsAtScaleOne(bool pooled) {
        var shape = Shape(
            SdfSolidPrimitive.Box,
            Vector3.One,
            shear: Shear
        );
        var program = (pooled
            ? EmitPool(
                bodyScale: 1f,
                shape: shape
            )
            : EmitStatic(
                shape: shape,
                stampScale: 1f
            ));
        var instruction = program.Instructions.Single(predicate: static i => (i.Op == SdfOp.Shear));

        Assert.Equal(
            expected: Shear.Linear,
            actual: instruction.Data0.X,
            precision: 6
        );
        Assert.Equal(
            expected: Shear.Quadratic,
            actual: instruction.Data0.Y,
            precision: 6
        );
    }
    // The probe reserves every warp form on every slot whatever a body authors, so a live flared, sheared, or bumped
    // shape never outgrows the envelope.
    [InlineData(SdfOp.AxialProfile)]
    [InlineData(SdfOp.GaussianPush)]
    [InlineData(SdfOp.Shear)]
    [Theory]
    public void ThePoolProbeReservesEveryWarpFormWithoutAnyAuthored(SdfOp op) => Assert.Contains(
        collection: CreationFixtures.PoolProbe.Instructions,
        filter: i => (i.Op == op)
    );
    [Fact]
    public void TheStaticPathEmitsOneGaussianPushPerBump() {
        var program = EmitStatic(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                bumps: Bumps
            ),
            stampScale: 1f
        );
        var instructions = program.Instructions.ToList();
        var headIndex = instructions.FindIndex(match: static i => (i.Op == SdfOp.GaussianPush));

        Assert.True(
            condition: (headIndex >= 0),
            userMessage: "no GaussianPush head emitted."
        );
        Assert.Equal(
            Bumps.Length,
            instructions.Count(predicate: i => (i.Op == SdfOp.GaussianPush))
        );
        Assert.Equal(
            Bumps[0].Push.Z,
            BitConverter.UInt32BitsToSingle(value: instructions[headIndex].Shape)
        );
    }
    // THE LAW: a bump grows the shape's reach by up to the sum of its Push magnitudes (ShapeBumpDocument.ReachExtra);
    // the un-bumped shape is the control.
    [Fact]
    public void TheStaticPathWidensABumpedShapesReachByThePushSum() {
        var extra = ShapeBumpDocument.ReachExtra(bumps: Bumps);
        var bumped = CreationStampEmitter.RenderReach(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                bumps: Bumps
            )),
            scale: 1f,
            fontFor: null
        );
        var plain = CreationStampEmitter.RenderReach(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One
            )),
            scale: 1f,
            fontFor: null
        );

        Assert.True(condition: (extra > 0f));
        Assert.Equal(
            actual: bumped,
            expected: (plain + extra),
            precision: 4
        );
    }
    [Fact]
    public void TooManyBumpsIsRefusedByName() {
        var tooMany = new ShapeBumpDocument[(ShapeBumpDocument.MaxBumps + 1)];

        Array.Fill(
            array: tooMany,
            value: Bumps[0]
        );

        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                bumps: tooMany
            )),
            needle: "bump"
        );
    }
}
