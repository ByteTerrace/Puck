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
    private static float AlphaFromRoughness(float roughness) => MathF.Sqrt(((roughness * roughness) + RoughnessFloorSquared));
    // roughness = sqrt(alpha^2 - floor) — the documented inverse.
    private static float RoughnessFromAlpha(float alpha) => MathF.Sqrt(((alpha * alpha) - RoughnessFloorSquared));

    private static SdfProgramBuilder NewBuilder() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }

    [Fact]
    public void AddMaterialRefusesRoughnessOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: -0.01f)));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: 1.01f)));
        var notFinite = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: float.NaN)));

        Assert.Contains(expectedSubstring: "roughness", actualString: negative.Message);
        Assert.Contains(expectedSubstring: "roughness", actualString: aboveOne.Message);
        Assert.Contains(expectedSubstring: "roughness", actualString: notFinite.Message);

        // Control: the boundaries and the midpoint are all admitted.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: 0f));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: 0.5f));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: 1f));
    }
    [Fact]
    public void AddMaterialRefusesSheenOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Sheen: -0.01f)));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Sheen: 1.01f)));

        Assert.Contains(expectedSubstring: "sheen", actualString: negative.Message);
        Assert.Contains(expectedSubstring: "sheen", actualString: aboveOne.Message);

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Sheen: 0f));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Sheen: 1f));
    }
    [Fact]
    public void AddMaterialRefusesMetalOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Metal: -0.01f)));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Metal: 1.01f)));

        Assert.Contains(expectedSubstring: "metal", actualString: negative.Message);
        Assert.Contains(expectedSubstring: "metal", actualString: aboveOne.Message);

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Metal: 0f));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Metal: 1f));
    }
    [Fact]
    public void AddMaterialRefusesCoatOutsideTheUnitRange() {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Coat: -0.01f)));
        var aboveOne = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Coat: 1.01f)));

        Assert.Contains(expectedSubstring: "coat", actualString: negative.Message);
        Assert.Contains(expectedSubstring: "coat", actualString: aboveOne.Message);

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Coat: 0f));
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Coat: 1f));
    }
    // A caller carrying over the retired raw exponent's magnitude (32, the old default) into the new normalized
    // field is refused rather than silently reinterpreted as a roughness fraction.
    [Fact]
    public void AddMaterialRefusesARawShininessMagnitudeInPlaceOfRoughness() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Specular: 0.5f, Roughness: 32f)));

        Assert.Contains(expectedSubstring: "roughness", actualString: refusal.Message);
    }
    [Fact]
    public void TheConstructorRefusesPackedRoughnessOrSheenOutsideTheUnitRange() {
        var instructions = new[] { Shape() };

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One, Roughness: 32f)]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One, Sheen: -1f)]
        ));

        // Control.
        _ = new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One, Roughness: 0.4f, Sheen: 0.3f)]
        );
    }
    [Fact]
    public void TheConstructorRefusesPackedMetalOrCoatOutsideTheUnitRange() {
        var instructions = new[] { Shape() };

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One, Metal: 1.5f)]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One, Coat: -0.2f)]
        ));

        // Control.
        _ = new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One, Metal: 0.8f, Coat: 0.2f)]
        );
    }
    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.4f)]
    [InlineData(0.7f)]
    [InlineData(1.0f)]
    public void RoughnessAlphaFloorMappingRoundTripsItsDocumentedInverse(float alpha) {
        var roughness = RoughnessFromAlpha(alpha: alpha);
        var recovered = AlphaFromRoughness(roughness: roughness);

        Assert.True(
            condition: (roughness >= 0f) && (roughness <= 1f),
            userMessage: $"roughness {roughness} for alpha {alpha} fell outside [0, 1]."
        );
        Assert.Equal(
            actual: recovered,
            expected: alpha,
            precision: 5
        );
    }
    [Fact]
    public void DefaultRoughnessReproducesTheWalterEquivalentAlphaOfExponent32WithinFloatTolerance() {
        // The Walter et al. (2007) Blinn-Phong-to-GGX alpha equivalence for the retired default exponent 32.
        var target = MathF.Sqrt((2f / (32f + 2f)));
        var alpha = AlphaFromRoughness(roughness: SdfMaterial.DefaultRoughness);

        Assert.True(
            condition: (MathF.Abs(alpha - target) < 1.0e-3f),
            userMessage: $"DefaultRoughness forward-mapped to alpha {alpha}, not within tolerance of {target}."
        );

        // Control: a roughness a full percentage point off the default is NOT within the same tolerance, so the
        // assertion above is not vacuous.
        var offByOne = AlphaFromRoughness(roughness: (SdfMaterial.DefaultRoughness + 0.01f));

        Assert.True(condition: (MathF.Abs(offByOne - target) > 1.0e-3f));
    }
    [Fact]
    public void PackedMaterialWordsCarryEveryShadingFieldInTheirDocumentedLanes() {
        var material = new SdfMaterial(Albedo: Vector3.One, Emissive: 0f, Specular: 0.35f, Roughness: 0.4f, Sheen: 0.3f, Metal: 0.6f, Coat: 0.25f);
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
    [Fact]
    public void MaterialLayersPackAuthoredSurfacesAndAllStops() {
        var stages = new SdfRevealStage[] { new(0.3f, new(new(0.1f, 0.2f, 0.3f), 0.7f, 0.1f)), new(0.8f, new(Vector3.One, 0.2f, 1f)) };
        var inset = new SdfInset(new(1f, 2f, 3f), Quaternion.Identity, 0.1f, 1f,
            new([new(0f, Vector3.Zero), new(0.1f, Vector3.UnitX), new(0.2f, Vector3.UnitY), new(0.3f, Vector3.UnitZ)]));
        var material = new SdfMaterial(Vector3.One, Inset: inset,
            Weathering: new(Edge: 1f, Under: stages, Lane: 3, Seed: uint.MaxValue));
        var program = new SdfProgram([Shape()], [material]);
        var words = program.Words;
        var offset = (int)words[3] * 4;
        Assert.Equal(4f, BitConverter.UInt32BitsToSingle(words[offset + 6 * 4 + 1]));
        Assert.Equal(0.3f, BitConverter.UInt32BitsToSingle(words[offset + 11 * 4 + 3]));
        Assert.Equal(uint.MaxValue, words[offset + 13 * 4]);
        Assert.Equal(3f, BitConverter.UInt32BitsToSingle(words[offset + 13 * 4 + 3]));
        Assert.Equal(0.8f, BitConverter.UInt32BitsToSingle(words[offset + 16 * 4 + 3]));
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(words[offset + 17 * 4 + 1]));
    }
    [Fact]
    public void UnauthoredLayersPackAsZero() {
        var program = new SdfProgram([Shape()], [new SdfMaterial(Vector3.One)]);
        var offset = (int)program.Words[3] * 4;
        for (var i = 4 * 4; i < 20 * 4; i++) {
            Assert.Equal(0u, program.Words[offset + i]);
        }
    }
    [Fact]
    public void WeatheringRequiresItsAuthoredSurfacesAtBothAdmissionDoors() {
        foreach (var weathering in new SdfWeathering[] { new(Edge: 1f), new(Lines: 1f), new(Settle: 1f), new(Lane: 4), new(Reach: 0f) }) {
            var material = new SdfMaterial(Vector3.One, Weathering: weathering);
            Assert.Throws<ArgumentOutOfRangeException>(() => NewBuilder().AddMaterial(material));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SdfProgram([Shape()], [material]));
        }
    }
    [Fact]
    public void AddMaterialRefusesWrapOrSoftenOutsideTheUnitRange() {
        var negativeWrap = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Wrap: -0.01f)));
        var aboveOneSoften = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Soften: 1.01f)));

        Assert.Contains(expectedSubstring: "wrap", actualString: negativeWrap.Message);
        Assert.Contains(expectedSubstring: "soften", actualString: aboveOneSoften.Message);

        // Control.
        _ = NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Wrap: 1f, Soften: 0f));
    }
    [Fact]
    public void AddMaterialRefusesANegativeBounceComponent() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => NewBuilder().AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Bounce: new Vector3(-0.1f, 0f, 0f))));

        Assert.Contains(expectedSubstring: "bounce", actualString: refusal.Message);
    }
    [Fact]
    public void InvalidInsetFramesAndRampsAreRefusedAtBothDoors() {
        var valid = new SdfInset(Vector3.Zero, Quaternion.Identity, 0.1f, 1f, new([new(0f, Vector3.Zero), new(1f, Vector3.One)]));
        _ = NewBuilder().AddMaterial(new(Vector3.One, Inset: valid));
        foreach (var inset in new[] {
            valid with { Ior = 0f }, valid with { Rotation = default },
            valid with { Paint = new([new(1f, Vector3.One), new(0f, Vector3.Zero)]) },
            valid with { Paint = new(Enumerable.Range(0, 5).Select(i => new SdfRadialStop(i, Vector3.One)).ToArray()) },
        }) {
            var material = new SdfMaterial(Vector3.One, Inset: inset);
            Assert.Throws<ArgumentOutOfRangeException>(() => NewBuilder().AddMaterial(material));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SdfProgram([Shape()], [material]));
        }
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

        Assert.Equal(expected: 0f, actual: BitConverter.UInt32BitsToSingle(value: words[(m1Base + 3)]));
        Assert.Equal(expected: 0f, actual: BitConverter.UInt32BitsToSingle(value: words[m2Base]));
    }

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
}
