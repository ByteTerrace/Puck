using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeDocument.Shear"/> and <see cref="ShapeDocument.Bumps"/> (<see cref="ShapeBumpDocument"/>)
/// are admitted on every primitive, refused by name against non-finite values, a nonzero reserved Shear.Z, a bump
/// list past <see cref="ShapeBumpDocument.MaxBumps"/>, or negative Radii; both warps emit inside their own field
/// scope on both emission paths (their Lipschitz factor would otherwise fold into the whole program's step scale);
/// and both grow a shape's cull bound (mirroring <c>ShapeFlareLawTests</c>'s convention).
/// </summary>
public sealed class ShapeWarpLawTests {
    private const string PrototypeId = "warped";
    private static readonly ShapeShearDocument Shear = new(Linear: 0.4f, Quadratic: 0.6f);
    private static readonly ShapeBumpDocument[] Bumps = [new(Center: new(x: 0f, y: 0f, z: 0.3f), Radii: new(x: 0.2f, y: 0.2f, z: 0.2f), Push: new(x: 0f, y: 0f, z: 0.15f))];

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
    private static CreationDocument Document(params ShapeDocument[] shapes) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: shapes,
            Frames: null
        );
    private static void AssertCanonicalizerAccepts(CreationDocument document) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.Empty(collection: violations);
    }
    private static void AssertCanonicalizerRefusesNaming(CreationDocument document, string needle) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: needle)
        );
    }

    // Sweep is not a closed solid: its curve facet refuses the warp facets by name (ShapeCurveLawTests), so this
    // every-primitive admission law ranges over the closed set only.
    public static IEnumerable<object[]> EveryPrimitive() =>
        Enum.GetValues<SdfSolidPrimitive>().Where(predicate: static type => (type != SdfSolidPrimitive.Sweep)).Select(selector: static type => new object[] { type });

    [Theory]
    [MemberData(memberName: nameof(EveryPrimitive))]
    public void ShearAndBumpsAreAdmittedOnEveryPrimitive(SdfSolidPrimitive type) =>
        AssertCanonicalizerAccepts(document: Document(Shape(type, Vector3.One, Shear, Bumps)));

    [Fact]
    public void AShearWithTheSameTargetAndDriverIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, shear: new ShapeShearDocument(Linear: 0.1f, Target: 1, Driver: 1))),
            needle: "shear"
        );

    [Fact]
    public void ANonFiniteShearIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, shear: new ShapeShearDocument(Linear: float.NaN))),
            needle: "shear"
        );

    [Fact]
    public void TooManyBumpsIsRefusedByName() {
        var tooMany = new ShapeBumpDocument[(ShapeBumpDocument.MaxBumps + 1)];

        Array.Fill(array: tooMany, value: Bumps[0]);

        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, bumps: tooMany)),
            needle: "bump"
        );
    }

    [Fact]
    public void ANegativeBumpRadiusIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, bumps: [Bumps[0] with { Radii = new DocumentVector3(x: -0.1f, y: 0.2f, z: 0.2f) }])),
            needle: "radii"
        );

    [Fact]
    public void ANonFiniteBumpPushIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, bumps: [Bumps[0] with { Push = new DocumentVector3(x: float.NaN, y: 0f, z: 0f) }])),
            needle: "push"
        );

    // --- Static emission path (CreationStampEmitter.EmitShapeChain's BuildTransformChain) ---

    private static SdfProgram EmitStatic(ShapeDocument shape, float stampScale) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.Emit(
            builder: builder,
            document: Document(shape),
            materialFor: _ => material,
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: stampScale, ReflectionNormal: null)
        );

        return builder.Build(buildInstanceGrid: false);
    }

    [Fact]
    public void TheStaticPathEmitsAShearInstructionCarryingTheAuthoredCoefficients() {
        var program = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, shear: Shear), stampScale: 1f);
        var instruction = program.Instructions.Single(predicate: static i => (i.Op == SdfOp.Shear));

        Assert.Equal(expected: Shear.Linear, actual: instruction.Data0.X, precision: 6);
        Assert.Equal(expected: Shear.Quadratic, actual: instruction.Data0.Y, precision: 6);
    }

    [Fact]
    public void TheStaticPathEmitsOneGaussianPushPerBump() {
        var program = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, bumps: Bumps), stampScale: 1f);
        var instructions = program.Instructions.ToList();
        var headIndex = instructions.FindIndex(match: static i => (i.Op == SdfOp.GaussianPush));

        Assert.True(condition: (headIndex >= 0), userMessage: "no GaussianPush head emitted.");
        Assert.Equal(Bumps.Length, instructions.Count(i => i.Op == SdfOp.GaussianPush));
        Assert.Equal(Bumps[0].Push.Z, BitConverter.UInt32BitsToSingle(instructions[headIndex].Shape));
    }

    // --- Pool emission path (WorldStampPool.EmitShape's warp prefix) ---

    private static SdfProgram EmitPool(ShapeDocument shape, float bodyScale, bool probeWorstCase = false) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: [new("#AAAAAA", null, null, null)],
                Shapes: [shape],
                Frames: null
            ),
            source: PrototypeId
        );
        var creation = new WorldPrototype(Id: PrototypeId, Document: canonical.Document, HashRaw: canonical.Hash);
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: PrototypeId), Scale: bodyScale, Motion: WorldLookMotion.Default)],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: bodyScale, Motion: WorldLookMotion.Default)]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(builder: builder, definition: definition, probeWorstCase: probeWorstCase, maxPlacementScale: bodyScale, slotBase: 0);

        return builder.Build(buildInstanceGrid: false);
    }

    [Fact]
    public void ThePoolEmitsAShearInstructionCarryingTheAuthoredCoefficientsAtBodyScaleOne() {
        var program = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, shear: Shear), bodyScale: 1f);
        var instruction = program.Instructions.Single(predicate: static i => (i.Op == SdfOp.Shear));

        Assert.Equal(expected: Shear.Linear, actual: instruction.Data0.X, precision: 6);
        Assert.Equal(expected: Shear.Quadratic, actual: instruction.Data0.Y, precision: 6);
    }

    [Fact]
    public void ThePoolsProbeEmitsShearAndGaussianPushEvenWithoutAnyAuthored() {
        var program = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One), bodyScale: 1f, probeWorstCase: true);

        Assert.Contains(collection: program.Instructions, filter: static i => (i.Op == SdfOp.Shear));
        Assert.Contains(collection: program.Instructions, filter: static i => (i.Op == SdfOp.GaussianPush));
    }

    // THE LAW: a shear/bump is a warp whose Lipschitz factor would otherwise fold into the WHOLE program's step
    // scale, so both emitters give a warped shape its own field scope. The un-warped, uniformly scaled Box is the
    // control (no scope at all).
    [Fact]
    public void AWarpedShapeEmitsInsideItsOwnFieldScopeOnBothPaths() {
        AssertWarpIsScoped(program: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Shear, Bumps), bodyScale: 1f));
        AssertWarpIsScoped(program: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Shear, Bumps), stampScale: 1f));
        Assert.Equal(expected: 1f, actual: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Shear, Bumps), bodyScale: 1f).StepScale);
        Assert.Equal(expected: 1f, actual: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Shear, Bumps), stampScale: 1f).StepScale);
    }

    private static void AssertWarpIsScoped(SdfProgram program) {
        var instructions = program.Instructions.ToList();
        var shapeIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.ShapeBlend));
        var pushIndex = instructions.FindLastIndex(startIndex: shapeIndex, match: static instruction => (instruction.Op == SdfOp.PushField));
        var popIndex = instructions.FindIndex(startIndex: shapeIndex, match: static instruction => (instruction.Op == SdfOp.PopField));

        Assert.True(condition: ((pushIndex >= 0) && (pushIndex < shapeIndex)), userMessage: "the warped shape has no PushField before it.");
        Assert.True(condition: (popIndex > shapeIndex), userMessage: "the warped shape has no PopField after it.");
    }

    // THE LAW: a bump grows the shape's reach by up to the sum of its Push magnitudes (ShapeBumpDocument.ReachExtra);
    // the un-bumped shape is the control.
    [Fact]
    public void TheStaticPathWidensABumpedShapesReachByThePushSum() {
        var extra = ShapeBumpDocument.ReachExtra(bumps: Bumps);
        var bumped = CreationStampEmitter.RenderReach(document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, bumps: Bumps)), scale: 1f, fontFor: null);
        var plain = CreationStampEmitter.RenderReach(document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One)), scale: 1f, fontFor: null);

        Assert.True(condition: (extra > 0f));
        Assert.Equal(expected: (plain + extra), actual: bumped, precision: 4);
    }
}
