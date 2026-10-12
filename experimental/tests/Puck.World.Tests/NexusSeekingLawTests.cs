using Xunit;

namespace Puck.World.Tests;

/// <summary>The seek law of <see cref="InSessionHistoryLawTests"/> over the shipped Nexus world, whose tick costs about
/// eighty milliseconds, one seed to a class. Each concrete class is a collection of its own, so the seeds run side by
/// side instead of one after another; the law each runs is the one the other worlds' theory rows run.</summary>
public abstract class NexusSeekingLawTests(ulong seed) {
    [Fact]
    public void SeekingBackThenForwardReproducesEveryRecordedTick() => InSessionHistoryLawTests.SeekBackThenForward(
        seed: seed,
        world: "nexus"
    );
}
/// <summary>The seek law over Nexus with the input stream seeded 3.</summary>
public sealed class NexusSeekingSeed3LawTests() : NexusSeekingLawTests(seed: 3UL);
/// <summary>The seek law over Nexus with the input stream seeded 77.</summary>
public sealed class NexusSeekingSeed77LawTests() : NexusSeekingLawTests(seed: 77UL);
/// <summary>The seek law over Nexus with the input stream seeded 0x5EED.</summary>
public sealed class NexusSeekingSeed5EEDLawTests() : NexusSeekingLawTests(seed: 0x5EEDUL);
