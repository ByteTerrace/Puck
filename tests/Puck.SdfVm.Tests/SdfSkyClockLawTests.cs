using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky and the bounded media animate on the frame's presented tick, reduced on the host: the environment
/// arrives with the twinkle's phase and the cloud layer's drift, shear and spin already integrated by the World's
/// environment resolver, which the block bakes as given, and the volume table carries each medium's integrated
/// advection and pulse gain, so its values move by one tick's worth of their rate between consecutive ticks wherever
/// the tick or the reduction wraps.</summary>
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
    public void The_block_carries_the_hosts_twinkle_phase_and_cloud_offsets_at_every_tick() {
        var environment = SdfEnvironment.Default();

        environment.TwinklePhase = 0.25f;
        environment.CloudDriftOffset = new Vector2(x: 0.5f, y: -0.25f);
        environment.CloudShearOffset = new Vector2(x: 0.125f, y: 0.0625f);
        environment.CloudSpinAngle = 1.5f;

        var twinkle = ((SdfEnvironment.TwinkleRow * 4) + 2);
        var drift = ((SdfEnvironment.CloudsRow + 2) * 4);
        var spin = ((SdfEnvironment.CloudsRow + 3) * 4);

        // The host integrates every rate to the presented tick (the World's environment resolver), so the block bakes
        // the phase and offsets the environment holds, whatever tick the frame presents.
        foreach (var tick in new[] { 0UL, (Wrap - 1UL), Wrap, (Wrap + 1234UL) }) {
            var rows = Bake(environment: environment, tick: tick);

            Assert.Equal(expected: 0.25f, actual: rows[twinkle]);
            Assert.Equal(expected: 0.5f, actual: rows[drift]);
            Assert.Equal(expected: -0.25f, actual: rows[(drift + 1)]);
            Assert.Equal(expected: 0.125f, actual: rows[(drift + 2)]);
            Assert.Equal(expected: 0.0625f, actual: rows[(drift + 3)]);
            Assert.Equal(expected: 1.5f, actual: rows[spin]);
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
