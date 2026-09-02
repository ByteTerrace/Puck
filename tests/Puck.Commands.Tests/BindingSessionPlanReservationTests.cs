using Xunit;

namespace Puck.Commands.Tests;

/// <summary>
/// The reservation oracle: <see cref="BindingSessionPlan.FromPage"/> must reserve EXACTLY the sources
/// <see cref="BindingProfile.Compile"/> turns into page selectors — no more, no fewer.
/// </summary>
/// <remarks>Under-reserving lets a guided session capture a page selector onto an ordinary command, so the source
/// silently flips pages instead of firing. Over-reserving refuses a capture that is legitimate — and, when the
/// phantom name is a control the walked page itself binds, refuses the very capture the plan suggests, making the
/// step impossible to complete. Both are the same defect: the plan's idea of "reserved" drifting from the
/// compiler's idea of "modifier".</remarks>
public sealed class BindingSessionPlanReservationTests {
    [Fact]
    public void ReservedSourcesAreExactlyTheCompiledProfilesModifierSources() {
        foreach (var (label, document) in Documents()) {
            var compiled = new HashSet<string>(
                collection: BindingProfile.Compile(document: document).Modifiers.SelectMany(selector: static modifier => modifier.Sources),
                comparer: StringComparer.OrdinalIgnoreCase
            );
            var reserved = new HashSet<string>(
                collection: (BindingSessionPlan.FromPage(
                    document: document,
                    pageId: "base"
                ).ReservedSources ?? []),
                comparer: StringComparer.OrdinalIgnoreCase
            );

            Assert.True(
                condition: compiled.SetEquals(other: reserved),
                userMessage: $"{label}: compiled [{string.Join(separator: ", ", values: compiled.Order(comparer: StringComparer.Ordinal))}] != reserved [{string.Join(separator: ", ", values: reserved.Order(comparer: StringComparer.Ordinal))}]"
            );
        }
    }
    [Fact]
    public void AMemberDifferingFromADeclaredModifierOnlyByCaseIsNotAPhantomControl() {
        // Compile resolves "LOOK" to the declared modifier "look" (its id lookup is OrdinalIgnoreCase), so the
        // member mints no implicit modifier and names no control at all. Reserving the raw string anyway made
        // "pad.leftShoulder" — a control the walked page BINDS — unreachable from the session that suggests it.
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "pad.leftShoulder", Sources: ["pad.leftTrigger"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Entries: [new BindingPageEntryDefinition(Sources: ["PAD.LEFTSHOULDER"], Command: "jump")]
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["PAD.LEFTSHOULDER"],
                    Page: new BindingPageDefinition(Id: "modal", Entries: [])
                ),
            ]
        );
        var reserved = BindingSessionPlan.FromPage(
            document: document,
            pageId: "base"
        ).ReservedSources;

        Assert.Equal(expected: "pad.leftTrigger", actual: Assert.Single(collection: reserved!));
    }
    [Fact]
    public void AnInheritedPageWalksItsEffectiveEntries() {
        // Compile flattens page inheritance before anything reads a page's entries, so a plan built over a page
        // that inherits must walk the same effective row set — otherwise a session over a modal page prompts for
        // the two controls it overrides and silently drops everything it merely keeps.
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "resting",
                        Entries: [
                            new BindingPageEntryDefinition(Sources: ["pad.south"], Command: "jump"),
                            new BindingPageEntryDefinition(Sources: ["pad.east"], Command: "menu"),
                        ]
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["pad.leftShoulder"],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Inherits: "resting",
                        Entries: [new BindingPageEntryDefinition(Sources: ["pad.south"], Command: "grapple")]
                    )
                ),
            ]
        );
        var plan = BindingSessionPlan.FromPage(
            document: document,
            pageId: "base"
        );

        Assert.Equal(
            actual: plan.Steps.Select(selector: static step => $"{step.Command}:{step.SuggestedSource}").ToArray(),
            // Surviving inherited entries first, then the page's own — the order the flattened page carries.
            expected: ["menu:pad.east", "grapple:pad.south",]
        );
    }

    // The reservation scenarios, each carrying a page "base" with at least one sourced entry to walk.
    private static IEnumerable<(string Label, BindingProfileDocument Document)> Documents() {
        yield return ("no declared modifiers, one raw chord member", Document(
            modifiers: [],
            rows: [new BindingChordDefinition(
                Group: "play",
                Chord: ["pad.leftShoulder"],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        ));
        yield return ("a held-only member", Document(
            modifiers: [],
            rows: [new BindingChordDefinition(
                Group: "play",
                Held: ["pad.rightShoulder"],
                Chord: [],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        ));
        yield return ("a raw member on a COMMAND row rather than a page row", Document(
            modifiers: [],
            rows: [new BindingChordDefinition(
                Group: "play",
                Chord: ["pad.leftShoulder", "pad.rightShoulder"],
                Command: new BindingCommandDefinition(Command: "recenter")
            )]
        ));
        yield return ("a member naming a declared modifier by id", Document(
            modifiers: [new BindingModifierDefinition(Id: "look", Sources: ["pad.leftTrigger"])],
            rows: [new BindingChordDefinition(
                Group: "play",
                Chord: ["look"],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        ));
        yield return ("a member naming a declared modifier's SOURCE", Document(
            modifiers: [new BindingModifierDefinition(Id: "look", Sources: ["pad.leftTrigger"])],
            rows: [new BindingChordDefinition(
                Group: "play",
                Chord: ["pad.leftTrigger"],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        ));
        yield return ("a member in a group the walked page does not belong to", Document(
            modifiers: [],
            rows: [
                new BindingChordDefinition(
                    Group: "editor",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "editor-base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "editor",
                    Chord: ["pad.select"],
                    Page: new BindingPageDefinition(Id: "editor-modal", Entries: [])
                ),
            ]
        ));
        yield return ("a member differing from a declared modifier ID only by case", Document(
            modifiers: [new BindingModifierDefinition(Id: "look", Sources: ["pad.leftTrigger"])],
            rows: [new BindingChordDefinition(
                Group: "play",
                Chord: ["LOOK"],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        ));
        yield return ("a member differing from a declared modifier SOURCE only by case", Document(
            modifiers: [new BindingModifierDefinition(Id: "look", Sources: ["pad.leftTrigger"])],
            rows: [new BindingChordDefinition(
                Group: "play",
                Chord: ["PAD.LEFTTRIGGER"],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        ));
    }
    private static BindingProfileDocument Document(IReadOnlyList<BindingModifierDefinition> modifiers, IReadOnlyList<BindingChordDefinition> rows) {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: modifiers,
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Entries: [new BindingPageEntryDefinition(Sources: ["pad.south"], Command: "jump")]
                    )
                ),
                .. rows,
            ]
        );
    }
}
