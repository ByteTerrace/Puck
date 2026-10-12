using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: one render-scale grammar controls durable scalar ceilings and per-view tier floors,
/// while a bindable pin is bounded, allocates nothing during a sweep, resumes within one policy step and is never saved.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldRenderScaleGrammarLawTests {
    [Fact]
    public void SessionQualitySurvivesAnotherReaderOfTheSameDocumentQuality() {
        var rows = new[] { new WorldViewQuality(Name: "world", RenderScale: 0.5f) };
        var first = (Fixtures.BuildDocument() with { ViewsRaw = new WorldViewDefaults(Quality: rows) });
        var copy = (first with { ViewsRaw = new WorldViewDefaults(Quality: [.. rows]) });
        var revised = (first with { ViewsRaw = new WorldViewDefaults(Quality: [new WorldViewQuality(Name: "world", RenderScale: 0.75f)]) });
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults());

        settings.ReadQuality(definition: first);
        Assert.True(condition: settings.SetResolution(operation: WorldRenderScaleOperation.Floor, refusal: out _,
            scale: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Eighth), view: "world"));
        settings.ReadQuality(definition: copy);
        settings.ReadQuality(definition: first);
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Eighth), settings.Floor(view: "world"));
        Assert.Equal(0.5f, settings.Ceiling(view: "world"));
        settings.ReadQuality(definition: revised);
        Assert.Equal(0.75f, settings.Ceiling(view: "world"));
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter), settings.Floor(view: "world"));
    }
}
