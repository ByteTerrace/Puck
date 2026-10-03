using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Name-keyed shadow selection, bounded fades and atomic instant publication.</summary>
public sealed class ShadowSlotLawTests {
    private static readonly WorldShadowSettings Policy = new(FadeSlots: 0, FadeTicks: 0, Overflow: WorldShadowOverflow.Instant, Slots: 2);

    private static WorldShadowCandidate Candidate(string name, int ordinal, double luminance,
        WorldShadowMode mode = WorldShadowMode.Auto, int? lightIndex = null) =>
        new(LightIndex: (lightIndex ?? ordinal), ListOrdinal: ordinal, Luminance: luminance, Mode: mode, Name: name);
    private static (WorldShadowReadout Counts, WorldShadowSlot[] Stable, WorldShadowHandoff[] Handoffs, WorldShadowQueued[] Queued) Read(WorldShadowAllocator allocator,
        ulong tick, double fraction = 0) {
        var stable = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var handoffs = new WorldShadowHandoff[WorldShadowAllocator.MaxFadeSlots];
        var queued = new WorldShadowQueued[WorldShadowAllocator.MaxSlots];
        var counts = allocator.CopyTo(new PresentedTick(Fraction: fraction, Whole: tick), stable, handoffs, queued);

        return (counts, stable[..counts.StableCount], handoffs[..counts.FadeCount], queued[..counts.QueuedCount]);
    }
    private static void Advance(WorldShadowAllocator allocator, ulong tick, WorldShadowCandidate[] candidates,
        WorldShadowSettings? policy = null, bool discontinuity = false, long revision = 0) =>
        allocator.Advance(tick, revision, discontinuity, candidates, (policy ?? Policy));

