using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Shadow query ceilings preserve the march's decisions, and the K word preserves four independent visibilities.</summary>
public sealed class SdfShadowVisibilityLawTests {
    [Fact]
    public void AQueryCeilingCannotTurnAClearRescaledFieldIntoAShadowHit() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.PushField().ResetPoint().Sphere(1f, material).PopField();
        var original = builder.Build();
        var instructions = original.Instructions.ToArray();

        instructions[^1] = instructions[^1] with { Data1 = new Vector4(w: 0f, x: 0f, y: 1000f, z: 0f) };
        var program = new SdfProgram(instructions, [new SdfMaterial(Albedo: Vector3.One)],
            instances: [new SdfInstanceRange(First: 0, End: instructions.Length, IsDynamic: false, Center: Vector3.Zero, Radius: 1f, Slot: 0)],
            screenSurfaces: null);

        Assert.True(condition: program.IndirectInstancesComposable);
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.True(condition: evaluator.TryDistance(FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: new Vector3(x: 1.04f, y: 0f, z: 0f))), out var field, out _));
        const float HitThreshold = 1f;

        Assert.True(condition: (((float)field) > HitThreshold));
        var clipped = MathF.Min(x: ((float)field), y: (SdfShadowQuery.Ceiling(stepCeiling: 0.08f, traveled: 0.02f, stepScale: 1000f, programStepScale: program.StepScale, sharpness: 9f) * program.StepScale));

        Assert.False(condition: (clipped < HitThreshold), userMessage: "Clipping a clear sample must not turn it into a hit at the shading-scaled threshold.");
    }
    [InlineData(0.02f, 1000f, 1f, 9f)]
    [InlineData(0.12f, 0.12f, 0.5f, 9f)]
    [InlineData(1.6f, 1f, 1f, 0.1f)]
    [InlineData(9f, 0.001f, 0.001f, 1000f)]
    [Theory]
    public void SaturationNeighboursKeepStrideHitAndVisibilityBits(float traveled, float scale, float programScale, float sharpness) {
        var stepCeiling = MathF.Max(x: 0.08f, y: (traveled * 0.05f));
        var ceiling = SdfShadowQuery.Ceiling(programStepScale: programScale, sharpness: sharpness, stepCeiling: stepCeiling, stepScale: scale, traveled: traveled);

        foreach (var value in new[] { float.BitDecrement(x: ceiling), ceiling, float.BitIncrement(x: ceiling), (ceiling * 2f) }) {
            var full = (value * programScale);
            var clipped = (MathF.Min(x: value, y: ceiling) * programScale);

            Assert.Equal(actual: (clipped < (0.001f * scale)), expected: (full < (0.001f * scale)));
            Assert.Equal(BitConverter.SingleToInt32Bits(value: Math.Clamp(max: stepCeiling, min: 0.02f, value: full)),
                BitConverter.SingleToInt32Bits(value: Math.Clamp(max: stepCeiling, min: 0.02f, value: clipped)));
            var fullVisibility = ((traveled >= 0.12f) ? MathF.Min(x: 1f, y: ((sharpness * (full / scale)) / traveled)) : 1f);
            var clippedVisibility = ((traveled >= 0.12f) ? MathF.Min(x: 1f, y: ((sharpness * (clipped / scale)) / traveled)) : 1f);

            Assert.Equal(BitConverter.SingleToInt32Bits(value: fullVisibility), BitConverter.SingleToInt32Bits(value: clippedVisibility));
        }
    }
    [Fact]
    public void EveryEightBitCodePacksAndUnpacksInEachIndependentSlot() {
        Assert.Equal(actual: SdfVisibility.ShadowBits, expected: 8);
        Assert.Equal(actual: SdfVisibility.ShadowMask, expected: 255u);

        for (var slot = 0; (slot < 4); slot++) {
            for (var code = 0; (code < 256); code++) {
                byte[] codes = [17, 61, 139, 233];

                codes[slot] = ((byte)code);
                var word = ((uint)codes[0]) | (((uint)codes[1]) << 8) | (((uint)codes[2]) << 16) | (((uint)codes[3]) << 24);
                var visibility = new Vector4(x: (codes[0] / 255f), y: (codes[1] / 255f), z: (codes[2] / 255f), w: (codes[3] / 255f));

                Assert.Equal(expected: word, actual: SdfVisibility.PackShadows(visibility: visibility));
                for (var lane = 0; (lane < 4); lane++) {
                    Assert.Equal(expected: (codes[lane] / 255f), actual: SdfVisibility.ShadowAt(slot: lane, word: word));
                }
            }
        }
    }
    [Fact]
    public void PackingSaturatesAndRoundsMidpointsUpWithoutMixingSlots() {
        Assert.Equal(expected: 0xFFFF0000u,
            actual: SdfVisibility.PackShadows(visibility: new Vector4(w: 2f, x: -2f, y: 0f, z: 1f)));
        Assert.Equal(expected: 0xFF804001u,
            actual: SdfVisibility.PackShadows(visibility: new Vector4(w: (254.5f / 255f), x: (0.5f / 255f), y: (63.5f / 255f), z: (127.5f / 255f))));

        for (var code = 0; (code < 255); code++) {
            var visibility = new Vector4(w: 1f, x: ((code + 0.25f) / 255f),
                y: ((code + 0.5f) / 255f), z: ((code + 0.75f) / 255f));
            var expected = ((uint)code) | (((uint)(code + 1)) << 8) | (((uint)(code + 1)) << 16) | 0xFF000000u;

            Assert.Equal(expected: expected, actual: SdfVisibility.PackShadows(visibility: visibility));
        }
    }
}
