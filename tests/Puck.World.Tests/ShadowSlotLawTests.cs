using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class ShadowSlotLawTests {
    private static readonly WorldShadowSettings Policy = new(FadeSlots: 1, FadeTicks: 8, Overflow: WorldShadowOverflow.Queue, Slots: 2);

    [Fact]
    public void AlwaysBodiesWinBeforeBrighterAutomaticBodiesAndNeverBodiesConsumeNothing() {
        var allocator = new WorldShadowAllocator();
        var candidates = new[] { Candidate(luminance: 100, mode: WorldShadowMode.Never, name: "never", ordinal: 0), Candidate("auto", 1, 99), Candidate(luminance: 0, mode: WorldShadowMode.Always, name: "second", ordinal: 3), Candidate(luminance: 0, mode: WorldShadowMode.Always, name: "first", ordinal: 2) };

        allocator.Advance(candidates: candidates, discontinuity: false, settings: Policy, structuralRevision: 0, tick: 10);
        var result = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 10));

        Assert.Equal(expected: new[] { "first", "second" }, actual: result.Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(expected: 2, actual: result.Counts.MarchSlots);
        allocator.Advance(tick: 11, structuralRevision: 0, discontinuity: false, candidates: candidates, settings: Policy with { Slots = 0, FadeSlots = 0 });
        Assert.Equal(expected: default, actual: allocator.CopyTo(tick: new PresentedTick(Fraction: 0, Whole: 11), stable: [], handoffs: [], queued: []));
    }
    [Fact]
    public void TiesUseListOrderWhileSelectedIdentitiesKeepTheirExistingSlots() {
        var allocator = new WorldShadowAllocator();

        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [Candidate("late", 2, 3), Candidate("early", 1, 3), Candidate("third", 3, 2)], settings: Policy);
        var first = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 10));

        allocator.Advance(tick: 11, structuralRevision: 0, discontinuity: false, candidates: [Candidate("late", 2, 10), Candidate("early", 1, 3), Candidate("third", 3, 2)], settings: Policy);
        var second = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 11));

        Assert.Equal(expected: new[] { "early", "late" }, actual: first.Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(expected: first.Stable.Select(selector: value => (value.Slot, value.Candidate.Name)), actual: second.Stable.Select(selector: value => (value.Slot, value.Candidate.Name)));
        Assert.Empty(collection: second.Handoffs);
    }
    [Fact]
    public void RepeatedDeliveryDoesNotDetectAFractionalCrossing() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 2), Candidate("b", 1, 1)], settings: policy);
        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 1), Candidate("b", 1, 2)], settings: policy);
        var same = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0.75, Whole: 10));

        Assert.Equal(expected: "a", actual: Assert.Single(collection: same.Stable).Candidate.Name);
        Assert.Empty(collection: same.Handoffs);
        allocator.Advance(tick: 11, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 1), Candidate("b", 1, 2)], settings: policy);
        Assert.Equal(expected: 11UL, actual: Assert.Single(collection: Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 11)).Handoffs).CrossingTick);
    }
    [Fact]
    public void PresentedWeightsAreExactAcrossReplayAndFrozenFramesBeyondTwoToThe53Ticks() {
        var origin = (1UL << 54);
        var first = Crossing(origin: origin);
        var replay = Crossing(origin: origin);
        var tick = new PresentedTick(Fraction: 0.5, Whole: (origin + 13));

        for (var frame = 0; (frame < 33); frame++) {
            var result = Read(allocator: first, tick: tick);
            var handoff = Assert.Single(collection: result.Handoffs);

            Assert.Equal(expected: 0.4375, actual: handoff.Weight);
            Assert.Equal(expected: (origin + 10), actual: handoff.CrossingTick);
            Assert.Equal(expected: Assert.Single(collection: Read(allocator: replay, tick: tick).Handoffs), actual: handoff);
            Assert.Equal(expected: 2, actual: result.Counts.MarchSlots);
        }
    }
    [Fact]
    public void RetiringAtDeliveryPreservesTheEarlierPresentedIntervalAndForwardGapsAreNotSeeks() {
        var allocator = Crossing(origin: 10);
        var policy = Policy with { Slots = 1 };
        var candidates = new[] { Candidate("a", 0, 1), Candidate("b", 1, 2) };

        allocator.Advance(candidates: candidates, discontinuity: false, settings: policy, structuralRevision: 0, tick: 23);
        Assert.Equal(expected: 0.375, actual: Assert.Single(collection: Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 23)).Handoffs).Weight);
        allocator.Advance(candidates: candidates, discontinuity: false, settings: policy, structuralRevision: 0, tick: 30);
        var earlier = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0.5, Whole: 25));
        var completed = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 28));

        Assert.Equal(expected: 0.6875, actual: Assert.Single(collection: earlier.Handoffs).Weight);
        Assert.Equal(expected: "a", actual: Assert.Single(collection: earlier.Stable).Candidate.Name);
        Assert.Empty(collection: completed.Handoffs);
        Assert.Equal(expected: "b", actual: Assert.Single(collection: completed.Stable).Candidate.Name);
        Assert.Equal(expected: 1, actual: completed.Counts.MarchSlots);
    }
    [InlineData(WorldShadowOverflow.Queue)]
    [InlineData(WorldShadowOverflow.Instant)]
    [Theory]
    public void ASecondDisjointCrossingUsesTheDeclaredOverflowPolicy(WorldShadowOverflow overflow) {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Overflow = overflow };

        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 4), Candidate("c", 2, 3), Candidate("b", 1, 2), Candidate("d", 3, 1)], settings: policy);
        allocator.Advance(tick: 20, structuralRevision: 0, discontinuity: false, candidates: [Candidate("b", 1, 4), Candidate("c", 2, 3), Candidate("a", 0, 2), Candidate("d", 3, 1)], settings: policy);
        allocator.Advance(tick: 21, structuralRevision: 0, discontinuity: false, candidates: [Candidate("b", 1, 4), Candidate("d", 3, 3), Candidate("a", 0, 2), Candidate("c", 2, 1)], settings: policy);
        var result = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 21));

        Assert.Equal(expected: "b", actual: Assert.Single(collection: result.Handoffs).Incoming.Name);
        Assert.Equal(expected: 3, actual: result.Counts.MarchSlots);
        if (overflow == WorldShadowOverflow.Queue) {
            Assert.Equal(expected: "d", actual: Assert.Single(collection: result.Queued).Incoming.Name);
            Assert.Equal(expected: WorldShadowQueueReason.FadeCapacity, actual: result.Queued[0].Reason);
            Assert.Equal(expected: "c", actual: result.Stable[1].Candidate.Name);
        } else {
            Assert.Empty(collection: result.Queued);
            Assert.Equal(expected: "d", actual: result.Stable[1].Candidate.Name);
        }
    }
    [Fact]
    public void AStaleQueuedTargetNeverStartsAndASelectedAlwaysBodyNeverFadesOut() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 3 };
        var pinned = Candidate(luminance: 0, mode: WorldShadowMode.Always, name: "pinned", ordinal: 9);

        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [pinned, Candidate("a", 0, 4), Candidate("c", 2, 3)], settings: policy);
        allocator.Advance(tick: 20, structuralRevision: 0, discontinuity: false, candidates: [pinned, Candidate("b", 1, 4), Candidate("d", 3, 3)], settings: policy);
        Assert.Equal(expected: "d", actual: Assert.Single(collection: Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 20)).Queued).Incoming.Name);
        allocator.Advance(tick: 21, structuralRevision: 0, discontinuity: false, candidates: [pinned, Candidate("b", 1, 4), Candidate("e", 4, 3)], settings: policy);
        allocator.Advance(tick: 28, structuralRevision: 0, discontinuity: false, candidates: [pinned, Candidate("b", 1, 4), Candidate("e", 4, 3)], settings: policy);
        var result = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 28));

        Assert.Equal(expected: "pinned", actual: result.Stable[0].Candidate.Name);
        Assert.Equal(expected: "e", actual: Assert.Single(collection: result.Handoffs).Incoming.Name);
        Assert.DoesNotContain(collection: result.Handoffs, filter: value => ((value.Outgoing.Name == "pinned") || (value.Incoming.Name == "d")));
        Assert.Empty(collection: result.Queued);
        Assert.Equal(expected: 4, actual: result.Counts.MarchSlots);
    }
    [InlineData(true, 0L, 25UL)]
    [InlineData(false, 1L, 25UL)]
    [InlineData(false, 0L, 5UL)]
    [Theory]
    public void SeekRevisionAndBackwardDeliveryCompleteEveryFade(bool discontinuity, long revision, ulong tick) {
        var allocator = Crossing(origin: 10);

        allocator.Advance(tick: tick, structuralRevision: revision, discontinuity: discontinuity,
            candidates: [Candidate("a", 0, 1), Candidate("b", 1, 2), Candidate("c", 2, 3)], settings: Policy with { Slots = 1 });
        var result = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: tick));

        Assert.Empty(collection: result.Handoffs);
        Assert.Equal(expected: "c", actual: Assert.Single(collection: result.Stable).Candidate.Name);
        Assert.Equal(expected: 1, actual: result.Counts.MarchSlots);
    }
    [Fact]
    public void AnInstantTierNeedsNoAdditionalMarchAndSteadyReadsAllocateNothing() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1, FadeTicks = 0 };
        var candidates = new[] { Candidate("a", 0, 1) };
        var stable = new WorldShadowSlot[4];
        var fades = new WorldShadowHandoff[2];
        var queued = new WorldShadowQueued[4];

        allocator.Advance(tick: 1, structuralRevision: 0, discontinuity: false, candidates: [Candidate("b", 1, 1)], settings: policy);
        allocator.Advance(candidates: candidates, discontinuity: false, settings: policy, structuralRevision: 0, tick: 2);
        var first = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 2));

        Assert.Equal(expected: "a", actual: Assert.Single(collection: first.Stable).Candidate.Name);
        Assert.Empty(collection: first.Handoffs);
        for (ulong tick = 3; (tick < 300); tick++) {
            allocator.Advance(candidates: candidates, discontinuity: false, settings: policy, structuralRevision: 0, tick: tick);
            _ = allocator.CopyTo(tick: new PresentedTick(Fraction: 0.5, Whole: tick), stable: stable, handoffs: fades, queued: queued);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (ulong tick = 300; (tick < 600); tick++) {
            allocator.Advance(candidates: candidates, discontinuity: false, settings: policy, structuralRevision: 0, tick: tick);
            _ = allocator.CopyTo(tick: new PresentedTick(Fraction: 0.5, Whole: tick), stable: stable, handoffs: fades, queued: queued);
        }
        Assert.Equal(expected: before, actual: GC.GetAllocatedBytesForCurrentThread());
    }
    [Fact]
    public void TooSmallOutputRefusesBeforeWritingAnySpan() {
        var allocator = Crossing(origin: 10);
        var stable = new WorldShadowSlot[] { new(Slot: 9, Candidate: Candidate("sentinel", 9, 1)) };

        Assert.Throws<ArgumentException>(testCode: () => allocator.CopyTo(tick: new PresentedTick(Fraction: 0, Whole: 21), stable: stable, handoffs: [], queued: []));
        Assert.Equal(expected: "sentinel", actual: stable[0].Candidate.Name);
    }
    [Fact]
    public void QueueWaitsForAnOutgoingIdentityEvenWhenAnotherFadeSlotIsFree() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { FadeSlots = 2 };

        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 3), Candidate("c", 2, 2), Candidate("b", 1, 1)], settings: policy);
        allocator.Advance(tick: 20, structuralRevision: 0, discontinuity: false, candidates: [Candidate("b", 1, 3), Candidate("c", 2, 2), Candidate("a", 0, 1)], settings: policy);
        var desired = new[] { Candidate("a", 0, 3), Candidate("b", 1, 2), Candidate("c", 2, 1) };

        allocator.Advance(candidates: desired, discontinuity: false, settings: policy, structuralRevision: 0, tick: 21);
        var waiting = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 21));

        Assert.Equal(expected: 1, actual: waiting.Counts.HandoffCount);
        Assert.Equal(expected: WorldShadowQueueReason.IdentityInUse, actual: Assert.Single(collection: waiting.Queued).Reason);
        Assert.Equal(expected: "a", actual: waiting.Queued[0].Incoming.Name);
        allocator.Advance(candidates: desired, discontinuity: false, settings: policy, structuralRevision: 0, tick: 28);
        var started = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 28));

        Assert.Equal(expected: "a", actual: Assert.Single(collection: started.Handoffs).Incoming.Name);
        Assert.Equal(expected: 3, actual: started.Stable.Select(selector: value => value.Candidate.Name).Concat(second: started.Handoffs.Select(selector: value => value.Incoming.Name)).Distinct().Count());
    }
    [Fact]
    public void FourStableSlotsAndTwoFadesNeverMarchMoreThanSixBodies() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 4, FadeSlots = 2 };
        var target = new[] { Candidate("b", 1, 4), Candidate("d", 3, 3), Candidate("f", 5, 2), Candidate("h", 7, 1) };

        allocator.Advance(tick: 10, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 4), Candidate("c", 2, 3), Candidate("e", 4, 2), Candidate("g", 6, 1)], settings: policy);
        allocator.Advance(candidates: target, discontinuity: false, settings: policy, structuralRevision: 0, tick: 20);
        var first = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 20));

        Assert.Equal(expected: new WorldShadowReadout(HandoffCount: 2, MarchSlots: 6, QueuedCount: 2, StableCount: 4), actual: first.Counts);
        allocator.Advance(candidates: target, discontinuity: false, settings: policy, structuralRevision: 0, tick: 28);
        Assert.Equal(expected: 6, actual: Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 28)).Counts.MarchSlots);
        allocator.Advance(candidates: target, discontinuity: false, settings: policy, structuralRevision: 0, tick: 36);
        var completed = Read(allocator: allocator, tick: new PresentedTick(Fraction: 0, Whole: 36));

        Assert.Equal(expected: new WorldShadowReadout(HandoffCount: 0, MarchSlots: 4, QueuedCount: 0, StableCount: 4), actual: completed.Counts);
        Assert.Equal(expected: new[] { "b", "d", "f", "h" }, actual: completed.Stable.Select(selector: value => value.Candidate.Name));
    }

    private static WorldShadowCandidate Candidate(string name, int ordinal, double luminance, WorldShadowMode mode = WorldShadowMode.Auto) =>
        new(LightIndex: ordinal, ListOrdinal: ordinal, Luminance: luminance, Mode: mode, Name: name);
    private static WorldShadowAllocator Crossing(ulong origin) {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        allocator.Advance(tick: origin, structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 2), Candidate("b", 1, 1)], settings: policy);
        allocator.Advance(tick: (origin + 10), structuralRevision: 0, discontinuity: false, candidates: [Candidate("a", 0, 1), Candidate("b", 1, 2)], settings: policy);
        return allocator;
    }
    private static (WorldShadowReadout Counts, WorldShadowSlot[] Stable, WorldShadowHandoff[] Handoffs, WorldShadowQueued[] Queued) Read(WorldShadowAllocator allocator, PresentedTick tick) {
        var stable = new WorldShadowSlot[4];
        var handoffs = new WorldShadowHandoff[2];
        var queued = new WorldShadowQueued[4];
        var counts = allocator.CopyTo(handoffs: handoffs, queued: queued, stable: stable, tick: tick);

        return (counts, stable[..counts.StableCount], handoffs[..counts.HandoffCount], queued[..counts.QueuedCount]);
    }
}
