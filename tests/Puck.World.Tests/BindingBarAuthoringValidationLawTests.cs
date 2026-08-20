using Puck.Commands;
using Puck.Input;
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
        yield return ["layout.centerRowLift", Policy(layout: layout with { CenterRowLift = -0.01f }), Policy(layout: layout with { CenterRowLift = 1.9f })];
        yield return ["layout.centerSlotSpacing", Policy(layout: layout with { CenterSlotSpacing = 0f }), Policy(layout: layout with { CenterSlotSpacing = 1.15f })];
        yield return ["layout.exoticRowLift", Policy(layout: layout with { ExoticRowLift = float.NaN }), Policy(layout: layout with { ExoticRowLift = 3.6f })];
        yield return ["layout.exoticSlotSpacing", Policy(layout: layout with { ExoticSlotSpacing = 0f }), Policy(layout: layout with { ExoticSlotSpacing = 1.15f })];
        yield return ["layout.badgeCorner", Policy(layout: layout with { BadgeCorner = float.NaN }), Policy(layout: layout with { BadgeCorner = -1f })];
        yield return ["layout.modifierHalfRatio", Policy(layout: layout with { ModifierHalfRatio = 0f }), Policy(layout: layout with { ModifierHalfRatio = 0.35f })];
        yield return ["layout.modifierSpacingRatio", Policy(layout: layout with { ModifierSpacingRatio = 0f }), Policy(layout: layout with { ModifierSpacingRatio = 1.1f })];
        yield return ["layout.modifierGlyphRatio", Policy(layout: layout with { ModifierGlyphRatio = 0f }), Policy(layout: layout with { ModifierGlyphRatio = 0.8f })];
        yield return ["layout.labelCellRatio", Policy(layout: layout with { LabelCellRatio = 0f }), Policy(layout: layout with { LabelCellRatio = 1.9f })];
        yield return ["layout.labelCellMinPx", Policy(layout: layout with { LabelCellMinPx = 0f }), Policy(layout: layout with { LabelCellMinPx = 12f })];
        yield return ["layout.labelGapRatio", Policy(layout: layout with { LabelGapRatio = float.NaN }), Policy(layout: layout with { LabelGapRatio = 1.4f })];
        yield return ["layout.hintCellRatio", Policy(layout: layout with { HintCellRatio = 0f }), Policy(layout: layout with { HintCellRatio = 1.6f })];
        yield return ["layout.hintCellMinPx", Policy(layout: layout with { HintCellMinPx = 0f }), Policy(layout: layout with { HintCellMinPx = 10f })];
        yield return ["layout.hintLineStepRatio", Policy(layout: layout with { HintLineStepRatio = 0f }), Policy(layout: layout with { HintLineStepRatio = 1.3f })];
        yield return ["layout.hintBaseGapRatio", Policy(layout: layout with { HintBaseGapRatio = float.NaN }), Policy(layout: layout with { HintBaseGapRatio = 2.2f })];
        yield return ["slotSet[0]", Policy(layout: layout) with { SlotSet = ["NotARealSource"] }, Policy(layout: layout) with { SlotSet = [InputSources.Gamepad.DpadUp] }];
        yield return ["slotSet[1]", Policy(layout: layout) with { SlotSet = [InputSources.Gamepad.DpadUp, InputSources.Gamepad.DpadUp] }, Policy(layout: layout) with { SlotSet = [InputSources.Gamepad.DpadUp, InputSources.Gamepad.DpadRight] }];
        yield return [$"slotSet declares {(WorldBindingBarCapacity.MaxSlots + 1)} entries", Policy(layout: layout) with { SlotSet = OverlongSlotSet() }, Policy(layout: layout)];
        yield return ["banks must declare at least one bank", Policy(layout: layout) with { Banks = [] }, Policy(layout: layout)];
        yield return ["banks declares 6 entries", Policy(layout: layout) with { Banks = [.. Enumerable.Range(start: 0, count: 6).Select(selector: static order => (OneBank[0] with { Id = $"bank{order}", Order = order }))] }, Policy(layout: layout)];
        yield return ["banks[1].id", Policy(layout: layout) with { Banks = [OneBank[0], (OneBank[0] with { Order = 1 })] }, Policy(layout: layout) with { Banks = [OneBank[0], (OneBank[0] with { Id = "second", Order = 1 })] }];
        yield return ["banks[1].order 0 is duplicated", Policy(layout: layout) with { Banks = [OneBank[0], (OneBank[0] with { Id = "second" })] }, Policy(layout: layout) with { Banks = [OneBank[0], (OneBank[0] with { Id = "second", Order = 1 })] }];
        yield return ["banks[0].order -1", Policy(layout: layout) with { Banks = [OneBank[0] with { Order = -1 }] }, Policy(layout: layout)];
        yield return ["banks[0].offsetX", Policy(layout: layout) with { Banks = [OneBank[0] with { OffsetX = float.NaN }] }, Policy(layout: layout) with { Banks = [OneBank[0] with { OffsetX = -0.24f }] }];
        yield return ["banks[0].offsetY", Policy(layout: layout) with { Banks = [OneBank[0] with { OffsetY = float.PositiveInfinity }] }, Policy(layout: layout) with { Banks = [OneBank[0] with { OffsetY = -0.18f }] }];
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

    /// <summary>An omitted layout override resolves to its engine default rather than zero — the property the
    /// nullable override surface rests on, and the reason a world that authors only <c>scale</c> still frames its bar
    /// exactly as the fully-authored one does.</summary>
    [Fact]
    public void OmittedLayoutOverridesResolveToTheirDefaults() {
        var bare = new WorldBindingBarLayout();

        Assert.Equal(expected: WorldBindingBarLayout.DefaultButtonSize, actual: bare.ResolvedButtonSize);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultCenterRowLift, actual: bare.ResolvedCenterRowLift);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultExoticRowLift, actual: bare.ResolvedExoticRowLift);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultModifierHalfRatio, actual: bare.ResolvedModifierHalfRatio);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultHintBaseGapRatio, actual: bare.ResolvedHintBaseGapRatio);
        Assert.Equal(expected: 1f, actual: bare.Scale);

        var authored = (bare with { ButtonSize = 0.5f });

        Assert.Equal(expected: 0.5f, actual: authored.ResolvedButtonSize);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultCenterGap, actual: authored.ResolvedCenterGap);
    }

    private static readonly WorldBindingBarBank[] OneBank = [new WorldBindingBarBank(Id: "resting", PageId: "base", Order: 0, Alpha: 1f)];

    private static string[] OverlongSlotSet() {
        var sources = new string[(WorldBindingBarCapacity.MaxSlots + 1)];

        for (var index = 0; (index < sources.Length); index++) {
            sources[index] = InputSources.Mouse.Button(number: (index + 1));
        }

        return sources;
    }
    private static WorldBindingBarAuthoring Policy(WorldBindingBarLayout layout) => new(
        Banks: OneBank,
        Layout: layout,
        SlotSet: [InputSources.Gamepad.DpadUp]
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
