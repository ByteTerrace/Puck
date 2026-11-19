using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The exactness a relief mask (<see cref="SdfProgramBuilder.BeginReliefMask"/>) promises, read through the fixed-point
/// evaluator: outside every band its weight is exactly one, so a masked program answers bit for bit as its unmasked twin;
/// on and inside a region whose inside weight is zero the relief is exactly zero, so a carve that is its own region keeps
/// its authored surface. The step bound widens by the band's slope through the one formula the analysis bakes
/// (<see cref="SdfProgram.NoiseDisplaceStepFactor"/>).
/// </summary>
public sealed class SdfReliefMaskLawTests {
    private const float Amplitude = 0.3f;
    private const float Frequency = 1.5f;
    private const float Fade = 0.6f;
    private static readonly Vector3 Bowl = new(x: 0f, y: 1.1f, z: 0f);
    private const float BowlRadius = 0.5f;

    // A slab with a bowl pressed into its top, relieved, in one scope; `mask` adds the bowl's region with inside weight
    // zero, `relief` leaves the relief out.
    private static SdfProgram Slab(bool mask, bool relief = true) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().PushField()
            .ResetPoint().Box(halfExtents: new Vector3(x: 2f, y: 0.5f, z: 2f), material: material, round: 0f)
            .ResetPoint().Translate(offset: Bowl).Sphere(blend: SdfBlendOp.Subtraction, material: material, radius: BowlRadius);
        if (mask) {
            builder.BeginReliefMask(fade: Fade, start: 0f).ResetPoint().Translate(offset: Bowl).Sphere(material: material, radius: BowlRadius).EndReliefMask();
        }
        if (relief) {
            builder.ResetPoint().NoiseDisplace(amplitude: Amplitude, frequency: Frequency, octaves: 3);
        }

        return builder.PopField().Build();
    }
    private static FixedQ4816 Distance(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))));

        return distance;
    }
    private static IEnumerable<Vector3> Lattice(float step) {
        for (var x = -2.5f; (x <= 2.5f); x += step) {
            for (var y = -0.8f; (y <= 1.8f); y += step) {
                for (var z = -2.5f; (z <= 2.5f); z += step) {
                    yield return new Vector3(x: x, y: y, z: z);
                }
            }
        }
    }

    [Fact]
    public void OutsideEveryBandTheMaskedFieldIsTheUnmaskedFieldBitForBit() {
        var masked = new SdfFieldEvaluator(program: Slab(mask: true));
        var unmasked = new SdfFieldEvaluator(program: Slab(mask: false));
        var compared = 0;

        foreach (var point in Lattice(step: 0.23f)) {
            // A margin past the band covers the region's fixed-point rounding.
            if ((Vector3.Distance(value1: point, value2: Bowl) - BowlRadius) <= (Fade + 0.05f)) { continue; }

            Assert.Equal(expected: Distance(field: unmasked, point: point), actual: Distance(field: masked, point: point));
            compared++;
        }

        Assert.True(compared > 1000);
    }

    [Fact]
    public void OnAndInsideARegionOfInsideWeightZeroTheReliefIsExactlyZero() {
        var masked = new SdfFieldEvaluator(program: Slab(mask: true));
        var unrelieved = new SdfFieldEvaluator(program: Slab(mask: false, relief: false));
        var compared = 0;

        foreach (var point in Lattice(step: 0.07f)) {
            // Strictly inside the region, past its fixed-point rounding.
            if ((Vector3.Distance(value1: point, value2: Bowl) - BowlRadius) > -0.01f) { continue; }

            Assert.Equal(expected: Distance(field: unrelieved, point: point), actual: Distance(field: masked, point: point));
            compared++;
        }

        Assert.True(compared > 100);
    }

    [Fact]
    public void AMaskWidensTheReliefsStepBoundByItsBandSlope() {
        var clamps = Slab(mask: true).FieldScopeClamps;
        var expected = (1f / SdfProgram.NoiseDisplaceStepFactor(amplitude: Amplitude, frequency: Frequency, gain: 0.5f, lacunarity: 2f, maskSlope: SdfProgram.ReliefMaskSlope(fade: Fade, inside: 0f, outside: 1f), octaves: 3));

        // The analysis folds the relief in additively, 1 + (factor − 1), which may round the factor's last bit.
        Assert.Contains(collection: clamps, filter: clamp => (MathF.Abs(x: (clamp.StepScale - expected)) <= (expected * 1e-6f)));
    }

    [Fact]
    public void AnythingButAMaskOrItsReliefAfterAMaskIsRefused() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().PushField().ResetPoint().Sphere(material: material, radius: 1f)
            .BeginReliefMask(fade: Fade, start: 0f).ResetPoint().Sphere(material: material, radius: 0.5f).EndReliefMask()
            .ResetPoint().Sphere(material: material, radius: 2f)
            .ResetPoint().NoiseDisplace(amplitude: Amplitude, frequency: Frequency)
            .PopField();

        Assert.Throws<ArgumentException>(testCode: () => builder.Build());
    }

    [Fact]
    public void AWeightOutsideTheUnitIntervalIsRefusedByName() {
        var builder = new SdfProgramBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.BeginReliefMask(fade: Fade, outside: 1.5f, start: 0f));
    }
}
