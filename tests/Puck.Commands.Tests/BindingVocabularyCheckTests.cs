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
}