    [Fact]
    public void NeverLightsLeaveAvailableSlotsVacant() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Candidate("never", 0, 100, WorldShadowMode.Never)]);
        var result = Read(allocator, 10);

        Assert.Empty(collection: result.Stable);
        Assert.Equal(actual: result.Counts, expected: default);
    }
    [Fact]
    public void AlwaysLightsWinBeforeBrighterAutomaticLightsAndNeverLightsConsumeNothing() {
        var allocator = new WorldShadowAllocator();
        var candidates = new[] {
            Candidate("never", 0, 100, WorldShadowMode.Never), Candidate("auto", 1, 99),
            Candidate("second", 3, 0, WorldShadowMode.Always), Candidate("first", 2, 0, WorldShadowMode.Always),
        };

        Advance(allocator, 10, candidates);
        var result = Read(allocator, 10);

        Assert.Equal(new[] { "first", "second" }, result.Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(new[] { 1, 2 }, result.Stable.Select(selector: value => value.Rank));
        Assert.Equal(2, result.Counts.MarchSlots);
        Advance(allocator, 11, candidates, Policy with { Slots = 0, FadeSlots = 0, FadeTicks = 0, Overflow = WorldShadowOverflow.Instant });
        Assert.Equal(default, Read(allocator, 11).Counts);
        Assert.Empty(collection: Read(allocator, 11).Stable);
    }
    [Fact]
    public void TiesUseAuthoredOrderWhileSelectedIdentitiesKeepTheirExistingSlots() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Candidate("late", 2, 3), Candidate("early", 1, 3), Candidate("third", 3, 2)]);
        var first = Read(allocator, 10);

        Advance(allocator, 11, [Candidate("late", 2, 10), Candidate("early", 1, 3), Candidate("third", 3, 2)]);
        var second = Read(allocator, 11);

        Assert.Equal(new[] { "early", "late" }, first.Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(first.Stable.Select(selector: value => (value.Slot, value.Candidate.Name)),
            second.Stable.Select(selector: value => (value.Slot, value.Candidate.Name)));
        Assert.Equal(new[] { 2, 1 }, second.Stable.Select(selector: value => value.Rank));
        Assert.Equal(0, second.Counts.FadeCount);
    }
    [Fact]
    public void ReorderingNamedLightsUpdatesTableIndicesWithoutCrossingSlots() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Candidate("a", 0, 3), Candidate("b", 1, 2)]);
        Advance(allocator, 11, [Candidate("b", 0, 4), Candidate("a", 1, 3)]);
        var result = Read(allocator, 11);

        Assert.Equal(new[] { (0, "a", 1, 2), (1, "b", 0, 1) },
            result.Stable.Select(selector: value => (value.Slot, value.Candidate.Name, value.Candidate.LightIndex, value.Rank)));
        Assert.Equal(0, result.Counts.FadeCount);
        Assert.Equal(0, result.Counts.QueuedCount);
    }
    [Fact]
    public void RepeatedDeliveryDoesNotDetectAFractionalCrossing() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1, FadeSlots = 1, FadeTicks = 8, Overflow = WorldShadowOverflow.Queue };

        Advance(allocator, 10, [Candidate("a", 0, 2), Candidate("b", 1, 1)], policy);
        Advance(allocator, 10, [Candidate("a", 0, 1), Candidate("b", 1, 2)], policy);

        Assert.Equal("a", Assert.Single(collection: Read(allocator: allocator, fraction: 0.75, tick: 10).Stable).Candidate.Name);
        Assert.Empty(collection: Read(allocator: allocator, fraction: 0.75, tick: 10).Handoffs);
        Advance(allocator, 11, [Candidate("a", 0, 1), Candidate("b", 1, 2)], policy);
        Assert.Equal("a", Assert.Single(collection: Read(allocator: allocator, fraction: 0.999, tick: 10).Stable).Candidate.Name);
        Assert.Equal("a", Assert.Single(collection: Read(allocator, 11).Stable).Candidate.Name);
        var handoff = Assert.Single(collection: Read(allocator, 11).Handoffs);

        Assert.Equal("b", handoff.Incoming.Name);
        Assert.Equal(11UL, handoff.CrossingTick);
    }
    [Fact]
    public void ReplayAndFrozenReadsKeepExactTickBoundariesBeyondTwoToThe53() {
        const ulong Origin = (1UL << 54);
        var first = new WorldShadowAllocator();
        var replay = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        foreach (var allocator in new[] { first, replay }) {
            Advance(allocator, Origin, [Candidate("a", 0, 2), Candidate("b", 1, 1)], policy);
            Advance(allocator, (Origin + 1), [Candidate("a", 0, 1), Candidate("b", 1, 2)], policy);
        }

        for (var frame = 0; (frame < 33); frame++) {
            var before = Read(allocator: first, fraction: 0.75, tick: Origin);
            var after = Read(allocator: first, fraction: 0.75, tick: (Origin + 1));

            Assert.Equal("a", Assert.Single(collection: before.Stable).Candidate.Name);
            Assert.Equal("b", Assert.Single(collection: after.Stable).Candidate.Name);
            Assert.Equal(before.Stable, Read(allocator: replay, fraction: 0.75, tick: Origin).Stable);
            Assert.Equal(after.Stable, Read(allocator: replay, fraction: 0.75, tick: (Origin + 1)).Stable);
            Assert.Equal(new WorldShadowReadout(FadeCount: 0, QueuedCount: 0, StableCount: 1), after.Counts);
        }
    }
    [Fact]
    public void AForwardDeliveryGapPreservesTheEarlierPresentedInterval() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1 };

        Advance(allocator, 10, [Candidate("a", 0, 3)], policy);
        Advance(allocator, 30, [Candidate("b", 1, 3)], policy);

        Assert.Equal("a", Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 25).Stable).Candidate.Name);
        Assert.Equal("b", Assert.Single(collection: Read(allocator, 30).Stable).Candidate.Name);
        Advance(allocator, 40, [Candidate("c", 2, 3)], policy);
        Assert.Equal("b", Assert.Single(collection: Read(allocator: allocator, fraction: 0.5, tick: 35).Stable).Candidate.Name);
        Assert.Equal("c", Assert.Single(collection: Read(allocator, 40).Stable).Candidate.Name);
    }
    [Fact]
    public void AnOverlappingInstantHandoffReleasesAndAcquiresAtomically() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { FadeSlots = 1, FadeTicks = 8 };

        Advance(allocator, 10, [Candidate("a", 0, 4), Candidate("c", 2, 3), Candidate("b", 1, 2)], policy);
        Advance(allocator, 20, [Candidate("b", 1, 4), Candidate("c", 2, 3), Candidate("a", 0, 2)], policy);
        Assert.Equal(new[] { "a", "c" }, Read(allocator, 20).Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal("b", Assert.Single(collection: Read(allocator, 20).Handoffs).Incoming.Name);
        Advance(allocator, 21, [Candidate("a", 0, 4), Candidate("b", 1, 3), Candidate("c", 2, 2)], policy);

        Assert.Equal(new[] { "a", "c" }, Read(allocator: allocator, fraction: 0.999, tick: 20).Stable.Select(selector: value => value.Candidate.Name));
        for (var frame = 0; (frame < 5); frame++) {
            var result = Read(allocator: allocator, fraction: (frame / 5d), tick: 21);

            Assert.Equal(new[] { (0, "b"), (1, "a") }, result.Stable.Select(selector: value => (value.Slot, value.Candidate.Name)));
            Assert.Equal(new WorldShadowReadout(FadeCount: 0, QueuedCount: 0, StableCount: 2), result.Counts);
        }
    }
    [Fact]
    public void ASelectedAlwaysLightRetainsItsSlotAcrossRepeatedAutomaticCrossings() {
        var allocator = new WorldShadowAllocator();
        var pinned = Candidate("pinned", 7, 0, WorldShadowMode.Always);

        Advance(allocator, 10, [pinned, Candidate("a", 0, 4)]);
        Advance(allocator, 20, [pinned, Candidate("b", 1, 5), Candidate("a", 0, 4)]);
        Advance(allocator, 21, [pinned, Candidate("c", 2, 6), Candidate("b", 1, 5)]);
        var result = Read(allocator, 21);

        Assert.Equal(new[] { (0, "pinned"), (1, "c") }, result.Stable.Select(selector: value => (value.Slot, value.Candidate.Name)));
        Assert.Equal(new WorldShadowReadout(FadeCount: 0, QueuedCount: 0, StableCount: 2), result.Counts);
    }
    [InlineData("seek")]
    [InlineData("revision")]
    [InlineData("backward")]
    [InlineData("policy")]
    [Theory]
    public void ADiscontinuityInstallsTheCurrentSelectionWithoutAnEarlierAssignment(string reset) {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Candidate("a", 0, 3), Candidate("b", 1, 2)]);
        var tick = ((reset == "backward") ? 5UL : 11UL);
        var policy = ((reset == "policy") ? Policy with { FadeSlots = 2, FadeTicks = 8, Overflow = WorldShadowOverflow.Queue } : Policy);

        Advance(allocator, tick, [Candidate("b", 1, 4), Candidate("a", 0, 3)], policy,
            discontinuity: (reset == "seek"), revision: ((reset == "revision") ? 1 : 0));

        Assert.Equal(new[] { "a", "b" }, Read(allocator, tick).Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(new[] { "a", "b" }, Read(allocator, 0).Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(new WorldShadowReadout(FadeCount: 0, QueuedCount: 0, StableCount: 2), Read(allocator, tick).Counts);
    }
    [Fact]
    public void AnInstantTierNeedsNoAdditionalMarchAndSteadyReadsAllocateNothing() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 1, FadeSlots = 0, FadeTicks = 0, Overflow = WorldShadowOverflow.Instant };
        var candidates = new[] { Candidate("a", 0, 1) };
        var stable = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var handoffs = new WorldShadowHandoff[WorldShadowAllocator.MaxFadeSlots];
        var queued = new WorldShadowQueued[WorldShadowAllocator.MaxSlots];

        Advance(allocator, 1, [Candidate("b", 1, 1)], policy);
        Advance(allocator, 2, candidates, policy);
        Assert.Equal("a", Assert.Single(collection: Read(allocator, 2).Stable).Candidate.Name);
        var result = default(WorldShadowReadout);

        var bytes = AllocationWindow.Least(window: () => {
            for (ulong tick = 3; (tick < 303); tick++) {
                allocator.Advance(tick, 0, false, candidates, policy);
                result = allocator.CopyTo(new PresentedTick(Fraction: 0.5, Whole: tick), stable, handoffs, queued);
            }
        });

        Assert.Equal(actual: bytes, expected: 0);
        Assert.Equal(new WorldShadowReadout(FadeCount: 0, QueuedCount: 0, StableCount: 1), result);
        Assert.Equal("a", stable[0].Candidate.Name);
    }
    [Fact]
    public void TooSmallOutputRefusesBeforeWritingAnySlot() {
        var allocator = new WorldShadowAllocator();

        Advance(allocator, 10, [Candidate("a", 0, 3), Candidate("b", 1, 2)]);
        var sentinel = new WorldShadowSlot(Slot: 9, Candidate: Candidate("sentinel", 9, 1), Rank: 9);
        var output = new[] { sentinel };
        var refused = false;

        try {
            allocator.CopyTo(new PresentedTick(Fraction: 0, Whole: 10), output, [], []);
        } catch (ArgumentException) {
            refused = true;
        }

        Assert.True(condition: refused);
        Assert.Equal(sentinel, output[0]);
    }
    [Fact]
    public void FourStableSlotsAndTwoFadesRespectTheFullPolicyBoundAndDrain() {
        var allocator = new WorldShadowAllocator();
        var policy = Policy with { Slots = 4, FadeSlots = 2, FadeTicks = 8, Overflow = WorldShadowOverflow.Queue };
        var target = new[] { Candidate("b", 1, 8), Candidate("d", 3, 7), Candidate("f", 5, 6), Candidate("h", 7, 5),
            Candidate("a", 0, 4), Candidate("c", 2, 3), Candidate("e", 4, 2), Candidate("g", 6, 1) };

        Advance(allocator, 10, [Candidate("a", 0, 4), Candidate("c", 2, 3), Candidate("e", 4, 2), Candidate("g", 6, 1)], policy);
        Advance(allocator, 20, target, policy);
        var result = Read(allocator, 20);

        Assert.Equal(new[] { "a", "c", "e", "g" }, result.Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(new WorldShadowReadout(FadeCount: 2, QueuedCount: 2, StableCount: 4), result.Counts);
        Assert.Equal(6, result.Counts.MarchSlots);
        Advance(allocator, 28, target, policy);
        Assert.Equal(6, Read(allocator, 28).Counts.MarchSlots);
        Advance(allocator, 36, target, policy);
        result = Read(allocator, 36);
        Assert.Equal(new[] { "b", "d", "f", "h" }, result.Stable.Select(selector: value => value.Candidate.Name));
        Assert.Equal(new WorldShadowReadout(FadeCount: 0, QueuedCount: 0, StableCount: 4), result.Counts);
    }
}
