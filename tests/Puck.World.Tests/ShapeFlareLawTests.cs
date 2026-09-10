using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
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
    private static readonly ShapeFlareDocument Flare = new(Amount: 1.5f, Bulge: 0.4f, Span: 1.2f, Top: 0.5f);

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
    private static SdfInstruction FlareInstruction(SdfProgram program) =>
        program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.AxialProfile));

    // Sweep is not a closed solid: its curve facet refuses the warp facets by name (ShapeCurveLawTests), so this
    // every-primitive admission law ranges over the closed set only.
    public static IEnumerable<object[]> EveryPrimitive() =>
        Enum.GetValues<SdfSolidPrimitive>().Where(predicate: static type => (type != SdfSolidPrimitive.Sweep)).Select(selector: static type => new object[] { type });

    [Theory]
    [MemberData(memberName: nameof(EveryPrimitive))]
    public void FlareIsAdmittedOnEveryPrimitive(SdfSolidPrimitive type) =>
        AssertCanonicalizerAccepts(document: Document(Shape(type, Vector3.One, Flare)));

    [Fact]
    public void ANormalFlareIsAccepted() =>
        AssertCanonicalizerAccepts(document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, Flare)));

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ANonPositiveOrNonFiniteSpanIsRefusedByName(float span) =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, (Flare with { Span = span }))),
            needle: "flare"
        );

    [Fact]
    public void ANonFiniteAmountIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, (Flare with { Amount = float.NaN }))),
            needle: "flare"
        );

    [Fact]
    public void ANonFiniteBulgeIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, (Flare with { Bulge = float.NaN }))),
            needle: "flare"
        );

    [Fact]
    public void ANonFiniteTopIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, (Flare with { Top = float.NaN }))),
            needle: "flare"
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
    public void TheStaticPathEmitsTheAuthoredValuesVerbatimAtScaleOne() {
        var instruction = FlareInstruction(program: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), stampScale: 1f));

        Assert.Equal(expected: Flare.Amount, actual: instruction.Data0.X, precision: 6);
        Assert.Equal(expected: Flare.Bulge, actual: instruction.Data0.Y, precision: 6);
        Assert.Equal(expected: Flare.Top!.Value, actual: instruction.Data0.Z, precision: 6);
        Assert.Equal(expected: (1f / Flare.Span), actual: instruction.Data0.W, precision: 6);
    }

    // The static chain converts the point into creation units through its own Scale(transform.Scale) op BEFORE the
    // flare, so Top/Span pass through in creation units — the lanes are identical at every stamp scale, and the
    // scale reaches the flare only through the chain's Scale op. (Baking the scale into Top/Span here would scale
    // them twice.)
    [Fact]
    public void TheStaticPathPassesTopAndSpanThroughUnscaledBecauseItsChainCarriesTheStampScale() {
        var atScaleTwo = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), stampScale: 2f);
        var atScaleOne = EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), stampScale: 1f);
        var instructions = atScaleTwo.Instructions.ToList();
        var scaleIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.Scale));
        var flareIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.AxialProfile));

        Assert.Equal(expected: FlareInstruction(program: atScaleOne).Data0, actual: FlareInstruction(program: atScaleTwo).Data0);
        Assert.Equal(expected: FlareInstruction(program: atScaleOne).Data1, actual: FlareInstruction(program: atScaleTwo).Data1);
        Assert.True(condition: ((scaleIndex >= 0) && (scaleIndex < flareIndex)), userMessage: "the chain's Scale op must precede the flare.");
        Assert.Equal(expected: 2f, actual: instructions[scaleIndex].Data0.X);
    }

    // --- Pool emission path (WorldStampPool.EmitShape's twist/bend/flare prefix) ---

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
    public void ThePoolEmitsTheAuthoredValuesVerbatimAtBodyScaleOne() {
        var instruction = FlareInstruction(program: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 1f));

        Assert.Equal(expected: Flare.Amount, actual: instruction.Data0.X, precision: 6);
        Assert.Equal(expected: Flare.Bulge, actual: instruction.Data0.Y, precision: 6);
        Assert.Equal(expected: Flare.Top!.Value, actual: instruction.Data0.Z, precision: 6);
        Assert.Equal(expected: (1f / Flare.Span), actual: instruction.Data0.W, precision: 6);
    }

    [Fact]
    public void ThePoolScalesTopAndSpanWithTheBodyScaleAndLeavesAmountAndBulgeAlone() {
        var atScaleTwo = FlareInstruction(program: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 2f));
        var authoredTwiceAtScaleOne = FlareInstruction(program: EmitPool(
            shape: Shape(SdfSolidPrimitive.Box, Vector3.One, (Flare with {
                Span = (Flare.Span * 2f),
                Top = (Flare.Top!.Value * 2f),
            })),
            bodyScale: 1f
        ));

        Assert.Equal(expected: authoredTwiceAtScaleOne.Data0, actual: atScaleTwo.Data0);
        Assert.Equal(expected: authoredTwiceAtScaleOne.Data1, actual: atScaleTwo.Data1);
    }

    // A domain-bearing pool chain mirrors the static chain: it carries the placement scale as its own Scale op
    // ahead of the fold, so Top/Span pass through in creation units there — scaling them by hand as well would
    // flare a folded, scaled body at twice its authored height.
    [Fact]
    public void ThePoolPassesTopAndSpanThroughUnscaledOnADomainBearingChainThatCarriesTheBodyScale() {
        IReadOnlyList<ShapeDomainOp> domain = [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)];
        var atScaleTwo = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare, domain: domain), bodyScale: 2f);
        var atScaleOne = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare, domain: domain), bodyScale: 1f);
        var instructions = atScaleTwo.Instructions.ToList();
        var scaleIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.Scale));
        var flareIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.AxialProfile));

        Assert.Equal(expected: FlareInstruction(program: atScaleOne).Data0, actual: FlareInstruction(program: atScaleTwo).Data0);
        Assert.True(condition: ((scaleIndex >= 0) && (scaleIndex < flareIndex)), userMessage: "the domain chain's Scale op must precede the flare.");
        Assert.Equal(expected: 2f, actual: instructions[scaleIndex].Data0.X);
        // The control: the same shape WITHOUT a domain has no chain-level Scale op, so the pool bakes the body scale
        // into Top/Span by hand and the lanes differ between the two scales.
        Assert.NotEqual(
            FlareInstruction(program: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 1f)).Data0,
            FlareInstruction(program: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 2f)).Data0
        );
    }

    // THE LAW: a flare scales the cross-section by up to max(s) about the shape's own axis, so both emitters widen the
    // primitive's reach by ShapeFlareDocument.ReachFactor — without it the flared surface clips at its tile edges (the
    // influence-sphere contract). The un-flared shape is the control.
    [Fact]
    public void ThePoolWidensAFlaredShapesBoundByTheFlaresReachFactor() {
        var factor = ShapeFlareDocument.ReachFactor(flare: Flare);
        var flared = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 1f).Instances.Single(predicate: static instance => instance.Active);
        var plain = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, flare: null), bodyScale: 1f).Instances.Single(predicate: static instance => instance.Active);

        Assert.True(condition: (factor > 1f));
        Assert.Equal(expected: (plain.Radius * factor), actual: flared.Radius, precision: 4);
    }

    [Fact]
    public void TheStaticPathWidensAFlaredShapesBoundByTheFlaresReachFactor() {
        var factor = ShapeFlareDocument.ReachFactor(flare: Flare);
        var transform = new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null);
        var flared = CreationStampEmitter.ShapeStampBound(document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, Flare)), shapeIndex: 0, transform: transform).Radius;
        var plain = CreationStampEmitter.ShapeStampBound(document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, flare: null)), shapeIndex: 0, transform: transform).Radius;

        Assert.Equal(expected: (plain * factor), actual: flared, precision: 4);
        Assert.Equal(expected: (plain * factor), actual: CreationStampEmitter.RenderReach(document: Document(Shape(SdfSolidPrimitive.Box, Vector3.One, Flare)), scale: 1f, fontFor: null), precision: 4);
    }

    // THE LAW: a flare is a warp whose Lipschitz factor would otherwise fold into the WHOLE program's step scale, so
    // both emitters give a flared shape its own field scope (the pop clamps the factor onto its own candidate). The
    // un-flared, uniformly scaled Box — no scope at all — is the control.
    [Fact]
    public void AFlaredShapeEmitsInsideItsOwnFieldScopeOnBothPaths() {
        AssertFlareIsScoped(program: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 1f));
        AssertFlareIsScoped(program: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), stampScale: 1f));
        Assert.DoesNotContain(collection: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, flare: null), bodyScale: 1f).Instructions, filter: static instruction => (instruction.Op == SdfOp.PushField));
        Assert.DoesNotContain(collection: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, flare: null), stampScale: 1f).Instructions, filter: static instruction => (instruction.Op == SdfOp.PushField));
        // The scoped flared program's global step scale is exactly 1: the flare's factor sits on the pop, not the program.
        Assert.Equal(expected: 1f, actual: EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), bodyScale: 1f).StepScale);
        Assert.Equal(expected: 1f, actual: EmitStatic(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, Flare), stampScale: 1f).StepScale);
    }

    private static void AssertFlareIsScoped(SdfProgram program) {
        var instructions = program.Instructions.ToList();
        var flareIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.AxialProfile));
        var shapeIndex = instructions.FindIndex(startIndex: flareIndex, match: static instruction => (instruction.Op == SdfOp.ShapeBlend));
        var pushIndex = instructions.FindLastIndex(startIndex: shapeIndex, match: static instruction => (instruction.Op == SdfOp.PushField));
        var popIndex = instructions.FindIndex(startIndex: shapeIndex, match: static instruction => (instruction.Op == SdfOp.PopField));

        Assert.True(condition: (flareIndex >= 0), userMessage: "no AxialProfile emitted.");
        Assert.True(condition: (shapeIndex > flareIndex), userMessage: "no shape follows the flare.");
        Assert.True(condition: ((pushIndex >= 0) && (pushIndex < shapeIndex)), userMessage: "the flared shape has no PushField before it.");
        Assert.True(condition: (popIndex > shapeIndex), userMessage: "the flared shape has no PopField after it.");
        Assert.DoesNotContain(collection: instructions.Skip(count: pushIndex).Take(count: (popIndex - pushIndex)).Skip(count: 1), filter: static instruction => (instruction.Op == SdfOp.PopField));
    }

    [Fact]
    public void ThePoolsProbeEmitsAFlareInstructionEvenWithoutOneAuthored() {
        var program = EmitPool(shape: Shape(SdfSolidPrimitive.Box, Vector3.One, flare: null), bodyScale: 1f, probeWorstCase: true);

        Assert.Contains(collection: program.Instructions, filter: static instruction => (instruction.Op == SdfOp.AxialProfile));
    }
}
