using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins every binding-bar authoring refusal to its field name and a one-value-different passing control.</summary>
public sealed class BindingBarAuthoringValidationLawTests {
    public static IEnumerable<object[]> Cases() {
        var layout = WorldBindingBarLayout.Default;

        yield return ["hideAfterRestSeconds", new WorldBindingBarAuthoring(HideAfterRestSeconds: -1f), new WorldBindingBarAuthoring(HideAfterRestSeconds: 0f)];
        yield return ["layout.buttonSize", Policy(layout with { ButtonSize = 0f }), Policy(layout with { ButtonSize = 0.01f })];
        yield return ["layout.centerGap", Policy(layout with { CenterGap = -0.01f }), Policy(layout with { CenterGap = 0f })];
        yield return ["layout.anchorOffsetY", Policy(layout with { AnchorOffsetY = 1.01f }), Policy(layout with { AnchorOffsetY = 1f })];
        yield return ["layout.glyphOffsetRatio", Policy(layout with { GlyphOffsetRatio = -0.01f }), Policy(layout with { GlyphOffsetRatio = 0f })];
        yield return ["layout.glyphSizeRatio", Policy(layout with { GlyphSizeRatio = 0f }), Policy(layout with { GlyphSizeRatio = 0.01f })];
        yield return ["layout.scale", Policy(layout with { Scale = 0f }), Policy(layout with { Scale = 0.01f })];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void InvalidValueRefusesByNameBesidePassingControl(string field, WorldBindingBarAuthoring invalid, WorldBindingBarAuthoring control) {
        var denied = WithPolicy(policy: invalid);
        var admitted = WithPolicy(policy: control);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, reason: out var deniedReason, neighbours: null));
        Assert.Contains(expectedSubstring: $"bindingOverlays[0].bindingBar.{field}", actualString: deniedReason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, reason: out var controlReason, neighbours: null), userMessage: controlReason);
    }

    private static WorldBindingBarAuthoring Policy(WorldBindingBarLayout layout) => new(Layout: layout);

    private static WorldDefinition WithPolicy(WorldBindingBarAuthoring policy) => Fixtures.BuildDocument() with {
        BindingOverlays = [
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
