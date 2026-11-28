using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the lit path's photometric conversions (<see cref="SdfPhotometry"/>), the camera exposure's
/// adaptation (<see cref="SdfExposure"/>) and the lights each view shades (<see cref="SdfLightSelection"/>). A light record
/// carries its illuminance or intensity over π; a point light's windowed falloff ends exactly at the range derived from
/// its intensity and the world's brightest exposure; a view shades only the lights that can reach what it shows.
/// </summary>
public sealed class SdfPhotometryLawTests {
    [Fact]
    public void ARecordCarriesItsQuantityOverPi_AndPowerSpreadsOverItsSolidAngle() {
        Assert.Equal(expected: (100000f / MathF.PI), actual: SdfPhotometry.DirectionalWeight(lux: 100000f));
        Assert.Equal(expected: (80f / MathF.PI), actual: SdfPhotometry.PositionalWeight(candela: 80f));
        Assert.Equal(expected: (800f / (4f * MathF.PI)), actual: SdfPhotometry.PointIntensity(lumens: 800f));
        // A hemisphere's cone is half the sphere, so a spot spreading its power over it is twice as intense.
        Assert.Equal(expected: (2f * SdfPhotometry.PointIntensity(lumens: 800f)), actual: SdfPhotometry.SpotIntensity(angle: (MathF.PI / 2f), lumens: 800f), tolerance: 1e-3f);
    }
    [Fact]
    public void ASpotsConeIsWholeInsideItsInnerAngleAndNoneOutsideItsOuter() {
        var (axis, offset) = SdfPhotometry.Cone(aim: new Vector3(x: 0f, y: -2f, z: 0f), angle: 0.6f, innerAngle: 0.3f);

        static float Attenuation(Vector3 axis, float offset, float angle) =>
            Math.Clamp(value: (Vector3.Dot(vector1: new Vector3(x: MathF.Sin(x: angle), y: -MathF.Cos(x: angle), z: 0f), vector2: axis) + offset), min: 0f, max: 1f);

        Assert.Equal(expected: 1f, actual: Attenuation(angle: 0f, axis: axis, offset: offset));
        Assert.Equal(expected: 1f, actual: Attenuation(angle: 0.29f, axis: axis, offset: offset));
        Assert.InRange(actual: Attenuation(angle: 0.45f, axis: axis, offset: offset), low: 0.01f, high: 0.99f);
        Assert.Equal(expected: 0f, actual: Attenuation(angle: 0.61f, axis: axis, offset: offset));
        // A zero aim shines straight down.
        Assert.Equal(expected: -Vector3.UnitY, actual: Vector3.Normalize(value: SdfPhotometry.Cone(aim: Vector3.Zero, angle: 0.6f, innerAngle: 0.3f).Axis));
    }
    [Fact]
    public void APointLightsFalloffIsTheInverseSquareWindowedToNothingAtItsRange() {
        var weight = SdfPhotometry.PositionalWeight(candela: SdfPhotometry.PointIntensity(lumens: 1500f));
        var gain = SdfPhotometry.ExposureOf(ev100: 7f);
        var range = SdfPhotometry.RangeOf(exposure: gain, weight: weight);

        // At the range a white surface facing the light would show the threshold share of display white.
        Assert.Equal(expected: SdfPhotometry.RangeThreshold, actual: ((weight * gain) / (range * range)), tolerance: 1e-6f);
        Assert.Equal(expected: 0f, actual: SdfPhotometry.Falloff(distance: range, range: range));
        Assert.Equal(expected: 0f, actual: SdfPhotometry.Falloff(distance: (2f * range), range: range));
        Assert.Equal(expected: (1f / 4f), actual: SdfPhotometry.Falloff(distance: 2f, range: 1e9f), tolerance: 1e-6f);
        Assert.InRange(actual: (SdfPhotometry.Falloff(distance: (0.9f * range), range: range) * (0.81f * range * range)), low: 0.01f, high: 0.99f);
        // A bulb's radius bounds the inverse square, and no range has no light.
        Assert.Equal(expected: SdfPhotometry.Falloff(distance: SdfPhotometry.MinimumDistance, range: 10f), actual: SdfPhotometry.Falloff(distance: 0f, range: 10f));
        Assert.Equal(expected: 0f, actual: SdfPhotometry.Falloff(distance: 1f, range: 0f));
        Assert.Equal(expected: 0f, actual: SdfPhotometry.RangeOf(exposure: gain, weight: 0f));
        Assert.Equal(expected: 0f, actual: SdfPhotometry.RangeOf(exposure: 0f, weight: weight));
    }
    [InlineData(500f)]
    [InlineData(1900f)]
    [InlineData(2700f)]
    [InlineData(3500f)]
    [InlineData(6500f)]
    [InlineData(12000f)]
    [Theory]
    public void ABlackBodysColourHasUnitLuminanceAndCoolsFromRedToBlue(float kelvin) {
        var color = SdfPhotometry.Blackbody(kelvin: kelvin);

        Assert.Equal(expected: 1f, actual: SdfPhotometry.LuminanceOf(color: color), tolerance: 1e-5f);
        Assert.True(condition: ((color.X >= 0f) && (color.Y >= 0f) && (color.Z >= 0f)));
        if (kelvin <= 3500f) {
            Assert.True(condition: (color.X > color.Z));
        }
        if (kelvin >= 12000f) {
            Assert.True(condition: (color.Z > color.X));
        }
    }
    [Fact]
    public void AColourOfNoLuminanceStaysAsItIs() {
        Assert.Equal(expected: Vector3.Zero, actual: SdfPhotometry.UnitLuminance(color: Vector3.Zero));
        Assert.Equal(expected: 1f, actual: SdfPhotometry.LuminanceOf(color: SdfPhotometry.UnitLuminance(color: new Vector3(x: 0.2f, y: 0.1f, z: 0.05f))), tolerance: 1e-6f);
    }
    [Fact]
    public void TheMeterExposesAnAverageSceneAtItsCalibration() {
        // A scene averaging 12.5 / 100 nits meters at EV100 0, which maps 1.2 nits to display white.
        Assert.Equal(expected: 0f, actual: SdfPhotometry.MeteredEv100(luminance: (SdfPhotometry.MeterCalibration / 100f)), tolerance: 1e-5f);
        Assert.Equal(expected: (1f / SdfPhotometry.SaturationHeadroom), actual: SdfPhotometry.ExposureOf(ev100: 0f));
        Assert.Equal(expected: (SdfPhotometry.MeteredEv100(luminance: 1e-6f)), actual: SdfPhotometry.MeteredEv100(luminance: 0f));
    }
    [Fact]
    public void AnAutomaticExposureAimsWithinItsBoundsAndEasesTowardThere() {
        var exposure = new SdfExposure(Adaptation: 1f, Compensation: 1f, Ev100: 10f, Local: 0f, Maximum: 15f, Minimum: 5f, Mode: SdfExposureMode.Auto, Band: 0f, Low: 0f, High: 1f, Center: 0f);

        Assert.Equal(expected: 15f, actual: exposure.Target(luminance: 1e9f));
        Assert.Equal(expected: 5f, actual: exposure.Target(luminance: 1e-6f));
        Assert.Equal(expected: (SdfPhotometry.MeteredEv100(luminance: 100f) - 1f), actual: exposure.Target(luminance: 100f));
        // A second of a one-second time constant closes all but 1/e of the way; a near step lands on the target.
        Assert.Equal(expected: (10f + (4f * (1f - MathF.Exp(x: -1f)))), actual: exposure.Adapt(current: 10f, seconds: 1f, target: 14f), tolerance: 1e-5f);
        Assert.Equal(expected: 14f, actual: exposure.Adapt(current: 13.999f, seconds: 0.001f, target: 14f));
        Assert.Equal(expected: 14f, actual: (exposure with { Adaptation = 0f }).Adapt(current: 10f, seconds: 0f, target: 14f));
        Assert.Equal(expected: 14f, actual: exposure.Adapt(current: float.NaN, seconds: 1f, target: 14f));
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: 5f), actual: exposure.BrightestGain);
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: SdfExposure.DefaultEv100), actual: SdfExposure.Default.BrightestGain);
    }
    [Fact]
    public void AViewShadesOnlyTheLightsThatCanReachWhatItShows() {
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: Vector3.Zero, target: -Vector3.UnitZ, viewportHeight: 100f, viewportWidth: 100f);
        var lights = new SdfLights();

        Point(index: 0, position: new Vector3(x: 0f, y: 0f, z: -10f), range: 2f);
        Point(index: 1, position: new Vector3(x: 0f, y: 0f, z: 10f), range: 2f);
        Point(index: 2, position: new Vector3(x: 0f, y: 0f, z: 3f), range: 5f);
        lights.Set(index: 3, light: new SdfLight(Color: Vector3.One, Direction: Vector3.UnitY, Kind: SdfLightKind.Directional, Param: 0f, Shadows: false, Weight: 1f));
        lights.Set(index: 4, light: new SdfLight(Color: Vector3.One, Direction: Vector3.UnitY, Kind: SdfLightKind.Directional, Param: 0f, Shadows: false, Weight: 0f));
        lights.Set(index: 5, light: new SdfLight(Color: Vector3.One, Direction: new Vector3(x: 0f, y: 0f, z: 10f), DynamicSlot: 0, Kind: SdfLightKind.Point, Param: 1f, Shadows: false, Weight: 1f));
        lights.Set(index: 6, light: new SdfLight(Color: Vector3.Zero, Direction: new Vector3(x: 0f, y: 0f, z: 10f), Kind: SdfLightKind.Occluder, Param: 1f, Shadows: false, Weight: 0f));
        lights.Set(index: 7, light: new SdfLight(Color: Vector3.Zero, Direction: new Vector3(x: 0f, y: 0f, z: 2f), Kind: SdfLightKind.Occluder, Param: 1f, Shadows: false, Weight: 0f));
        Point(index: 8, position: new Vector3(x: 0f, y: 0f, z: -10f), range: 0f);
        lights.Count = 9;

        // Ahead, wrapping the eye, a directional and a light riding a transform are shaded; one behind past its range, a
        // dark directional, an occluder row's place, which lights nothing, and a light of no range are not.
        Assert.Equal(expected: ((UInt128)0b0_0010_1101), actual: SdfLightSelection.Select(camera: in camera, exposure: 1f, far: 100f, lights: lights));
        // A far plane before a light's sphere leaves it out.
        Assert.False(condition: SdfLightSelection.Reaches(camera: in camera, exposure: 1f, far: 5f, light: lights[0]));
        // A view exposed so dimly that the light wrapping the eye gives a white surface past 1.6 m less than the cutoff
        // leaves it out: its reach there no longer holds the eye, and it lies behind the view.
        Assert.Equal(expected: ((UInt128)0b0_0010_1001), actual: SdfLightSelection.Select(camera: in camera, exposure: 0.01f, far: 100f, lights: lights));
        Assert.Equal(expected: (SdfPhotometry.RangeOf(exposure: 0.01f, weight: 1f) * (1f + SdfLightSelection.CutoffMargin)), actual: SdfLightSelection.ReachAt(exposure: 0.01f, light: lights[2]));

        void Point(int index, Vector3 position, float range) =>
            lights.Set(index: index, light: new SdfLight(Color: Vector3.One, Direction: position, Kind: SdfLightKind.Point, Param: range, Shadows: false, Weight: 1f));
    }
    // An occluder is shaded by a view whose frustum its sphere of reach meets, and a creation's box fits the solid its
    // proportions suggest: a sphere near a cube, a capsule along a long axis whose short sides are alike, else the box.
    [Fact]
    public void AViewShadesTheOccludersWhoseReachItSeesAndABoxFitsItsSolid() {
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: Vector3.Zero, target: -Vector3.UnitZ, viewportHeight: 100f, viewportWidth: 100f);
        var occluders = new SdfOccluders();

        Assert.True(condition: occluders.TryAdd(occluder: new SdfOccluder { Center = new Vector3(x: 0f, y: 0f, z: -5f), Radius = 1f, Reach = 2f, Strength = 1f }));
        Assert.True(condition: occluders.TryAdd(occluder: new SdfOccluder { Center = new Vector3(x: 0f, y: 0f, z: 10f), Radius = 1f, Reach = 2f, Strength = 1f }));
        Assert.True(condition: occluders.TryAdd(occluder: new SdfOccluder { Center = new Vector3(x: 0f, y: 0f, z: 1.5f), Radius = 1f, Reach = 2f, Shadow = 1f }));
        // No strength, or no reach, adds nothing and is refused.
        Assert.False(condition: occluders.TryAdd(occluder: new SdfOccluder { Center = Vector3.Zero, Radius = 1f, Reach = 2f }));
        Assert.False(condition: occluders.TryAdd(occluder: new SdfOccluder { Center = Vector3.Zero, Radius = 1f, Strength = 1f }));
        Assert.Equal(expected: ((UInt128)0b101), actual: SdfLightSelection.SelectOccluders(camera: in camera, exposure: 0f, far: 100f, lightMask: UInt128.Zero, lights: new SdfLights(), occluders: occluders));

        // An occluder behind the view whose shadow toward a lamp behind it falls before the view is kept for that shadow,
        // and only while the view shades the lamp.
        var lamp = new SdfLights();
        var behind = new SdfOccluders();

        lamp.Set(index: 0, light: new SdfLight(Color: Vector3.One, Direction: new Vector3(x: 0f, y: 0f, z: 12f), Kind: SdfLightKind.Point, Param: 30f, Shadows: false, Weight: 1f));
        Assert.True(condition: behind.TryAdd(occluder: new SdfOccluder { Center = new Vector3(x: 0f, y: 0f, z: 10f), Radius = 1f, Reach = 2f, Shadow = 1f }));
        Assert.Equal(expected: UInt128.Zero, actual: SdfLightSelection.SelectOccluders(camera: in camera, exposure: 0f, far: 100f, lightMask: UInt128.Zero, lights: lamp, occluders: behind));
        Assert.Equal(expected: UInt128.One, actual: SdfLightSelection.SelectOccluders(camera: in camera, exposure: 0f, far: 100f, lightMask: UInt128.One, lights: lamp, occluders: behind));

        Assert.Equal(expected: SdfOccluderShape.Sphere, actual: SdfOccluder.Fit(fit: SdfOccluderFit.Auto, high: new Vector3(x: 0.5f, y: 1.1f, z: 0.5f), low: new Vector3(x: -0.5f, y: 0f, z: -0.5f))!.Value.Shape);
        var capsule = SdfOccluder.Fit(fit: SdfOccluderFit.Auto, high: new Vector3(x: 0.4f, y: 2f, z: 0.25f), low: new Vector3(x: -0.4f, y: 0f, z: -0.25f))!.Value;

        Assert.Equal(expected: SdfOccluderShape.Capsule, actual: capsule.Shape);
        Assert.Equal(expected: 0f, actual: capsule.AxisX.X);
        Assert.Equal(expected: 0f, actual: capsule.AxisX.Z);
        Assert.Equal(expected: (1f - capsule.Radius), actual: capsule.AxisX.Y, precision: 5);
        Assert.Equal(expected: SdfOccluderShape.Box, actual: SdfOccluder.Fit(fit: SdfOccluderFit.Auto, high: new Vector3(x: 0.32f, y: 0.14f, z: 0.24f), low: new Vector3(x: -0.32f, y: 0f, z: -0.24f))!.Value.Shape);
        Assert.Null(SdfOccluder.Fit(fit: SdfOccluderFit.Auto, high: Vector3.One, low: new Vector3(x: 0f, y: 1f, z: 0f)));
    }
}
