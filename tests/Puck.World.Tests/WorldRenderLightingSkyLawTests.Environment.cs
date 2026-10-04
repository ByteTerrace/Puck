using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

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
        Assert.True(condition: TryValidateLocal(definition: AuthoredGameFixtures.Load(relativePath: "tests/Puck.Parity/parity.world.json")));
        Assert.True(condition: TryValidateLocal(definition: AuthoredGameFixtures.Load(relativePath: "tests/Puck.Counters/sky-cycle.world.json")));
    }
    [Fact]
    public void KeyedSkyColourRefreshesOnlyAfterCrossingOneDisplayCode() {
        WorldRenderSkyLayer.Gradient Gradient(string color) => new(Stops: [
            new WorldRenderSkyStop(Color: new BindableColor(Raw: color), Elevation: -1f),
            new WorldRenderSkyStop(Color: new BindableColor(Raw: color), Elevation: 1f),
        ], Name: "light");
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "cycle", PeriodSeconds: 1d)]),
            RenderRaw = BaseDefaults() with {
                Sky = new WorldRenderSky(Clock: "cycle", Layers: [Gradient(color: "#404040")], Keys: [
                    new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["light"] = Gradient(color: "#404040") with { Name = null } }),
                    new WorldRenderSkyKey(At: .5d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["light"] = Gradient(color: "#414141") with { Name = null } }),
                ]),
            },
        };

        Assert.True(condition: TryValidateLocal(definition: definition));
        var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var refresh = new SdfSkyEnvironmentRefresh();

        bool Owes(ulong tick) {
            var mirror = ClientFixtures.StateMirror(definition: definition, engineTick: tick);
            var resolved = resolver.Resolve(definition: definition, mirror: mirror, revision: 0);
            var layers = new SdfSkyLayer[SdfSky.MaxLayers];

            resolved.Sky.Pack(lights: resolved.Lights, farDistance: WorldRenderFarDistance.Resolve(defaults: definition.Render), details: new SdfSkyDetails(), block: out var block, layers: layers);
            return refresh.Owes(block: block, layers: layers);
        }
        Assert.True(condition: Owes(tick: 0UL));
        refresh.Rendered();
        Assert.False(condition: Owes(tick: 1260UL));
        Assert.True(condition: refresh.Projected);
        Assert.True(condition: refresh.Skipped);
        Assert.True(condition: Owes(tick: 20160UL));
        Assert.False(condition: refresh.Skipped);
    }
    [Fact]
    public void RepeatedPanelsResolveTheirFieldsAndLayerControls() {
        var resolved = Resolve(BaseDefaults() with {
            Sky = new WorldRenderSky(Layers: [
                new WorldRenderSkyLayer.Panel(Direction: Vector3.UnitZ, Size: new Vector2(x: .2f, y: .4f), Color: new BindableColor(Raw: "#4080FF"), Intensity: 2f, Blur: .1f, Name: "key"),
                new WorldRenderSkyLayer.Panel(Name: "fill") { Opacity = .5f, Tier = WorldSkyTier.Low, Visibility = WorldSkyVisibility.Camera },
            ]),
        });

        Assert.Equal(3, resolved.Sky.LayerCount);
        Assert.Equal(SdfSkyLayerKind.Panel, resolved.Sky.LayerAt(index: 1).Kind);
        Assert.Equal(SdfSkyVisibility.Lighting, resolved.Sky.LayerAt(index: 1).Visibility);
        Assert.Equal(SdfSkyBlend.Add, resolved.Sky.LayerAt(index: 1).Blend);
        var panel = resolved.Sky.Parameters<SdfSkyPanel>(index: 1);

        Assert.Equal(Vector3.UnitZ, panel.Direction);
        Assert.Equal(new Vector2(x: .2f, y: .4f), panel.Size);
        Assert.Equal(new Vector3(x: (64f / 255f), y: (128f / 255f), z: 1f), panel.Color);
        Assert.Equal(actual: panel.Intensity, expected: 2f);
        Assert.Equal(actual: panel.Blur, expected: .1f);
        Assert.Equal(SdfSkyVisibility.Camera, resolved.Sky.LayerAt(index: 2).Visibility);
        Assert.Equal(.5f, resolved.Sky.LayerAt(index: 2).Opacity);
    }
    [Fact]
    public void PanelGeometryAndEnvironmentGainsRefuseInvalidValues() {
        bool Valid(WorldRenderSkyLayer.Panel panel) => TryValidateLocal(definition: Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [panel]) },
        });
        Assert.True(condition: Valid(panel: new WorldRenderSkyLayer.Panel()));
        Assert.False(condition: Valid(panel: new WorldRenderSkyLayer.Panel(Direction: Vector3.Zero)));
        Assert.False(condition: Valid(panel: new WorldRenderSkyLayer.Panel(Size: new Vector2(x: 0f, y: 1f))));
        Assert.False(condition: Valid(panel: new WorldRenderSkyLayer.Panel(Intensity: -1f)));
        Assert.False(condition: Valid(panel: new WorldRenderSkyLayer.Panel(Blur: float.NaN)));
        foreach (var gain in new[] { -1f, float.NaN, float.PositiveInfinity }) {
            Assert.False(condition: TryValidateLocal(definition: Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Ambient: gain) } }));
            Assert.False(condition: TryValidateLocal(definition: Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Reflection: gain) } }));
        }
        Assert.True(condition: TryValidateLocal(definition: Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Ambient: 0f, Reflection: 0f) } }));
    }
}
