using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapePanelDocument"/> — a shape's second-material inset face region — is refused by name
/// wherever its one-deep field scope has nowhere to nest (a Plane, a domain-folded shape, a grouped shape, or a
/// creation that already needs a scope of its own), clamped by name against the shape's own local half-extents, and
/// otherwise renders as two shape instructions composed in the shape's own field scope on BOTH emission paths, the
/// copy from its own transform chain, the authored depth exact whatever the inset — never reaching the deterministic
/// contact evaluator either path feeds, and never outgrowing the probe that reserves it.
/// </summary>
public sealed class ShapePanelLawTests {
    private const string PrototypeId = "panelled";
    // Q48.16 resolves 1.5e-5; the evaluator's float-to-fixed conversions and the rounded box's own arithmetic sit well
    // inside this.
    private const double DistanceTolerance = 1e-3;

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, ShapePanelDocument? panel = null, int? group = null, IReadOnlyList<ShapeDomainOp>? domain = null, SdfBlendOp? blend = null, int id = 0) =>
        new(
            Id: id,
            Name: null,
            Type: type,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: (blend ?? SdfBlendOp.Union),
            Smooth: 0f,
            Group: group,
            Domain: domain,
            Panel: panel
        );
    private static CreationDocument Document(params ShapeDocument[] shapes) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: shapes,
            Frames: null
        );
    private static void AssertCanonicalizerRefusesNaming(CreationDocument document, string needle) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: needle)
        );
    }
    private static void AssertCanonicalizerAccepts(CreationDocument document) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.Empty(collection: violations);
    }

    // A recessed panel's plate is Box(0.4, 0.3, 0.2); its face defaults to +Z (the Box's front), whose half-extent
    // is 1.04 * 0.2 - 0.04 * 0.2 = 0.2 (the rounded-box fillet is an inset), so a depth of 0.1 and an inset of 0.05
    // sit inside every clamp. The uniform plate takes AppendScaledPrimitive's Scale-op branch, the non-uniform one
    // its native extents — the two branch shapes every path law must cover.
    private static readonly ShapePanelDocument RecessedPanel = new(Inset: 0.05f, Depth: 0.1f, Material: 1);
    private static readonly ShapePanelDocument RaisedPanel = new(Inset: 0.05f, Depth: -0.1f, Material: 1);
    private static readonly Vector3 PlateScale = new(x: 0.4f, y: 0.3f, z: 0.2f);
    private static readonly Vector3 UniformPlateScale = new(value: 0.2f);

    [Fact]
    public void ARecessedPanelOnABoxIsAccepted() =>
        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel)));

    [Fact]
    public void ARaisedPanelOnABoxIsAccepted() =>
        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, RaisedPanel)));

    [Fact]
    public void APanelOnAPlaneIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Plane, Vector3.One, RecessedPanel)),
            needle: "Plane"
        );

    [Fact]
    public void APanelOnAGroupedShapeIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel, group: 1)),
            needle: "grouped"
        );

    [Fact]
    public void APanelOnADomainFoldedShapeIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel, domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)])),
            needle: "domain"
        );

    [Fact]
    public void APanelOnAScopeForcedCreationIsRefusedByName() {
        // The panelled shape itself authors a plain Union blend; a SIBLING shape's Subtraction blend (outside the
        // union family RequiresScope leaves scope-free) is what forces the whole creation to need a scope, leaving
        // the panel nowhere of its own to nest.
        var panelled = Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel);
        var sibling = Shape(SdfSolidPrimitive.Sphere, Vector3.One, blend: SdfBlendOp.Subtraction, id: 1);

        AssertCanonicalizerRefusesNaming(document: Document(panelled, sibling), needle: "scope");
    }

    [Fact]
    public void ANonFiniteFaceIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, (RecessedPanel with { Face = new DocumentVector3(x: float.NaN, y: 0f, z: 1f) }))),
            needle: "face"
        );

    [Fact]
    public void AZeroLengthFaceIsRefusedByNameWhileANonUnitOneIsNormalized() {
        var zero = RecessedPanel with { Face = new DocumentVector3(x: 0f, y: 0f, z: 0f) };
        var control = RecessedPanel with { Face = new DocumentVector3(x: 0f, y: 0f, z: 2f) };

        AssertCanonicalizerRefusesNaming(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, zero)), needle: "face");
        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, control)));

        // Normalization never turns the zero face into some default axis behind validation's back: the canonical
        // document (normalized, then validated) refuses it by name, while the non-unit control canonicalizes to +Z.
        var refusal = Assert.Throws<DocumentValidationException>(() => CreationCanonicalizer.Canonicalize(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, zero)), source: PrototypeId));

        Assert.Contains("face", refusal.Message, StringComparison.Ordinal);

        var normalizedControl = CreationCanonicalizer.Canonicalize(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, control)), source: PrototypeId).Document;

        Assert.Equal(Vector3.UnitZ, normalizedControl.Shapes![0].Panel!.Face!.Value);
    }

    [Fact]
    public void ANonFiniteOrNegativeInsetIsRefusedByName() {
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, (RecessedPanel with { Inset = -0.01f }))),
            needle: "inset"
        );
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, (RecessedPanel with { Inset = float.NaN }))),
            needle: "inset"
        );
    }

    [Fact]
    public void ANonFiniteDepthIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, (RecessedPanel with { Depth = float.NaN }))),
            needle: "depth"
        );

    [Fact]
    public void AnInsetPastTheSmallestLocalHalfExtentIsRefusedByName() {
        // The smallest half-extent of PlateScale is 0.2 (Z); 0.25 erodes the panel to nothing. The control leaves
        // a 0.01 sliver along Z, whose 0.02 full extent still admits the 0.01 depth.
        var control = RecessedPanel with { Inset = 0.19f, Depth = 0.01f };
        var cell = RecessedPanel with { Inset = 0.25f };

        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, control)));
        AssertCanonicalizerRefusesNaming(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, cell)), needle: "inset");
    }

    [Fact]
    public void ADepthPastTheErodedCopysFullFaceExtentIsRefusedByName() {
        // The copy eroded by 0.05 spans 2 * 0.15 = 0.30 along +Z; deeper than that a recess carves an enclosed
        // void and a raise floats detached, so the plate's own 0.40 is not the ceiling.
        var recessControl = RecessedPanel with { Depth = 0.29f };
        var recessCell = RecessedPanel with { Depth = 0.31f };
        var raiseControl = RecessedPanel with { Depth = -0.29f };
        var raiseCell = RecessedPanel with { Depth = -0.31f };
        var plateExtentCell = RecessedPanel with { Depth = 0.39f };

        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, recessControl)));
        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, raiseControl)));
        AssertCanonicalizerRefusesNaming(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, recessCell)), needle: "depth");
        AssertCanonicalizerRefusesNaming(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, raiseCell)), needle: "depth");
        AssertCanonicalizerRefusesNaming(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, plateExtentCell)), needle: "depth");
    }

    [Fact]
    public void ADepthNoDeeperThanTheInsetStillCarves() {
        // The concept-sheet regime: inset and depth of the same order. With the floor placed from the eroded copy's
        // own half-extent, a shallow depth carves exactly that deep instead of vanishing behind the inset.
        var shape = Shape(SdfSolidPrimitive.Box, PlateScale, (RecessedPanel with { Inset = 0.04f, Depth = 0.03f }));

        AssertCanonicalizerAccepts(document: Document(shape));
        Assert.Equal(0.03, MeasuredRecessDepth(shape: shape), precision: 3);
    }

    [Fact]
    public void APanelledShapeChargesTwoAgainstTheStampBudget() {
        var document = Document(Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel));
        var bare = Document(Shape(SdfSolidPrimitive.Box, PlateScale));

        Assert.Equal(2, document.StampShapeCount());
        Assert.Equal(1, bare.StampShapeCount());
        Assert.Equal(2, CreationStampEmitter.PerCopyInstanceCount(document: document));
        Assert.Equal(1, CreationStampEmitter.PerCopyInstanceCount(document: bare));
    }

    // The Box face HalfExtent reads is the one the emitted program's zero set sits at, on every axis, for a plate
    // riding a Scale op (uniform), one baking native extents (non-uniform), and one spelled as a chamfered
    // rectangle — measured through the evaluator, never derived from the same table under test.
    [Theory]
    [InlineData(0.6f, 0.7f, 0.8f, 0f)]
    [InlineData(0.5f, 0.5f, 0.5f, 0f)]
    [InlineData(0.6f, 0.7f, 0.8f, 0.05f)]
    [InlineData(0.5f, 0.5f, 0.5f, 0.05f)]
    public void HalfExtentReadsTheBoxFaceTheEmittedZeroSetSitsAt(float x, float y, float z, float chamfer) {
        var scale = new Vector3(x, y, z);
        var shape = Shape(SdfSolidPrimitive.Box, scale) with { Chamfer = ((chamfer > 0f) ? chamfer : null) };

        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }) {
            Assert.Equal(MeasuredPlateFace(shape: shape, axis: axis), SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Box, scale, SdfLift.Extrude, axis), precision: 3);
        }
    }

    [Fact]
    public void HalfExtentMatchesTheExtrudeAndRevolveAxisConventions() {
        var scale = new Vector3(x: 0.4f, y: 0.25f, z: 0.6f);

        // Prism extrude: Z is the extrusion half-depth.
        Assert.Equal(scale.Z, SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Prism, scale, SdfLift.Extrude, Vector3.UnitZ), tolerance: 1e-6f);
        // Cylinder / revolved Prism: Y is the axial half-height.
        Assert.Equal(scale.Y, SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Cylinder, scale, SdfLift.Extrude, Vector3.UnitY), tolerance: 1e-6f);
        Assert.Equal(scale.Y, SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Prism, scale, SdfLift.Revolve, Vector3.UnitY), tolerance: 1e-6f);
    }

    [Fact]
    public void HalfExtentReadsASphereOrEllipsoidsSupportOffAxis() {
        var diagonal = Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: 0f));

        // A unit sphere's support is its radius in every direction; the L1 projection would read the diagonal as √2.
        Assert.Equal(1f, SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Sphere, Vector3.One, SdfLift.Extrude, diagonal), tolerance: 1e-6f);
        // An ellipsoid with radii (2, 1, 1): support along (1,1,0)/√2 is √(2² + 1²)/√2 = √2.5.
        Assert.Equal(MathF.Sqrt(x: 2.5f), SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Ellipsoid, new Vector3(x: 2f, y: 1f, z: 1f), SdfLift.Extrude, diagonal), tolerance: 1e-6f);
        // Every principal axis stays exact.
        Assert.Equal(2f, SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Ellipsoid, new Vector3(x: 2f, y: 1f, z: 1f), SdfLift.Extrude, Vector3.UnitX), tolerance: 1e-6f);
    }

    [Fact]
    public void HalfExtentRefusesAZeroOrNonFiniteAxisByName() {
        var zero = Assert.Throws<ArgumentOutOfRangeException>(() => SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Sphere, Vector3.One, SdfLift.Extrude, Vector3.Zero));
        var nan = Assert.Throws<ArgumentOutOfRangeException>(() => SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Sphere, Vector3.One, SdfLift.Extrude, new Vector3(x: float.NaN, y: 0f, z: 0f)));

        Assert.Equal("axis", zero.ParamName);
        Assert.Equal("axis", nan.ParamName);
        // The control: a non-unit finite axis is normalized and read through.
        Assert.Equal(1f, SdfSolidGeometry.HalfExtent(SdfSolidPrimitive.Sphere, Vector3.One, SdfLift.Extrude, new Vector3(x: 0f, y: 0f, z: 3f)), tolerance: 1e-6f);
    }

    [Fact]
    public void ReachWideningAddsOnlyAPositivePanelRaise() {
        var baseReach = SdfSolidGeometry.Reach(SdfSolidPrimitive.Box, PlateScale, SdfLift.Extrude);

        Assert.Equal(baseReach, SdfSolidGeometry.Reach(SdfSolidPrimitive.Box, PlateScale, SdfLift.Extrude, panelRaise: 0f), tolerance: 1e-6f);
        Assert.Equal(baseReach, SdfSolidGeometry.Reach(SdfSolidPrimitive.Box, PlateScale, SdfLift.Extrude, panelRaise: -1f), tolerance: 1e-6f);
        Assert.Equal(baseReach + 0.3f, SdfSolidGeometry.Reach(SdfSolidPrimitive.Box, PlateScale, SdfLift.Extrude, panelRaise: 0.3f), tolerance: 1e-6f);
    }

    private static SdfProgram EmitStatic(ShapeDocument shape) {
        var builder = new SdfProgramBuilder();
        var albedo0 = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var albedo1 = builder.AddMaterial(material: new SdfMaterial(Albedo: (Vector3.One * 0.5f)));

        // materialFor is keyed by the ShapeDocument.Material slot: the plate's own shape carries 0, and Emit's
        // panel substitution (shape with { Material = panel.Material }) always carries the panel's own slot (1 for
        // every panel this file authors) — so the two calls resolve to the two distinct palette ids below by
        // construction, with no need to inspect the shape's Panel facet itself.
        CreationStampEmitter.Emit(
            builder: builder,
            document: Document(shape),
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null),
            materialFor: s => ((s.Material == 0)
                ? albedo0
                : albedo1)
        );

        return builder.Build(buildInstanceGrid: false);
    }
    // The static per-shape stamp exactly as WorldPlacementStamper's perShape branch emits it: one tight instance,
    // the shape's own material, the creation's whole palette handed through for the panel.
    private static SdfProgram EmitShapeStamp(CreationDocument document) {
        var builder = new SdfProgramBuilder();
        var paletteIds = new[] {
            builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            builder.AddMaterial(material: new SdfMaterial(Albedo: (Vector3.One * 0.5f))),
        };
        var transform = new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null);

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2f);
        CreationStampEmitter.EmitShapeStamp(
            builder: builder,
            document: document,
            shapeIndex: 0,
            transform: transform,
            material: paletteIds[0],
            paletteIds: paletteIds
        );
        _ = builder.EndInstance();

        return builder.Build(buildInstanceGrid: false);
    }
    // The distance the evaluator reads at a point, on the first (only) instance's program.
    private static double DistanceAt(SdfProgram program, Vector3 point) {
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.True(condition: evaluator.TryDistance(
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point)),
            distance: out var distance,
            material: out _
        ));

        return (double)distance;
    }
    private static double DistanceAt(SdfProgram program, float z) =>
        DistanceAt(program: program, point: new Vector3(x: 0f, y: 0f, z: z));
    // The plate's face along a principal axis, measured from outside: a point beyond it reads its own height minus
    // the face.
    private static double MeasuredPlateFace(ShapeDocument shape, Vector3 axis) {
        const float Probe = 0.9f;

        return (Probe - DistanceAt(program: EmitStatic(shape: (shape with { Panel = null })), point: (axis * Probe)));
    }
    private static double MeasuredPlateFace(ShapeDocument shape) =>
        MeasuredPlateFace(shape: shape, axis: Vector3.UnitZ);
    // A recess floor, measured from inside the void just above it: the point reads its distance down to the floor
    // (the copy's own boundary is the nearest surface there). Returns how far below the plate's face the floor sits.
    private static double MeasuredRecessDepth(ShapeDocument shape) {
        var face = MeasuredPlateFace(shape: shape);
        var probe = (float)(face - (0.5 * shape.Panel!.Depth));

        return (face - (probe - DistanceAt(program: EmitStatic(shape: shape), z: probe)));
    }

    [Fact]
    public void ARecessFloorSitsExactlyDepthBelowThePlateFaceWhateverTheInset() {
        foreach (var inset in new[] { 0f, 0.02f, 0.05f, 0.09f }) {
            var shape = Shape(SdfSolidPrimitive.Box, PlateScale, (RecessedPanel with { Inset = inset }));

            Assert.Equal(RecessedPanel.Depth, MeasuredRecessDepth(shape: shape), precision: 3);
        }
    }

    [Fact]
    public void ARaisedPanelStandsExactlyDepthProudOfThePlateFaceWhateverTheInset() {
        const float Probe = 0.9f;

        foreach (var inset in new[] { 0f, 0.02f, 0.05f, 0.09f }) {
            var shape = Shape(SdfSolidPrimitive.Box, PlateScale, (RaisedPanel with { Inset = inset }));
            var face = MeasuredPlateFace(shape: shape);
            var raisedFace = (Probe - DistanceAt(program: EmitStatic(shape: shape), z: Probe));

            Assert.Equal(-RaisedPanel.Depth, (raisedFace - face), precision: 3);
        }
    }

    [Fact]
    public void APanelledShapeEmitsExactlyTwoShapesInOneScopeOnTheStaticPath() {
        var program = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel));
        var shapeCount = program.Instructions.Count(instruction => instruction.Op == SdfOp.ShapeBlend);
        var pushCount = program.Instructions.Count(instruction => instruction.Op == SdfOp.PushField);
        var popCount = program.Instructions.Count(instruction => instruction.Op == SdfOp.PopField);

        Assert.Equal(2, shapeCount);
        Assert.Equal(1, pushCount);
        Assert.Equal(1, popCount);
    }

    [Fact]
    public void ARecessedPanelComposesBySubtractionAndARaisedOneByUnion() {
        var recessed = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel));
        var raised = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, PlateScale, RaisedPanel));
        var recessedBlends = recessed.Instructions.Where(instruction => instruction.Op == SdfOp.ShapeBlend).Select(instruction => (SdfBlendOp)instruction.Blend).ToArray();
        var raisedBlends = raised.Instructions.Where(instruction => instruction.Op == SdfOp.ShapeBlend).Select(instruction => (SdfBlendOp)instruction.Blend).ToArray();

        Assert.Equal(2, recessedBlends.Length);
        Assert.Contains(SdfBlendOp.Subtraction, recessedBlends);
        Assert.Equal(2, raisedBlends.Length);
        Assert.Contains(SdfBlendOp.Union, raisedBlends);
        Assert.DoesNotContain(SdfBlendOp.Subtraction, raisedBlends);
    }

    [Fact]
    public void APanelledShapeUsesItsOwnMaterialForThePanelCandidateOnBothStaticForms() {
        var whole = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel));
        var perShape = EmitShapeStamp(document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel)));

        Assert.Equal(2, whole.Instructions.Where(instruction => instruction.Op == SdfOp.ShapeBlend).Select(instruction => instruction.Material).Distinct().Count());
        // The per-shape stamp (WorldPlacementStamper's perShape branch) resolves the panel's slot through the palette
        // it is handed: the second shape carries palette slot 1, not the plate's own material.
        var perShapeMaterials = perShape.Instructions.Where(instruction => instruction.Op == SdfOp.ShapeBlend).Select(instruction => instruction.Material).ToArray();

        Assert.Equal(2, perShapeMaterials.Length);
        Assert.NotEqual(perShapeMaterials[0], perShapeMaterials[1]);
    }

    // The contact/collider seam: EmitFixed/VisitFixedPrimitiveCopies never read Panel, so the copies a panelled
    // shape visits are identical to the same shape with no panel at all.
    [Fact]
    public void ContactCopiesAreIdenticalWithAndWithoutAPanel() {
        var panelled = Document(Shape(SdfSolidPrimitive.Box, PlateScale, RecessedPanel));
        var bare = Document(Shape(SdfSolidPrimitive.Box, PlateScale));
        var transform = new FixedCreationStampTransform(
            Origin: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Scale: FixedQ4816.One,
            ReflectionNormal: null
        );
        var panelledCopies = new List<FixedCreationStampPrimitiveCopy>();
        var bareCopies = new List<FixedCreationStampPrimitiveCopy>();

        CreationStampEmitter.VisitFixedPrimitiveCopies(document: panelled, transform: transform, visitor: panelledCopies.Add);
        CreationStampEmitter.VisitFixedPrimitiveCopies(document: bare, transform: transform, visitor: bareCopies.Add);

        Assert.Single(panelledCopies);
        Assert.Single(bareCopies);
        Assert.Equal(bareCopies[0].Center, panelledCopies[0].Center);
        Assert.Equal(bareCopies[0].HalfExtents, panelledCopies[0].HalfExtents);
        Assert.Equal(bareCopies[0].UniformScale, panelledCopies[0].UniformScale);
        Assert.Equal(bareCopies[0].PlaneNormal, panelledCopies[0].PlaneNormal);
    }

    // The static probes. The per-shape probe reserves PerCopyInstanceCount chains for a scope-free, text-free copy
    // (two for a panelled shape); the scoped probe reserves MaxShapesPerStamp chains per whole-creation copy, each
    // carrying the field scope a text-carrying creation's shape opens for itself.
    [Fact]
    public void StaticPerShapeProbeDominatesAPanelledShapeAtTheWordLevel() {
        foreach (var scale in new[] { UniformPlateScale, PlateScale }) {
            var document = Document(Shape(SdfSolidPrimitive.Box, scale, RecessedPanel));
            var stamp = EmitShapeStamp(document: document);
            var probeBuilder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitProbe(builder: probeBuilder, reservedCount: 0, reservedShapeInstances: CreationStampEmitter.PerCopyInstanceCount(document: document));

            var probe = probeBuilder.Build(buildInstanceGrid: false);

            Assert.True(
                condition: (probe.Words.Length >= stamp.Words.Length),
                userMessage: $"the per-shape probe reserves {probe.Words.Length} words for a panelled Box at {scale}, which emits {stamp.Words.Length}."
            );

            // The control: one probe chain alone does not cover the panelled shape's two chains.
            var singleBuilder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitProbe(builder: singleBuilder, reservedCount: 0, reservedShapeInstances: 1);
            Assert.True(condition: (singleBuilder.Build(buildInstanceGrid: false).Words.Length < stamp.Words.Length));
        }
    }

    [Fact]
    public void StaticScopedProbeDominatesAWholeCreationOfPanelledShapesWithinTheStampBudget() {
        var shapeCount = (WorldPlacementPolicy.MaxShapesPerStamp / 2);
        var shapes = Enumerable.Range(0, shapeCount).Select(index => Shape(SdfSolidPrimitive.Box, UniformPlateScale, RecessedPanel, id: index)).ToArray();
        var document = Document(shapes);

        Assert.True(condition: (document.StampShapeCount() <= WorldPlacementPolicy.MaxShapesPerStamp));

        // The whole-creation form a text-carrying, scope-free creation takes: one instance, no creation scope, every
        // panelled shape opening its own.
        var builder = new SdfProgramBuilder();
        var albedo0 = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var albedo1 = builder.AddMaterial(material: new SdfMaterial(Albedo: (Vector3.One * 0.5f)));

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 4f);
        CreationStampEmitter.Emit(
            builder: builder,
            document: document,
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null),
            materialFor: s => ((s.Material == 0) ? albedo0 : albedo1),
            inScope: false
        );
        _ = builder.EndInstance();

        var creation = builder.Build(buildInstanceGrid: false);
        var probeBuilder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitProbe(builder: probeBuilder, reservedCount: 1);

        var probe = probeBuilder.Build(buildInstanceGrid: false);

        Assert.True(
            condition: (probe.Words.Length >= creation.Words.Length),
            userMessage: $"the scoped probe reserves {probe.Words.Length} words per stamp; {shapeCount} panelled shapes emit {creation.Words.Length}."
        );
    }

    // The dynamic (animated stamp pool) path — mirrors WorldStampPoolBoundLawTests's own EmitPool scaffold, over a
    // single panelled shape at a body look scale.
    private static ShapeDocument BodyShape(Vector3 scale, ShapePanelDocument? panel) => new(
        Id: 0,
        Name: null,
        Type: SdfSolidPrimitive.Box,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Panel: panel
    );
    private static SdfProgram EmitPool(Vector3 scale, ShapePanelDocument? panel, float bodyScale = 1f, bool probeWorstCase = false) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: [new("#AAAAAA", null, null, null), new("#5555FF", null, null, null)],
                Shapes: [BodyShape(scale: scale, panel: panel)],
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
    // The one field scope a live pool program opens for its single panelled slot (parked slots open none), or the
    // first one a probe opens: the instructions from its PushField through its PopField.
    private static SdfInstruction[] FirstScope(SdfProgram program) {
        var instructions = program.Instructions;
        var push = -1;

        for (var index = 0; (index < instructions.Count); index++) {
            if (instructions[index].Op == SdfOp.PushField) {
                push = index;
                break;
            }
        }

        Assert.True(condition: (push >= 0), userMessage: "no field scope was emitted");

        var pop = push;

        while (instructions[pop].Op != SdfOp.PopField) {
            pop++;
        }

        return instructions.Skip(push).Take((pop - push) + 1).ToArray();
    }
    // The panel's shape pair and the point ops that place the copy: the plate shape, the copy shape, whether a
    // ResetPoint separates them (the copy's own chain), and the last Translate/Scale before the copy.
    private static (SdfInstruction Plate, SdfInstruction Copy, bool FreshChain, SdfInstruction FaceTranslate, SdfInstruction? CopyScale) PanelPair(SdfInstruction[] scope) {
        var shapes = scope.Select((instruction, index) => (instruction, index)).Where(pair => pair.instruction.Op == SdfOp.ShapeBlend).ToArray();

        Assert.Equal(2, shapes.Length);

        var between = scope.Skip(shapes[0].index + 1).Take(shapes[1].index - shapes[0].index - 1).ToArray();
        var reset = Array.FindLastIndex(between, instruction => instruction.Op == SdfOp.ResetPoint);
        var faceTranslate = Array.FindLastIndex(between, instruction => instruction.Op == SdfOp.Translate);

        Assert.True(condition: (faceTranslate > reset), userMessage: "no face translate follows the copy's own chain");

        return (
            shapes[0].instruction,
            shapes[1].instruction,
            (reset >= 0),
            between[faceTranslate],
            between.Skip(faceTranslate + 1).Where(instruction => instruction.Op == SdfOp.Scale).Cast<SdfInstruction?>().LastOrDefault()
        );
    }
    private static void AssertSameShape(SdfInstruction expected, SdfInstruction actual) {
        Assert.Equal(expected.Op, actual.Op);
        Assert.Equal(expected.Shape, actual.Shape);
        Assert.Equal(expected.Blend, actual.Blend);
        Assert.Equal(expected.Data0, actual.Data0);
        Assert.Equal(expected.Data1, actual.Data1);
    }

    [Fact]
    public void ARaisedPanelledShapePacksAnInstanceRadiusCoveringTheRaise() {
        var program = EmitPool(scale: PlateScale, panel: RaisedPanel);
        var active = program.Instances.Where(instance => instance.Active).ToArray();
        var required = (SdfSolidGeometry.Reach(SdfSolidPrimitive.Box, PlateScale, SdfLift.Extrude) + 0.1f);

        Assert.NotEmpty(active);
        Assert.True(
            condition: active.Any(instance => (instance.Radius >= required)),
            userMessage: $"expected an instance covering reach+|depth| {required}, saw radii [{string.Join(", ", active.Select(instance => instance.Radius))}]"
        );
    }

    [Fact]
    public void APanelledShapeEmitsExactlyTwoShapesInItsOwnScopeInThePool() {
        foreach (var scale in new[] { UniformPlateScale, PlateScale }) {
            var program = EmitPool(scale: scale, panel: RecessedPanel);

            Assert.Equal(1, program.Instructions.Count(instruction => instruction.Op == SdfOp.PushField));

            var (_, copy, freshChain, _, _) = PanelPair(scope: FirstScope(program: program));

            Assert.True(condition: freshChain, userMessage: "the pool's panel copy chained after the plate's own shape instead of from its own ResetPoint");
            Assert.Equal((uint)SdfBlendOp.Subtraction, copy.Blend);
        }
    }

    // Static-vs-dynamic parity: the same panelled document places the same two shape instructions, the same face
    // translate, and the same copy scale on both paths, for a plate that rides a Scale op and one that bakes its
    // extents natively.
    [Fact]
    public void StaticAndDynamicPathsEmitTheSamePanelPairForTheSamePanelledDocument() {
        foreach (var scale in new[] { UniformPlateScale, PlateScale }) {
            foreach (var panel in new[] { RecessedPanel, RaisedPanel }) {
                var staticPair = PanelPair(scope: FirstScope(program: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, scale, panel))));
                var poolPair = PanelPair(scope: FirstScope(program: EmitPool(scale: scale, panel: panel)));

                AssertSameShape(expected: staticPair.Plate, actual: poolPair.Plate);
                AssertSameShape(expected: staticPair.Copy, actual: poolPair.Copy);
                Assert.True(condition: staticPair.FreshChain);
                Assert.True(condition: poolPair.FreshChain);
                Assert.Equal(staticPair.FaceTranslate.Data0, poolPair.FaceTranslate.Data0);
                Assert.Equal(staticPair.CopyScale.HasValue, poolPair.CopyScale.HasValue);

                if (staticPair.CopyScale is { } copyScale) {
                    Assert.Equal(copyScale.Data0, poolPair.CopyScale!.Value.Data0);
                }
            }
        }
    }

    // Units: a body look at scale 2 renders the same panel as the same creation authored at twice the size at
    // scale 1 — inset and depth are creation units and scale with the placement, never world units read raw.
    [Fact]
    public void ThePoolScalesAPanelsInsetAndDepthWithThePlacement() {
        foreach (var scale in new[] { UniformPlateScale, PlateScale }) {
            foreach (var panel in new[] { RecessedPanel, RaisedPanel }) {
                var scaled = FirstScope(program: EmitPool(scale: scale, panel: panel, bodyScale: 2f));
                var authoredTwice = FirstScope(program: EmitPool(scale: (scale * 2f), panel: (panel with { Inset = (panel.Inset * 2f), Depth = (panel.Depth * 2f) })));

                Assert.Equal(authoredTwice.Length, scaled.Length);

                for (var index = 0; (index < scaled.Length); index++) {
                    Assert.Equal(authoredTwice[index].Op, scaled[index].Op);
                    Assert.Equal(authoredTwice[index].Shape, scaled[index].Shape);
                    Assert.Equal(authoredTwice[index].Blend, scaled[index].Blend);
                    Assert.Equal(authoredTwice[index].Data0, scaled[index].Data0);
                    Assert.Equal(authoredTwice[index].Data1, scaled[index].Data1);
                }
            }
        }
    }

    // The pool's probe: every slot reserves the panel form unconditionally, so a live panelled slot never outgrows
    // the envelope — per slot and over the whole program, for a plate on either emission branch.
    [Fact]
    public void ThePoolProbeDominatesALivePanelledSlot() {
        var probe = EmitPool(scale: UniformPlateScale, panel: RecessedPanel, probeWorstCase: true);
        var probeScope = FirstScope(program: probe);

        foreach (var scale in new[] { UniformPlateScale, PlateScale }) {
            var live = EmitPool(scale: scale, panel: RecessedPanel);
            var liveScope = FirstScope(program: live);

            Assert.True(
                condition: (probeScope.Length >= liveScope.Length),
                userMessage: $"the probe slot holds {probeScope.Length} instructions in its scope; a live panelled Box at {scale} holds {liveScope.Length}."
            );
            Assert.True(
                condition: (probe.Words.Length >= live.Words.Length),
                userMessage: $"the pool probe reserves {probe.Words.Length} words; a live panelled Box at {scale} emits {live.Words.Length}."
            );
        }
    }
}
