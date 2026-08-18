using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins every binding-bar authoring refusal to its field name and a one-value-different passing control.</summary>
public sealed class BindingBarAuthoringValidationLawTests {
    public static IEnumerable<object[]> Cases() {
        var layout = WorldBindingBarLayout.Default;

        yield return ["visible.windowSeconds", new WorldBindingBarAuthoring(Visible: new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: -1f)), new WorldBindingBarAuthoring(Visible: new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: 3f))];
        yield return ["visible.predicates[0].windowSeconds", new WorldBindingBarAuthoring(Visible: new OverlayPredicate.Any(Predicates: [new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: float.NaN)])), new WorldBindingBarAuthoring(Visible: new OverlayPredicate.Any(Predicates: [new OverlayPredicate.Now(Fact: OverlayFact.WheelOpen)]))];
        yield return ["layout.buttonSize", Policy(layout: layout with { ButtonSize = 0f }), Policy(layout: layout with { ButtonSize = 0.01f })];
        yield return ["layout.centerGap", Policy(layout: layout with { CenterGap = -0.01f }), Policy(layout: layout with { CenterGap = 0f })];
        yield return ["layout.anchorOffsetY", Policy(layout: layout with { AnchorOffsetY = 1.01f }), Policy(layout: layout with { AnchorOffsetY = 1f })];
        yield return ["layout.glyphOffsetRatio", Policy(layout: layout with { GlyphOffsetRatio = -0.01f }), Policy(layout: layout with { GlyphOffsetRatio = 0f })];
        yield return ["layout.glyphSizeRatio", Policy(layout: layout with { GlyphSizeRatio = 0f }), Policy(layout: layout with { GlyphSizeRatio = 0.01f })];
        yield return ["layout.scale", Policy(layout: layout with { Scale = 0f }), Policy(layout: layout with { Scale = 0.01f })];
    }
    [MemberData(nameof(Cases))]
    [Theory]
    public void InvalidValueRefusesByNameBesidePassingControl(string field, WorldBindingBarAuthoring invalid, WorldBindingBarAuthoring control) {
        var denied = WithPolicy(policy: invalid);
        var admitted = WithPolicy(policy: control);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"bindingOverlays[0].bindingBar.{field}");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldBindingBarAuthoring Policy(WorldBindingBarLayout layout) => new(Layout: layout);
    private static WorldDefinition WithPolicy(WorldBindingBarAuthoring policy) => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "binding-bar-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: []
                ),
                BindingBar: policy
            ),
        ],
    };
}
