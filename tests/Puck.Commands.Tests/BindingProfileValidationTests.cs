using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingProfileValidationTests {
    [Fact]
    public void NullPageEntriesAreRefusedByTheStructuralGate() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            entry: null!,
            modifiers: []
        )));
    }
    [Fact]
    public void NonFiniteModifierThresholdsAreRefused() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [new BindingModifierDefinition(
                Id: "shift",
                Sources: ["key.shift"],
                PressThreshold: float.NaN
            )],
            entry: new BindingPageEntryDefinition(Sources: ["key.a"], Command: "action")
        )));
    }
    [Fact]
    public void UndefinedBindingModesAndPhasesAreRefused() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Sources: ["key.a"],
                Command: "action",
                Mode: ((BindingEntryMode)42)
            )
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Sources: ["key.a"],
                Command: "action",
                ActivateOn: ((CommandPhase)42)
            )
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Sources: null,
                Command: "action",
                Activator: new BindingActivatorDefinition(
                    Sequence: ["key.a"],
                    Mode: ((BindingActivatorMode)42)
                )
            )
        )));
    }
    [Fact]
    public void ThresholdConsumersRejectNaNAndInvertedPairs() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => new HeldOrderTracker(
            modifierCount: 1,
            pressThreshold: float.NaN,
            releaseThreshold: 0.4f
        ));
        _ = Assert.Throws<ArgumentException>(testCode: static () => new BindingSession(plan: new BindingSessionPlan(
            Steps: [new BindingSessionStep(Command: "action", SuggestedSource: "key.a")],
            PressThreshold: 0.5f,
            ReleaseThreshold: float.PositiveInfinity
        )));
    }
    [Fact]
    public void EntryIdsMustBeNonEmptyAndUniqueWithinTheirEffectivePage() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: DocumentWithEntries(
            new BindingPageEntryDefinition(Sources: ["key.a"], Command: "action", Id: string.Empty)
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: DocumentWithEntries(
            new BindingPageEntryDefinition(Sources: ["key.a"], Command: "action", Id: "choice"),
            new BindingPageEntryDefinition(Sources: ["key.b"], Command: "action", Id: "choice")
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: DocumentWithWheel(
            new BindingPageEntryDefinition(Sources: null, Command: "action", Id: "choice"),
            new BindingPageEntryDefinition(Sources: null, Command: "action", Id: "choice")
        )));
    }
    [Fact]
    public void TextIsRefusedWhereNoCommandPressCanDeliverIt() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Sources: ["key.a"],
                Channel: new ChannelRef.Name(Value: "move"),
                Text: "left"
            )
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Sources: ["key.a"],
                Command: "action",
                ActivateOn: CommandPhase.Completed,
                Text: "argument"
            )
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "shift", Sources: ["key.shift"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["shift"],
                    Command: new BindingCommandDefinition(
                        Channel: new ChannelRef.Name(Value: "move"),
                        Text: "left"
                    )
                ),
            ]
        )));
        // A wheel sector's commit is a press: its text rides the activation as a submitted line.
        Assert.NotNull(@object: BindingProfile.Compile(document: DocumentWithWheel(
            new BindingPageEntryDefinition(Sources: null, Command: "action", Text: "argument"),
            new BindingPageEntryDefinition(Sources: null, Command: "action")
        )));
        // …and only a press: a sector activating on any other phase carries no line, exactly as a page entry does not.
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: DocumentWithWheel(
            new BindingPageEntryDefinition(
                Sources: null,
                Command: "action",
                ActivateOn: CommandPhase.Completed,
                Text: "argument"
            ),
            new BindingPageEntryDefinition(Sources: null, Command: "action")
        )));
    }
    [Fact]
    public void WheelSectorRefusesAnAuthoredLabel() {
        // A sector's display text resolves from the wheel's labelRow keyed by the sector id, so a label authored on
        // the sector row itself would silently mean nothing — refused by name like every other foreign sector member.
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: DocumentWithWheel(
            new BindingPageEntryDefinition(Sources: null, Command: "action", Label: "Choose"),
            new BindingPageEntryDefinition(Sources: null, Command: "action")
        )));
    }
    [Fact]
    public void TextPayloadsMustBeNonblankSingleLineAndBounded() {
        foreach (var text in new[] {
            string.Empty,
            " \t ",
            "first\rsecond",
            "first\nsecond",
            "first\u0085second",
            "first\u2028second",
            "first\u2029second",
            new string(c: 'a', count: (BindingProfile.MaxTextPayloadLength + 1)),
        }) {
            _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: Document(
                modifiers: [],
                entry: new BindingPageEntryDefinition(
                    Sources: ["key.a"],
                    Command: "action",
                    Text: text
                )
            )));
        }

        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "shift", Sources: ["key.shift"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["shift"],
                    Command: new BindingCommandDefinition(Command: "action", Text: "first\nsecond")
                ),
            ]
        )));

        // A wheel sector's text reaches the same per-tick transport through InputRouter.Activate, so it is measured
        // against the same bound rather than riding into a snapshot unchecked.
        foreach (var text in new[] {
            string.Empty,
            " \t ",
            "first\nsecond",
            new string(c: 'a', count: (BindingProfile.MaxTextPayloadLength + 1)),
        }) {
            _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: DocumentWithWheel(
                new BindingPageEntryDefinition(Sources: null, Command: "action", Text: text),
                new BindingPageEntryDefinition(Sources: null, Command: "action")
            )));
        }
    }
    [Fact]
    public void TextPayloadPreservesOuterWhitespaceAndTreatsSeparatorsAsArguments() {
        const string text = "  first; second && third | fourth  ";
        var profile = BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Sources: ["key.a"],
                Command: "action",
                Text: text
            )
        ));

        Assert.Equal(
            expected: text,
            actual: Assert.Single(collection: new PagedInputBindings(profile: profile).Resolve(slot: 0, source: "key.a")!).Text
        );
    }

    private static BindingProfileDocument Document(
        IReadOnlyList<BindingModifierDefinition> modifiers,
        BindingPageEntryDefinition entry
    ) {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: modifiers,
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [entry])
            )]
        );
    }
    private static BindingProfileDocument DocumentWithEntries(params BindingPageEntryDefinition[] entries) {
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
    private static BindingProfileDocument DocumentWithWheel(params BindingPageEntryDefinition[] sectors) {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "hold", Entries: [])
            )],
            Wheels: [new BindingWheelDefinition(
                Id: "wheel",
                Group: "play",
                HoldPages: ["hold"],
                Rings: [new BindingPageDefinition(Id: "ring", Entries: sectors)]
            )]
        );
    }
}
