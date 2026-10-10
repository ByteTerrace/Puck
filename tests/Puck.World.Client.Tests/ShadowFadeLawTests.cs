using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Tick-defined shadow handoffs, bounded overflow and visibility controls on the CPU.</summary>
public sealed class ShadowFadeLawTests {
    private static readonly WorldShadowSettings Policy = new(FadeSlots: 1, FadeTicks: 8, Overflow: WorldShadowOverflow.Queue, Slots: 2);

    private static WorldShadowCandidate Candidate(string name, int ordinal, double luminance,
        WorldShadowMode mode = WorldShadowMode.Auto) =>
        new(LightIndex: ordinal, ListOrdinal: ordinal, Luminance: luminance, Mode: mode, Name: name);
    private static void Advance(WorldShadowAllocator allocator, ulong tick, WorldShadowCandidate[] candidates,
        WorldShadowSettings? policy = null, bool discontinuity = false, long revision = 0) =>
        allocator.Advance(tick, revision, discontinuity, candidates, (policy ?? Policy));
    private static WorldShadowAllocator Crossing(ulong origin = 10) {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        Advance(allocator, origin, [Candidate("a", 0, 2), Candidate("b", 1, 1)], policy);
        Advance(allocator, (origin + 10), [Candidate("a", 0, 1), Candidate("b", 1, 2)], policy);
        return allocator;
    }
    private static (WorldShadowReadout Counts, WorldShadowSlot[] Stable, WorldShadowHandoff[] Handoffs, WorldShadowQueued[] Queued)
        Read(WorldShadowAllocator allocator, ulong tick, double fraction = 0) {
        var stable = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var handoffs = new WorldShadowHandoff[WorldShadowAllocator.MaxFadeSlots];
        var queued = new WorldShadowQueued[WorldShadowAllocator.MaxSlots];
        var counts = allocator.CopyTo(new PresentedTick(Fraction: fraction, Whole: tick), stable, handoffs, queued);

        return (counts, stable[..counts.StableCount], handoffs[..counts.FadeCount], queued[..counts.QueuedCount]);
    }

    [Fact]
    public void PresentedWeightsAreExactAcrossReplayAndFrozenFramesBeyondTwoToThe53Ticks() {
        const ulong Origin = (1UL << 54);
        var first = Crossing(origin: Origin);
        var replay = Crossing(origin: Origin);

        for (var frame = 0; (frame < 33); frame++) {
            var result = Read(allocator: first, fraction: 0.5, tick: (Origin + 13));
            var handoff = Assert.Single(collection: result.Handoffs);

            Assert.Equal(0.4375, handoff.Weight);
            Assert.Equal((Origin + 10), handoff.CrossingTick);
            Assert.Equal(8UL, handoff.DurationTicks);
            Assert.Equal(Assert.Single(collection: Read(allocator: replay, fraction: 0.5, tick: (Origin + 13)).Handoffs), handoff);
            Assert.Equal(2, result.Counts.MarchSlots);
        }
    }
    [Fact]
    public void RetirementPreservesTheEarlierPresentedIntervalAndForwardGapsAreNotSeeks() {
        var allocator = Crossing();
        var policy = Policy with { Slots = 1 };
        var candidates = new[] { Candidate("a", 0, 1), Candidate("b", 1, 2) };

        Advance(allocator, 23, candidates, policy);
        Assert.Equal(0.375, Assert.Single(collection: Read(allocator, 23).Handoffs).Weight);
        Advance(allocator, 30, candidates, policy);
        var earlier = Read(allocator: allocator, fraction: 0.5, tick: 25);

        Assert.Equal(0.6875, Assert.Single(collection: earlier.Handoffs).Weight);
        Assert.Equal("a", Assert.Single(collection: earlier.Stable).Candidate.Name);
        var completed = Read(allocator, 28);

        Assert.Empty(collection: completed.Handoffs);
        Assert.Equal("b", Assert.Single(collection: completed.Stable).Candidate.Name);
        Assert.Equal(1, completed.Counts.MarchSlots);
    }
    [InlineData(WorldShadowOverflow.Queue)]
    [InlineData(WorldShadowOverflow.Instant)]
    [Theory]
    public void ASecondDisjointCrossingUsesTheDeclaredOverflowPolicy(WorldShadowOverflow overflow) {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Overflow = overflow };

