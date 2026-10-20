using System.Collections.Frozen;
using Puck.Commands;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A command chord shows on the bar as its members' badges and its action's icon, only when the bar places
/// every member; the bar never spells a source or a command name.</summary>
public sealed class BindingBarChordHintLawTests {
    private static readonly OverlayResolvedGlyph Arrow = new(Glyph0: 7, Glyph1: 0);
    private static readonly OverlayResolvedGlyph LabelBadge = new(Glyph0: 3, Glyph1: 4, IsLabel: true);
    private static readonly OverlayResolvedGlyph PadBadge = new(Glyph0: 5, Glyph1: 0);

    private static BindingChordCommandView Chord(string? action, string? label, params string[] sources) => new(
        Action: action,
        Chord: [],
        Command: "channel.0",
        Held: sources,
        HoldRelease: false,
        Icon: null,
        Label: label,
        Sources: sources
    );
    private static (int Count, OverlayBindingHint[] Hints) Compose(bool modifiers, bool text, params BindingChordCommandView[] chords) {
        var hints = new OverlayBindingHint[BindingBarWriter.MaxHintLines];
        var count = BindingBarSeatComposer.ComposeHints(
            destination: hints,
            members: new OverlayResolvedGlyph[(BindingBarWriter.MaxHintLines * BindingBarWriter.MaxHintMembers)],
            modifiers: modifiers,
            resolveAction: static chord => (string.Equals(a: chord.Action, b: "forward", comparisonType: StringComparison.Ordinal)
                ? Arrow
                : OverlayResolvedGlyph.None),
            resolveBadge: static source => (source.StartsWith(value: "mouse.", comparisonType: StringComparison.Ordinal)
                ? LabelBadge
                : (source.StartsWith(value: "gamepad.", comparisonType: StringComparison.Ordinal)
                    ? PadBadge
                    : OverlayResolvedGlyph.None)),
            slotSet: ["gamepad.leftShoulder", "gamepad.rightShoulder"],
            text: text,
            view: new BindingPageView(
                Buttons: [],
                ButtonsBySource: FrozenDictionary<string, BindingPageButtonView>.Empty,
                CommandChords: chords,
                Group: "play",
                Icon: null,
                Label: null,
                Modifiers: [],
                PageId: "base"
            )
        );

        return (count, hints);
    }

    [Fact]
    public void AChordShowsOnlyWhenTheBarPlacesEveryMemberAndDrawsItWithBadgesAndAnIcon() {
        var mouse = Chord(action: "forward", label: "Forward", sources: ["mouse.button1", "mouse.button2"]);
        var shoulders = Chord(action: "forward", label: "Forward", sources: ["gamepad.leftShoulder", "gamepad.rightShoulder"]);
        var unbadged = Chord(action: "forward", label: "Forward", sources: ["keyboard.q"]);
        var (count, hints) = Compose(modifiers: false, text: true, chords: [mouse, shoulders, unbadged]);

        Assert.Equal(expected: 1, actual: count);
        Assert.Equal(expected: [PadBadge, PadBadge], actual: hints[0].Members.ToArray());
        Assert.Equal(expected: Arrow, actual: hints[0].Action);
        Assert.Equal(expected: string.Empty, actual: hints[0].Label);

        (count, hints) = Compose(modifiers: true, text: true, chords: [mouse, unbadged]);
        Assert.Equal(expected: 1, actual: count);
        Assert.Equal(expected: [LabelBadge, LabelBadge], actual: hints[0].Members.ToArray());

        // A text-off bar drops label badges, so the mouse chord leaves with them.
        (count, _) = Compose(modifiers: true, text: false, chords: [mouse]);
        Assert.Equal(expected: 0, actual: count);
    }
    [Fact]
    public void AnActionWithNoIconDrawsItsLabelWhileTheBarDrawsTextAndOtherwiseStaysOff() {
        var snapshot = Chord(action: "world.snapshot", label: "Snapshot", sources: ["gamepad.leftShoulder", "gamepad.rightShoulder"]);
        var unlabelled = Chord(action: "world.snapshot", label: null, sources: ["gamepad.leftShoulder"]);
        var crowded = Chord(action: "forward", label: null, sources: ["gamepad.leftShoulder", "gamepad.rightShoulder", "gamepad.a", "gamepad.b", "gamepad.x"]);
        var (count, hints) = Compose(modifiers: true, text: true, chords: [snapshot, unlabelled, crowded]);

        Assert.Equal(expected: 1, actual: count);
        Assert.Equal(expected: OverlayResolvedGlyph.None, actual: hints[0].Action);
        Assert.Equal(expected: "Snapshot", actual: hints[0].Label);

        (count, _) = Compose(modifiers: true, text: false, chords: [snapshot]);
        Assert.Equal(expected: 0, actual: count);
        Assert.Throws<ArgumentException>(testCode: () => BindingBarSeatComposer.ComposeHints(
            destination: new OverlayBindingHint[2],
            members: new OverlayResolvedGlyph[1],
            modifiers: true,
            resolveAction: static _ => Arrow,
            resolveBadge: static _ => PadBadge,
            slotSet: [],
            text: true,
            view: new BindingPageView(
                Buttons: [],
                ButtonsBySource: FrozenDictionary<string, BindingPageButtonView>.Empty,
                CommandChords: [],
                Group: "play",
                Icon: null,
                Label: null,
                Modifiers: [],
                PageId: "base"
            )
        ));
    }
}
