using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The unauthored shadow policy retains the pinned sun in the frame's first stable slot.</summary>
public sealed class ShadowBootDefaultLawTests {
    [Fact]
    public void AnUnauthoredShadowPolicyBootsWithThePinnedSunInSlotZero() {
        var document = Fixtures.BuildDocument() with { RenderRaw = null };

        Assert.True(condition: WorldDefinitionLoader.TryLoad(utf8: WorldDefinitionSerialization.Serialize(definition: document),
            sourceName: "s60-fix-shadow-default.world.json", definition: out var definition, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: definition);
        Assert.Null(@object: definition.RenderRaw);
        Assert.Equal(expected: new WorldShadowSettings(FadeSlots: 0, FadeTicks: 0, Overflow: WorldShadowOverflow.Instant, Slots: 1),
            actual: WorldShadowSettings.From(render: definition.Render));
        var client = ClientFixtures.Client(definition: definition);

        client.DeliverDefinition(definition: definition, version: default);
        client.DeliverSnapshot(snapshot: new WorldSnapshot(Tick: 0, EngineTick: 0, Revision: 0, StepTicks: 1,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty));
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var frame = resolver.Resolve(definition: definition, revision: 0, mirror: client.StateMirror);
        var slots = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var readout = resolver.ShadowSlots.CopyTo(tick: new PresentedTick(Fraction: 0, Whole: 0), stable: slots,
            handoffs: new WorldShadowHandoff[WorldShadowAllocator.MaxFadeSlots], queued: new WorldShadowQueued[WorldShadowAllocator.MaxSlots]);

        Assert.Equal(expected: 1, actual: readout.StableCount);
        Assert.Equal(expected: 0, actual: readout.FadeCount);
        Assert.Equal(expected: 0, actual: readout.QueuedCount);
        Assert.Equal(expected: 0, actual: slots[0].Slot);
        Assert.Equal(expected: "sun", actual: slots[0].Candidate.Name);
        Assert.Equal(expected: WorldShadowMode.Always, actual: slots[0].Candidate.Mode);
        Assert.Equal(expected: slots[0].Candidate.LightIndex, actual: frame.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 1u, actual: frame.Lights[frame.Lights.ShadowSlots[0]].Shadows);
    }
}
