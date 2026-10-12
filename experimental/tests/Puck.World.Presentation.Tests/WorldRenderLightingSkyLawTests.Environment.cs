using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldRenderLightingSkyLawTests {
    [Fact]
    public void PanelSectionKeysResolveThroughTheSkyClock() {
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "cycle", PeriodSeconds: 1d)]),
            RenderRaw = BaseDefaults() with {
                Sky = new WorldRenderSky(Clock: "cycle", Layers: [new WorldRenderSkyLayer.Panel(Name: "key")], Keys: [
                    new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["key"] = new WorldRenderSkyLayer.Panel(Intensity: 1f, Blur: 0f, Color: new BindableColor(Raw: "#FF0000")) }),
                    new WorldRenderSkyKey(At: .5d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["key"] = new WorldRenderSkyLayer.Panel(Intensity: 3f, Blur: .2f, Color: new BindableColor(Raw: "#0000FF")) }),
                ]),
            },
        };

        Assert.True(condition: TryValidateLocal(definition: definition));
        var mirror = ClientFixtures.StateMirror(definition: definition, engineTick: 12600UL);
        var resolved = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: definition, mirror: mirror, revision: 0);
        var panel = resolved.Sky.First<SdfSkyPanel>();

        Assert.Equal(actual: panel.Intensity, expected: 2f);
        Assert.Equal(actual: panel.Blur, expected: .1f);
        var halfLinear = ((1.055f * MathF.Pow(x: .5f, y: (1f / 2.4f))) - .055f);

        Assert.InRange(Vector3.Distance(value1: new Vector3(x: halfLinear, y: 0f, z: halfLinear), value2: panel.Color), 0f, 1e-6f);
        Assert.True(condition: TryValidateLocal(definition: AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Canaries/ambient-from-sky/fixture.world.json")));
        Assert.True(condition: TryValidateLocal(definition: AuthoredGameFixtures.Load(relativePath: "tests/Puck.Parity/parity.puck")));
        Assert.True(condition: TryValidateLocal(definition: AuthoredGameFixtures.Load(relativePath: "tests/Puck.Counters/sky-cycle.puck")));
    }
}
