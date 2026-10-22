using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <see cref="SdfMaterial.Roughness"/>/<see cref="SdfMaterial.Sheen"/>/<see cref="SdfMaterial.Metal"/>/
/// <see cref="SdfMaterial.Coat"/> are normalized [0, 1] curve parameters, not raw shading magnitudes —
/// <c>AddMaterial</c> and the packed <see cref="SdfProgram"/> constructor both refuse a value outside that range
/// (including one shaped like the retired raw Blinn-Phong exponent, e.g. 32), the roughness-to-GGX-alpha floor
/// formula documented on <see cref="SdfMaterial.Roughness"/> round-trips its own documented inverse,
/// <see cref="SdfMaterial.DefaultRoughness"/> reproduces the Walter-equivalent alpha of the retired Blinn-Phong
/// default exponent 32 within float tolerance, and the packed material words carry every field in its documented
/// lane.
/// </summary>
public sealed class SdfMaterialLawTests {
    // alpha = sqrt(roughness^2 + SdfRoughnessFloorSquared) — SdfMaterial.Roughness's documented GGX floor formula,
    // mirrored host-side so this file has no shader dependency.
    private const float RoughnessFloorSquared = 0.018f;

    private static float AlphaFromRoughness(float roughness) => MathF.Sqrt(x: ((roughness * roughness) + RoughnessFloorSquared));
    private static SdfProgramBuilder NewBuilder() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }
    // roughness = sqrt(alpha^2 - floor) — the documented inverse.
    private static float RoughnessFromAlpha(float alpha) => MathF.Sqrt(x: ((alpha * alpha) - RoughnessFloorSquared));
    private static SdfInstruction Shape() => new(
        Blend: ((uint)SdfBlendOp.Union),
        Data0: new Vector4(
            w: 0f,
            x: 1f,
            y: 0f,
            z: 0f
        ),
        Data1: Vector4.Zero,
        Material: 0u,
        Op: SdfOp.ShapeBlend,
        Shape: ((uint)SdfShapeType.Sphere)
    );

    [Fact]
    public void AddMaterialRefusesANegativeFillComponent() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Fill: new Vector3(
                x: -0.1f,
                y: 0f,
                z: 0f
            )
        )));

        Assert.Contains(
            expectedSubstring: "fill",
            actualString: refusal.Message
        );
    }
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void BothMaterialDoorsRefuseInvalidIndirectInputs(float value) {
        foreach (var material in new[] {
            new SdfMaterial(Vector3.One, Bleed: new Vector3(x: 1, y: value, z: 1)),
            new SdfMaterial(Vector3.One, Receive: value),
        }) {
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: material));
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram([Shape()], [material]));
        }
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Vector3.One, Bleed: Vector3.Zero, Receive: 0));
        _ = new SdfProgram([Shape()], [new SdfMaterial(Vector3.One, Receive: 2)]);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void IndirectInputsUseTheirReservedMaterialLanesWithAndWithoutInset(bool inset) {
        var material = new SdfMaterial(Vector3.One, Fill: new Vector3(x: 0.2f, y: 0.3f, z: 0.4f),
            Bleed: new Vector3(x: 0.5f, y: 0.6f, z: 0.7f), Receive: 2f,
            Inset: (inset ? new SdfInset(Vector3.Zero, Quaternion.Identity, 0.1f, 1f,
                new([new(0, Vector3.Zero), new(1, Vector3.One)])) : null));
        var program = new SdfProgram([Shape()], [material]);
        var offset = (((int)program.Words[SdfProgram.ProgramMaterialOffsetLane]) * 4);

        Assert.Equal(0.5f, BitConverter.UInt32BitsToSingle(value: program.Words[(offset + 11)]));
        Assert.Equal(2f, BitConverter.UInt32BitsToSingle(value: program.Words[(offset + 15)]));
        Assert.Equal(0.6f, BitConverter.UInt32BitsToSingle(value: program.Words[(offset + 30)]));
        Assert.Equal(0.7f, BitConverter.UInt32BitsToSingle(value: program.Words[(offset + 31)]));
        Assert.Equal(0.2f, BitConverter.UInt32BitsToSingle(value: program.Words[(offset + 12)]));

        var defaults = new SdfProgram([Shape()], [new SdfMaterial(Vector3.One)]);
        var defaultOffset = (((int)defaults.Words[SdfProgram.ProgramMaterialOffsetLane]) * 4);

        foreach (var lane in new[] { 11, 15, 30, 31 }) {
            Assert.Equal(1f, BitConverter.UInt32BitsToSingle(value: defaults.Words[(defaultOffset + lane)]));
        }
    }
    // A caller carrying over the retired raw exponent's magnitude (32, the old default) into the new normalized
    // field is refused rather than silently reinterpreted as a roughness fraction.
    [Fact]
    public void AddMaterialRefusesARawShininessMagnitudeInPlaceOfRoughness() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: 32f
        )));

        Assert.Contains(
            expectedSubstring: "roughness",
            actualString: refusal.Message
        );
    }
    [Fact]
    public void AddMaterialRefusesCoatOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Coat: -0.01f
        )));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Coat: 1.01f
        )));

        Assert.Contains(
            expectedSubstring: "coat",
            actualString: negative.Message
        );
        Assert.Contains(
            expectedSubstring: "coat",
            actualString: aboveOne.Message
        );

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Coat: 0f
        ));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Coat: 1f
        ));
    }
    [Fact]
    public void AddMaterialRefusesMetalOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Metal: -0.01f
        )));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Metal: 1.01f
        )));

        Assert.Contains(
            expectedSubstring: "metal",
            actualString: negative.Message
        );
        Assert.Contains(
            expectedSubstring: "metal",
            actualString: aboveOne.Message
        );

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Metal: 0f
        ));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Metal: 1f
        ));
    }
    [Fact]
    public void AddMaterialRefusesRoughnessOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: -0.01f
        )));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: 1.01f
        )));
        var notFinite = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: float.NaN
        )));

        Assert.Contains(
            expectedSubstring: "roughness",
            actualString: negative.Message
        );
        Assert.Contains(
            expectedSubstring: "roughness",
            actualString: aboveOne.Message
        );
        Assert.Contains(
            expectedSubstring: "roughness",
            actualString: notFinite.Message
        );

        // Control: the boundaries and the midpoint are all admitted.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: 0f
        ));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: 0.5f
        ));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Specular: 0.5f,
            Roughness: 1f
        ));
    }
    [Fact]
    public void AddMaterialRefusesSheenOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Sheen: -0.01f
        )));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Sheen: 1.01f
        )));

        Assert.Contains(
            expectedSubstring: "sheen",
            actualString: negative.Message
        );
        Assert.Contains(
            expectedSubstring: "sheen",
            actualString: aboveOne.Message
        );

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Sheen: 0f
        ));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Sheen: 1f
        ));
    }
    [Fact]
    public void AddMaterialRefusesWrapOrSoftenOutsideTheUnitRange() {
        var negativeWrap = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Wrap: -0.01f
        )));
        var aboveOneSoften = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Soften: 1.01f
        )));

        Assert.Contains(
            expectedSubstring: "wrap",
            actualString: negativeWrap.Message
        );
        Assert.Contains(
            expectedSubstring: "soften",
            actualString: aboveOneSoften.Message
        );

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(
            Albedo: Vector3.One,
            Wrap: 1f,
            Soften: 0f
        ));
    }
    // A default-constructed material (unauthored Metal/Coat) packs both as exactly 0 — the byte-identical-with-the
    // pre-migration-shape contract Metal/Coat's XML docs promise.
    [Fact]
    public void AnUnauthoredMaterialPacksMetalAndCoatAsExactlyZero() {
        var program = new SdfProgram(
            instructions: [Shape()],
            materials: [new SdfMaterial(Albedo: Vector3.One)]
        );
        var words = program.Words;
        var materialOffsetVectors = words[3];
        var m1Base = ((int)((materialOffsetVectors * 4u) + 4u));
        var m2Base = (m1Base + 4);

        Assert.Equal(
            expected: 0f,
            actual: BitConverter.UInt32BitsToSingle(value: words[(m1Base + 3)])
        );
        Assert.Equal(
            expected: 0f,
            actual: BitConverter.UInt32BitsToSingle(value: words[m2Base])
        );
    }
    [Fact]
    public void DefaultRoughnessReproducesTheWalterEquivalentAlphaOfExponent32WithinFloatTolerance() {
        // The Walter et al. (2007) Blinn-Phong-to-GGX alpha equivalence for the retired default exponent 32.
        var target = MathF.Sqrt(x: (2f / (32f + 2f)));
        var alpha = AlphaFromRoughness(roughness: SdfMaterial.DefaultRoughness);

        Assert.True(
            condition: (MathF.Abs(x: (alpha - target)) < 1.0e-3f),
            userMessage: $"DefaultRoughness forward-mapped to alpha {alpha}, not within tolerance of {target}."
        );

        // Control: a roughness a full percentage point off the default is NOT within the same tolerance, so the
        // assertion above is not vacuous.
        var offByOne = AlphaFromRoughness(roughness: (SdfMaterial.DefaultRoughness + 0.01f));

        Assert.True(condition: (MathF.Abs(x: (offByOne - target)) > 1.0e-3f));
    }
    [Fact]
    public void InvalidInsetFramesAndRampsAreRefusedAtBothDoors() {
        var valid = new SdfInset(
            Vector3.Zero,
            Quaternion.Identity,
            0.1f,
            1f,
            new([new(
                    0f,
                    Vector3.Zero
                ), new(
                    1f,
                    Vector3.One
                )])
        );

        _ = NewBuilder().AddMaterial(material: new(
            Vector3.One,
            Inset: valid
        ));
        foreach (var inset in new[] {
            valid with { Ior = 0f }, valid with { Rotation = default },
            valid with { Paint = new([new(
                1f,
                Vector3.One
            ), new(
                0f,
                Vector3.Zero
            )]) },
            valid with { Paint = new(Enumerable.Range(
            count: 5,
            start: 0
        ).Select(selector: i => new SdfRadialStop(
            i,
            Vector3.One
        )).ToArray()) },
        }) {
            var material = new SdfMaterial(
                Vector3.One,
                Inset: inset
            );

            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: material));
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
                [Shape()],
                [material]
            ));
        }
    }
    /// <summary>Paints pack into the paint table between the instruction data and the materials, each material's
    /// contiguous: material row 20 names its first paint's vector and its count (sdfMaterialLoad), and a paint's rows
    /// carry its frame and shape type, rotation, residual scale, the shape's two data vectors as the builder packs them,
    /// colour and stroke, slot and reach, then its side table, a sweep's offset naming that table (sdfApplyPaints).
    /// An invalid paint, or more than the per-material maximum, is refused at both doors.</summary>
    [Fact]
    public void PaintsPackIntoThePaintTableAndAreRefusedOutOfRangeAtBothDoors() {
        var plane = new SdfPaint(Color: new Vector3(x: 0.5f, y: 0.25f, z: 0.125f), Origin: new Vector3(x: 0f, y: 2f, z: 0f), Rotation: new Quaternion(x: 0f, y: 0f, z: 0f, w: 2f), Shape: SdfPaintShape.Of(scale: Vector3.One, type: SdfSolidPrimitive.Plane), Stroke: 0.02f);
        var sweep = new SdfPaint(Color: Vector3.One, Origin: Vector3.Zero, Rotation: Quaternion.Identity, Shape: SdfPaintShape.Of(curve: new SdfSweepParameters(A: Vector3.Zero, B: new Vector3(x: 0.5f, y: 0.25f, z: 0f), Bulge: 0f, C: Vector3.UnitX, RadiusEnd: 0.01f, RadiusStart: 0.01f, StrandOffset: 0f, Strands: 1, Twist: 0f), scale: new Vector3(value: 2f), type: SdfSolidPrimitive.Sweep), Stroke: 0f, Slot: 5);
        var program = new SdfProgram([Shape()], [new SdfMaterial(Vector3.One), new SdfMaterial(Vector3.One, Paints: [plane, sweep])]);
        float Lane(int word) => BitConverter.UInt32BitsToSingle(value: program.Words[word]);
        var paintVector = (((int)program.Words[SdfProgram.ProgramDataOffsetLane]) + SdfProgram.InstructionDataVectors);
        var secondMaterial = ((((int)program.Words[SdfProgram.ProgramMaterialOffsetLane]) + SdfProgram.MaterialVectorsPerEntry + 20) * 4);

        Assert.Equal(expected: 0u, actual: program.Words[(((((int)program.Words[SdfProgram.ProgramMaterialOffsetLane]) + 20) * 4) + 1)]);
        Assert.Equal(expected: (uint)paintVector, actual: program.Words[secondMaterial]);
        Assert.Equal(expected: 2u, actual: program.Words[(secondMaterial + 1)]);
        Assert.Equal(expected: (paintVector + (2 * SdfProgram.PaintVectorsPerEntry)), actual: (int)program.Words[SdfProgram.ProgramMaterialOffsetLane]);
        // The shading words, which a lighting-only upload rewrites, span the paints and the materials.
        Assert.Equal(expected: new Range(start: (paintVector * 4), end: ((((int)program.Words[SdfProgram.ProgramMaterialOffsetLane]) + (2 * SdfProgram.MaterialVectorsPerEntry)) * 4)), actual: program.ShadingWords);

        var first = (paintVector * 4);

        Assert.Equal(expected: [0f, 2f, 0f], actual: [Lane(first), Lane(first + 1), Lane(first + 2)]);
        Assert.Equal(expected: (uint)SdfShapeType.Plane, actual: program.Words[(first + 3)]);
        Assert.Equal(expected: [0f, 0f, 0f, 1f], actual: Enumerable.Range(start: (first + 4), count: 4).Select(selector: Lane));
        Assert.Equal(expected: [0.5f, 0.25f, 0.125f, 0.02f], actual: Enumerable.Range(start: (first + 20), count: 4).Select(selector: Lane));
        Assert.Equal(expected: SdfProgram.PaintWinningFrame, actual: program.Words[(first + 24)]);
        Assert.Equal(expected: [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity], actual: Enumerable.Range(start: (first + 28), count: 3).Select(selector: Lane));

        var second = ((paintVector + SdfProgram.PaintVectorsPerEntry) * 4);

        Assert.Equal(expected: (uint)SdfShapeType.Sweep, actual: program.Words[(second + 3)]);
        Assert.Equal(expected: (uint)((paintVector + SdfProgram.PaintVectorsPerEntry) + 8), actual: program.Words[(second + 12)]);
        Assert.Equal(expected: 5u, actual: program.Words[(second + 24)]);
        Assert.Equal(expected: [0f, 0f, 0f, 0.02f, 1f, 0.5f, 0f, 0.02f, 2f, 0f, 0f, 0f], actual: Enumerable.Range(start: (second + 32), count: 12).Select(selector: Lane));
        foreach (var paint in new[] {
            plane with { Rotation = default }, plane with { Stroke = -1f },
            plane with { Origin = new Vector3(x: float.NaN, y: 0f, z: 0f) }, plane with { Color = new Vector3(x: -1f, y: 0f, z: 0f) },
            plane with { Slot = -2 }, plane with { Shape = plane.Shape with { Type = SdfShapeType.Path } },
        }) {
            var material = new SdfMaterial(Vector3.One, Paints: [plane, paint]);

            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: material));
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram([Shape()], [material]));
        }
        var crowded = new SdfMaterial(Vector3.One, Paints: Enumerable.Repeat(element: plane, count: (SdfProgram.MaxPaintsPerMaterial + 1)).ToArray());

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: crowded));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram([Shape()], [crowded]));
    }
    /// <summary>A paint shape is the instruction the builder packs for a document primitive: a residual scale transform
    /// folds into its scale (a torus scaled differently in X and Z keeps one; scaled alike, it packs its native ellipse), a convex polygon carries its vertices and their count, and a primitive the views pass
    /// cannot evaluate (a path profile) is refused.</summary>
    [Fact]
    public void PaintShapesCaptureThePrimitiveTheBuilderPacks() {
        var torus = SdfPaintShape.Of(scale: new Vector3(x: 1f, y: 2f, z: 3f), type: SdfSolidPrimitive.Torus);
        var elliptic = SdfPaintShape.Of(scale: new Vector3(x: 1f, y: 2f, z: 1f), type: SdfSolidPrimitive.Torus);
        var polygon = SdfPaintShape.Of(profile: new SdfPrismProfile(Kind: SdfPrismProfileKind.Convex, CornerRadius: 0f, Vertices: [new Vector2(x: -1f, y: -1f), new Vector2(x: 0f, y: 1f), new Vector2(x: 1f, y: -1f)]), scale: new Vector3(value: 0.5f), type: SdfSolidPrimitive.Prism);

        Assert.Equal(expected: SdfShapeType.Torus, actual: torus.Type);
        Assert.Equal(expected: new Vector3(x: 1f, y: 2f, z: 3f), actual: torus.Scale);
        Assert.Empty(collection: torus.Table);
        Assert.Equal(expected: SdfShapeType.Ellipse, actual: elliptic.Type);
        Assert.Equal(expected: Vector3.One, actual: elliptic.Scale);
        Assert.Equal(expected: SdfShapeType.ConvexPolygon, actual: polygon.Type);
        Assert.Equal(expected: SdfPrismProfile.PackConvexReference(count: 3, tableVector: 0), actual: BitConverter.SingleToUInt32Bits(value: polygon.Data0.X));
        Assert.Equal(expected: 6, actual: polygon.Table.Count);
        // Bounds: the canonical box at the scale, a revolve's reach on every axis, a sweep's control points widened by
        // its radius, and a plane unbounded.
        Assert.Equal(expected: (SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Torus).HalfExtents * new Vector3(x: 1f, y: 2f, z: 3f)), actual: torus.Bound.HalfExtents);
        Assert.Equal(expected: new Vector3(value: 0.5f * 1.0001f), actual: polygon.Bound.HalfExtents);
        var revolve = SdfPaintShape.Of(lift: SdfLift.Revolve, scale: new Vector3(x: 0.2f, y: 0.3f, z: 0.5f), type: SdfSolidPrimitive.Prism);
        Assert.Equal(expected: new Vector3(value: SdfSolidGeometry.Reach(lift: SdfLift.Revolve, scale: new Vector3(x: 0.2f, y: 0.3f, z: 0.5f), type: SdfSolidPrimitive.Prism)), actual: revolve.Bound.HalfExtents);
        var sweep = SdfPaintShape.Of(curve: new SdfSweepParameters(A: Vector3.Zero, B: new Vector3(x: 0.5f, y: 0.5f, z: 0f), Bulge: 0f, C: Vector3.UnitX, RadiusEnd: 0.25f, RadiusStart: 0.25f, StrandOffset: 0f, Strands: 1, Twist: 0f), scale: new Vector3(value: 2f), type: SdfSolidPrimitive.Sweep);
        Assert.Equal(expected: new Vector3(x: 1f, y: 0.5f, z: 0f), actual: sweep.Bound.Center);
        Assert.Equal(expected: new Vector3(x: 1.5f, y: 1f, z: 0.5f), actual: sweep.Bound.HalfExtents);
        Assert.True(condition: SdfPaintShape.Of(scale: Vector3.One, type: SdfSolidPrimitive.Plane).Bound.IsUnbounded);
        Assert.Throws<ArgumentException>(testCode: () => SdfPaintShape.Of(profile: new SdfPrismProfile(Kind: SdfPrismProfileKind.Path), scale: Vector3.One, type: SdfSolidPrimitive.Prism));
    }
    /// <summary>A polygon paint covers every sector of its outline: reading the packed paint the way sdfApplyPaints does
    /// (the vertex count and table vector in the shape's first data lane, the vertices two to a vector) yields exactly the
    /// polygon's own vertices, and the sdfConvexPolygon2D distance at each sector's centre is inside. A count read one
    /// too high adds a vertex at the table's zeroed tail and cuts a sector away.</summary>
    [Theory]
    [InlineData(SdfPrismProfileKind.Polygon, 5)]
    [InlineData(SdfPrismProfileKind.Polygon, 6)]
    [InlineData(SdfPrismProfileKind.Convex, 6)]
    public void PolygonPaintsPackWholeAndCoverEverySector(SdfPrismProfileKind kind, int sides) {
        var vertices = Enumerable.Range(start: 0, count: sides).Select(selector: index => new Vector2(x: MathF.Sin(x: (index * MathF.Tau / sides)), y: MathF.Cos(x: (index * MathF.Tau / sides)))).ToArray();
        var profile = ((kind == SdfPrismProfileKind.Polygon)
            ? new SdfPrismProfile(Kind: kind, Sides: sides)
            : new SdfPrismProfile(Kind: kind, CornerRadius: 0f, Vertices: vertices));
        var scale = new Vector3(x: 0.115f, y: 0.105f, z: 0.06f);
        var paint = new SdfPaint(Color: Vector3.One, Origin: Vector3.Zero, Rotation: Quaternion.Identity, Shape: SdfPaintShape.Of(profile: profile, scale: scale, type: SdfSolidPrimitive.Prism), Stroke: 0f);
        var program = new SdfProgram([Shape()], [new SdfMaterial(Vector3.One, Paints: [paint])]);
        var first = ((((int)program.Words[SdfProgram.ProgramDataOffsetLane]) + SdfProgram.InstructionDataVectors) * 4);
        var reference = program.Words[(first + 12)];
        var count = (int)((reference & ((1u << SdfPrismProfile.ConvexCountBits) - 1u)) + 1u);
        var tableWord = (int)((reference >> SdfPrismProfile.ConvexCountBits) * 4u);
        var packed = Enumerable.Range(start: 0, count: count).Select(selector: index => new Vector2(x: BitConverter.UInt32BitsToSingle(value: program.Words[(tableWord + (2 * index))]), y: BitConverter.UInt32BitsToSingle(value: program.Words[(tableWord + (2 * index) + 1)]))).ToArray();

        Assert.Equal(expected: sides, actual: count);
        Assert.Equal(expected: vertices.Select(selector: vertex => vertex * new Vector2(x: scale.X, y: scale.Y)), actual: packed);
        // sdfConvexPolygon2D: the running minimum squared segment distance, signed by crossing parity.
        float Distance(Vector2 point) {
            var previous = packed[^1];
            var best = Vector2.Dot(value1: (point - packed[0]), value2: (point - packed[0]));
            var sign = 1f;

            foreach (var vertex in packed) {
                var edge = (previous - vertex);
                var offset = (point - vertex);
                var closest = (offset - (edge * Math.Clamp(value: (Vector2.Dot(value1: offset, value2: edge) / Vector2.Dot(value1: edge, value2: edge)), min: 0f, max: 1f)));

                best = MathF.Min(x: best, y: Vector2.Dot(value1: closest, value2: closest));
                if (((point.Y >= vertex.Y) == (point.Y < previous.Y)) && (((point.Y >= vertex.Y) == ((edge.X * offset.Y) > (edge.Y * offset.X))))) { sign = -sign; }
                previous = vertex;
            }

            return (sign * MathF.Sqrt(x: best));
        }

        for (var sector = 0; (sector < sides); sector++) {
            var angle = ((sector + 0.5f) * MathF.Tau / sides);
            var centre = (new Vector2(x: MathF.Sin(x: angle) * scale.X, y: MathF.Cos(x: angle) * scale.Y) * 0.5f);

            Assert.True(condition: (Distance(point: centre) < 0f), userMessage: $"sector {sector} of the {sides}-sided {kind} paint is uncovered");
        }
    }
    [Fact]
    public void MaterialLayersPackAuthoredSurfacesAndAllStops() {
        var stages = new SdfRevealStage[] { new(
            0.3f,
            new(
                new(
                    x: 0.1f,
                    y: 0.2f,
                    z: 0.3f
                ),
                0.7f,
                0.1f
            )
        ), new(
            0.8f,
            new(
                Vector3.One,
                0.2f,
                1f
            )
        ) };
        var inset = new SdfInset(
            new(
                x: 1f,
                y: 2f,
                z: 3f
            ),
            Quaternion.Identity,
            0.1f,
            1f,
            new([new(
                    0f,
                    Vector3.Zero
                ), new(
                    0.1f,
                    Vector3.UnitX
                ), new(
                    0.2f,
                    Vector3.UnitY
                ), new(
                    0.3f,
                    Vector3.UnitZ
                )])
        );
        var material = new SdfMaterial(
            Vector3.One,
            Inset: inset,
            Weathering: new(
                Edge: 1f,
                Under: stages,
                Lane: 3,
                Seed: uint.MaxValue
            )
        );
        var program = new SdfProgram(
            [Shape()],
            [material]
        );
        var words = program.Words;
        var offset = (((int)words[3]) * 4);

        Assert.Equal(
            4f,
            BitConverter.UInt32BitsToSingle(value: words[((offset + (6 * 4)) + 1)])
        );
        Assert.Equal(
            0.3f,
            BitConverter.UInt32BitsToSingle(value: words[((offset + (11 * 4)) + 3)])
        );
        Assert.Equal(
            uint.MaxValue,
            words[(offset + (13 * 4))]
        );
        Assert.Equal(
            3f,
            BitConverter.UInt32BitsToSingle(value: words[((offset + (13 * 4)) + 3)])
        );
        Assert.Equal(
            0.8f,
            BitConverter.UInt32BitsToSingle(value: words[((offset + (16 * 4)) + 3)])
        );
        Assert.Equal(
            1f,
            BitConverter.UInt32BitsToSingle(value: words[((offset + (17 * 4)) + 1)])
        );
    }
    [Fact]
    public void PackedMaterialWordsCarryEveryShadingFieldInTheirDocumentedLanes() {
        var material = new SdfMaterial(
            Albedo: Vector3.One,
            Emissive: 0f,
            Specular: 0.35f,
            Roughness: 0.4f,
            Sheen: 0.3f,
            Metal: 0.6f,
            Coat: 0.25f
        );
        var program = new SdfProgram(
            instructions: [Shape()],
            materials: [material]
        );
        var words = program.Words;
        var materialOffsetVectors = words[3];
        var m1Base = ((int)((materialOffsetVectors * 4u) + 4u));
        var m2Base = (m1Base + 4);

        Assert.Equal(
            actual: BitConverter.UInt32BitsToSingle(value: words[m1Base]),
            expected: material.Specular
        );
        Assert.Equal(
            actual: BitConverter.UInt32BitsToSingle(value: words[(m1Base + 1)]),
            expected: material.Roughness
        );
        Assert.Equal(
            actual: BitConverter.UInt32BitsToSingle(value: words[(m1Base + 2)]),
            expected: material.Sheen
        );
        Assert.Equal(
            actual: BitConverter.UInt32BitsToSingle(value: words[(m1Base + 3)]),
            expected: material.Metal
        );
        Assert.Equal(
            actual: BitConverter.UInt32BitsToSingle(value: words[m2Base]),
            expected: material.Coat
        );
    }
    [InlineData(0.2f)]
    [InlineData(0.4f)]
    [InlineData(0.7f)]
    [InlineData(1.0f)]
    [Theory]
    public void RoughnessAlphaFloorMappingRoundTripsItsDocumentedInverse(float alpha) {
        var roughness = RoughnessFromAlpha(alpha: alpha);
        var recovered = AlphaFromRoughness(roughness: roughness);

        Assert.True(
            condition: ((roughness >= 0f) && (roughness <= 1f)),
            userMessage: $"roughness {roughness} for alpha {alpha} fell outside [0, 1]."
        );
        Assert.Equal(
            actual: recovered,
            expected: alpha,
            precision: 5
        );
    }
    [Fact]
    public void TheConstructorRefusesPackedMetalOrCoatOutsideTheUnitRange() {
        var instructions = new[] { Shape() };

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(
                    Albedo: Vector3.One,
                    Metal: 1.5f
                )]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(
                    Albedo: Vector3.One,
                    Coat: -0.2f
                )]
        ));

        // Control.
        _ = new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(
                    Albedo: Vector3.One,
                    Metal: 0.8f,
                    Coat: 0.2f
                )]
        );
    }
    [Fact]
    public void TheConstructorRefusesPackedRoughnessOrSheenOutsideTheUnitRange() {
        var instructions = new[] { Shape() };

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(
                    Albedo: Vector3.One,
                    Roughness: 32f
                )]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(
                    Albedo: Vector3.One,
                    Sheen: -1f
                )]
        ));

        // Control.
        _ = new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(
                    Albedo: Vector3.One,
                    Roughness: 0.4f,
                    Sheen: 0.3f
                )]
        );
    }
    [Fact]
    public void UnauthoredLayersPackAsZero() {
        var program = new SdfProgram(
            [Shape()],
            [new SdfMaterial(Vector3.One)]
        );
        var offset = (((int)program.Words[3]) * 4);

        // The inset's unused tail shares two lanes with the material's unit-default indirect bleed channels.
        for (var i = (4 * 4); (i < (SdfProgram.MaterialVectorsPerEntry * 4)); i++) {
            Assert.Equal(
                ((i is 30 or 31) ? BitConverter.SingleToUInt32Bits(value: 1f) : 0u),
                program.Words[(offset + i)]
            );
        }
    }
    [Fact]
    public void WeatheringRequiresItsAuthoredSurfacesAtBothAdmissionDoors() {
        foreach (var weathering in new SdfWeathering[] { new(Edge: 1f), new(Lines: 1f), new(Settle: 1f), new(Lane: 4), new(Reach: 0f) }) {
            var material = new SdfMaterial(
                Vector3.One,
                Weathering: weathering
            );

            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: material));
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
                [Shape()],
                [material]
            ));
        }
    }
}
