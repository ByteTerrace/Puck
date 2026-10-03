using Microsoft.Extensions.DependencyInjection;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The real boot presenter consumes coherent client revisions at definition and snapshot delivery.</summary>
public sealed class BootShadowDeliveryLawTests {
    private static WorldStateRow Weights(long first, long second, long third) => new(
        Name: CellName.Parse(candidate: "shadowWeights"), Kind: CellKind.Int, Cells: [
            new StateCell(Key: CellName.Parse(candidate: "first"), Value: CellValue.Int(value: first)),
            new StateCell(Key: CellName.Parse(candidate: "second"), Value: CellValue.Int(value: second)),
            new StateCell(Key: CellName.Parse(candidate: "third"), Value: CellValue.Int(value: third)),
        ]);
    private static WorldRenderLight.Directional Light(string name) => new(
        Name: name, Shadow: WorldShadowMode.Auto, Weight: new BindableScalar(binding: $"state.shadowWeights.{name}"));
    private static WorldDefinition WithWeights(WorldDefinition definition, long first, long second, long third) {
        var weights = Weights(first: first, second: second, third: third);

        return definition.WithWorldState([.. definition.AuthoredState.Where(predicate: row => (row.Name != weights.Name)), weights]);
    }

    [Fact]
    public void DefinitionReorderAndSnapshotKeepBootSlotsAndTheLiveFadeAtTheRealRevision() {
        using var directory = new TemporaryDirectory(prefix: "s60-fix-boot-");
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: directory,
            world: "tests/Puck.Counters/counters.world.json",
            edit: definition => WithWeights(definition: definition with {
                RenderRaw = definition.Render with {
                    ShadowLights = 2,
                    ShadowFadeSlots = 1,
                    ShadowFadeTicks = 8,
                    ShadowOverflow = WorldShadowOverflow.Queue,
                    Lighting = new WorldRenderLighting(Lights: [Light(name: "first"), Light(name: "second"), Light(name: "third")]),
                },
            }, first: 3, second: 2, third: 1)).Build();
        var client = host.Services.GetRequiredService<WorldClient>();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();

        SdfFrame Frame() => presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 1f, width: 64);
        void Snapshot(ulong tick) => client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: tick, EngineTick: tick, Revision: 0, StepTicks: 1, Entries: ReadOnlyMemory<EntitySnapshot>.Empty));
        void Deliver(ulong tick, long first, long second, long third) {
            client.DeliverState(WithWeights(definition: client.Definition, first: first, second: second, third: third), default,
                new WorldStateStamp(EngineTick: tick, Everything: true, MovedRows: default, Tick: tick));
            Snapshot(tick: tick);
        }

        Assert.True(condition: (client.DefinitionRevision > 0));
        Assert.Equal(0, Frame().Lights.ShadowSlots[0]);
        Deliver(first: 2, second: 3, third: 1, tick: 10);
        Assert.Equal(0, Frame().Lights.ShadowSlots[0]);
        Assert.Contains("shadow[1] light=second", presenter.DescribeShadowSlots(definition: client.Definition));

        Deliver(first: 1, second: 3, third: 4, tick: 20);
        Snapshot(tick: 22);
        Assert.Equal(0, Frame().Lights.ShadowSlots[0]);
        var before = presenter.DescribeShadowSlots(definition: client.Definition);

        Assert.Contains(actualString: before, expectedSubstring: "fades=1 queued=0");
        Assert.Contains(actualString: before, expectedSubstring: "slot=0 outgoing=first index=0 incoming=third index=2 crossing=20 duration=8 weight=0.25");
        var revision = client.DefinitionRevision;
        var reordered = client.Definition with {
            RenderRaw = client.Definition.Render with {
                Lighting = new WorldRenderLighting(Lights: [Light(name: "second"), Light(name: "third"), Light(name: "first")]),
            },
        };

        client.DeliverDefinition(definition: reordered, version: default);
        Assert.Equal((revision + 1), client.DefinitionRevision);
        Assert.Equal(2, Frame().Lights.ShadowSlots[0]);
        var installed = presenter.DescribeShadowSlots(definition: reordered);

        Assert.Contains(actualString: installed, expectedSubstring: "shadow[0] light=first index=2");
        Assert.Contains(actualString: installed, expectedSubstring: "shadow[1] light=second index=0");
        Assert.Contains(actualString: installed, expectedSubstring: "slot=0 outgoing=first index=2 incoming=third index=1 crossing=20 duration=8 weight=0.25");
        Snapshot(tick: 22);
        Assert.Equal(2, Frame().Lights.ShadowSlots[0]);
        Assert.Equal(installed, presenter.DescribeShadowSlots(definition: reordered));

        Snapshot(tick: 28);
        Assert.Equal(1, Frame().Lights.ShadowSlots[0]);
        var completed = presenter.DescribeShadowSlots(definition: reordered);

        Assert.Contains(actualString: completed, expectedSubstring: "fades=0 queued=0");
        Assert.Contains(actualString: completed, expectedSubstring: "shadow[0] light=third index=1");
        Assert.Contains(actualString: completed, expectedSubstring: "shadow[1] light=second index=0");
    }
}
