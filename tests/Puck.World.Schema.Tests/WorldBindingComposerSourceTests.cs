using Puck.Commands;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves the composer's entry-by-source replacement law over a row naming several sources: a later
/// layer's row replaces the earlier layer's entries at each of its OWN listed sources independently, never the
/// whole earlier entry, and a fully-overridden entry survives combined rather than split.</summary>
public sealed class WorldBindingComposerSourceTests {
    [Fact]
    public void ALaterLayersNarrowerRowReplacesOnlyTheSourceItNamesLeavingTheRestOfTheEarlierEntryInPlace() {
        var baseDocument = Document(entries: [
            new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth", "keyboard.space"], Command: "base.jump"),
        ]);
        var overlay = Document(entries: [
            new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth"], Command: "overlay.jump"),
        ]);

        var composed = WorldBindingComposer.Compose(baseDocument, overlay);
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: composed));

        Assert.Equal(expected: "overlay.jump", actual: Assert.Single(collection: bindings.Resolve(slot: 0, source: "gamepad.buttonSouth")!).Command);
        Assert.Equal(expected: "base.jump", actual: Assert.Single(collection: bindings.Resolve(slot: 0, source: "keyboard.space")!).Command);

        var entries = composed.Chords[0].Page!.Entries;

        Assert.Equal(expected: 2, actual: entries.Count);
        Assert.Equal(expected: ["gamepad.buttonSouth"], actual: Assert.Single(collection: entries, predicate: static entry => (entry.Command == "overlay.jump")).Sources);
        Assert.Equal(expected: ["keyboard.space"], actual: Assert.Single(collection: entries, predicate: static entry => (entry.Command == "base.jump")).Sources);
    }
    [Fact]
    public void ALaterLayersRowClaimingEveryOneOfTheEarlierEntrysSourcesReplacesItWhollyAndStaysCombined() {
        var baseDocument = Document(entries: [
            new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth", "keyboard.space"], Command: "base.jump"),
        ]);
        var overlay = Document(entries: [
            new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth", "keyboard.space"], Command: "overlay.jump"),
        ]);

        var composed = WorldBindingComposer.Compose(baseDocument, overlay);
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: composed));

        Assert.Equal(expected: "overlay.jump", actual: Assert.Single(collection: bindings.Resolve(slot: 0, source: "gamepad.buttonSouth")!).Command);
        Assert.Equal(expected: "overlay.jump", actual: Assert.Single(collection: bindings.Resolve(slot: 0, source: "keyboard.space")!).Command);

        var entry = Assert.Single(collection: composed.Chords[0].Page!.Entries);

        Assert.Equal(expected: ["gamepad.buttonSouth", "keyboard.space"], actual: entry.Sources);
    }

    private static BindingProfileDocument Document(IReadOnlyList<BindingPageEntryDefinition> entries) {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: entries)
            )]
        );
    }
}
