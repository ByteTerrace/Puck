using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the vocabulary gate's tolerance for malformed documents: a shape the gate exists to refuse
/// must produce a refusal line, never an exception.</summary>
public sealed class BindingVocabularyCheckTests {
    [Fact]
    public void ANullActivatorSequenceIsRefusedNotThrown() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Entries: [
                            new BindingPageEntryDefinition(
                                Sources: null,
                                Command: "x",
                                Activator: new BindingActivatorDefinition(Sequence: null!)
                            ),
                        ]
                    )
                ),
            ]
        );
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static _ => null,
                SourceKind: null
            )
        ).Errors;

        // The entry labels by its (empty) sequence and the unknown command is refused as data, not as a crash.
        var error = Assert.Single(collection: errors);

        Assert.Contains(actualString: error, expectedSubstring: "activator[]");
        Assert.Contains(actualString: error, expectedSubstring: "names no registered command");
    }
    [Fact]
    public void ANullPageEntryIsRefusedNotThrown() {
        var document = Document(entry: null!);
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: null,
                SourceKind: null
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "entry 0 is null", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AValueLessActivatorDispatchesDigitalForKindValidation() {
        var document = Document(entry: new BindingPageEntryDefinition(
            Sources: null,
            Command: "axis.command",
            Activator: new BindingActivatorDefinition(Sequence: ["key.a"])
        ));
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Axis1D,
                    Routing: CommandRouting.Immediate,
                    Bindability: CommandBindability.Bindable
                ),
                SourceKind: static _ => CommandValueKind.Digital
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "sends digital", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AnUnaddressableTextSourceIsRefusedEvenWhenItsKindIsKnown() {
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["keyboard.text"], Command: "type"));
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Digital,
                    Routing: CommandRouting.Immediate,
                    Bindability: CommandBindability.Bindable
                ),
                SourceKind: static _ => CommandValueKind.Digital,
                SourceAddressable: static source => (source != "keyboard.text")
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "unaddressable control \"keyboard.text\"", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void ArgumentBindingRequiresAWireArgsCommandAndAPressCapableSource() {
        var document = Document(entry: new BindingPageEntryDefinition(
            Sources: ["gamepad.leftTrigger"],
            Command: "action",
            Text: "first second"
        ));
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Axis1D,
                    Routing: CommandRouting.Simulation,
                    Bindability: CommandBindability.Bindable
                ),
                SourceKind: static _ => CommandValueKind.Axis1D
            )
        ).Errors;

        Assert.Contains(collection: errors, filter: static error => error.Contains(comparisonType: StringComparison.Ordinal, value: "accepts no wire arguments"));
        Assert.Contains(collection: errors, filter: static error => error.Contains(comparisonType: StringComparison.Ordinal, value: "has no press edge"));

        errors = BindingVocabularyCheck.Validate(
            document: document with {
                Chords = [document.Chords[0] with {
                    Page = document.Chords[0].Page! with {
                        Entries = [document.Chords[0].Page!.Entries[0] with { Sources = ["key.a"], }],
                    },
                }],
            },
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Digital,
                    Routing: CommandRouting.Simulation,
                    Bindability: CommandBindability.Bindable,
                    AcceptsWireArgs: true
                ),
                SourceKind: static _ => CommandValueKind.Digital
            )
        ).Errors;

        Assert.Empty(collection: errors);
    }
    [Fact]
    public void APlainSourceTheCatalogCannotResolveIsRefusedByName() {
        // The structural gate has no physical vocabulary, so a typo'd source compiles into a row that tables a
        // control which will never signal — dead forever, and silent until this refusal.
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouht"], Command: "jump"));
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: null,
                SourceKind: static source => ((source == "gamepad.buttonSouth")
                ? CommandValueKind.Digital
                : null)
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "binds unknown control \"gamepad.buttonSouht\"", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AnUnaddressableSourceIsRefusedOnceRatherThanTwice() {
        // A catalog reports an unaddressable control by answering null for its kind, so the two refusals would
        // otherwise both fire on the same source.
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["keyboard.text"], Command: "type"));
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: null,
                SourceKind: static _ => null,
                SourceAddressable: static source => (source != "keyboard.text")
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "unaddressable control \"keyboard.text\"", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void ARowMemberNamingNoDeclaredModifierAndNoKnownControlIsRefused() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "shift", Sources: ["key.shift"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                // "SHIFT" resolves to the declared modifier (ids are case-insensitive there, so it must be here);
                // "key.shfit" resolves to nothing at all and would become a permanently dead implicit modifier.
                new BindingChordDefinition(
                    Group: "play",
                    Held: ["SHIFT", "key.shfit"],
                    Page: new BindingPageDefinition(Id: "held", Entries: [])
                ),
            ]
        );
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: null,
                SourceKind: static source => ((source == "key.shift")
                ? CommandValueKind.Digital
                : null)
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "names \"key.shfit\", which is neither a declared modifier nor a known control", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AKnownControlAsARowMemberIsAdmitted() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Held: ["gamepad.leftShoulder"],
                    Page: new BindingPageDefinition(Id: "held", Entries: [])
                ),
            ]
        );
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: null,
                SourceKind: static _ => CommandValueKind.Digital
            )
        ).Errors;

        Assert.Empty(collection: errors);
    }
    [Fact]
    public void AWheelSectorsTextRequiresAWireArgsCommand() {
        // A sector commit submits "<command> <text>" through InputRouter.Activate exactly as a bound press does, so
        // it needs the same wire-args destination — without the gate the radial was the one door authored arguments
        // reached a verb that parses none through, silently dropped.
        var document = new BindingProfileDocument(
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
                Rings: [new BindingPageDefinition(
                    Id: "ring",
                    Entries: [
                        new BindingPageEntryDefinition(Sources: null, Command: "action", Text: "argument"),
                        new BindingPageEntryDefinition(Sources: null, Command: "action"),
                    ]
                )]
            )]
        );
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Digital,
                    Routing: CommandRouting.Immediate,
                    Bindability: CommandBindability.Bindable
                ),
                SourceKind: null
            )
        ).Errors;

        Assert.Contains(expectedSubstring: "binds text arguments to \"action\", which accepts no wire arguments", actualString: Assert.Single(collection: errors));

        errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Digital,
                    Routing: CommandRouting.Immediate,
                    Bindability: CommandBindability.Bindable,
                    AcceptsWireArgs: true
                ),
                SourceKind: null
            )
        ).Errors;

        Assert.Empty(collection: errors);
    }

    [Fact]
    public void ADeclaredModifiersOwnSourcesAreCheckedAgainstTheControlCatalog() {
        // The third place a physical source id appears, and the one the gate used to skip: a typo here compiles
        // into a modifier that can never latch, so the page it selects is dead forever with no refusal line.
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: "aim", Sources: ["gamepad.buttonSouht"]),
                new BindingModifierDefinition(Id: "type", Sources: ["keyboard.text"]),
                new BindingModifierDefinition(Id: "look", Sources: ["gamepad.leftTrigger"]),
            ],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: ["aim"],
                Page: new BindingPageDefinition(Id: "modal", Entries: [])
            )]
        );
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: null,
                SourceKind: static source => ((source == "gamepad.buttonSouht")
                ? null
                : CommandValueKind.Digital),
                SourceAddressable: static source => (source != "keyboard.text")
            )
        ).Errors;

        Assert.Equal(expected: 2, actual: errors.Length);
        Assert.Contains(collection: errors, filter: static error => (error == "modifier \"aim\" declares unknown control \"gamepad.buttonSouht\""));
        Assert.Contains(collection: errors, filter: static error => (error == "modifier \"type\" declares unaddressable control \"keyboard.text\""));
    }
    [Fact]
    public void AMisCasedSourceIsNotAnUnknownControl() {
        // The catalog resolves source ids case-insensitively because BindingProfile compiles and dispatches them
        // that way (InputSourceVocabulary's remarks state the rule). A gate holding a stricter opinion than the
        // compiler refuses rows that press and release perfectly well.
        var catalog = new HashSet<string>(
            collection: ["gamepad.buttonSouth", "gamepad.leftTrigger",],
            comparer: StringComparer.OrdinalIgnoreCase
        );
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "look", Sources: ["Gamepad.LeftTrigger"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Entries: [new BindingPageEntryDefinition(Sources: ["Gamepad.ButtonSouth"], Command: "action")]
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["LOOK"],
                    Page: new BindingPageDefinition(Id: "modal", Entries: [])
                ),
            ]
        );
        var errors = BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: static name => new CommandMetadata(
                    Name: name,
                    ValueKind: CommandValueKind.Digital,
                    Routing: CommandRouting.Immediate,
                    Bindability: CommandBindability.Bindable
                ),
                SourceKind: source => (catalog.Contains(item: source)
                ? CommandValueKind.Digital
                : null)
            )
        ).Errors;

        Assert.Empty(collection: errors);

        // And the same document compiles into a profile that really does dispatch the mis-cased row.
        var profile = BindingProfile.Compile(document: document);
        var bindings = new PagedInputBindings(profile: profile);

        // The canonically-spelled control drives the mis-cased row.
        Assert.NotEmpty(collection: (bindings.Resolve(
            signal: InputSignal.Press(source: "gamepad.buttonSouth"),
            slot: 0
        ) ?? []));
        // And the mis-cased modifier source latches the chord whose mis-cased member names it, flipping the page.
        _ = bindings.Resolve(
            signal: InputSignal.Press(source: "gamepad.leftTrigger"),
            slot: 0
        );

        Assert.Equal(expected: "modal", actual: bindings.ViewFor(slot: 0).PageId);
    }

    [Fact]
    public void TheReportIsTheWholeAnswerAndCarriesNoCallerState() {
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouht"], Command: "jump"));
        var lookups = new BindingVocabularyLookups(SourceKind: static _ => null);

        // A caller with no vocabularies at all still gets the checks that need none — and gets them clean here,
        // because every finding this document carries needs a catalog to see.
        Assert.True(condition: BindingVocabularyCheck.Validate(
            document: document,
            lookups: BindingVocabularyLookups.None
        ).IsClean);

        var first = BindingVocabularyCheck.Validate(
            document: document,
            lookups: lookups
        );
        var second = BindingVocabularyCheck.Validate(
            document: document,
            lookups: lookups
        );

        // Checked twice, reported twice — never accumulated, the way a caller-owned list would have.
        Assert.False(condition: first.IsClean);
        Assert.Equal(actual: second.Errors, expected: first.Errors);
        Assert.Single(collection: second.Errors);

        _ = Assert.Throws<ArgumentNullException>(testCode: () => BindingVocabularyCheck.Validate(
            document: null!,
            lookups: BindingVocabularyLookups.None
        ));
        _ = Assert.Throws<ArgumentNullException>(testCode: () => BindingVocabularyCheck.Validate(
            document: document,
            lookups: null!
        ));
    }

    private static BindingProfileDocument Document(BindingPageEntryDefinition entry) {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [entry])
            )]
        );
    }
}
