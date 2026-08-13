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
                                Source: null,
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

        Assert.Contains(expectedSubstring: "activator[]", actualString: error);
        Assert.Contains(expectedSubstring: "names no registered command", actualString: error);
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

        Assert.Contains(expectedSubstring: "entry 0 is null", actualString: Assert.Single(errors));
    }

    [Fact]
    public void AValueLessActivatorDispatchesDigitalForKindValidation() {
        var document = Document(entry: new BindingPageEntryDefinition(
            Source: null,
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

        Assert.Contains(expectedSubstring: "sends digital", actualString: Assert.Single(errors));
    }

    [Fact]
    public void AnUnaddressableTextSourceIsRefusedEvenWhenItsKindIsKnown() {
        var document = Document(entry: new BindingPageEntryDefinition(Source: "keyboard.text", Command: "type"));
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
            sourceAddressable: static source => source != "keyboard.text",
            errors: errors
        );

        Assert.Contains(expectedSubstring: "unaddressable control \"keyboard.text\"", actualString: Assert.Single(errors));
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
