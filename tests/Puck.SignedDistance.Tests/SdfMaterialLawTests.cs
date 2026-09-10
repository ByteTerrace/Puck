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
