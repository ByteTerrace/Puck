using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins every binding-bar authoring refusal to its field name and a one-value-different passing control.</summary>
public sealed class BindingBarAuthoringValidationLawTests {
    public static IEnumerable<object[]> Cases() {
        var layout = WorldBindingBarLayout.Default;

        yield return ["visible.windowSeconds", Policy(layout: layout) with { Visible = new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: -1f) }, Policy(layout: layout) with { Visible = new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: 3f) }];
        yield return ["visible.predicates[0].windowSeconds", Policy(layout: layout) with { Visible = new OverlayPredicate.Any(Predicates: [new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: float.NaN)]) }, Policy(layout: layout) with { Visible = new OverlayPredicate.Any(Predicates: [new OverlayPredicate.Now(Fact: OverlayFact.WheelOpen)]) }];
        yield return ["layout.buttonSize", Policy(layout: layout with { ButtonSize = 0f }), Policy(layout: layout with { ButtonSize = 0.01f })];
        yield return ["layout.centerGap", Policy(layout: layout with { CenterGap = -0.01f }), Policy(layout: layout with { CenterGap = 0f })];
        yield return ["layout.anchorOffsetY", Policy(layout: layout with { AnchorOffsetY = 1.01f }), Policy(layout: layout with { AnchorOffsetY = 1f })];
        yield return ["layout.glyphOffsetRatio", Policy(layout: layout with { GlyphOffsetRatio = -0.01f }), Policy(layout: layout with { GlyphOffsetRatio = 0f })];
        yield return ["layout.glyphSizeRatio", Policy(layout: layout with { GlyphSizeRatio = 0f }), Policy(layout: layout with { GlyphSizeRatio = 0.01f })];
        yield return ["layout.scale", Policy(layout: layout with { Scale = 0f }), Policy(layout: layout with { Scale = 0.01f })];
        yield return ["slotSet[0]", Policy(layout: layout) with { SlotSet = ["NotARealButton"] }, Policy(layout: layout) with { SlotSet = ["DpadUp"] }];
        yield return ["slotSet[1]", Policy(layout: layout) with { SlotSet = ["DpadUp", "DpadUp"] }, Policy(layout: layout) with { SlotSet = ["DpadUp", "DpadRight"] }];
        yield return ["banks must declare at least one bank", Policy(layout: layout) with { Banks = [] }, Policy(layout: layout)];
        yield return ["banks declares 6 entries", Policy(layout: layout) with { Banks = [.. Enumerable.Repeat(element: OneBank[0], count: 6)] }, Policy(layout: layout)];
        yield return ["banks[1].id", Policy(layout: layout) with { Banks = [OneBank[0], OneBank[0]] }, Policy(layout: layout) with { Banks = [OneBank[0], (OneBank[0] with { Id = "second" })] }];
        yield return ["banks[0].pageId 'no-such-page'", Policy(layout: layout) with { Banks = [OneBank[0] with { PageId = "no-such-page" }] }, Policy(layout: layout)];
        yield return ["banks[0].alpha", Policy(layout: layout) with { Banks = [OneBank[0] with { Alpha = 1.5f }] }, Policy(layout: layout)];
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

    private static readonly WorldBindingBarBank[] OneBank = [new WorldBindingBarBank(Id: "resting", PageId: "base", OffsetX: 0f, OffsetY: 0f, Alpha: 1f)];

    private static WorldBindingBarAuthoring Policy(WorldBindingBarLayout layout) => new(
        Banks: OneBank,
        Layout: layout,
        SlotSet: ["DpadUp"]
    );
    private static WorldDefinition WithPolicy(WorldBindingBarAuthoring policy) => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "binding-bar-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [
                        new BindingChordDefinition(
                            Group: "bindingBarLaw",
                            Page: new BindingPageDefinition(Id: "base", Entries: [])
                        ),
                    ]
                ),
                BindingBar: policy
            ),
        ],
    };
}
