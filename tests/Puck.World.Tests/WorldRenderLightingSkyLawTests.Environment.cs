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
        Assert.True(TryValidateLocal(definition));
        var mirror = ClientFixtures.StateMirror(definition: definition, engineTick: 12600UL);
        var resolved = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: definition, mirror: mirror, revision: 0);
        var panel = resolved.Sky.First<SdfSkyPanel>();
        Assert.Equal(2f, panel.Intensity);
        Assert.Equal(.1f, panel.Blur);
        var halfLinear = 1.055f * MathF.Pow(.5f, 1f / 2.4f) - .055f;
        Assert.InRange(Vector3.Distance(new Vector3(halfLinear, 0f, halfLinear), panel.Color), 0f, 1e-6f);
        Assert.True(TryValidateLocal(AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Canaries/ambient-from-sky/fixture.world.json")));
        Assert.True(TryValidateLocal(AuthoredGameFixtures.Load(relativePath: "tests/Puck.Parity/parity.world.json")));
        Assert.True(TryValidateLocal(AuthoredGameFixtures.Load(relativePath: "tests/Puck.Counters/sky-cycle.world.json")));
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
                Sky = new WorldRenderSky(Clock: "cycle", Layers: [Gradient("#404040")], Keys: [
                    new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["light"] = Gradient("#404040") with { Name = null } }),
                    new WorldRenderSkyKey(At: .5d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["light"] = Gradient("#414141") with { Name = null } }),
                ]),
            },
        };
        Assert.True(TryValidateLocal(definition));
        var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var refresh = new SdfSkyEnvironmentRefresh();
        bool Owes(ulong tick) {
            var mirror = ClientFixtures.StateMirror(definition: definition, engineTick: tick);
            var resolved = resolver.Resolve(definition: definition, mirror: mirror, revision: 0);
            var layers = new SdfSkyLayer[SdfSky.MaxLayers];
            resolved.Sky.Pack(resolved.Lights, new SdfSkyDetails(), out var block, layers);
            return refresh.Owes(block, layers);
        }
        Assert.True(Owes(0UL));
        refresh.Rendered();
        Assert.False(Owes(1260UL));
        Assert.True(refresh.Projected);
        Assert.True(refresh.Skipped);
        Assert.True(Owes(20160UL));
        Assert.False(refresh.Skipped);
    }

    [Fact]
    public void RepeatedPanelsResolveTheirFieldsAndLayerControls() {
        var resolved = Resolve(BaseDefaults() with {
            Sky = new WorldRenderSky(Layers: [
                new WorldRenderSkyLayer.Panel(Direction: Vector3.UnitZ, Size: new Vector2(.2f, .4f), Color: new BindableColor(Raw: "#4080FF"), Intensity: 2f, Blur: .1f, Name: "key"),
                new WorldRenderSkyLayer.Panel(Name: "fill") { Visibility = WorldSkyVisibility.Camera, Opacity = .5f, Tier = WorldSkyTier.Low },
            ]),
        });
        Assert.Equal(3, resolved.Sky.LayerCount);
        Assert.Equal(SdfSkyLayerKind.Panel, resolved.Sky.LayerAt(1).Kind);
        Assert.Equal(SdfSkyVisibility.Lighting, resolved.Sky.LayerAt(1).Visibility);
        Assert.Equal(SdfSkyBlend.Add, resolved.Sky.LayerAt(1).Blend);
        var panel = resolved.Sky.Parameters<SdfSkyPanel>(1);
        Assert.Equal(Vector3.UnitZ, panel.Direction);
        Assert.Equal(new Vector2(.2f, .4f), panel.Size);
        Assert.Equal(new Vector3(64f / 255f, 128f / 255f, 1f), panel.Color);
        Assert.Equal(2f, panel.Intensity);
        Assert.Equal(.1f, panel.Blur);
        Assert.Equal(SdfSkyVisibility.Camera, resolved.Sky.LayerAt(2).Visibility);
        Assert.Equal(.5f, resolved.Sky.LayerAt(2).Opacity);
    }

    [Fact]
    public void PanelGeometryAndEnvironmentGainsRefuseInvalidValues() {
        bool Valid(WorldRenderSkyLayer.Panel panel) => TryValidateLocal(Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [panel]) },
        });
        Assert.True(Valid(new WorldRenderSkyLayer.Panel()));
        Assert.False(Valid(new WorldRenderSkyLayer.Panel(Direction: Vector3.Zero)));
        Assert.False(Valid(new WorldRenderSkyLayer.Panel(Size: new Vector2(0f, 1f))));
        Assert.False(Valid(new WorldRenderSkyLayer.Panel(Intensity: -1f)));
        Assert.False(Valid(new WorldRenderSkyLayer.Panel(Blur: float.NaN)));
        foreach (var gain in new[] { -1f, float.NaN, float.PositiveInfinity }) {
            Assert.False(TryValidateLocal(Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Ambient: gain) } }));
            Assert.False(TryValidateLocal(Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Reflection: gain) } }));
        }
        Assert.True(TryValidateLocal(Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Ambient: 0f, Reflection: 0f) } }));
    }
}
