using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeCurveDocument"/> is admitted only on, and required on,
/// <see cref="SdfSolidPrimitive.Sweep"/>; refused alongside Panel/Trims/Flare/Shear/Bumps/Domain on the same shape;
/// requires a uniform <see cref="ShapeDocument.Scale"/> (refused by name otherwise); and that uniform scale bakes
/// onto every one of the curve's own lengths identically on both emission paths.
/// </summary>
public sealed class ShapeCurveLawTests {
    private const string PrototypeId = "swept";
    private static readonly ShapeCurveDocument Curve = new(
        A: new Vector3(0.1f, 0f, 0f),
        B: new Vector3(0f, 1f, 0f),
        C: new Vector3(-0.1f, 2f, 0f),
        RadiusStart: 0.1f,
        RadiusEnd: 0.08f,
        Bulge: 0.02f,
        Strands: 1,
        Twist: 0f,
        StrandOffset: 0f
    );

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, ShapeCurveDocument? curve, int id = 0, ShapePanelDocument? panel = null, IReadOnlyList<ShapeDomainOp>? domain = null, ShapeFlareDocument? flare = null) =>
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
            Panel: panel,
            Flare: flare,
            Curve: curve
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
    private static SdfInstruction SweepInstruction(SdfProgram program) =>
        program.Instructions.Single(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) == SdfShapeType.Sweep)));

    public static IEnumerable<object[]> EveryNonSweepPrimitive() =>
        Enum.GetValues<SdfSolidPrimitive>().Where(predicate: static type => (type != SdfSolidPrimitive.Sweep)).Select(selector: static type => new object[] { type });

    [Fact]
    public void ANormalSweepIsAccepted() =>
        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve)));

    [Theory]
    [MemberData(memberName: nameof(EveryNonSweepPrimitive))]
    public void CurveIsRefusedOnEveryOtherPrimitive(SdfSolidPrimitive type) =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(type, Vector3.One, Curve)),
            needle: "curve"
        );

    [Fact]
    public void ASweepWithNoCurveIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, curve: null)),
            needle: "requires a curve"
        );

    [Fact]
    public void ASweepWithAPanelIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve, panel: new ShapePanelDocument(Material: 0, Inset: 0.1f, Depth: 0.01f))),
            needle: "panel"
        );

    [Fact]
    public void ASweepWithDomainOpsIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve, domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)])),
            needle: "domain"
        );

    [Fact]
    public void ASweepWithAFlareIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve, flare: new ShapeFlareDocument(Amount: 1f, Bulge: 0f, Span: 1f))),
            needle: "flare"
        );

    [Fact]
    public void ANonUniformScaleOnASweepIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, new Vector3(2f, 1f, 1f), Curve)),
            needle: "non-uniform"
        );

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    public void ANonPositiveRadiusStartIsRefusedByName(float radiusStart) =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, (Curve with { RadiusStart = radiusStart }))),
            needle: "radiusStart"
        );

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AStrandCountOutsideTheAdmittedRangeIsRefusedByName(int strands) =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, (Curve with { Strands = strands }))),
            needle: "strands"
        );

    // --- Static emission path (CreationStampEmitter.Emit -> SdfSolidGeometry.AppendScaledPrimitive) ---

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
    public void TheStaticPathEmitsASweepInstruction() {
        var instruction = SweepInstruction(program: EmitStatic(shape: Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve), stampScale: 1f));

        Assert.Equal(expected: 1f, actual: instruction.Data0.Y, precision: 4); // strands
        Assert.Equal(expected: Curve.Twist!.Value, actual: instruction.Data0.Z, precision: 4);
        Assert.Equal(expected: Curve.StrandOffset!.Value, actual: instruction.Data0.W, precision: 4);
    }

    // THE LAW: a Sweep's own control points/radii already carry creation-unit dimensions, so the uniform placement
    // scale must reach them exactly as it reaches every other primitive's canonical unit dimensions — a doubled
    // stamp scale doubles the curve's own reach (SdfSolidGeometry.SweepReach), matching RenderReach's own doubling.
    [Fact]
    public void TheStaticPathScalesTheCurvesReachWithTheUniformStampScale() {
        var reachAtOne = CreationStampEmitter.RenderReach(document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve)), scale: 1f, fontFor: null);
        var reachAtTwo = CreationStampEmitter.RenderReach(document: Document(Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve)), scale: 2f, fontFor: null);

        Assert.Equal(expected: (reachAtOne * 2f), actual: reachAtTwo, precision: 3);
    }

    // --- Pool emission path (WorldStampPool.EmitShape) ---

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
    public void ThePoolEmitsASweepInstructionWithoutThrowing() {
        var program = EmitPool(shape: Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve), bodyScale: 1f);

        Assert.Contains(
            collection: program.Instructions,
            filter: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) == SdfShapeType.Sweep))
        );
    }

    [Fact]
    public void ThePoolsProbeDoesNotThrowForASweepBearingBody() {
        var exception = Record.Exception(testCode: () => EmitPool(shape: Shape(SdfSolidPrimitive.Sweep, Vector3.One, Curve), bodyScale: 1f, probeWorstCase: true));

        Assert.Null(exception);
    }
}
