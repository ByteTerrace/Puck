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

        Assert.Contains(collection: errors, filter: static error => error.Contains(comparisonType: StringComparison.Ordinal, value: "accepts no wire arguments"));
        Assert.Contains(collection: errors, filter: static error => error.Contains(comparisonType: StringComparison.Ordinal, value: "has no press edge"));

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