        Advance(allocator, 10, [Candidate("a", 0, 4), Candidate("c", 2, 3), Candidate("b", 1, 2), Candidate("d", 3, 1)], policy);
        Advance(allocator, 20, [Candidate("b", 1, 4), Candidate("c", 2, 3), Candidate("a", 0, 2), Candidate("d", 3, 1)], policy);
        Advance(allocator, 21, [Candidate("b", 1, 4), Candidate("d", 3, 3), Candidate("a", 0, 2), Candidate("c", 2, 1)], policy);
        var result = Read(allocator, 21);

        Assert.Equal("b", Assert.Single(collection: result.Handoffs).Incoming.Name);
        Assert.Equal(3, result.Counts.MarchSlots);
        if (overflow == WorldShadowOverflow.Queue) {
            var queued = Assert.Single(collection: result.Queued);

            Assert.Equal("d", queued.Incoming.Name);
            Assert.Equal(1, queued.Slot);
            Assert.Equal(WorldShadowQueueReason.FadeCapacity, queued.Reason);
            Assert.Equal("c", result.Stable[1].Candidate.Name);
        } else {
            Assert.Empty(collection: result.Queued);
            Assert.Equal("d", result.Stable[1].Candidate.Name);
        }
    }
    [Fact]
    public void AStaleQueuedTargetNeverStartsAndASelectedAlwaysLightNeverFadesOut() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 3 };
        var pinned = Candidate(luminance: 0, mode: WorldShadowMode.Always, name: "pinned", ordinal: 7);

        Advance(allocator, 10, [pinned, Candidate("a", 0, 4), Candidate("c", 2, 3)], policy);
        Advance(allocator, 20, [pinned, Candidate("b", 1, 4), Candidate("d", 3, 3)], policy);
        Assert.Equal("d", Assert.Single(collection: Read(allocator, 20).Queued).Incoming.Name);
        var current = new[] { pinned, Candidate("b", 1, 4), Candidate("e", 4, 3) };

        Advance(allocator, 21, current, policy);
        Assert.Equal("e", Assert.Single(collection: Read(allocator, 21).Queued).Incoming.Name);
        Advance(allocator, 28, current, policy);
        var result = Read(allocator, 28);

        Assert.Equal("pinned", result.Stable[0].Candidate.Name);
        var started = Assert.Single(collection: result.Handoffs);

        Assert.Equal("e", started.Incoming.Name);
        Assert.True(condition: ((started.Flags & WorldShadowHandoffFlags.FromQueue) != 0));
        Assert.DoesNotContain(collection: result.Handoffs, filter: value => ((value.Outgoing.Name == "pinned") || (value.Incoming.Name == "d")));
        Assert.Empty(collection: result.Queued);
        Assert.Equal(4, result.Counts.MarchSlots);
    }
    [InlineData("seek")]
    [InlineData("revision")]
    [InlineData("backward")]
    [InlineData("policy")]
    [Theory]
    public void ADiscontinuityClearsLiveFadesAndInstallsTheCurrentSelection(string reset) {
        var allocator = Crossing();

        Assert.Single(collection: Read(allocator, 23).Handoffs);
        var tick = ((reset == "backward") ? 5UL : 25UL);
        var policy = Policy with { Slots = 1, FadeSlots = ((reset == "policy") ? 2 : 1) };

        Advance(allocator, tick, [Candidate("a", 0, 1), Candidate("b", 1, 2), Candidate("c", 2, 3)], policy,
            discontinuity: (reset == "seek"), revision: ((reset == "revision") ? 1 : 0));
        foreach (var presented in new[] { 0UL, tick }) {
            var result = Read(allocator, presented);

            Assert.Equal("c", Assert.Single(collection: result.Stable).Candidate.Name);
            Assert.Empty(collection: result.Handoffs);
            Assert.Empty(collection: result.Queued);
            Assert.Equal(1, result.Counts.MarchSlots);
        }
    }
    [InlineData("stable")]
    [InlineData("handoffs")]
    [InlineData("queued")]
    [Theory]
    public void EveryOutputCapacityRefusesBeforeWritingAnySpan(string shortSpan) {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Candidate("a", 0, 4), Candidate("c", 2, 3)]);
        Advance(allocator, 20, [Candidate("b", 1, 4), Candidate("d", 3, 3)]);
        var result = Read(allocator, 20);
        var stableSentinel = new WorldShadowSlot(9, Candidate("sentinel", 9, 1), 9);
        var handoffSentinel = Assert.Single(collection: result.Handoffs) with { Slot = 9 };
        var queuedSentinel = Assert.Single(collection: result.Queued) with { Slot = 9 };
        var stable = new[] { stableSentinel, stableSentinel };
        var handoffs = new[] { handoffSentinel };
        var queued = new[] { queuedSentinel };
        var refused = false;

        try {
            allocator.CopyTo(new PresentedTick(Fraction: 0, Whole: 20),
                stable.AsSpan(length: ((shortSpan == "stable") ? 1 : 2), start: 0),
                handoffs.AsSpan(length: ((shortSpan == "handoffs") ? 0 : 1), start: 0),
                queued.AsSpan(length: ((shortSpan == "queued") ? 0 : 1), start: 0));
        } catch (ArgumentException) {
            refused = true;
        }
        Assert.True(condition: refused);
        Assert.All(stable, value => Assert.Equal(actual: value, expected: stableSentinel));
        Assert.Equal(handoffSentinel, handoffs[0]);
        Assert.Equal(queuedSentinel, queued[0]);
    }
    [Fact]
    public void FadeStorageAndVisibilityControlsHaveTheDeclaredUnmanagedSizes() {
        Assert.Equal(32, Unsafe.SizeOf<WorldShadowFade>());
        Assert.Equal(16, Unsafe.SizeOf<SdfShadowHandoff>());
        Assert.False(condition: RuntimeHelpers.IsReferenceOrContainsReferences<WorldShadowFade>());
        Assert.False(condition: RuntimeHelpers.IsReferenceOrContainsReferences<SdfShadowHandoff>());
        Assert.True(condition: typeof(WorldShadowFade).IsLayoutSequential);
        Assert.True(condition: typeof(SdfShadowHandoff).IsLayoutSequential);
        Assert.Equal(actual: WorldShadowAllocator.MaxFadeSlots, expected: 2);
    }
    [Fact]
    public void ActiveFrozenReadsAllocateNothing() {
        var allocator = Crossing();
        var stable = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var handoffs = new WorldShadowHandoff[WorldShadowAllocator.MaxFadeSlots];
        var queued = new WorldShadowQueued[WorldShadowAllocator.MaxSlots];
        var result = default(WorldShadowReadout);
        var bytes = AllocationWindow.Least(() => {
            for (var frame = 0; (frame < 1000); frame++) {
                result = allocator.CopyTo(new PresentedTick(Fraction: 0.25, Whole: 23), stable, handoffs, queued);
            }
        });

        Assert.Equal(actual: bytes, expected: 0);
        Assert.Equal(new WorldShadowReadout(FadeCount: 1, QueuedCount: 0, StableCount: 1), result);
        Assert.Equal(0.40625, handoffs[0].Weight);
    }
    [Fact]
    public void NameReordersRemapBothSidesOfAnActiveFadeWithoutRestartingIt() {
        var allocator = Crossing();

        allocator.RemapLights(lights: [
            new WorldRenderLight.Directional(Name: "b", Shadow: WorldShadowMode.Auto),
            new WorldRenderLight.Directional(Name: "a", Shadow: WorldShadowMode.Auto),
        ]);
        var remapped = Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 23).Handoffs);

        Assert.Equal((1, 0), (remapped.Outgoing.LightIndex, remapped.Incoming.LightIndex));
        Assert.Equal(1, Assert.Single(collection: Read(allocator, 19).Stable).Candidate.LightIndex);
        Advance(allocator, 23, [Candidate("b", 0, 2), Candidate("a", 1, 1)], Policy with { Slots = 1 });
        var result = Read(allocator: allocator, fraction: 0.5, tick: 23);
        var handoff = Assert.Single(collection: result.Handoffs);

        Assert.Equal((0, "a", 1), (result.Stable[0].Slot, result.Stable[0].Candidate.Name, result.Stable[0].Candidate.LightIndex));
        Assert.Equal(("a", 1), (handoff.Outgoing.Name, handoff.Outgoing.LightIndex));
        Assert.Equal(("b", 0), (handoff.Incoming.Name, handoff.Incoming.LightIndex));
        Assert.Equal(20UL, handoff.CrossingTick);
        Assert.Equal(0.4375, handoff.Weight);
        Assert.Equal(0.1875, Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 21).Handoffs).Weight);
        Assert.Empty(collection: result.Queued);
    }
    [Fact]
    public void ReadingCompletionDoesNotRetireOrMutateTheDeliveredFade() {
        var allocator = Crossing();
        var before = Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 23).Handoffs);
        var completed = Read(allocator, 28);

        Assert.Empty(collection: completed.Handoffs);
        Assert.Equal("b", Assert.Single(collection: completed.Stable).Candidate.Name);
        Assert.Equal(before, Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 23).Handoffs));
        Advance(allocator, 23, [Candidate("a", 0, 1), Candidate("b", 1, 2)], Policy with { Slots = 1 });
        Assert.Equal(before, Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 23).Handoffs));
    }
    [InlineData(0f, 0.2f, 1f)]
    [InlineData(0.25f, 0.4f, 0.8f)]
    [InlineData(0.5f, 0.6f, 0.6f)]
    [InlineData(1f, 1f, 0.2f)]
    [Theory]
    public void AVisibilityBlendChangesOcclusionAndPreservesAnUnshadowedLight(float weight, float outgoing, float incoming) {
        var control = new SdfShadowHandoff(Incoming: 3, Outgoing: 2, Slot: 1, Weight: weight);

        Assert.InRange(MathF.Abs(x: (control.OutgoingVisibility(marched: 0.2f) - outgoing)), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(x: (control.IncomingVisibility(marched: 0.2f) - incoming)), 0f, 0.000001f);
        Assert.Equal(1f, control.OutgoingVisibility(marched: 1f));
        Assert.Equal(1f, control.IncomingVisibility(marched: 1f));
    }
    [Fact]
    public void AControlCarriesThePresentedWeightAndBothCurrentTableIndices() {
        var handoff = Assert.Single(collection: Read(Crossing(), 23, 0.5).Handoffs);

        Assert.Equal(new SdfShadowHandoff(Incoming: 1, Outgoing: 0, Slot: 0, Weight: 0.4375f), handoff.Control);
        Assert.Equal(WorldShadowHandoffFlags.Active | WorldShadowHandoffFlags.SelectionChanged, handoff.Flags);
    }
    [InlineData(8UL, true)]
    [InlineData(4UL, false)]
    [Theory]
    public void CompletionNearTheUnsignedTickLimitNeverWraps(ulong remainingTicks, bool completes) {
        var allocator = new WorldShadowAllocator();
        var crossing = (ulong.MaxValue - remainingTicks);
        var policy = Policy with { Slots = 1 };

        Advance(allocator, (crossing - 1), [Candidate("a", 0, 2), Candidate("b", 1, 1)], policy);
        Advance(allocator, crossing, [Candidate("a", 0, 1), Candidate("b", 1, 2)], policy);
        var result = Read(allocator, ulong.MaxValue);

        if (completes) {
            Assert.Empty(collection: result.Handoffs);
            Assert.Equal("b", Assert.Single(collection: result.Stable).Candidate.Name);
        } else {
            Assert.Equal(0.5, Assert.Single(collection: result.Handoffs).Weight);
            Assert.Equal("a", Assert.Single(collection: result.Stable).Candidate.Name);
        }
    }
    [Fact]
    public void QueueWaitsForTheOccupiedSlotUntilTheFirstEligibleDelivery() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1, FadeSlots = 2 };

        Advance(allocator, 10, [Candidate("a", 0, 3), Candidate("b", 1, 2), Candidate("c", 2, 1)], policy);
        Advance(allocator, 20, [Candidate("b", 1, 3), Candidate("a", 0, 2), Candidate("c", 2, 1)], policy);
        var desired = new[] { Candidate("c", 2, 3), Candidate("b", 1, 2), Candidate("a", 0, 1) };

        foreach (var tick in new[] { 21UL, 27UL }) {
            Advance(allocator, tick, desired, policy);
            var waiting = Read(allocator, tick);
            var queued = Assert.Single(collection: waiting.Queued);

            Assert.Equal((0, "c", WorldShadowQueueReason.SlotInHandoff), (queued.Slot, queued.Incoming.Name, queued.Reason));
            Assert.Equal(("a", "b"), (Assert.Single(collection: waiting.Handoffs).Outgoing.Name, waiting.Handoffs[0].Incoming.Name));
            Assert.True(condition: (waiting.Counts.FadeCount < policy.FadeSlots));
        }
        for (var frame = 0; (frame < 3); frame++) {
            var presented = Read(allocator: allocator, fraction: 0.5, tick: 30);

            Assert.Empty(collection: presented.Handoffs);
            Assert.Equal("b", Assert.Single(collection: presented.Stable).Candidate.Name);
            Assert.Equal("c", Assert.Single(collection: presented.Queued).Incoming.Name);
            Assert.Equal(0.9375, Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 27).Handoffs).Weight);
        }
        Advance(allocator, 28, desired, policy);
        var started = Read(allocator, 28);
        var handoff = Assert.Single(collection: started.Handoffs);

        Assert.Equal((0, "b", "c", 28UL), (handoff.Slot, handoff.Outgoing.Name, handoff.Incoming.Name, handoff.CrossingTick));
        Assert.Equal(0d, handoff.Weight);
        Assert.True(condition: ((handoff.Flags & WorldShadowHandoffFlags.FromQueue) != 0));
        Assert.Empty(collection: started.Queued);
        Assert.Equal(2, started.Counts.MarchSlots);
    }
    [Fact]
    public void QueueWaitsForAnOutgoingIdentityWithSpareFadeCapacity() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { FadeSlots = 2 };

        Advance(allocator, 10, [Candidate("a", 0, 3), Candidate("c", 2, 2), Candidate("b", 1, 1)], policy);
        Advance(allocator, 20, [Candidate("b", 1, 3), Candidate("c", 2, 2), Candidate("a", 0, 1)], policy);
        var desired = new[] { Candidate("a", 0, 3), Candidate("b", 1, 2), Candidate("c", 2, 1) };

        foreach (var tick in new[] { 21UL, 27UL }) {
            Advance(allocator, tick, desired, policy);
            var waiting = Read(allocator, tick);
            var queued = Assert.Single(collection: waiting.Queued);

            Assert.Equal((1, "a", WorldShadowQueueReason.IdentityInUse), (queued.Slot, queued.Incoming.Name, queued.Reason));
            Assert.Equal("b", Assert.Single(collection: waiting.Handoffs).Incoming.Name);
            Assert.True(condition: (waiting.Counts.FadeCount < policy.FadeSlots));
        }
        for (var frame = 0; (frame < 3); frame++) {
            var presented = Read(allocator: allocator, fraction: 0.5, tick: 30);

            Assert.Empty(collection: presented.Handoffs);
            Assert.Equal(new[] { "b", "c" }, presented.Stable.Select(selector: value => value.Candidate.Name));
            Assert.Equal("a", Assert.Single(collection: presented.Queued).Incoming.Name);
            Assert.Equal(0.9375, Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 27).Handoffs).Weight);
        }
        Advance(allocator, 28, desired, policy);
        var started = Read(allocator, 28);
        var handoff = Assert.Single(collection: started.Handoffs);

        Assert.Equal((1, "c", "a", 28UL), (handoff.Slot, handoff.Outgoing.Name, handoff.Incoming.Name, handoff.CrossingTick));
        Assert.Equal(0d, handoff.Weight);
        Assert.True(condition: ((handoff.Flags & WorldShadowHandoffFlags.FromQueue) != 0));
        Assert.Empty(collection: started.Queued);
        Assert.Equal(3, started.Counts.MarchSlots);
        Assert.Equal(3, started.Stable.Select(selector: value => value.Candidate.Name)
            .Concat(second: started.Handoffs.Select(selector: value => value.Incoming.Name)).Distinct().Count());
    }
}
