using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingSessionTests {
    [Fact]
    public void SessionProtocolIsDeterministicAcrossSuggestionsMismatchesConflictsAndAnalogHysteresis() {
        var first = RunProtocol();
        var second = RunProtocol();

        Assert.Equal(expected: first.Events, actual: second.Events);
        Assert.Equal(expected: first.Captures, actual: second.Captures);
        Assert.Contains(collection: first.Events, filter: static item => item.Kind == BindingSessionEventKind.ReservedRejected);
        Assert.Contains(collection: first.Events, filter: static item => item.Kind == BindingSessionEventKind.Mismatch);
        Assert.Contains(collection: first.Events, filter: static item => item.Kind == BindingSessionEventKind.ConflictRejected);
        Assert.Contains(collection: first.Events, filter: static item => item.Kind == BindingSessionEventKind.ConfirmationProgress);
        Assert.Equal(expected: BindingSessionEventKind.SessionCompleted, actual: first.Events[^1].Kind);

        Assert.Collection(
            first.Captures,
            capture => {
                Assert.Equal(expected: "jump", actual: capture.Command);
                Assert.Equal(expected: "pad.south", actual: capture.Source);
                Assert.True(condition: capture.MatchedSuggestion);
            },
            capture => {
                Assert.Equal(expected: "interact", actual: capture.Command);
                Assert.Equal(expected: "pad.west", actual: capture.Source);
                Assert.False(condition: capture.MatchedSuggestion);
            },
            capture => {
                Assert.Equal(expected: "target", actual: capture.Command);
                Assert.Equal(expected: "axis.rightTrigger", actual: capture.Source);
                Assert.False(condition: capture.MatchedSuggestion);
            }
        );
    }

    [Fact]
    public void ResultRewritesDisplacesAppendsAndStillCompiles() {
        var document = Document(entries: [
            new BindingPageEntryDefinition(Source: "pad.south", Command: "jump"),
            new BindingPageEntryDefinition(Source: "pad.east", Command: "menu"),
            new BindingPageEntryDefinition(Source: "pad.north", Command: "keep"),
        ]);
        var result = new BindingSessionResult(Captures: [
            new BindingSessionCapture(Command: "jump", Source: "pad.east", MatchedSuggestion: false),
            new BindingSessionCapture(Command: "new-action", Source: "pad.west", MatchedSuggestion: false),
        ]);

        var rewritten = result.Apply(
            document: document,
            pageId: "base",
            displaced: out var displaced
        );
        var entries = Assert.Single(rewritten.Chords).Page!.Entries;

        Assert.Equal(expected: "menu", actual: Assert.Single(displaced).Command);
        Assert.Equal(expected: "pad.east", actual: Assert.Single(entries, predicate: static entry => entry.Command == "jump").Source);
        Assert.Equal(expected: "pad.north", actual: Assert.Single(entries, predicate: static entry => entry.Command == "keep").Source);
        Assert.Equal(expected: "pad.west", actual: Assert.Single(entries, predicate: static entry => entry.Command == "new-action").Source);
        _ = BindingProfile.Compile(document: rewritten);
    }

    [Fact]
    public void PlanFromPageSkipsActivatorRowsAndReservesModifierSources() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "shift", Source: "pad.leftTrigger")],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [
                    new BindingPageEntryDefinition(Source: "pad.south", Command: "jump"),
                    new BindingPageEntryDefinition(
                        Source: null,
                        Command: "combo",
                        Activator: new BindingActivatorDefinition(Sequence: ["pad.west", "pad.north"])
                    ),
                ])
            )]
        );

        var plan = BindingSessionPlan.FromPage(
            document: document,
            pageId: "base",
            requiredPresses: 2
        );

        var step = Assert.Single(plan.Steps);

        Assert.Equal(expected: "jump", actual: step.Command);
        Assert.Equal(expected: "pad.south", actual: step.SuggestedSource);
        Assert.Equal(expected: 2, actual: plan.RequiredPresses);
        Assert.Equal(expected: "pad.leftTrigger", actual: Assert.Single(plan.ReservedSources!));
    }

    private static (IReadOnlyList<BindingSessionEvent> Events, IReadOnlyList<BindingSessionCapture> Captures) RunProtocol() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            Steps: [
                new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south"),
                new BindingSessionStep(Command: "interact", SuggestedSource: "pad.east"),
                new BindingSessionStep(Command: "target", SuggestedSource: "axis.leftTrigger"),
            ],
            RequiredPresses: 3,
            ReservedSources: ["pad.menu"],
            PressThreshold: 0.6f,
            ReleaseThreshold: 0.4f
        ));
        var events = new List<BindingSessionEvent> {
            Press(session: session, source: "pad.menu"),
            Press(session: session, source: "pad.south"),
            Press(session: session, source: "pad.north"),
            Press(session: session, source: "pad.south"),
            Press(session: session, source: "pad.south"),
            Press(session: session, source: "pad.south"),
            Press(session: session, source: "pad.south"),
            Press(session: session, source: "pad.west"),
            Press(session: session, source: "pad.west"),
            Press(session: session, source: "pad.west"),
        };

        events.Add(item: session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.59f)));
        events.Add(item: session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.7f)));
        events.Add(item: session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.55f)));
        _ = session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.3f));
        events.Add(item: session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.7f)));
        _ = session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.3f));
        events.Add(item: session.Advance(signal: Axis(source: "axis.rightTrigger", value: 0.7f)));

        Assert.Equal(expected: BindingSessionStatus.Completed, actual: session.Status);

        return (Events: events, Captures: session.Result.Captures);
    }

    private static BindingSessionEvent Press(BindingSession session, string source) {
        var result = session.Advance(signal: InputSignal.Press(source: source));

        _ = session.Advance(signal: InputSignal.Release(source: source));

        return result;
    }

    private static InputSignal Axis(string source, float value) {
        return new InputSignal(
            Source: source,
            DeviceId: default,
            Value: CommandValue.Axis(value: value),
            Phase: CommandPhase.Active
        );
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
