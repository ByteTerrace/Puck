using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky and the bounded media animate on the frame's presented tick, reduced on the host: the environment
/// bakes the twinkle's phase (zero when nothing twinkles) and the cloud layer's integrated drift, shear and spin, and the
/// volume table carries each medium's integrated advection and pulse gain, so the values move by one tick's worth of
/// their rate between consecutive ticks wherever the tick or the reduction wraps.</summary>
public sealed class SdfSkyClockLawTests {
    private const ulong Wrap = (1UL << 32);

    private static SdfFrame Frame(SdfEnvironment environment, ulong tick) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Sphere(
            radius: 1f,
            material: material
        );

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Views: [
                new SdfViewSnapshot(
                    Camera: new CameraSnapshot(
                        AspectRatio: 1f,
                        Forward: Vector3.UnitZ,
                        Position: Vector3.Zero,
                        Right: Vector3.UnitX,
                        TanHalfFieldOfView: 0.5f,
                        Up: Vector3.UnitY
                    ),
                    Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
                ),
            ],
            Time: 0f
        ) {
            Clock = new PresentedTick(
                Fraction: 0d,
                Whole: tick
            ),
            Environment = environment,
        };
    }
    private static float[] Bake(SdfEnvironment environment, ulong tick) {
        var rows = new float[SdfEnvironment.LaneCount];

        SdfFrameBlock.BakeEnvironment(
            frame: Frame(
                environment: environment,
                tick: tick
            ),
            rows: rows
        );

        return rows;
    }

    [Fact]
    public void The_twinkle_phase_is_its_periods_phase_at_the_tick_and_zero_when_nothing_twinkles() {
        var environment = SdfEnvironment.Default();

        environment.StarBrightness = 1f;
        environment.StarDensity = 48f;
        environment.TwinkleShare = 0.5f;
        environment.TwinkleDepth = 0.5f;
        environment.TwinkleRate = 2f;

        var lane = ((SdfEnvironment.TwinkleRow * 4) + 2);
        var period = SdfFrameBlock.TwinklePeriodTicks(rateHertz: 2f);
        var tick = (Wrap + 1234UL);

        Assert.Equal(actual: period, expected: 25200UL);
        Assert.Equal(
            actual: Bake(environment: environment, tick: tick)[lane],
            expected: ((float)(((double)(tick % period)) / period))
        );

        // Red leg: with no star brightness nothing twinkles, and the phase bakes zero so a still sky can stand.
        environment.StarBrightness = 0f;

        Assert.Equal(expected: 0f, actual: Bake(environment: environment, tick: tick)[lane]);
    }
    [Fact]
    public void The_cloud_offsets_move_by_one_ticks_worth_of_wind_across_two_to_the_thirty_two() {
        var environment = SdfEnvironment.Default();

        environment.CloudDrift = new Vector2(x: 0.02f, y: -0.01f);
        environment.CloudShear = new Vector2(x: 0.005f, y: 0.003f);
        environment.CloudSpin = 0.1f;

        var before = Bake(environment: environment, tick: (Wrap - 1UL));
        var after = Bake(environment: environment, tick: Wrap);
        var drift = ((SdfEnvironment.CloudsRow + 2) * 4);
        var spin = ((SdfEnvironment.CloudsRow + 3) * 4);
        (int Lane, double Rate, double Modulus)[] lanes = [
            (drift, 0.02d, SdfVolume.NoisePeriodCells),
            ((drift + 1), -0.01d, SdfVolume.NoisePeriodCells),
            ((drift + 2), 0.005d, SdfVolume.NoisePeriodCells),
            ((drift + 3), 0.003d, SdfVolume.NoisePeriodCells),
            (spin, 0.1d, Math.Tau),
        ];

        foreach (var (lane, rate, modulus) in lanes) {
            var step = Math.IEEERemainder(
                x: (after[lane] - ((double)before[lane])),
                y: modulus
            );

            Assert.InRange(
                actual: Math.Abs(value: (step - (((double)((float)rate)) / EngineTicks.PerSecond))),
                high: 1e-3d,
                low: 0d
            );
        }
    }
    [Fact]
    public void A_mediums_advection_and_pulse_are_its_rates_integrated_to_the_tick() {
        var volume = new SdfVolume(
            Kind: SdfVolumeKind.Cloud,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            HalfExtent: Vector3.One,
            DynamicSlot: SdfProgram.NoDynamicTransformSlot,
            Axis: 1f,
            Width: 0.5f,
            Speed: 2f,
            Seed: 7u,
            Steps: 16,
            Ramp: [new SdfDensityStop(Density: 0f, Color: Vector3.One)],
            Intensity: 1f,
            Extinction: 1f,
            PulseAmplitude: 0.5f,
            PulseFrequency: 0.25f
        );
        // One second in: four cells of advection along X, 0.84 along Z, and a quarter of the pulse's cycle.
        var second = SdfVolumeMotion.At(
            clock: new PresentedTick(Fraction: 0d, Whole: EngineTicks.PerSecond),
            volume: volume
        );

        Assert.Equal(expected: 4d, actual: second.Advection, precision: 5);
        Assert.Equal(expected: (4d * SdfVolume.CloudDriftZ), actual: second.AdvectionZ, precision: 5);
        Assert.Equal(expected: 1.5d, actual: second.Pulse, precision: 5);

        // Where the advection wraps its lattice period the value jumps by a whole period, which the noise repeats at.
        var wrapTick = ((ulong)(((SdfVolume.NoisePeriodCells / 2d) / 4d) * EngineTicks.PerSecond));
        var before = SdfVolumeMotion.At(clock: new PresentedTick(Fraction: 0d, Whole: wrapTick), volume: volume);
        var after = SdfVolumeMotion.At(clock: new PresentedTick(Fraction: 0d, Whole: (wrapTick + 1UL)), volume: volume);
        var step = Math.IEEERemainder(
            x: (after.Advection - ((double)before.Advection)),
            y: SdfVolume.NoisePeriodCells
        );

        Assert.InRange(actual: Math.Abs(value: (step - (4d / EngineTicks.PerSecond))), high: 1e-3d, low: 0d);
    }
}
