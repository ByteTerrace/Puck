using Puck.Overlays;
using Xunit;

namespace Puck.World.Tests;

public sealed class HudFrameSourceGenerationLawTests {
    [Fact]
    public void ASourceRetainsAcrossVisibleGenerationsAndReleasesWhenAbsent() {
        var events = new List<string>();
        var generation = NewGeneration(events: events);

        generation.BeginGeneration();
        generation.MarkActive(key: 2);
        generation.EndGeneration();
        generation.BeginGeneration();
        generation.MarkActive(key: 2);
        generation.EndGeneration();
        generation.BeginGeneration();
        generation.EndGeneration();

        Assert.Equal(expected: ["retain:2", "release:2"], actual: events);
        Assert.False(condition: generation.IsActive(key: 2));
    }

    [Fact]
    public void RepeatedElementsShareOneGenerationReference() {
        var events = new List<string>();
        var generation = NewGeneration(events: events);

        generation.BeginGeneration();
        generation.MarkActive(key: 0);
        generation.MarkActive(key: 0);
        generation.MarkActive(key: 1);
        generation.EndGeneration();
        generation.BeginGeneration();
        generation.MarkActive(key: 1);
        generation.EndGeneration();

        Assert.Equal(expected: ["retain:0", "retain:1", "release:0"], actual: events);
        Assert.True(condition: generation.IsActive(key: 1));
    }

    [Fact]
    public void ASourceReturningAfterRetirementIsRetainedAgain() {
        var events = new List<string>();
        var generation = NewGeneration(events: events);

        generation.BeginGeneration();
        generation.MarkActive(key: 3);
        generation.EndGeneration();
        generation.BeginGeneration();
        generation.EndGeneration();
        generation.BeginGeneration();
        generation.MarkActive(key: 3);
        generation.EndGeneration();

        Assert.Equal(expected: ["retain:3", "release:3", "retain:3"], actual: events);
    }

    private static OverlayFrameSourceGeneration NewGeneration(List<string> events) => new(
        retain: key => events.Add(item: $"retain:{key}"),
        release: key => events.Add(item: $"release:{key}")
    );
}
