using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky arrives as host-resolved values while bounded media animate on the presented tick: the environment
/// preserves the twinkle phase and cloud offsets without a second integration, and the
/// volume table carries each medium's integrated advection and pulse gain, so the values move by one tick's worth of
/// their rate between consecutive ticks wherever the tick or the reduction wraps.</summary>
public sealed class SdfSkyClockLawTests {
    private const ulong Wrap = (1UL << 32);

    private static SdfFrame Frame(SdfLighting environment, ulong tick) {
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
    private static SdfSkyFrameData Bake(SdfLighting environment, ulong tick) {
        var frame = Frame(environment: environment, tick: tick);
        var upload = new SdfLightingUpload();

        upload.Pack(environment: frame.Environment);
        return upload.SkyFrame;
    }

    [Fact]
    public void The_frame_bake_preserves_resolved_sky_offsets_and_phase_at_every_tick() {
        var environment = SdfLighting.Default();

        environment.TwinklePhase = 0.75f;
        environment.CloudOffset = new Vector2(x: 12.5f, y: -7.25f);
        environment.CloudShearOffset = new Vector2(x: -0.5f, y: 0.25f);
        environment.CloudSpinAngle = 1.25f;
        foreach (var tick in new ulong[] { 0UL, (Wrap - 1UL), Wrap, (1UL << 40), ulong.MaxValue }) {
            var rows = Bake(environment: environment, tick: tick);

            Assert.Equal(actual: rows.TwinklePhase, expected: 0.75f);
            Assert.Equal(actual: rows.CloudOffset.X, expected: 12.5f);
            Assert.Equal(actual: rows.CloudOffset.Y, expected: -7.25f);
            Assert.Equal(actual: rows.CloudShearOffset.X, expected: -0.5f);
            Assert.Equal(actual: rows.CloudShearOffset.Y, expected: 0.25f);
            Assert.Equal(actual: rows.CloudSpinAngle, expected: 1.25f);
        }
    }
    [Fact]
    public void A_mediums_advection_and_pulse_are_its_rates_integrated_to_the_tick() {
        var volume = new SdfVolume(
            Kind: SdfVolumeKind.Cloud,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            HalfExtent: Vector3.One,
            DynamicSlot: -1,
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
