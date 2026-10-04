using System.Numerics;
using Puck.Assets.Documents;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

// The infinity views a sky authors (the `view` and `far` layers): the validator refuses an unnamed, unaimed or unbounded
// one by name and a world over the cap, and a stack at the cap validates; resolution lowers each layer to the one GPU
// kind with its fallback and coverage; and the view specs the host renders carry the layer's cone in the viewer's frame.
public sealed partial class WorldRenderLightingSkyLawTests {
    private static WorldRenderSkyLayer.View ViewLayer(string name = "lobby", string? destination = "lobby") => new(Name: name, Destination: destination);
    private static WorldRenderSkyLayer.Far FarLayer(string name = "planet", params string[] prototypes) => new(Name: name, Prototypes: ((prototypes.Length == 0) ? ["ball"] : prototypes));
    private static WorldDefinition WithSky(params WorldRenderSkyLayer[] layers) => (Fixtures.BuildDocument() with {
        CreationsRaw = [CreationFixtures.Sphere(id: "ball", scale: 1f)],
        RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: layers) },
    });

    [Fact]
    public void AnInfinityViewLayerOfEveryShapeValidatesWhenWellFormed() {
        var definition = WithSky(ViewLayer(), FarLayer());
        var valid = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.True(condition: valid, userMessage: reason);
    }
    [Fact]
    public void AnInfinityViewThatIsUnnamedUnaimedOrUnboundedRefusesByName() {
        Laws.Refuses(locally: true, needle: "layers[0].name is required", definition: WithSky(new WorldRenderSkyLayer.View(Destination: "lobby")));
        Laws.Refuses(locally: true, needle: "contains '$' or '~'", definition: WithSky(ViewLayer(name: "a$b")));
        Laws.Refuses(locally: true, needle: "layers[0].destination is required", definition: WithSky(ViewLayer(destination: null)));
        Laws.Refuses(locally: true, needle: "layers[0].prototypes is required", definition: WithSky(new WorldRenderSkyLayer.Far(Name: "planet")));
        Laws.Refuses(locally: true, needle: "names 'absent', which this world does not declare", definition: WithSky(FarLayer(prototypes: "absent")));
        Laws.Refuses(locally: true, needle: "names a second infinity view", definition: WithSky(ViewLayer(), FarLayer(name: "lobby")));
        Laws.Refuses(locally: true, needle: "layers[0].scale 0 lies outside (0, 1]", definition: WithSky(ViewLayer() with { Scale = 0f }));
        Laws.Refuses(locally: true, needle: "layers[0].refresh 0 must be at least 1", definition: WithSky(ViewLayer() with { Refresh = 0 }));
        Laws.Refuses(locally: true, needle: "layers[0].farDistance", definition: WithSky(ViewLayer() with { FarDistance = -1f }));
        Laws.Refuses(locally: true, needle: "the camera alone sees one", definition: WithSky(ViewLayer() with { Visibility = WorldSkyVisibility.Both }));
        Laws.Refuses(locally: true, needle: "its mask is a cone or none", definition: WithSky(ViewLayer() with { Mask = new WorldRenderSkyMask(Band: [0.0, 0.5]) }));
        Laws.Refuses(
            locally: true,
            needle: "reaches the horizon plane",
            definition: WithSky(ViewLayer() with { Mask = new WorldRenderSkyMask(Cone: new WorldRenderSkyCone(Toward: new DocumentVector3(x: 0f, y: 1f, z: 0f), Spread: (Math.PI / 2d))) })
        );
    }
    [Fact]
    public void AWorldOverTheInfinityViewCapRefusesByNameAndOneAtTheCapValidates() {
        WorldRenderSkyLayer[] Views(int count) => [.. Enumerable.Range(count: count, start: 0).Select(selector: index => ((WorldRenderSkyLayer)ViewLayer(name: $"view{index}")))];

        Assert.True(condition: TryValidateLocal(definition: WithSky(layers: Views(count: SdfSky.MaxInfinityViews))));
        Laws.Refuses(locally: true, needle: $"is infinity view {(SdfSky.MaxInfinityViews + 1)}; a world carries at most {SdfSky.MaxInfinityViews}", definition: WithSky(layers: Views(count: (SdfSky.MaxInfinityViews + 1))));
        Assert.Equal(actual: SdfSky.MaxInfinityViews, expected: WorldInfinityViewPlan.MaxViews);
    }
    [Fact]
    public void ViewAndFarLayersResolveToTheOneKindWithTheirFallbackAndCoverage() {
        var fallback = new BindableColor(Raw: "#336699");
        var sky = Resolve(defaults: BaseDefaults() with {
            Sky = new WorldRenderSky(Layers: [
                (ViewLayer() with { Fallback = fallback }),
                (FarLayer() with { Fallback = fallback }),
            ]),
        }).Sky;

        Assert.Equal(actual: StackOf(sky: sky).TakeLast(count: 2), expected: [(SdfSkyLayerKind.View, "lobby"), (SdfSkyLayerKind.View, "planet")]);

        var first = (sky.LayerCount - 2);
        var view = sky.Parameters<SdfSkyView>(index: first);
        var far = sky.Parameters<SdfSkyView>(index: (first + 1));

        Assert.Equal(actual: view.Coverage, expected: 0u);
        Assert.Equal(actual: far.Coverage, expected: 1u);
        Assert.Equal(actual: view.ImageSlot, expected: -1);
        Assert.Equal(actual: view.Fallback.X, expected: (0x33 / 255f), precision: 3);
        Assert.Equal(actual: view.Fallback.Y, expected: (0x66 / 255f), precision: 3);
        Assert.Equal(actual: view.Fallback.Z, expected: (0x99 / 255f), precision: 3);
    }
    [Fact]
    public void TheSpecsCarryTheLayersConeInTheViewersFrameAndTheirDefaults() {
        var up = new Vector3(x: 0f, y: 0f, z: 1f);

        var (right, frameUp, forward) = SdfSky.FrameOf(up: up);
        var toward = new Vector3(x: 0.2f, y: 0.9f, z: 0.1f);
        var sky = new WorldRenderSky(
            Frame: new WorldRenderSkyFrame(Up: new DocumentVector3(x: up.X, y: up.Y, z: up.Z)),
            Layers: [
                (ViewLayer() with {
                    Anchor = new DocumentVector3(x: 1f, y: 2f, z: 3f),
                    Mask = new WorldRenderSkyMask(Cone: new WorldRenderSkyCone(Toward: new DocumentVector3(x: toward.X, y: toward.Y, z: toward.Z), Spread: 0.3d)),
                    Shadows = true,
                    Turn = 90f,
                }),
                (FarLayer() with { Tier = WorldSkyTier.High }),
            ]
        );
        var specs = WorldInfinityViewSpecs.Of(sky: sky);

        Assert.Equal(actual: specs.Select(selector: static spec => (spec.Name, spec.Kind)), expected: [("lobby", InfinityViewKind.World), ("planet", InfinityViewKind.Far)]);

        var lobby = specs[0];
        var expectedAxis = (((toward.X * right) + (toward.Y * frameUp)) + (toward.Z * forward));

        Assert.NotNull(@object: lobby.Mask);
        Assert.True(condition: (Vector3.Distance(value1: lobby.Mask!.Value.Direction, value2: Vector3.Normalize(value: expectedAxis)) < 1e-5f));
        Assert.Equal(expected: 0.3f, actual: lobby.Mask.Value.HalfAngle, precision: 5);
        Assert.Equal(expected: new Vector3(x: 1f, y: 2f, z: 3f), actual: lobby.Anchor);
        Assert.Equal(expected: InfinityViewLevers.Shadows, actual: lobby.Levers);
        Assert.Equal(expected: WorldInfinityViewSpecs.DefaultScale, actual: lobby.Scale);
        Assert.Equal(expected: WorldInfinityViewSpecs.DefaultRefresh, actual: lobby.Refresh);
        Assert.Equal(expected: WorldInfinityViewSpecs.DefaultFarDistance, actual: lobby.FarDistance);
        Assert.True(condition: (Vector3.Distance(value1: Vector3.Transform(value: Vector3.UnitX, rotation: lobby.Orientation), value2: -Vector3.UnitZ) < 1e-5f));
        Assert.Null(@object: specs[1].Mask);
        Assert.Equal(expected: Puck.Abstractions.Presentation.QualityTier.High, actual: specs[1].MinimumTier);
        Assert.True(condition: specs.All(predicate: static spec => spec.TryValidate(reason: out _)));
    }
}
