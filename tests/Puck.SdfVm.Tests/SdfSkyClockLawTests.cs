using System.Numerics;
using Puck.Hosting;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky and the bounded media animate on the frame's presented tick, reduced on the host: the sky arrives
/// with the twinkle's phase and the cloud layer's drift, shear and spin already integrated by the World's environment
/// resolver, which the layer table packs as given (<see cref="SdfSky.Pack"/>), and the volume table carries each medium's integrated
/// advection and pulse gain, so its values move by one tick's worth of their rate between consecutive ticks wherever
/// the tick or the reduction wraps.</summary>
public sealed class SdfSkyClockLawTests {
    [Fact]
    public void The_layers_carry_the_hosts_twinkle_phase_and_cloud_offsets() {
        var sky = new SdfSky();

        _ = sky.Add(blend: SdfSkyBlend.Add, label: "stars", parameters: new SdfSkyStars { Brightness = 1f, TwinklePhase = 0.25f });
        _ = sky.Add(label: "clouds", parameters: new SdfSkyClouds { Coverage = 0.5f, DriftOffset = new Vector2(x: 0.5f, y: -0.25f), ShearOffset = new Vector2(x: 0.125f, y: 0.0625f), SpinAngle = 1.5f });

        // The host integrates every rate to the presented tick (the World's environment resolver), so the layer table
        // packs the phase and offsets the sky holds, which no tick reaches.
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(
            block: out _,
            details: new SdfSkyDetails(),
            farDistance: 40f,
            layers: layers,
            lights: SdfLights.Default(),
            softboxes: new SdfSoftbox[SdfSky.MaxSoftboxes]
        );

        var stars = SdfSky.PayloadOf<SdfSkyStars>(layer: ref layers[1]);
        var clouds = SdfSky.PayloadOf<SdfSkyClouds>(layer: ref layers[2]);

        Assert.Equal(actual: stars.TwinklePhase, expected: 0.25f);
        Assert.Equal(expected: new Vector2(x: 0.5f, y: -0.25f), actual: clouds.DriftOffset);
        Assert.Equal(expected: new Vector2(x: 0.125f, y: 0.0625f), actual: clouds.ShearOffset);
        Assert.Equal(actual: clouds.SpinAngle, expected: 1.5f);
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
