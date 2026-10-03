using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Slot identity, reset stability and delivered queue fairness.</summary>
public sealed class ShadowAllocatorRevisionLawTests {
    private static readonly WorldShadowSettings Policy = new(FadeSlots: 1, FadeTicks: 8, Overflow: WorldShadowOverflow.Queue, Slots: 2);

    private static WorldShadowCandidate Light(string name, int index, double luminance, WorldShadowMode mode = WorldShadowMode.Auto) => new(LightIndex: index, ListOrdinal: index, Luminance: luminance, Mode: mode, Name: name);
    private static void Advance(WorldShadowAllocator allocator, ulong tick, WorldShadowCandidate[] lights, WorldShadowSettings? policy = null, long revision = 0) =>
        allocator.Advance(tick, revision, false, lights, (policy ?? Policy));
    private static (WorldShadowSlot[] Held, WorldShadowHandoff[] Fades, WorldShadowQueued[] Queued) Read(WorldShadowAllocator allocator, ulong tick) {
        var held = new WorldShadowSlot[4];
        var fades = new WorldShadowHandoff[2];
        var queued = new WorldShadowQueued[4];
        var counts = allocator.CopyTo(new PresentedTick(Fraction: 0, Whole: tick), held, fades, queued);

        return (held[..counts.StableCount], fades[..counts.FadeCount], queued[..counts.QueuedCount]);
    }

