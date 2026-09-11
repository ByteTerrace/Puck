using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every static-stamp chain (<see cref="CreationStampEmitter.EmitShapeStamp"/>/<see cref="CreationStampEmitter.Emit"/>)
/// carries the stamp scale as its own <c>Scale(transform.Scale)</c> point op, ahead of the primitive. A shape's
/// <see cref="ShapeDocument.Rounding"/>/<see cref="ShapeDocument.Chamfer"/> and a <see cref="ShapePanelDocument"/>'s
/// <see cref="ShapePanelDocument.Inset"/>/<see cref="ShapePanelDocument.Depth"/> are baked into the primitive's own
/// LOCAL geometry, in creation units, and that Scale op re-multiplies the whole primitive at evaluation time — so the
/// packed instruction itself must stay verbatim (never pre-multiplied in C#) whatever the stamp scale, on pain of
/// double-scaling. <see cref="ShapeDocument.Dilate"/>/<see cref="ShapeDocument.Onion"/>/<see cref="ShapeDocument.Smooth"/>
/// are the opposite case: field ops and a blend radius that act on the running world-space accumulator directly, past
/// where the Scale op's re-multiply can reach them, so <see cref="CreationStampEmitter.Emit"/> must scale them
/// explicitly — exactly as the animated pool scales them with a body look
/// (<see cref="WorldStampPoolShapeUnitsLawTests"/>).
/// </summary>
public sealed class CreationStampEmitterUnitsLawTests {
    private const string PrototypeId = "static-radii";

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, float? rounding = null, float? chamfer = null, float? dilate = null, float? onion = null, float? smooth = null, SdfBlendOp? blend = null, ShapePanelDocument? panel = null, int id = 0) => new(
        Id: id,
        Name: null,
        Type: type,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: blend,
        Smooth: smooth,
        Group: 0,
        Rounding: rounding,
        Chamfer: chamfer,
        Dilate: dilate,
        Onion: onion,
        Panel: panel
    );
    // The per-shape, tight-instance static form (WorldPlacementStamper.EmitShapeStamp's own calling convention:
    // inScope false, no caller-held scope) — a single shape opens its own scope only when its own modifiers need one.
    private static SdfProgram EmitOne(ShapeDocument shape, float stampScale, int? panelMaterial = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var resolvedPanelMaterial = (panelMaterial.HasValue
            ? builder.AddMaterial(material: new SdfMaterial(Albedo: (Vector3.One * 0.5f)))
            : material);

        _ = builder.BeginInstance(
            boundCenter: Vector3.Zero,
            boundRadius: 8f
        );
        CreationStampEmitter.EmitShapeStamp(
            builder: builder,
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: null,
                Shapes: [shape],
                Frames: null
            ),
            shapeIndex: 0,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: stampScale,
                ReflectionNormal: null
            ),
            material: material,
            paletteIds: [material, resolvedPanelMaterial]
        );
        _ = builder.EndInstance();

        return builder.Build(buildInstanceGrid: false);
    }
    // The whole-creation, one-shared-scope static form (WorldPlacementStamper.EmitPlacement's "scoped" branch — a
    // creation whose blend composes internally).
    private static SdfProgram EmitScoped(IReadOnlyList<ShapeDocument> shapes, float stampScale, bool callerScope = true) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstance(
            boundCenter: Vector3.Zero,
            boundRadius: 8f
        );
        if (callerScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }
        CreationStampEmitter.Emit(
            builder: builder,
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: null,
                Shapes: shapes,
                Frames: null
            ),
            inScope: callerScope,
            materialFor: _ => material,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: stampScale,
                ReflectionNormal: null
            )
        );
        if (callerScope) {
            _ = builder.PopField();
        }
        _ = builder.EndInstance();

        return builder.Build(buildInstanceGrid: false);
    }
    private static SdfInstruction ShapeBlendOf(SdfProgram program, uint blend = ((uint)SdfBlendOp.Union)) =>
        program.Instructions.Single(predicate: instruction => ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Blend == blend)));

    /// <summary>Both per-shape and shared scopes retain erosion without eroding a sibling or opening a
    /// forbidden nested scope. The containing scope owns the noise clamp.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopeOwnershipPreservesErosionOnItsAuthoredShape(bool callerScope) {
        var eroding = Shape(SdfSolidPrimitive.Sphere, Vector3.One) with {
            Erode = new ShapeErodeDocument(2, 1f, 0f, 2f),
        };
        var sibling = Shape(SdfSolidPrimitive.Box, Vector3.One, id: 1);
        var program = EmitScoped([eroding, sibling], 2f, callerScope);
        var erosionIndex = Enumerable.Range(0, program.Instructions.Count)
            .Single(index => program.Instructions[index].Op == SdfOp.LaneErode);
        var erosion = program.Instructions[erosionIndex];

        Assert.Equal(new Vector4(2f, 1f, 0f, 2f), erosion.Data0);
        Assert.Equal(2f, erosion.Data1.X);
        var targetIndex = Enumerable.Range(erosionIndex + 1, program.Instructions.Count - erosionIndex - 1)
            .First(index => program.Instructions[index].Op == SdfOp.ShapeBlend);
        // Primitive lowering may insert Scale; a Reset would silently discard the pending erosion.
        Assert.DoesNotContain(program.Instructions.Skip(erosionIndex + 1).Take(targetIndex - erosionIndex - 1),
            instruction => instruction.Op == SdfOp.ResetPoint);
        Assert.Equal((uint)SdfShapeType.Sphere, program.Instructions[targetIndex].Shape);
        Assert.Equal(2, program.Instructions.Count(instruction => instruction.Op == SdfOp.ShapeBlend));
        Assert.Single(program.Instructions, instruction => instruction.Op == SdfOp.PushField);
        Assert.Single(program.Instructions, instruction => instruction.Op == SdfOp.PopField);
        Assert.Equal(1f, program.StepScale);
        var clamp = Assert.Single(program.FieldScopeClamps);
        Assert.Equal(callerScope ? 2 : 1, clamp.ShapeCount);
        Assert.True(clamp.StepScale < 1f);
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Cylinder, 0.02f, 0f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0f, 0.03f)]
    [InlineData(SdfSolidPrimitive.Box, 0f, 0.03f)]
    public void RoundingAndChamferStayInCreationUnitsAcrossStampScale(SdfSolidPrimitive type, float rounding, float chamfer) {
        var scale = new Vector3(x: 0.3f, y: 0.2f, z: 0.3f);
        var shape = Shape(type, scale, ((rounding > 0f) ? rounding : null), ((chamfer > 0f) ? chamfer : null));
        var atOne = ShapeBlendOf(program: EmitOne(shape: shape, stampScale: 1f));
        var atTwo = ShapeBlendOf(program: EmitOne(shape: shape, stampScale: 2f));

        Assert.Equal(atOne.Shape, atTwo.Shape);
        Assert.Equal(atOne.Data0, atTwo.Data0);
        Assert.Equal(atOne.Data1, atTwo.Data1);
    }

    [Fact]
    public void AStampAtScaleOneEmitsTheAuthoredDilateVerbatim() {
        var program = EmitOne(shape: Shape(SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), dilate: 0.02f), stampScale: 1f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Dilate));

        Assert.Equal(0.02f, instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void AStampAtScaleTwoDoublesTheEmittedDilate() {
        var program = EmitOne(shape: Shape(SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), dilate: 0.02f), stampScale: 2f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Dilate));

        Assert.Equal(0.04f, instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void AStampAtScaleTwoDoublesTheEmittedOnion() {
        var program = EmitOne(shape: Shape(SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f), onion: 0.02f), stampScale: 2f);
        var instruction = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.Onion));

        Assert.Equal(0.04f, instruction.Data0.X, precision: 6);
    }

    [Fact]
    public void AWholeCreationStampAtScaleTwoDoublesTheEmittedSmoothBlendRadius() {
        // The blend radius composes a shape with the shapes emitted before it in the SAME shared scope — a
        // world-space parameter, never baked into the shape's own local geometry, so it needs the stamp scale
        // explicitly (unlike Rounding/Chamfer's automatic re-multiply through the chain's own Scale op).
        var baseShape = Shape(SdfSolidPrimitive.Box, Vector3.One, id: 1);
        var smoothed = Shape(SdfSolidPrimitive.Sphere, new Vector3(value: 0.3f), blend: SdfBlendOp.SmoothUnion, smooth: 0.05f, id: 2);
        var program = EmitScoped(shapes: [baseShape, smoothed], stampScale: 2f);
        var instruction = ShapeBlendOf(program: program, blend: (uint)SdfBlendOp.SmoothUnion);

        Assert.Equal(0.1f, instruction.Data1.X, precision: 6);
    }

    /// <summary>A panel's Inset/Depth are creation units resolved against the plate's own creation-unit scale
    /// (<c>ShapePanelDocument.Resolve</c>), then carried through the SAME chain the plate's own Rounding/Chamfer
    /// ride — so, like those, the packed plate and panel-copy instructions stay verbatim across the stamp scale
    /// (never pre-multiplied in C#): the chain's own Scale op reaches them automatically at evaluation time.</summary>
    [Fact]
    public void PanelInsetAndDepthStayInCreationUnitsAcrossStampScale() {
        var scale = new Vector3(x: 0.3f, y: 0.2f, z: 0.3f);
        var shape = Shape(SdfSolidPrimitive.Box, scale, panel: new ShapePanelDocument(Inset: 0.02f, Depth: -0.03f, Material: 1));
        var atOne = EmitOne(shape: shape, stampScale: 1f, panelMaterial: 1);
        var atTwo = EmitOne(shape: shape, stampScale: 2f, panelMaterial: 1);
        var shapesAtOne = atOne.Instructions.Where(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend)).ToArray();
        var shapesAtTwo = atTwo.Instructions.Where(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend)).ToArray();

        // Plate, then panel copy — two ShapeBlend instructions at every stamp scale.
        Assert.Equal(2, shapesAtOne.Length);
        Assert.Equal(shapesAtOne.Length, shapesAtTwo.Length);

        for (var index = 0; (index < shapesAtOne.Length); index++) {
            Assert.Equal(shapesAtOne[index].Data0, shapesAtTwo[index].Data0);
            Assert.Equal(shapesAtOne[index].Data1, shapesAtTwo[index].Data1);
        }
    }
}
