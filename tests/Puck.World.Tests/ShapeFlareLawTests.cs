using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeFlareDocument"/> is admitted on every primitive, refused by name against a non-finite
/// amount/bulge/top or a span that is not finite and strictly positive; Top/Span (creation-unit lengths) scale with
/// the placement on both emission paths exactly as Rounding/Chamfer do (see
/// <c>WorldStampPoolShapeUnitsLawTests</c>'s convention), while Amount/Bulge (dimensionless ratios) pass through
/// unscaled.
/// </summary>
public sealed class ShapeFlareLawTests {
    private const string PrototypeId = "flared";

    private static readonly ShapeFlareDocument Flare = new(
        Amount: 1.5f,
        Bulge: 0.4f,
        Span: 1.2f,
        Top: 0.5f
    );

    private static void AssertFlareIsScoped(SdfProgram program) {
        var instructions = program.Instructions.ToList();
        var flareIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.AxialProfile));
        var shapeIndex = instructions.FindIndex(
            startIndex: flareIndex,
            match: static instruction => (instruction.Op == SdfOp.ShapeBlend)
        );
        var pushIndex = instructions.FindLastIndex(
            startIndex: shapeIndex,
            match: static instruction => (instruction.Op == SdfOp.PushField)
        );
        var popIndex = instructions.FindIndex(
            startIndex: shapeIndex,
            match: static instruction => (instruction.Op == SdfOp.PopField)
        );

        Assert.True(
            condition: (flareIndex >= 0),
            userMessage: "no AxialProfile emitted."
        );
        Assert.True(
            condition: (shapeIndex > flareIndex),
            userMessage: "no shape follows the flare."
        );
        Assert.True(
            condition: ((pushIndex >= 0) && (pushIndex < shapeIndex)),
            userMessage: "the flared shape has no PushField before it."
        );
        Assert.True(
            condition: (popIndex > shapeIndex),
            userMessage: "the flared shape has no PopField after it."
        );
        Assert.DoesNotContain(
            collection: instructions.Skip(count: pushIndex).Take(count: (popIndex - pushIndex)).Skip(count: 1),
            filter: static instruction => (instruction.Op == SdfOp.PopField)
        );
    }
    private static CreationDocument Document(params ShapeDocument[] shapes) => CreationFixtures.Document(
        name: PrototypeId,
        shapes: shapes
    );
    // --- Pool emission path (WorldStampPool.EmitShape's twist/bend/flare prefix) ---

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
    private static SdfInstruction FlareInstruction(SdfProgram program) =>
        program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.AxialProfile));
    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, ShapeFlareDocument? flare, int id = 0, IReadOnlyList<ShapeDomainOp>? domain = null) =>
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
            Flare: flare
        );

    // THE LAW: a flare is a warp whose Lipschitz factor would otherwise fold into the WHOLE program's step scale, so
    // both emitters give a flared shape its own field scope (the pop clamps the factor onto its own candidate). The
    // un-flared, uniformly scaled Box — no scope at all — is the control.
    [Fact]
    public void AFlaredShapeEmitsInsideItsOwnFieldScopeOnBothPaths() {
        AssertFlareIsScoped(program: EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            ),
            bodyScale: 1f
        ));
        AssertFlareIsScoped(program: EmitStatic(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            ),
            stampScale: 1f
        ));
        Assert.DoesNotContain(
            collection: EmitPool(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    flare: null
                ),
                bodyScale: 1f
            ).Instructions,
            filter: static instruction => (instruction.Op == SdfOp.PushField)
        );
        Assert.DoesNotContain(
            collection: EmitStatic(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    flare: null
                ),
                stampScale: 1f
            ).Instructions,
            filter: static instruction => (instruction.Op == SdfOp.PushField)
        );
        // The scoped flared program's global step scale is exactly 1: the flare's factor sits on the pop, not the program.
        Assert.Equal(
            expected: 1f,
            actual: EmitPool(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    Flare
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
                    Flare
                ),
                stampScale: 1f
            ).StepScale
        );
    }
    [InlineData("amount", float.NaN)]
    [InlineData("bulge", float.NaN)]
    [InlineData("span", 0f)]
    [InlineData("span", -1f)]
    [InlineData("span", float.NaN)]
    [InlineData("span", float.PositiveInfinity)]
    [InlineData("span", float.NegativeInfinity)]
    [InlineData("top", float.NaN)]
    [Theory]
    public void AFlareLaneOutsideItsAdmittedRangeIsRefusedByName(string lane, float value) =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                lane switch {
                    "amount" => (Flare with { Amount = value }),
                    "bulge" => (Flare with { Bulge = value }),
                    "span" => (Flare with { Span = value }),
                    _ => (Flare with { Top = value }),
                }
            )),
            needle: "flare"
        );
    [Fact]
    public void ANormalFlareIsAccepted() =>
        CreationFixtures.AssertAccepts(document: Document(Shape(
            SdfSolidPrimitive.Box,
            Vector3.One,
            Flare
        )));
    // Sweep is not a closed solid: its curve facet refuses the warp facets by name (ShapeCurveLawTests), so this
    // every-primitive admission law ranges over the closed set only.
    public static TheoryData<SdfSolidPrimitive> EveryPrimitive() => CreationFixtures.EveryPrimitiveExcept(excluded: SdfSolidPrimitive.Sweep);
    [MemberData(memberName: nameof(EveryPrimitive))]
    [Theory]
    public void FlareIsAdmittedOnEveryPrimitive(SdfSolidPrimitive type) =>
        CreationFixtures.AssertAccepts(document: Document(Shape(
            type,
            Vector3.One,
            Flare
        )));
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void BothPathsEmitTheAuthoredValuesVerbatimAtScaleOne(bool pooled) {
        var shape = Shape(
            SdfSolidPrimitive.Box,
            Vector3.One,
            Flare
        );
        var instruction = FlareInstruction(program: (pooled
            ? EmitPool(
                bodyScale: 1f,
                shape: shape
            )
            : EmitStatic(
                shape: shape,
                stampScale: 1f
            )));

        Assert.Equal(
            expected: Flare.Amount,
            actual: instruction.Data0.X,
            precision: 6
        );
        Assert.Equal(
            expected: Flare.Bulge,
            actual: instruction.Data0.Y,
            precision: 6
        );
        Assert.Equal(
            expected: Flare.Top!.Value,
            actual: instruction.Data0.Z,
            precision: 6
        );
        Assert.Equal(
            expected: (1f / Flare.Span),
            actual: instruction.Data0.W,
            precision: 6
        );
    }
    // A domain-bearing pool chain mirrors the static chain: it carries the placement scale as its own Scale op
    // ahead of the fold, so Top/Span pass through in creation units there — scaling them by hand as well would
    // flare a folded, scaled body at twice its authored height.
    [Fact]
    public void ThePoolPassesTopAndSpanThroughUnscaledOnADomainBearingChainThatCarriesTheBodyScale() {
        IReadOnlyList<ShapeDomainOp> domain = [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)];
        var atScaleTwo = EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare,
                domain: domain
            ),
            bodyScale: 2f
        );
        var atScaleOne = EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare,
                domain: domain
            ),
            bodyScale: 1f
        );
        var instructions = atScaleTwo.Instructions.ToList();
        var scaleIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.Scale));
        var flareIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.AxialProfile));

        Assert.Equal(
            expected: FlareInstruction(program: atScaleOne).Data0,
            actual: FlareInstruction(program: atScaleTwo).Data0
        );
        Assert.True(
            condition: ((scaleIndex >= 0) && (scaleIndex < flareIndex)),
            userMessage: "the domain chain's Scale op must precede the flare."
        );
        Assert.Equal(
            expected: 2f,
            actual: instructions[scaleIndex].Data0.X
        );
        // The control: the same shape WITHOUT a domain has no chain-level Scale op, so the pool bakes the body scale
        // into Top/Span by hand and the lanes differ between the two scales.
        Assert.NotEqual(
            FlareInstruction(program: EmitPool(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    Flare
                ),
                bodyScale: 1f
            )).Data0,
            FlareInstruction(program: EmitPool(
                shape: Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    Flare
                ),
                bodyScale: 2f
            )).Data0
        );
    }
    [Fact]
    public void ThePoolScalesTopAndSpanWithTheBodyScaleAndLeavesAmountAndBulgeAlone() {
        var atScaleTwo = FlareInstruction(program: EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            ),
            bodyScale: 2f
        ));
        var authoredTwiceAtScaleOne = FlareInstruction(program: EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                (Flare with {
                    Span = (Flare.Span * 2f),
                    Top = (Flare.Top!.Value * 2f),
                })
            ),
            bodyScale: 1f
        ));

        Assert.Equal(
            expected: authoredTwiceAtScaleOne.Data0,
            actual: atScaleTwo.Data0
        );
        Assert.Equal(
            expected: authoredTwiceAtScaleOne.Data1,
            actual: atScaleTwo.Data1
        );
    }
    // THE LAW: a flare scales the cross-section by up to max(s) about the shape's own axis, so both emitters widen the
    // primitive's reach by ShapeFlareDocument.ReachFactor — without it the flared surface clips at its tile edges (the
    // influence-sphere contract). The un-flared shape is the control.
    [Fact]
    public void ThePoolWidensAFlaredShapesBoundByTheFlaresReachFactor() {
        var factor = ShapeFlareDocument.ReachFactor(flare: Flare);
        var flared = EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            ),
            bodyScale: 1f
        ).Instances.Single(predicate: static instance => instance.Active);
        var plain = EmitPool(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                flare: null
            ),
            bodyScale: 1f
        ).Instances.Single(predicate: static instance => instance.Active);

        Assert.True(condition: (factor > 1f));
        Assert.Equal(
            expected: (plain.Radius * factor),
            actual: flared.Radius,
            precision: 4
        );
    }
    // The static chain converts the point into creation units through its own Scale(transform.Scale) op BEFORE the
    // flare, so Top/Span pass through in creation units — the lanes are identical at every stamp scale, and the
    // scale reaches the flare only through the chain's Scale op. (Baking the scale into Top/Span here would scale
    // them twice.)
    [Fact]
    public void TheStaticPathPassesTopAndSpanThroughUnscaledBecauseItsChainCarriesTheStampScale() {
        var atScaleTwo = EmitStatic(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            ),
            stampScale: 2f
        );
        var atScaleOne = EmitStatic(
            shape: Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            ),
            stampScale: 1f
        );
        var instructions = atScaleTwo.Instructions.ToList();
        var scaleIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.Scale));
        var flareIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.AxialProfile));

        Assert.Equal(
            expected: FlareInstruction(program: atScaleOne).Data0,
            actual: FlareInstruction(program: atScaleTwo).Data0
        );
        Assert.Equal(
            expected: FlareInstruction(program: atScaleOne).Data1,
            actual: FlareInstruction(program: atScaleTwo).Data1
        );
        Assert.True(
            condition: ((scaleIndex >= 0) && (scaleIndex < flareIndex)),
            userMessage: "the chain's Scale op must precede the flare."
        );
        Assert.Equal(
            expected: 2f,
            actual: instructions[scaleIndex].Data0.X
        );
    }
    [Fact]
    public void TheStaticPathWidensAFlaredShapesBoundByTheFlaresReachFactor() {
        var factor = ShapeFlareDocument.ReachFactor(flare: Flare);
        var transform = new CreationStampTransform(
            Origin: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: 1f,
            ReflectionNormal: null
        );
        var flared = CreationStampEmitter.ShapeStampBound(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                Flare
            )),
            shapeIndex: 0,
            transform: transform
        ).Radius;
        var plain = CreationStampEmitter.ShapeStampBound(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                Vector3.One,
                flare: null
            )),
            shapeIndex: 0,
            transform: transform
        ).Radius;

        Assert.Equal(
            actual: flared,
            expected: (plain * factor),
            precision: 4
        );
        Assert.Equal(
            expected: (plain * factor),
            actual: CreationStampEmitter.RenderReach(
                document: Document(Shape(
                    SdfSolidPrimitive.Box,
                    Vector3.One,
                    Flare
                )),
                scale: 1f,
                fontFor: null
            ),
            precision: 4
        );
    }
}
