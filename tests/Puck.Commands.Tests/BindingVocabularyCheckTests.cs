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
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: static _ => null,
            sourceKind: null,
            errors: errors
        );

        // The entry labels by its (empty) sequence and the unknown command is refused as data, not as a crash.
        var error = Assert.Single(collection: errors);

        Assert.Contains(actualString: error, expectedSubstring: "activator[]");
        Assert.Contains(actualString: error, expectedSubstring: "names no registered command");
    }
    [Fact]
    public void ANullPageEntryIsRefusedNotThrown() {
        var document = Document(entry: null!);
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: null,
            sourceKind: null,
            errors: errors
        );

        Assert.Contains(expectedSubstring: "entry 0 is null", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AValueLessActivatorDispatchesDigitalForKindValidation() {
        var document = Document(entry: new BindingPageEntryDefinition(
            Sources: null,
            Command: "axis.command",
            Activator: new BindingActivatorDefinition(Sequence: ["key.a"])
        ));
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: static name => new CommandMetadata(
                Name: name,
                ValueKind: CommandValueKind.Axis1D,
                Routing: CommandRouting.Immediate,
                Bindability: CommandBindability.Bindable
            ),
            sourceKind: static _ => CommandValueKind.Digital,
            errors: errors
        );

        Assert.Contains(expectedSubstring: "sends digital", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AnUnaddressableTextSourceIsRefusedEvenWhenItsKindIsKnown() {
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["keyboard.text"], Command: "type"));
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: static name => new CommandMetadata(
                Name: name,
                ValueKind: CommandValueKind.Digital,
                Routing: CommandRouting.Immediate,
                Bindability: CommandBindability.Bindable
            ),
            sourceKind: static _ => CommandValueKind.Digital,
            sourceAddressable: static source => (source != "keyboard.text"),
            errors: errors
        );

        Assert.Contains(expectedSubstring: "unaddressable control \"keyboard.text\"", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void ArgumentBindingRequiresAWireArgsCommandAndAPressCapableSource() {
        var document = Document(entry: new BindingPageEntryDefinition(
            Sources: ["gamepad.leftTrigger"],
            Command: "action",
            Text: "first second"
        ));
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: static name => new CommandMetadata(
                Name: name,
                ValueKind: CommandValueKind.Axis1D,
                Routing: CommandRouting.Simulation,
                Bindability: CommandBindability.Bindable
            ),
            sourceKind: static _ => CommandValueKind.Axis1D,
            errors: errors
        );

        Assert.Contains(collection: errors, filter: static error => error.Contains(value: "accepts no wire arguments", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: static error => error.Contains(value: "has no press edge", comparisonType: StringComparison.Ordinal));

        errors.Clear();

        BindingVocabularyCheck.Validate(
            document: document with {
                Chords = [document.Chords[0] with {
                    Page = document.Chords[0].Page! with {
                        Entries = [document.Chords[0].Page!.Entries[0] with { Sources = ["key.a"], }],
                    },
                }],
            },
            command: static name => new CommandMetadata(
                Name: name,
                ValueKind: CommandValueKind.Digital,
                Routing: CommandRouting.Simulation,
                Bindability: CommandBindability.Bindable,
                AcceptsWireArgs: true
            ),
            sourceKind: static _ => CommandValueKind.Digital,
            errors: errors
        );

        Assert.Empty(collection: errors);
    }

    [Fact]
    public void APlainSourceTheCatalogCannotResolveIsRefusedByName() {
        // The structural gate has no physical vocabulary, so a typo'd source compiles into a row that tables a
        // control which will never signal — dead forever, and silent until this refusal.
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouht"], Command: "jump"));
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: null,
            sourceKind: static source => ((source == "gamepad.buttonSouth")
            ? CommandValueKind.Digital
            : null),
            errors: errors
        );

        Assert.Contains(expectedSubstring: "binds unknown control \"gamepad.buttonSouht\"", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AnUnaddressableSourceIsRefusedOnceRatherThanTwice() {
        // A catalog reports an unaddressable control by answering null for its kind, so the two refusals would
        // otherwise both fire on the same source.
        var document = Document(entry: new BindingPageEntryDefinition(Sources: ["keyboard.text"], Command: "type"));
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: null,
            sourceKind: static _ => null,
            sourceAddressable: static source => (source != "keyboard.text"),
            errors: errors
        );

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
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: null,
            sourceKind: static source => ((source == "key.shift")
            ? CommandValueKind.Digital
            : null),
            errors: errors
        );

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
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: null,
            sourceKind: static _ => CommandValueKind.Digital,
            errors: errors
        );

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
        var errors = new List<string>();

        BindingVocabularyCheck.Validate(
            document: document,
            command: static name => new CommandMetadata(
                Name: name,
                ValueKind: CommandValueKind.Digital,
                Routing: CommandRouting.Immediate,
                Bindability: CommandBindability.Bindable
            ),
            sourceKind: null,
            errors: errors
        );

        Assert.Contains(expectedSubstring: "binds text arguments to \"action\", which accepts no wire arguments", actualString: Assert.Single(collection: errors));

        errors.Clear();

        BindingVocabularyCheck.Validate(
            document: document,
            command: static name => new CommandMetadata(
                Name: name,
                ValueKind: CommandValueKind.Digital,
                Routing: CommandRouting.Immediate,
                Bindability: CommandBindability.Bindable,
                AcceptsWireArgs: true
            ),
            sourceKind: null,
            errors: errors
        );

        Assert.Empty(collection: errors);
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