    [Fact]
    public void APreviouslyQueuedCrossingTakesCapacityBeforeAFreshLowerSlot() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Light("a", 0, 4), Light("b", 1, 3), Light("c", 2, 2), Light("d", 3, 1), Light("e", 4, 0)]);
        Advance(allocator, 20, [Light("c", 2, 5), Light("d", 3, 4), Light("a", 0, 2), Light("b", 1, 1), Light("e", 4, 0)]);
        Assert.Equal("d", Assert.Single(collection: Read(allocator: allocator, tick: 20).Queued).Incoming.Name);
        Advance(allocator, 28, [Light("e", 4, 5), Light("d", 3, 4), Light("c", 2, 2), Light("a", 0, 1), Light("b", 1, 0)]);
        var result = Read(allocator: allocator, tick: 28);
        var started = Assert.Single(collection: result.Fades);

        Assert.Equal((1, "b", "d", 28UL), (started.Slot, started.Outgoing.Name, started.Incoming.Name, started.CrossingTick));
        Assert.True(condition: ((started.Flags & WorldShadowHandoffFlags.FromQueue) != 0));
        var waiting = Assert.Single(collection: result.Queued);

        Assert.Equal((0, "e", WorldShadowQueueReason.FadeCapacity), (waiting.Slot, waiting.Incoming.Name, waiting.Reason));
    }
    [Fact]
    public void OlderQueuedTargetsStayAheadOfNewerLowerSlotsAcrossDeliveries() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 3 };

        Advance(allocator, 10, [Light("a", 0, 8), Light("b", 1, 7), Light("c", 2, 6)], policy);
        Advance(allocator, 20, [Light("d", 3, 8), Light("b", 1, 7), Light("f", 5, 6)], policy);
        var desired = new[] { Light("d", 3, 8), Light("e", 4, 7), Light("f", 5, 6) };

        Advance(allocator, 21, desired, policy);
        Assert.Equal(new[] { (2, "f"), (1, "e") }, Read(allocator: allocator, tick: 21).Queued.Select(selector: value => (value.Slot, value.Incoming.Name)));
        Advance(allocator, 28, desired, policy);
        var started = Assert.Single(collection: Read(allocator: allocator, tick: 28).Fades);

        Assert.Equal((2, "c", "f", 28UL), (started.Slot, started.Outgoing.Name, started.Incoming.Name, started.CrossingTick));
        Assert.Equal("e", Assert.Single(collection: Read(allocator: allocator, tick: 28).Queued).Incoming.Name);
    }
    [Fact]
    public void ReusingATableIndexForANewNameStartsAFadeInsteadOfRepointingTheHolder() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        Advance(allocator, 10, [Light("x", 0, 1)], policy);
        Advance(allocator, 11, [Light("y", 0, 1)], policy);
        var result = Read(allocator: allocator, tick: 11);

        Assert.Equal(("x", -1), (Assert.Single(collection: result.Held).Candidate.Name, result.Held[0].Candidate.LightIndex));
        var handoff = Assert.Single(collection: result.Fades);

        Assert.Equal(("x", "y", 11UL), (handoff.Outgoing.Name, handoff.Incoming.Name, handoff.CrossingTick));
        Assert.Equal((-1, 0), (handoff.Control.Outgoing, handoff.Control.Incoming));
        Assert.Equal("x", Assert.Single(collection: Read(allocator: allocator, tick: 10).Held).Candidate.Name);
        Advance(allocator, 19, [Light("y", 0, 1)], policy);
        Assert.Equal("y", Assert.Single(collection: Read(allocator: allocator, tick: 19).Held).Candidate.Name);
        Assert.Empty(collection: Read(allocator: allocator, tick: 19).Fades);
    }
    [Fact]
    public void ReusingATableIndexOnAnInstantTierReplacesItsOwnerAtTheDeliveredBoundary() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1, FadeSlots = 0, FadeTicks = 0, Overflow = WorldShadowOverflow.Instant };

        Advance(allocator, 10, [Light("x", 0, 1)], policy);
        Advance(allocator, 11, [Light("y", 0, 1)], policy);
        Assert.Equal("x", Assert.Single(collection: Read(allocator: allocator, tick: 10).Held).Candidate.Name);
        var result = Read(allocator: allocator, tick: 11);

        Assert.Equal("y", Assert.Single(collection: result.Held).Candidate.Name);
        Assert.Empty(collection: result.Fades);
        Assert.Empty(collection: result.Queued);
    }
    [Fact]
    public void ADepartedIncomingNameSurvivesItsFadeWhenItsIndexIsReused() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        Advance(allocator, 10, [Light("a", 0, 2), Light("b", 1, 1)], policy);
        Advance(allocator, 20, [Light("a", 0, 1), Light("b", 1, 2)], policy);
        Advance(allocator, 21, [Light("c", 1, 3)], policy);
        var result = Read(allocator: allocator, tick: 21);
        var fade = Assert.Single(collection: result.Fades);

        Assert.Equal(("a", "b", -1, -1), (fade.Outgoing.Name, fade.Incoming.Name, fade.Control.Outgoing, fade.Control.Incoming));
        Assert.Equal("c", Assert.Single(collection: result.Queued).Incoming.Name);
        Advance(allocator, 28, [Light("c", 1, 3)], policy);
        fade = Assert.Single(collection: Read(allocator: allocator, tick: 28).Fades);
        Assert.Equal(("b", "c", 28UL), (fade.Outgoing.Name, fade.Incoming.Name, fade.CrossingTick));
    }
    [Fact]
    public void ReusingAQueuedNamesIndexDoesNotGiveTheReplacementItsQueueAge() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Light("a", 0, 4), Light("b", 1, 3), Light("y", 2, 2), Light("c", 3, 1)]);
        Advance(allocator, 20, [Light("c", 3, 5), Light("y", 2, 4), Light("a", 0, 1), Light("b", 1, 0)]);
        Assert.Equal("y", Assert.Single(collection: Read(allocator: allocator, tick: 20).Queued).Incoming.Name);
        Advance(allocator, 28, [Light("c", 3, 5), Light("z", 2, 4), Light("a", 0, 1), Light("b", 1, 0)]);
        var fade = Assert.Single(collection: Read(allocator: allocator, tick: 28).Fades);

        Assert.Equal("z", fade.Incoming.Name);
        Assert.False(condition: ((fade.Flags & WorldShadowHandoffFlags.FromQueue) != 0));
    }
    [Fact]
    public void AStructuralRevisionPreservesSurvivingHoldersDespiteTheirNewRanking() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Light("a", 0, 2), Light("b", 1, 1)]);
        var current = new[] { Light("a", 0, 1), Light("b", 1, 2) };

        Advance(allocator, 11, current);
        Advance(allocator, 12, current, revision: 1);
        var result = Read(allocator: allocator, tick: 12);

        Assert.Equal(new[] { (0, "a", 2), (1, "b", 1) }, result.Held.Select(selector: value => (value.Slot, value.Candidate.Name, value.Rank)));
        Assert.Empty(collection: result.Fades);
        Assert.Empty(collection: result.Queued);
    }
    [InlineData(WorldShadowMode.Always)]
    [InlineData(WorldShadowMode.Auto)]
    [Theory]
    public void AnEqualPriorityHolderWinsOverANonholderAfterAReorder(WorldShadowMode mode) {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        Advance(allocator, 10, [Light(index: 0, luminance: 1, mode: mode, name: "a"), Light(index: 1, luminance: 1, mode: mode, name: "b")], policy);
        allocator.RemapLights(lights: [new WorldRenderLight.Directional(Name: "b", Shadow: mode), new WorldRenderLight.Directional(Name: "a", Shadow: mode)]);
        Advance(allocator, 11, [Light(index: 0, luminance: 1, mode: mode, name: "b"), Light(index: 1, luminance: 1, mode: mode, name: "a")], policy);
        var result = Read(allocator: allocator, tick: 11);

        Assert.Equal(("a", 1), (Assert.Single(collection: result.Held).Candidate.Name, result.Held[0].Candidate.LightIndex));
        Assert.Empty(collection: result.Fades);
        Assert.Empty(collection: result.Queued);
    }
}
