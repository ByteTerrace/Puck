using System.Numerics;
using Puck.Commands;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A bar can keep its modifier row to the modifiers the document declares, leaving out the ones the compiler
/// minted for a raw source a chord names, and a control that is unbound but opens a deeper page shows that page.</summary>
public sealed class BindingBarDeclaredModifiersLawTests {
    private static readonly OverlayResolvedGlyph Badge = new(Glyph0: 5, Glyph1: 0);
    private static readonly OverlayResolvedGlyph WheelIcon = new(Glyph0: 9, Glyph1: 0);

    private static BindingPageView Resting() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(
                Id: "lt",
                Sources: ["gamepad.leftTrigger"]
            )],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Entries: []
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["lt"],
                    Page: new BindingPageDefinition(
                        Id: "view",
                        Entries: []
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["gamepad.leftShoulder"],
                    Page: new BindingPageDefinition(
                        Id: "wheel",
                        Label: "Wheel",
                        Entries: []
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Held: ["mouse.button1", "mouse.button2"],
                    Command: new BindingCommandDefinition(
                        Command: "test.walk",
                        Label: "Walk"
                    )
                ),
            ]
        ));

        Assert.True(condition: profile.TryGetPageView(pageId: "base", view: out var view));

        return view;
    }

    [Fact]
    public void TheModifierRowKeepsToDeclaredModifiersWhenAskedAndHintsFollowIt() {
        var view = Resting();
        var all = new OverlayBindingModifier[8];
        var declared = new OverlayBindingModifier[8];

        Assert.Equal(
            expected: 4,
            actual: BindingBarSeatComposer.ComposeModifiers(destination: all, resolveBadge: static _ => Badge, text: true, view: view)
        );
        Assert.Equal(
            expected: 1,
            actual: BindingBarSeatComposer.ComposeModifiers(declaredOnly: true, destination: declared, resolveBadge: static _ => Badge, text: true, view: view)
        );

        OverlayBindingHint[] Hints(bool declaredOnly, out int count) {
            var hints = new OverlayBindingHint[BindingBarWriter.MaxHintLines];

            count = BindingBarSeatComposer.ComposeHints(
                declaredOnly: declaredOnly,
                destination: hints,
                members: new OverlayResolvedGlyph[(BindingBarWriter.MaxHintLines * BindingBarWriter.MaxHintMembers)],
                modifiers: true,
                resolveAction: static _ => OverlayResolvedGlyph.None,
                resolveBadge: static _ => Badge,
                slotSet: [],
                text: true,
                view: view
            );

            return hints;
        }

        _ = Hints(declaredOnly: false, count: out var every);
        _ = Hints(declaredOnly: true, count: out var kept);

        Assert.Equal(expected: 1, actual: every);
        Assert.Equal(expected: 0, actual: kept);
    }
    [Fact]
    public void AnUnboundControlThatOpensAPageWearsThatPagesIcon() {
        var view = Resting();
        var plates = new Dictionary<string, BindingPlatePlacement>(comparer: StringComparer.Ordinal) {
            ["gamepad.leftShoulder"] = new BindingPlatePlacement(Badge: Vector2.Zero, Position: Vector2.Zero),
            ["gamepad.rightShoulder"] = new BindingPlatePlacement(Badge: Vector2.Zero, Position: Vector2.Zero),
        };
        var destination = new OverlayBindingSlot[2];

        BindingBarSeatComposer.ComposeBank(
            bankAlpha: 1f,
            bankOrder: 0,
            destination: destination,
            frame: 0,
            hideUnbound: true,
            isCommandHeld: null,
            isPressed: null,
            plates: plates,
            resolveBadge: static _ => Badge,
            resolveIcon: static key => (string.Equals(a: key, b: "wheel", comparisonType: StringComparison.Ordinal)
                ? WheelIcon
                : OverlayResolvedGlyph.None),
            slotSet: ["gamepad.leftShoulder", "gamepad.rightShoulder"],
            text: true,
            view: view
        );

        Assert.True(condition: destination[0].Bound);
        Assert.True(condition: destination[0].Visible);
        Assert.Equal(expected: (WheelIcon.Glyph0, WheelIcon.Glyph1), actual: (destination[0].IconGlyph0, destination[0].IconGlyph1));
        Assert.False(condition: destination[1].Bound);
        Assert.False(condition: destination[1].Visible);
    }
}
