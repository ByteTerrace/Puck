using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingSessionTests {
    [Fact]
    public void SessionProtocolIsDeterministicAcrossSuggestionsMismatchesConflictsAndAnalogHysteresis() {
        var first = RunProtocol();
        var second = RunProtocol();

        Assert.Equal(actual: second.Events, expected: first.Events);
        Assert.Equal(actual: second.Captures, expected: first.Captures);
        Assert.Contains(collection: first.Events, filter: static item => (item.Kind == BindingSessionEventKind.ReservedRejected));
        Assert.Contains(collection: first.Events, filter: static item => (item.Kind == BindingSessionEventKind.Mismatch));
        Assert.Contains(collection: first.Events, filter: static item => (item.Kind == BindingSessionEventKind.ConflictRejected));
        Assert.Contains(collection: first.Events, filter: static item => (item.Kind == BindingSessionEventKind.ConfirmationProgress));
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
            new BindingPageEntryDefinition(Sources: ["pad.south"], Command: "jump"),
            new BindingPageEntryDefinition(Sources: ["pad.east"], Command: "menu"),
            new BindingPageEntryDefinition(Sources: ["pad.north"], Command: "keep"),
        ]);
        var result = new BindingSessionResult(Captures: [
            new BindingSessionCapture(Command: "jump", Source: "pad.east", MatchedSuggestion: false),
            new BindingSessionCapture(Command: "new-action", Source: "pad.west", MatchedSuggestion: false),
        ]);

        var rewritten = result.Apply(
            displaced: out var displaced,
            document: document,
            pageId: "base"
        );
        var entries = Assert.Single(collection: rewritten.Chords).Page!.Entries;

        Assert.Equal(expected: "menu", actual: Assert.Single(collection: displaced).Command);
        Assert.Equal(expected: "pad.east", actual: Assert.Single(entries, predicate: static entry => (entry.Command == "jump")).Sources![0]);
        Assert.Equal(expected: "pad.north", actual: Assert.Single(entries, predicate: static entry => (entry.Command == "keep")).Sources![0]);
        Assert.Equal(expected: "pad.west", actual: Assert.Single(entries, predicate: static entry => (entry.Command == "new-action")).Sources![0]);
        _ = BindingProfile.Compile(document: rewritten);
    }
    [Fact]
    public void PlanFromPageSkipsActivatorRowsAndReservesModifierSources() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "shift", Sources: ["pad.leftTrigger"])],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [
                    new BindingPageEntryDefinition(Sources: ["pad.south"], Command: "jump"),
                    new BindingPageEntryDefinition(
                        Sources: null,
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

        var step = Assert.Single(collection: plan.Steps);

        Assert.Equal(expected: "jump", actual: step.Command);
        Assert.Equal(expected: "pad.south", actual: step.SuggestedSource);
        Assert.Equal(expected: 2, actual: plan.RequiredPresses);
        Assert.Equal(expected: "pad.leftTrigger", actual: Assert.Single(collection: plan.ReservedSources!));
    }
    [Fact]
    public void ResultMatchesCommandIdentityCaseInsensitively() {
        var document = Document(entries: [new BindingPageEntryDefinition(
            Sources: ["key.old"],
            Command: "jump"
        )]);
        var result = new BindingSessionResult(Captures: [new BindingSessionCapture(
            Command: "JUMP",
            Source: "key.new",
            MatchedSuggestion: false
        )]);

        var rewritten = result.Apply(
            displaced: out _,
            document: document,
            pageId: "base"
        );
        var entry = Assert.Single(collection: Assert.Single(collection: rewritten.Chords).Page!.Entries);

        Assert.Equal(expected: "jump", actual: entry.Command);
        Assert.Equal(expected: "key.new", actual: entry.Sources![0]);
    }
    [Fact]
    public void ASinglePressPlanConfirmsOnTheVeryFirstPress() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        ));
        var confirmed = session.Advance(signal: InputSignal.Press(source: "pad.north"));

        Assert.Equal(expected: BindingSessionEventKind.SessionCompleted, actual: confirmed.Kind);
        Assert.Equal(expected: 0, actual: confirmed.StepIndex);
        Assert.Equal(expected: BindingSessionStatus.Completed, actual: session.Status);
        Assert.Equal(expected: "pad.north", actual: Assert.Single(collection: session.Captures).Source);
    }
    [Fact]
    public void ACompletedSessionIsInertAndSaysWhichStepItStoppedOn() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        ));

        _ = session.Advance(signal: InputSignal.Press(source: "pad.south"));

        var ignored = session.Advance(signal: InputSignal.Press(source: "pad.north"));

        Assert.Equal(expected: BindingSessionEventKind.None, actual: ignored.Kind);
        // The step index is the one the session actually stands on — never a hard-coded zero that reads as a rewind.
        Assert.Equal(expected: 1, actual: ignored.StepIndex);
        Assert.Equal(expected: "pad.south", actual: Assert.Single(collection: session.Captures).Source);
    }
    [Fact]
    public void ANonPressCarriesTheStepStillBeingPrompted() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            Steps: [
                new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south"),
                new BindingSessionStep(Command: "interact", SuggestedSource: "pad.east"),
            ]
        ));

        _ = session.Advance(signal: InputSignal.Press(source: "pad.south"));

        var release = session.Advance(signal: InputSignal.Release(source: "pad.south"));

        Assert.Equal(expected: BindingSessionEventKind.None, actual: release.Kind);
        Assert.Equal(expected: 1, actual: release.StepIndex);
    }
    [Fact]
    public void AbandonStopsTheMachineAndKeepsTheCapturesItAlreadyConfirmed() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            Steps: [
                new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south"),
                new BindingSessionStep(Command: "interact", SuggestedSource: "pad.east"),
            ]
        ));

        _ = session.Advance(signal: InputSignal.Press(source: "pad.south"));

        Assert.True(condition: session.Abandon());
        Assert.False(condition: session.Abandon());
        Assert.Equal(expected: BindingSessionStatus.Abandoned, actual: session.Status);
        Assert.Null(@object: session.PendingSource);
        Assert.Equal(expected: BindingSessionEventKind.None, actual: session.Advance(signal: InputSignal.Press(source: "pad.east")).Kind);
        Assert.Equal(expected: "jump", actual: Assert.Single(collection: session.Result.Captures).Command);
    }
    [Fact]
    public void ACompletedSessionHasNothingLeftToAbandon() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        ));

        _ = session.Advance(signal: InputSignal.Press(source: "pad.south"));

        Assert.False(condition: session.Abandon());
        Assert.Equal(expected: BindingSessionStatus.Completed, actual: session.Status);
    }
    [Fact]
    public void TheResultIsCachedUntilTheNextCaptureConfirms() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            Steps: [
                new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south"),
                new BindingSessionStep(Command: "interact", SuggestedSource: "pad.east"),
            ]
        ));
        var empty = session.Result;

        Assert.Same(actual: session.Result, expected: empty);

        _ = session.Advance(signal: InputSignal.Press(source: "pad.south"));

        var afterFirst = session.Result;

        Assert.NotSame(actual: afterFirst, expected: empty);
        Assert.Same(actual: session.Result, expected: afterFirst);
    }
    [Fact]
    public void APlanWithNoReservedSourcesRefusesNothing() {
        var session = new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 1,
            ReservedSources: null,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        ));

        Assert.Equal(expected: BindingSessionEventKind.SessionCompleted, actual: session.Advance(signal: InputSignal.Press(source: "pad.south")).Kind);
    }
    [Fact]
    public void AMalformedPlanIsRefusedAtConstruction() {
        _ = Assert.Throws<ArgumentNullException>(testCode: static () => new BindingSession(plan: null!));
        _ = Assert.Throws<ArgumentException>(testCode: static () => new BindingSession(plan: new BindingSessionPlan(Steps: [])));
        _ = Assert.Throws<ArgumentException>(testCode: static () => new BindingSession(plan: new BindingSessionPlan(
            RequiredPresses: 0,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => new BindingSession(plan: new BindingSessionPlan(
            PressThreshold: float.NaN,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        )));
        _ = Assert.Throws<ArgumentException>(testCode: static () => new BindingSession(plan: new BindingSessionPlan(
            PressThreshold: 0.4f,
            ReleaseThreshold: 0.5f,
            Steps: [new BindingSessionStep(Command: "jump", SuggestedSource: "pad.south")]
        )));
    }
    [Fact]
    public void PlanFromPageAlsoReservesARawChordMemberThatBecomesAnImplicitModifier() {
        // The document declares NO modifiers, but a chord row names a raw source id — BindingProfile.Compile mints
        // an implicit modifier for it, so capturing it onto a command would flip the page instead of firing.
        var plan = BindingSessionPlan.FromPage(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: [
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: [],
                        Page: new BindingPageDefinition(Id: "base", Entries: [new BindingPageEntryDefinition(
                            Sources: ["pad.south"],
                            Command: "jump"
                        )])
                    ),
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: ["pad.leftShoulder"],
                        Held: ["pad.rightShoulder"],
                        Page: new BindingPageDefinition(Id: "held", Entries: [])
                    ),
                ]
            ),
            pageId: "base",
            requiredPresses: 1
        );

        Assert.Contains(collection: plan.ReservedSources!, expected: "pad.leftShoulder");
        Assert.Contains(collection: plan.ReservedSources!, expected: "pad.rightShoulder");

        var session = new BindingSession(plan: plan);

        Assert.Equal(expected: BindingSessionEventKind.ReservedRejected, actual: session.Advance(signal: InputSignal.Press(source: "pad.leftShoulder")).Kind);
    }
    [Fact]
    public void PlanFromPageRefusesAPageWhoseEveryEntryIsActivatorTriggered() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [new BindingPageEntryDefinition(
                    Sources: null,
                    Command: "combo",
                    Activator: new BindingActivatorDefinition(Sequence: ["pad.west", "pad.north"])
                )])
            )]
        );

        _ = Assert.Throws<ArgumentException>(testCode: () => BindingSessionPlan.FromPage(
            document: document,
            pageId: "base"
        ));
    }
    [Fact]
    public void AChannelStepIsWrittenBackAsAChannelEntryNotAsItsInternalCommandName() {
        var document = Document(entries: [new BindingPageEntryDefinition(
            Sources: ["key.old"],
            Channel: new ChannelRef.Name(Value: "movement")
        )]);
        var plan = BindingSessionPlan.FromPage(
            document: document,
            pageId: "base",
            requiredPresses: 1
        );
        var session = new BindingSession(plan: plan);

        _ = session.Advance(signal: InputSignal.Press(source: "key.new"));

        var rewritten = session.Result.Apply(
            displaced: out _,
            document: document,
            pageId: "base"
        );
        var entry = Assert.Single(collection: Assert.Single(collection: rewritten.Chords).Page!.Entries);

        Assert.Equal(actual: entry.Channel, expected: new ChannelRef.Name(Value: "movement"));
        Assert.Null(@object: entry.Command);
        Assert.Equal(expected: "key.new", actual: entry.Sources![0]);
        _ = BindingProfile.Compile(document: rewritten);
    }
    [Fact]
    public void ACapturedChannelWithNoExistingEntryIsAppendedAsAChannelRow() {
        var document = Document(entries: [new BindingPageEntryDefinition(
            Sources: ["key.keep"],
            Command: "keep"
        )]);
        var result = new BindingSessionResult(Captures: [new BindingSessionCapture(
            Channel: new ChannelRef.Name(Value: "movement"),
            Command: BindingProfile.ChannelCommandName(channel: new ChannelRef.Name(Value: "movement")),
            MatchedSuggestion: false,
            Source: "key.new"
        )]);
        var rewritten = result.Apply(
            displaced: out _,
            document: document,
            pageId: "base"
        );
        var appended = Assert.Single(
            Assert.Single(collection: rewritten.Chords).Page!.Entries,
            predicate: static entry => (entry.Channel is not null)
        );

        Assert.Null(@object: appended.Command);
        Assert.Equal(expected: "key.new", actual: appended.Sources![0]);
        _ = BindingProfile.Compile(document: rewritten);
    }
    [Fact]
    public void TwoCapturesOfOneCommandLeaveThatCommandOnTheLastConfirmedSource() {
        var document = Document(entries: [new BindingPageEntryDefinition(
            Sources: ["key.old"],
            Command: "jump"
        )]);
        var result = new BindingSessionResult(Captures: [
            new BindingSessionCapture(Command: "jump", Source: "key.first", MatchedSuggestion: false),
            new BindingSessionCapture(Command: "jump", Source: "key.second", MatchedSuggestion: false),
        ]);
        var rewritten = result.Apply(
            displaced: out var displaced,
            document: document,
            pageId: "base"
        );
        var entry = Assert.Single(collection: Assert.Single(collection: rewritten.Chords).Page!.Entries);

        Assert.Empty(collection: displaced);
        Assert.Equal(expected: "key.second", actual: entry.Sources![0]);
        _ = BindingProfile.Compile(document: rewritten);
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
