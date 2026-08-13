using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingProfileValidationTests {
    [Fact]
    public void NullPageEntriesAreRefusedByTheStructuralGate() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: null!
        )));
    }

    [Fact]
    public void NonFiniteModifierThresholdsAreRefused() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [new BindingModifierDefinition(
                Id: "shift",
                Source: "key.shift",
                PressThreshold: float.NaN
            )],
            entry: new BindingPageEntryDefinition(Source: "key.a", Command: "action")
        )));
    }

    [Fact]
    public void UndefinedBindingModesAndPhasesAreRefused() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Source: "key.a",
                Command: "action",
                Mode: (BindingEntryMode)42
            )
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Source: "key.a",
                Command: "action",
                ActivateOn: (CommandPhase)42
            )
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => BindingProfile.Compile(document: Document(
            modifiers: [],
            entry: new BindingPageEntryDefinition(
                Source: null,
                Command: "action",
                Activator: new BindingActivatorDefinition(
                    Sequence: ["key.a"],
                    Mode: (BindingActivatorMode)42
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
}
