using Puck.Abstractions.Presentation;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the render ceilings and the <c>views.quality</c> rows are refused by name when they cannot
/// apply: a ceiling outside [0.125, 1], a view named twice, a tier no preset authors, and a row that names no render
/// view, which would otherwise never match anything and silently do nothing.
/// </summary>
public sealed class WorldViewQualityLawTests {
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    private static WorldDefinition Rows(params WorldViewQuality[] rows) => new(ViewsRaw: new WorldViewDefaults(Quality: rows));

    [Fact]
    public void RowsThatApplyAreAdmitted() {
        Assert.Equal(string.Empty, Validate(definition: Rows(
            new WorldViewQuality(Name: "*", RenderScale: 0.5f),
            new WorldViewQuality(Name: "world", RenderScaleFloor: WorldRenderScaleTier.Eighth),
            new WorldViewQuality(Name: "world$2"),
            new WorldViewQuality(Name: "session$0", RenderScale: 1f))));
    }
    [Fact]
    public void ARowNamingNoRenderViewIsRefusedByName() {
        Assert.Contains("views.quality row 'wolrd' names no render view", Validate(definition: Rows(new WorldViewQuality(Name: "wolrd", RenderScale: 0.5f))));
    }
    [Fact]
    public void ARepeatedViewIsRefusedByName() {
        Assert.Contains("views.quality repeats view 'world'", Validate(definition: Rows(new WorldViewQuality(Name: "world"), new WorldViewQuality(Name: "world"))));
    }
    [InlineData(0.1f)]
    [InlineData(1.5f)]
    [Theory]
    public void ACeilingOutsideTheScalarRangeIsRefusedByName(float scale) {
        Assert.Contains("views.quality[world].renderScale", Validate(definition: Rows(new WorldViewQuality(Name: "world", RenderScale: scale))));
        Assert.Contains("render.renderScale", Validate(definition: new WorldDefinition(RenderRaw: new WorldRenderDefaults(RenderScale: scale))));
    }
    [Fact]
    public void ATierWithoutAPresetIsRefusedByName() {
        Assert.Contains("views.quality[world].tier names no High preset", Validate(definition: Rows(new WorldViewQuality(Name: "world", Tier: QualityTier.High))));
    }
}
