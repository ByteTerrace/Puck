using System.Numerics;
using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class ConsoleDisclosureLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void LightingReportsTheCadenceOfValueAndSectionKeys(bool section) {
        var color = new BindableColor(keys: new WorldKeyTrack<BindableColor>(clock: "day", keys: [
            new WorldKey<BindableColor>(At: 0d, Value: new BindableColor(Raw: "#ffffff"), Ease: WorldEase.Linear)]));
        var scalar = new BindableScalar(keys: new WorldKeyTrack<float>(clock: "day", keys: [new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0.1f)]));
        var direction = new BindableDirection(keys: new WorldKeyTrack<Vector3>(clock: "day", keys: [new WorldKey<Vector3>(At: 0d, Value: Vector3.UnitY, Ease: WorldEase.Linear)]));
        var lights = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Name: "sun", Shadow: WorldShadowMode.Always,
            Color: color, Direction: direction)], Curvature: new WorldRenderCurvature(Cavity: scalar));
        var sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Clouds(Color: color), new WorldRenderSkyLayer.Fog(Density: scalar, Name: "haze")]);

        if (section) {
            lights = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Name: "sun", Shadow: WorldShadowMode.Always)],
                Curvature: new WorldRenderCurvature(), Clock: "day", Keys: [new WorldRenderLightingKey(At: 0d,
                    Lights: new Dictionary<string, WorldRenderLight> { ["sun"] = new WorldRenderLight.Directional(Color: new BindableColor(Raw: "#ffffff"), Direction: Vector3.UnitY) },
                    Curvature: new WorldRenderCurvature(Cavity: 0.1f))]);
            sky = sky with {
                Layers = [sky.Layers![0], new WorldRenderSkyLayer.Fog(Name: "haze")],
                Clock = "day",
                Keys = [new WorldRenderSkyKey(At: 0d,
                Layers: new Dictionary<string, WorldRenderSkyLayer> { ["haze"] = new WorldRenderSkyLayer.Fog(Density: 0.1f) })],
            };
        }
        using var host = new DisclosureHost(Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1d)]),
            RenderRaw = new WorldRenderDefaults(Lighting: lights, Sky: sky),
        });
        var result = host.Console(line: "world.lighting");

        Assert.False(condition: result.IsError);
        var output = result.Output;

        Assert.Contains(Field(change: "shadow-direction", name: "direction"), output);
        Assert.Contains(Field(change: "lighting-visible", name: "color"), output);
        Assert.Contains(Field(change: "geometry-or-camera", name: "cavity"), output);
        Assert.Contains(Field(change: "visual-only", name: "color"), output);
        Assert.Contains(Field(change: "lighting-visible", name: "density"), output);

        static string Field(string name, string change) => $"{name}={CommandEcho.Quote(value: $"keys(clock: day, 1 keys) class={change}")}";
    }
}
