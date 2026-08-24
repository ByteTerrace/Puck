using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The bound-preserving contract of <see cref="SdfOp.NoiseDisplace"/>: the fBm field op is admitted to the ISA only
/// because its hash is a deterministic integer sequence, its output range is host-normalized to <c>[-1, 1]</c>, and
/// its derivative bound folds into <c>SdfProgram.AnalyzeLipschitz</c> as a step clamp — so each of those legs gets a
/// law here. The step-clamp mirror below reproduces <c>NoiseDisplaceLipschitz</c>'s float operation order exactly, so
/// its equality assertions are bitwise, not tolerance-band.
/// </summary>
public sealed class SdfNoiseDisplaceLawTests {
    // KEEP IN SYNC with SdfProgram.NoiseDisplaceLipschitz: (15/4)·√3, the quintic-blend slope bound over the [-1, 1]
    // corner span, axes combined Euclidean.
    private const float NoiseGradientBound = 6.49519053f;

    private static SdfProgram BuildNoiseSphere(float frequency, float amplitude, int octaves, float gain, float lacunarity) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder
            .ResetPoint()
            .Sphere(
                radius: 1.0f,
                material: material
            )
            .NoiseDisplace(
                amplitude: amplitude,
                frequency: frequency,
                gain: gain,
                lacunarity: lacunarity,
                octaves: octaves
            )
            .Build();
    }
    private static float MirrorStepScale(float frequency, float amplitude, int octaves, float gain, float lacunarity) {
        var gainSum = 0.0f;
        var gainPower = 1.0f;

        for (var octave = 0; (octave < octaves); octave++) {
            gainSum += gainPower;
            gainPower *= gain;
        }

        var gradientSum = 0.0f;
        var termPower = 1.0f;

        for (var octave = 0; (octave < octaves); octave++) {
            gradientSum += termPower;
            termPower *= (gain * lacunarity);
        }

        var factor = (1.0f + (((MathF.Abs(x: amplitude) * (1.0f / gainSum)) * MathF.Abs(x: frequency)) * (NoiseGradientBound * gradientSum)));

        return (1.0f / factor);
    }

    [Fact]
    public void TheStepClampMatchesTheDerivativeBound() {
        const float Frequency = 0.35f;
        const float Amplitude = 2.5f;
        const int Octaves = 4;
        const float Gain = 0.5f;
        const float Lacunarity = 2.0f;

        var program = BuildNoiseSphere(
            amplitude: Amplitude,
            frequency: Frequency,
            gain: Gain,
            lacunarity: Lacunarity,
            octaves: Octaves
        );

        Assert.Equal(
            actual: program.StepScale,
            expected: MirrorStepScale(
                amplitude: Amplitude,
                frequency: Frequency,
                gain: Gain,
                lacunarity: Lacunarity,
                octaves: Octaves
            )
        );
        Assert.True(condition: (program.StepScale < 1.0f));
    }

    [Fact]
    public void AZeroAmplitudeIsAnExactIdentityOnTheStepScale() {
        var program = BuildNoiseSphere(
            amplitude: 0.0f,
            frequency: 3.0f,
            gain: 0.5f,
            lacunarity: 2.0f,
            octaves: 8
        );

        Assert.Equal(
            actual: program.StepScale,
            expected: 1.0f
        );
    }

    [Fact]
    public void ANoiseFreeProgramKeepsTheExactUnitStepScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder
            .ResetPoint()
            .Sphere(
                radius: 1.0f,
                material: material
            )
            .Build();

        Assert.Equal(
            actual: program.StepScale,
            expected: 1.0f
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SdfProgramBuilder.MaxNoiseOctaves + 1)]
    public void AnOctaveCountOutsideTheCapRefusesByName(int octaves) {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.NoiseDisplace(
            amplitude: 1.0f,
            frequency: 1.0f,
            octaves: octaves
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "octaves"
        );
    }

    [Fact]
    public void ANonFiniteFrequencyRefusesByName() {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.NoiseDisplace(
            amplitude: 1.0f,
            frequency: float.NaN
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "frequency"
        );
    }

    [Fact]
    public void ANonPositiveGainRefusesByName() {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.NoiseDisplace(
            amplitude: 1.0f,
            frequency: 1.0f,
            gain: 0.0f
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "gain"
        );
    }

    [Fact]
    public void AParkedInstanceCarryingTheFieldOpRefuses() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentException>(testCode: () => builder
            .BeginInstanceDynamic(
                active: false,
                boundOffset: Vector3.Zero,
                boundRadius: 2.0f,
                slot: 0
            )
            .ResetPoint()
            .Sphere(
                radius: 1.0f,
                material: material
            )
            .NoiseDisplace(
                amplitude: 0.5f,
                frequency: 1.0f
            )
            .EndInstance()
            .Build());

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "NoiseDisplace"
        );
    }

    [Fact]
    public void AnUncontainableCellJitterPrototypeRefusesAtBuild() {
        // jitter/2 (1.2) + capsule reach (~2.05 from the fold frame) > min(spacing)/2 (1.75 for spacing 3.5): packing
        // would collapse the step scale into an immediate-accept solid, so Build refuses by name instead.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentException>(testCode: () => builder
            .ResetPoint()
            .CellJitter(
                jitter: 2.4f,
                spacing: new Vector3(x: 3.5f, y: 3.5f, z: 3.5f)
            )
            .Capsule(
                endpoint: new Vector3(x: 0f, y: 3.4f, z: 0f),
                material: material,
                radius: 0.35f
            )
            .Build());

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "cannot be contained"
        );
    }

    [Fact]
    public void AContainableCellJitterPrototypeBuildsWithABoundedStepClamp() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder
            .ResetPoint()
            .CellJitter(
                jitter: 1.0f,
                spacing: new Vector3(x: 7f, y: 7f, z: 7f)
            )
            .Sphere(
                material: material,
                radius: 1.0f
            )
            .Build();

        Assert.True(condition: (program.StepScale > 0.01f));
        Assert.True(condition: (program.StepScale <= 1.0f));
    }

    [Fact]
    public void TheWarpFreeEvaluatorRefusesTheOpByName() {
        var program = BuildNoiseSphere(
            amplitude: 1.0f,
            frequency: 1.0f,
            gain: 0.5f,
            lacunarity: 2.0f,
            octaves: 2
        );
        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "NoiseDisplace"
        );
    }
}
