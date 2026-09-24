using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
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
        A: new Vector3(
            x: 0.1f,
            y: 0f,
            z: 0f
        ),
        B: new Vector3(
            x: 0f,
            y: 1f,
            z: 0f
        ),
        C: new Vector3(
            x: -0.1f,
            y: 2f,
            z: 0f
        ),
        RadiusStart: 0.1f,
        RadiusEnd: 0.08f,
        Bulge: 0.02f,
        Strands: 1,
        Twist: 0f,
        StrandOffset: 0f
    );

    private static CreationDocument Document(params ShapeDocument[] shapes) => CreationFixtures.Document(
        name: PrototypeId,
        shapes: shapes
    );
    private static SdfProgram EmitPool(ShapeDocument shape, float bodyScale) => CreationFixtures.EmitPool(
        bodyScale: bodyScale,
        name: PrototypeId,
        shapes: [shape]
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
    private static SdfInstruction SweepInstruction(SdfProgram program) =>
        program.Instructions.Single(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) == SdfShapeType.Sweep)));

    [InlineData("radiusStart", 0f)]
    [InlineData("radiusStart", -0.1f)]
    [InlineData("strands", 0f)]
    [InlineData("strands", 5f)]
    [Theory]
    public void ACurveLaneOutsideItsAdmittedRangeIsRefusedByName(string lane, float value) =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                ((lane == "strands")
                    ? (Curve with { Strands = ((int)value) })
                    : (Curve with { RadiusStart = value }))
            )),
            needle: lane
        );
    [Fact]
    public void ANonUniformScaleOnASweepIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                new Vector3(
                    x: 2f,
                    y: 1f,
                    z: 1f
                ),
                Curve
            )),
            needle: "non-uniform"
        );
    [Fact]
    public void ANormalSweepIsAccepted() =>
        CreationFixtures.AssertAccepts(document: Document(Shape(
            SdfSolidPrimitive.Sweep,
            Vector3.One,
            Curve
        )));
    [InlineData("domain")]
    [InlineData("flare")]
    [InlineData("panel")]
    [Theory]
    public void ASweepCarryingAnotherFacetIsRefusedByName(string facet) =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                Curve,
                domain: ((facet == "domain")
                    ? [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)]
                    : null),
                flare: ((facet == "flare")
                    ? new ShapeFlareDocument(
                        Amount: 1f,
                        Bulge: 0f,
                        Span: 1f
                    )
                    : null),
                panel: ((facet == "panel")
                    ? new ShapePanelDocument(
                        Material: 0,
                        Inset: 0.1f,
                        Depth: 0.01f
                    )
                    : null)
            )),
            needle: facet
        );
    [Fact]
    public void ASweepWithNoCurveIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                curve: null
            )),
            needle: "requires a curve"
        );
    [MemberData(memberName: nameof(EveryNonSweepPrimitive))]
    [Theory]
    public void CurveIsRefusedOnEveryOtherPrimitive(SdfSolidPrimitive type) =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                type,
                Vector3.One,
                Curve
            )),
            needle: "curve"
        );
    public static TheoryData<SdfSolidPrimitive> EveryNonSweepPrimitive() => CreationFixtures.EveryPrimitiveExcept(excluded: SdfSolidPrimitive.Sweep);
    [Fact]
    public void ThePoolEmitsASweepInstructionWithoutThrowing() {
        var program = EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                Curve
            ),
            bodyScale: 1f
        );

        Assert.Contains(
            collection: program.Instructions,
            filter: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) == SdfShapeType.Sweep))
        );
    }
    [Fact]
    public void TheStaticPathEmitsASweepInstruction() {
        var instruction = SweepInstruction(program: CreationFixtures.EmitStatic(document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                Curve
            ))));

        Assert.Equal(
            expected: 1f,
            actual: instruction.Data0.Y,
            precision: 4
        ); // strands
        Assert.Equal(
            expected: Curve.Twist!.Value,
            actual: instruction.Data0.Z,
            precision: 4
        );
        Assert.Equal(
            expected: Curve.StrandOffset!.Value,
            actual: instruction.Data0.W,
            precision: 4
        );
    }
    // THE LAW: a Sweep's own control points/radii already carry creation-unit dimensions, so the uniform placement
    // scale must reach them exactly as it reaches every other primitive's canonical unit dimensions — a doubled
    // stamp scale doubles the curve's own reach (SdfSolidGeometry.SweepReach), matching RenderReach's own doubling.
    [Fact]
    public void TheStaticPathScalesTheCurvesReachWithTheUniformStampScale() {
        var reachAtOne = CreationStampEmitter.RenderReach(
            document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                Curve
            )),
            scale: 1f,
            fontFor: null
        );
        var reachAtTwo = CreationStampEmitter.RenderReach(
            document: Document(Shape(
                SdfSolidPrimitive.Sweep,
                Vector3.One,
                Curve
            )),
            scale: 2f,
            fontFor: null
        );

        Assert.Equal(
            actual: reachAtTwo,
            expected: (reachAtOne * 2f),
            precision: 3
        );
    }
}
