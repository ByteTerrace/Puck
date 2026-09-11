using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a shape's creation-unit edge radii (<see cref="ShapeDocument.Rounding"/>, <see cref="ShapeDocument.Chamfer"/>)
/// scale with the body look in the animated stamp pool exactly as the static stamper scales them through its
/// <c>Scale(transform.Scale)</c> chain — a look at scale 2 emits the same shape lanes as the creation authored at
/// twice its scale with twice its radius at scale 1, and a look at scale 1 emits the authored radius verbatim.
/// </summary>
public sealed class WorldStampPoolShapeUnitsLawTests {
    private const string PrototypeId = "radii";

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, float? rounding, float? chamfer) => new(
        Id: 0,
        Name: null,
        Type: type,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Rounding: rounding,
        Chamfer: chamfer
    );
    private static SdfProgram Program(IReadOnlyList<ShapeDocument> shapes, float bodyScale) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: [new("#AAAAAA", null, null, null)],
                Shapes: shapes,
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

        pool.Emit(builder: builder, definition: definition, probeWorstCase: false, maxPlacementScale: bodyScale, slotBase: 0);

        return builder.Build(buildInstanceGrid: false);
    }
    private static SdfInstruction ShapeInstruction(ShapeDocument shape, float bodyScale) {
        var program = Program(shapes: [shape], bodyScale: bodyScale);

        // The compact live pool emits only the authored primitive and its modifiers.
        return program.Instructions.Single(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Shape != ((uint)SdfShapeType.Sphere))));
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Cylinder, 0.02f, 0f)]
    [InlineData(SdfSolidPrimitive.Prism, 0.02f, 0f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0f, 0.03f)]
    [InlineData(SdfSolidPrimitive.Box, 0f, 0.03f)]
    public void ALookAtScaleTwoEmitsTheCreationAuthoredAtTwiceItsScaleAndRadius(SdfSolidPrimitive type, float rounding, float chamfer) {
        // X == Z keeps the Cylinder on its native arm at every scale; the Prism and Box take their native extents.
        var scale = new Vector3(x: 0.3f, y: 0.2f, z: 0.3f);
        var scaled = ShapeInstruction(shape: Shape(type, scale, ((rounding > 0f) ? rounding : null), ((chamfer > 0f) ? chamfer : null)), bodyScale: 2f);
        var authoredTwice = ShapeInstruction(shape: Shape(type, (scale * 2f), ((rounding > 0f) ? (rounding * 2f) : null), ((chamfer > 0f) ? (chamfer * 2f) : null)), bodyScale: 1f);

        Assert.Equal(authoredTwice.Shape, scaled.Shape);
        Assert.Equal(authoredTwice.Data0, scaled.Data0);
        Assert.Equal(authoredTwice.Data1, scaled.Data1);
    }

    [Fact]
    public void ALookAtScaleOneEmitsTheAuthoredRoundingVerbatim() {
        // A Cylinder emits its rounding on Data1.w (the family fillet lane) and insets Data0.xy by it.
        var instruction = ShapeInstruction(shape: Shape(SdfSolidPrimitive.Cylinder, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), 0.02f, null), bodyScale: 1f);

        Assert.Equal(((uint)SdfShapeType.Cylinder), instruction.Shape);
        Assert.Equal(0.02f, instruction.Data1.W, precision: 6);
        Assert.Equal((0.3f - 0.02f), instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void ALookAtScaleTwoDoublesTheEmittedRounding_TheDiscriminator() {
        // The lane read directly: raw authored 0.02 at look scale 2 must land as 0.04, never 0.02.
        var instruction = ShapeInstruction(shape: Shape(SdfSolidPrimitive.Cylinder, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), 0.02f, null), bodyScale: 2f);

        Assert.Equal(0.04f, instruction.Data1.W, precision: 6);
    }

    [Fact]
    public void ALookAtScaleOneEmitsTheAuthoredDilateVerbatim() {
        // Dilate/Onion are field ops of their own (SdfOp.Dilate/Onion), not a lane on the shape's own instruction.
        var shape = (Shape(SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), null, null) with { Dilate = 0.02f });
        var program = Program(shapes: [shape], bodyScale: 1f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Dilate));

        Assert.Equal(0.02f, instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void ALookAtScaleTwoDoublesTheEmittedDilate() {
        var shape = (Shape(SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), null, null) with { Dilate = 0.02f });
        var program = Program(shapes: [shape], bodyScale: 2f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Dilate));

        Assert.Equal(0.04f, instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void ALookAtScaleTwoDoublesTheEmittedOnion() {
        var shape = (Shape(SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), null, null) with { Onion = 0.02f });
        var program = Program(shapes: [shape], bodyScale: 2f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Onion));

        Assert.Equal(0.04f, instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void AGroupAtLookScaleTwoDoublesTheEmittedSmoothBlendRadius() {
        // A grouped member's smooth-blend radius (Data1.x on its own ShapeBlend instruction) composes it against the
        // group's shared accumulator (Pass 2, EmitGroup) — a WORLD-space blend-radius parameter, not a baked-local
        // one, so it takes the placement scale exactly like Dilate/Onion do, never the rounding/chamfer exemption.
        var baseShape = (Shape(SdfSolidPrimitive.Box, Vector3.One, null, null) with { Id = 1, Group = 1 });
        var smoothed = (Shape(SdfSolidPrimitive.Sphere, new Vector3(value: 0.3f), null, null) with {
            Id = 2,
            Group = 1,
            Blend = SdfBlendOp.SmoothUnion,
            Smooth = 0.05f,
        });
        var program = Program(shapes: [baseShape, smoothed], bodyScale: 2f);
        var instruction = program.Instructions.Single(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Blend == ((uint)SdfBlendOp.SmoothUnion))));

        Assert.Equal(0.1f, instruction.Data1.X, precision: 6);
    }

    [Fact]
    public void AGroupAtLookScaleTwoDoublesTheEmittedDilate() {
        var baseShape = (Shape(SdfSolidPrimitive.Box, Vector3.One, null, null) with { Id = 1, Group = 1 });
        var dilated = (Shape(SdfSolidPrimitive.Sphere, new Vector3(value: 0.3f), null, null) with {
            Id = 2,
            Group = 1,
            Dilate = 0.02f,
        });
        var program = Program(shapes: [baseShape, dilated], bodyScale: 2f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Dilate));

        Assert.Equal(0.04f, instruction.Data0.X, precision: 6);
    }
}
