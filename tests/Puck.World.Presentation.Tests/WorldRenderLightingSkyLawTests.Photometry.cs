using System.Numerics;
using Puck.SignedDistance;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldRenderLightingSkyLawTests {
    [Fact]
    public void APhotometricLightResolvesToItsQuantityOverPi_ItsColourToUnitLuminance_AndItsRangeToTheWorldsDarkestExposure() {
        var exposure = new WorldRenderExposure(Mode: SdfExposureMode.Auto, Minimum: 7f, Maximum: 16f);
        var resolved = Resolve(defaults: BaseDefaults() with {
            Exposure = exposure,
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Illuminance: 100000f, Temperature: 5500f),
                new WorldRenderLight.Point(Position: new Vector3(x: 1f, y: 2f, z: 3f), Power: 800f, Temperature: 2700f),
                new WorldRenderLight.Point(Intensity: 50f, Color: new BindableColor(Raw: "#FF8000")),
            ]),
        });
        var sun = resolved.Lights[0];
        var bulb = resolved.Lights[1];
        var lamp = resolved.Lights[2];
        var gain = WorldRenderExposure.Resolve(exposure: exposure).BrightestGain;

        Assert.Equal(expected: SdfPhotometry.DirectionalWeight(lux: 100000f), actual: sun.Weight);
        Assert.Equal(expected: SdfPhotometry.Blackbody(kelvin: 5500f), actual: sun.Color);
        Assert.Equal(expected: SdfPhotometry.PositionalWeight(candela: SdfPhotometry.PointIntensity(lumens: 800f)), actual: bulb.Weight);
        Assert.Equal(expected: SdfPhotometry.RangeOf(exposure: gain, weight: bulb.Weight), actual: bulb.Param);
        Assert.Equal(expected: (Vector3.Zero, 1f), actual: (bulb.SpotAxis, bulb.SpotOffset));
        Assert.Equal(expected: SdfPhotometry.Blackbody(kelvin: 2700f), actual: bulb.Color);
        Assert.Equal(expected: SdfPhotometry.PositionalWeight(candela: 50f), actual: lamp.Weight);
        Assert.Equal(expected: 1f, actual: SdfPhotometry.LuminanceOf(color: lamp.Color), tolerance: 1e-6f);
        Assert.Equal(expected: gain, actual: resolved.Exposure.BrightestGain);
    }
    [Fact]
    public void ASpotResolvesToAPointWithItsConeAboutItsAim_ItsAimTurningWithItsAnchor() {
        var resolved = Resolve(
            defaults: BaseDefaults() with {
                Lighting = new WorldRenderLighting(Lights: [
                    new WorldRenderLight.Spot(Position: Vector3.UnitY, Direction: new Vector3(x: 1f, y: 0f, z: 0f), Angle: 0.6f, InnerAngle: 0.3f, Power: 1500f, Anchor: new WorldAnchor.Placement(PlacementId: "arm", ShapeId: null)),
                    new WorldRenderLight.Spot(Intensity: 200f),
                ]),
            },
            resolveLightAnchor: static anchor => new SdfAnchor(new Vector3(x: 5f, y: 0f, z: 0f), Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (MathF.PI / 2f)))
        );
        var aimed = resolved.Lights[0];
        var plain = resolved.Lights[1];
        var (axis, offset) = SdfPhotometry.Cone(aim: Vector3.UnitX, angle: 0.6f, innerAngle: 0.3f);

        Assert.Equal(expected: SdfLightKind.Point, actual: aimed.Kind);
        Assert.Equal(expected: SdfPhotometry.PositionalWeight(candela: SdfPhotometry.SpotIntensity(angle: 0.6f, lumens: 1500f)), actual: aimed.Weight);
        Assert.Equal(expected: offset, actual: aimed.SpotOffset);
        // The anchor's quarter turn about up carries +X to -Z, the aim and the position alike.
        Assert.True(condition: (Vector3.Distance(value1: Vector3.Transform(value: axis, rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (MathF.PI / 2f))), value2: aimed.SpotAxis) < 1e-4f));
        Assert.True(condition: (Vector3.Distance(value1: new Vector3(x: 5f, y: 1f, z: 0f), value2: aimed.Direction) < 1e-5f));
        // An unaimed spot shines straight down in its default cone.
        var (down, downOffset) = SdfPhotometry.Cone(aim: -Vector3.UnitY, angle: WorldRenderLight.Spot.DefaultAngle, innerAngle: (WorldRenderLight.Spot.DefaultAngle * WorldRenderLight.Spot.DefaultInnerShare));

        Assert.Equal(expected: (down, downOffset), actual: (plain.SpotAxis, plain.SpotOffset));
        Assert.Equal(expected: SdfPhotometry.PositionalWeight(candela: 200f), actual: plain.Weight);
    }
    [Fact]
    public void TheLightingReadBackEchoesASpotAndTheExposure() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with {
                Exposure = new WorldRenderExposure(Mode: SdfExposureMode.Auto, Minimum: 7f, Maximum: 16f, Compensation: 2.5f),
                Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Spot(Name: "down", Angle: 0.6f, Power: 1500f, Temperature: 4000f)]),
            },
        };
        var text = WorldLightingText.Describe(definition: definition);

        Assert.Contains(expectedSubstring: "name=down type=spot", actualString: text);
        Assert.Contains(expectedSubstring: "angle=0.6", actualString: text);
        Assert.Contains(expectedSubstring: "power=1500", actualString: text);
        Assert.Contains(expectedSubstring: "temperature=4000", actualString: text);
        Assert.Contains(expectedSubstring: "exposure mode=auto", actualString: text);
        Assert.Contains(expectedSubstring: "compensation=2.5", actualString: text);
    }
}
