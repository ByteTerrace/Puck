using Puck.Commands;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves the composer's modifier-absorption law: a later layer's modifier under a new id that shares a
/// source with an earlier one replaces that modifier, and every already-merged row's chord/held reference follows
/// the rename — so a world renames the engine's hold key without a second modifier ever sharing its source.</summary>
public sealed class WorldBindingComposerModifierTests {
    [Fact]
    public void ALaterModifierSharingASourceAbsorbsTheEarlierOneAndRewritesEveryChordThatHeldIt() {
        var baseDocument = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "tab", Sources: ["keyboard.tab"])],
            Chords: [
                Row(group: "play", chord: [], page: "base"),
                Row(group: "play", chord: ["tab"], page: "play-wheel"),
                Row(group: "editor", chord: [], page: "editor-base"),
                Row(group: "editor", chord: ["tab"], page: "editor-wheel"),
            ]
        );
        var overlay = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "actionWheel", Sources: ["gamepad.leftShoulder", "keyboard.tab"])],
            Chords: [Row(group: "play", chord: ["actionWheel"], page: "actionWheel")]
        );

        var composed = WorldBindingComposer.Compose(baseDocument, overlay);

        var modifier = Assert.Single(collection: composed.Modifiers);

        Assert.Equal(expected: "actionWheel", actual: modifier.Id);
        Assert.Equal(expected: ["gamepad.leftShoulder", "keyboard.tab"], actual: modifier.Sources);
        // The engine's play hold page and the world's share (group, [actionWheel]) after the rename: one row survives,
        // the world's page id winning as any later same-key row does.
        Assert.Equal(expected: "actionWheel", actual: Assert.Single(collection: composed.Chords, predicate: static row => ((row.Group == "play") && (row.Chord is { Count: 1 }))).Page!.Id);
        Assert.Equal(expected: ["actionWheel"], actual: Assert.Single(collection: composed.Chords, predicate: static row => (row.Page?.Id == "editor-wheel")).Chord);
        _ = BindingProfile.Compile(document: composed);
    }
    [Fact]
    public void ALaterModifierUnderTheSameIdOverridesInPlaceWithoutRenaming() {
        var baseDocument = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "tab", Sources: ["keyboard.tab"])],
            Chords: [Row(group: "play", chord: [], page: "base"), Row(group: "play", chord: ["tab"], page: "play-wheel")]
        );
        var overlay = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "tab", Sources: ["gamepad.leftShoulder"])],
            Chords: []
        );

        var composed = WorldBindingComposer.Compose(baseDocument, overlay);
        var modifier = Assert.Single(collection: composed.Modifiers);

        Assert.Equal(expected: "tab", actual: modifier.Id);
        Assert.Equal(expected: ["gamepad.leftShoulder"], actual: modifier.Sources);
        Assert.Equal(expected: ["tab"], actual: Assert.Single(collection: composed.Chords, predicate: static row => (row.Page?.Id == "play-wheel")).Chord);
    }
    [Fact]
    public void AbsorbingBothHalvesOfAnExistingChordRefusesByName() {
        var baseDocument = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: "lt", Sources: ["gamepad.leftTrigger"]),
                new BindingModifierDefinition(Id: "rt", Sources: ["gamepad.rightTrigger"]),
            ],
            Chords: [Row(group: "play", chord: [], page: "base"), Row(group: "play", chord: ["lt", "rt"], page: "both")]
        );
        var overlay = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "triggers", Sources: ["gamepad.leftTrigger", "gamepad.rightTrigger"])],
            Chords: []
        );

        var exception = Assert.Throws<ArgumentException>(() => WorldBindingComposer.Compose(baseDocument, overlay));

        Assert.Contains(expectedSubstring: "\"triggers\"", actualString: exception.Message);
    }

    private static BindingChordDefinition Row(string group, IReadOnlyList<string> chord, string page) => new(
        Group: group,
        Chord: chord,
        Page: new BindingPageDefinition(Id: page, Entries: [])
    );
}
