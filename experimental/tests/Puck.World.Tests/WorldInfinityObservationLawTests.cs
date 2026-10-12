using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Infinity layers observe through the authority's ordinary destination admission and nesting lifetime,
/// with names independent of physical screen indices.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldInfinityObservationLawTests {
    private const string Layer = "zenith";
    private const int Screen = WorldPrototypeFacets.DerivedFaceBase;

    private static readonly ulong Step = EngineTicks.PerRate(ratePerSecond: 30u);

    private static IHost Boot(TemporaryDirectory state) => state.Own(owner: WorldBootHarness.Compose(
        stateDirectory: state,
        presentation: WorldHostPresentation.None,
        world: "tests/Puck.World.Canaries/portal-nested/fixture.puck",
        edit: definition => definition with {
            RenderRaw = definition.Render with {
                Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.View(Name: Layer, Destination: "middle")]),
            },
        }
    ).Build());
    private static void Settle(WorldInstanceHost instances) {
        instances.Boot!.Server.Advance(stepTicks: Step);
        instances.SettleBootScreenSessions(stepped: true);
        instances.StepInstances(masterDeltaTicks: Step);
    }

    [Fact]
    public void InfinityLayersUseNamedAuthorityOwnedObservationsAndReleaseOnRemoval() {
        using var state = new TemporaryDirectory(prefix: "puck-infinity-observation-");
        var host = Boot(state: state);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        var screen = instances.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: Screen);
        var sky = instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer);

        Assert.NotNull(@object: screen);
        Assert.NotNull(@object: sky);
        Assert.NotNull(@object: screen.Observation);
        Assert.NotNull(@object: sky.Observation);
        Assert.NotSame(actual: sky, expected: screen);
        Assert.Equal(expected: screen.InstanceName, actual: sky.InstanceName);
        Assert.NotEqual(expected: screen.Observation.Session, actual: sky.Observation.Session);
        Assert.Equal(expected: new WorldObservationSite.InfinityLayer(Name: Layer), actual: sky.Site);
        Assert.Equal(expected: new WorldObservationSite.Screen(Index: Screen), actual: screen.Site);

        var boot = instances.Boot!;

        boot.Server.EnqueueMutation(mutation: new WorldMutation.SetRenderDefaults(
            Principal: Principal.Console,
            Render: boot.Server.Definition.Render with { Sky = new WorldRenderSky(Layers: []) }
        ));
        Settle(instances: instances);

        Assert.Null(@object: instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer));
        Assert.Null(@object: sky.Observation);
        Assert.Same(expected: screen, actual: instances.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: Screen));
        Assert.NotNull(@object: screen.Observation);
    }
    [Fact]
    public void InfinityObservationsFollowTheExistingNestingLimit() {
        using var state = new TemporaryDirectory(prefix: "puck-infinity-observation-depth-");
        var host = Boot(state: state);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        var first = instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer);

        Assert.NotNull(@object: first);
        Assert.NotNull(@object: first.Observation);
        var boot = instances.Boot!;

        boot.Server.EnqueueMutation(mutation: new WorldMutation.SetViewDefaults(
            Principal: Principal.Console,
            Views: boot.Server.Definition.Views with { NestingDepthRaw = 0 }
        ));
        Settle(instances: instances);

        Assert.Null(@object: first.Observation);
        Assert.Null(@object: instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer));

        boot.Server.EnqueueMutation(mutation: new WorldMutation.SetViewDefaults(
            Principal: Principal.Console,
            Views: boot.Server.Definition.Views with { NestingDepthRaw = 2 }
        ));
        Settle(instances: instances);

        var reopened = instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer);

        Assert.NotNull(@object: reopened);
        Assert.NotNull(@object: reopened.Observation);
        Assert.NotSame(actual: reopened, expected: first);
    }
    [Fact]
    public void AnOverCapLiveEditIsRefusedBeforeAnyNewObservationOpens() {
        using var state = new TemporaryDirectory(prefix: "puck-infinity-observation-cap-");
        var host = Boot(state: state);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        var boot = instances.Boot!;
        var before = boot.Server.Definition;
        var retained = instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer);
        var echoes = new List<WorldEditEcho>();

        Assert.NotNull(@object: retained);
        Assert.NotNull(@object: retained.Observation);
        boot.Server.EchoTap += echo => echoes.Add(item: echo);

        WorldRenderSkyLayer[] Layers(int count) => [
            new WorldRenderSkyLayer.View(Name: Layer, Destination: "middle"),
            .. Enumerable.Range(count: (count - 1), start: 1).Select(selector: index =>
                new WorldRenderSkyLayer.View(Name: $"extra{index}", Destination: "middle")),
        ];

        boot.Server.EnqueueMutation(mutation: new WorldMutation.SetRenderDefaults(
            Principal: Principal.Console,
            Render: before.Render with { Sky = new WorldRenderSky(Layers: Layers(count: (SdfSky.MaxInfinityViews + 1))) }
        ));
        Settle(instances: instances);

        Assert.Contains(collection: echoes, filter: echo => (echo.Rejected && echo.Message.Contains(comparisonType: StringComparison.Ordinal, value: $"infinity view {(SdfSky.MaxInfinityViews + 1)}")));
        Assert.Same(expected: before, actual: boot.Server.Definition);
        Assert.Same(expected: retained, actual: instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer));
        Assert.NotNull(@object: retained.Observation);
        for (var index = 1; (index <= SdfSky.MaxInfinityViews); index++) {
            Assert.Null(@object: instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: $"extra{index}"));
        }

        boot.Server.EnqueueMutation(mutation: new WorldMutation.SetRenderDefaults(
            Principal: Principal.Console,
            Render: before.Render with { Sky = new WorldRenderSky(Layers: Layers(count: SdfSky.MaxInfinityViews)) }
        ));
        Settle(instances: instances);

        Assert.Same(expected: retained, actual: instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: Layer));
        for (var index = 1; (index < SdfSky.MaxInfinityViews); index++) {
            Assert.NotNull(@object: instances.InfinitySession(instanceName: WorldInstanceHost.BootInstanceName, layerName: $"extra{index}")?.Observation);
        }
    }
}
