using Puck.Physics.Motion;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the subject-bearing overlay predicates, ranked HUD frame candidates, and seat-relative camera
/// anchors: authoring refusals (each with a passing control), the speaking presence curve, the state comparison,
/// ranked-winner selection with cross-fade, and per-seat view registration naming.</summary>
public sealed class OverlayPresentationPredicateLawTests {
    private const int RateHz = 240;

    private static readonly WorldHudRect UnitRect = new(Height: 1f, Width: 1f, X: 0f, Y: 0f);

    private static WorldCamera Camera(WorldAnchor? anchor = null, IReadOnlyList<WorldCameraAnchorCandidate>? anchors = null) => new(
        Name: "portrait",
        Anchor: anchor,
        Rig: new WorldCameraProgram(
            Name: "portrait-rig",
            Version: WorldCameraProgram.CurrentVersion,
            Operations: [new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 0.9f))]
        ),
        RenderWidth: 320U,
        RenderHeight: 240U,
        Anchors: anchors
    );
    private static WorldFrameSource CaptureSource(string title) => new WorldScreenSource.Capture(WindowTitle: title, Profile: WorldFeedProfile.Default);
    private static WorldHudElement FrameElement(WorldFrameSource? source = null, IReadOnlyList<WorldHudFrameCandidate>? sources = null, float fadeSeconds = 0f) => new(
        Id: "face",
        Kind: WorldHudElementKind.Frame,
        Rect: UnitRect,
        Style: WorldHudStyleToken.Primary,
        Source: source,
        Sources: sources,
        FadeSeconds: fadeSeconds
    );
    private static WorldStateRow IntRow(string name, long value) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)]
    );
    private static WorldStateRow TextRow(string name, string text) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: CellKind.Text,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: text)]
    );
    private static WorldDefinition WithCameras(params WorldCamera[] cameras) => Fixtures.BuildDocument() with { CamerasRaw = cameras };
    private static WorldDefinition WithPanel(OverlayPredicate? visible = null, WorldHudElement? element = null) => (Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [IntRow(name: "score", value: 3), TextRow(name: "phase", text: "lobby")]),
        HudRaw = new WorldHudSection(
            Defaults: new WorldHudDefaults(Enabled: true),
            Panels: [
                new WorldHudPanel(
                    Elements: ((element is { } authored) ? [authored] : []),
                    Id: "portrait",
                    Layer: WorldHudLayer.Over,
                    Rect: UnitRect,
                    Style: WorldHudPanelStyle.Chip,
                    Visible: visible
                ),
            ]
        ),
    });
    private static bool Validates(WorldDefinition definition) => WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out _
    );

    [Fact]
    public void ASpeakingPredicateRefusesANegativeWindowWhileANonNegativeOnePasses() {
        Laws.RefusalWithControl(
            lawId: "overlay.speaking-window",
            deniedOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.Speaking(Subject: new OverlaySubject.Seat(), WindowSeconds: -1f))),
            controlOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.Speaking(Subject: new OverlaySubject.Seat(), WindowSeconds: 1f, FadeSeconds: 0.5f))));
    }
    [Fact]
    public void ANearPredicateRefusesANegativeDistanceWhileANonNegativeOnePasses() {
        Laws.RefusalWithControl(
            lawId: "overlay.near-distance",
            deniedOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.Near(Subject: new OverlaySubject.RecentSpeaker(), Distance: -0.5f))),
            controlOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.Near(Subject: new OverlaySubject.RecentSpeaker(), Distance: 2f, Of: new OverlaySubject.AnySeat()))));
    }
    [Fact]
    public void AStatePredicateRefusesBothValueAndTextWhileExactlyOnePasses() {
        Laws.RefusalWithControl(
            lawId: "overlay.state-one-literal",
            deniedOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.State(Binding: "state.score", Value: 1f, Text: "x"))),
            controlOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.State(Binding: "state.score", Comparison: ActionStateComparison.Greater, Value: 1f))));
    }
    [Fact]
    public void AStatePredicateRefusesAnOrderedTextComparisonWhileEqualityPasses() {
        Laws.RefusalWithControl(
            lawId: "overlay.state-text-comparison",
            deniedOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.State(Binding: "state.phase", Comparison: ActionStateComparison.Less, Text: "lobby"))),
            controlOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.State(Binding: "state.phase", Comparison: ActionStateComparison.NotEqual, Text: "lobby"))));
    }
    [Fact]
    public void AStatePredicateRefusesALiteralOfTheWrongRowKind() {
        Laws.RefusalWithControl(
            lawId: "overlay.state-row-kind",
            deniedOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.State(Binding: "state.phase", Value: 1f))),
            controlOutcome: () => Validates(definition: WithPanel(visible: new OverlayPredicate.State(Binding: "state.phase", Text: "lobby"))));
    }
    [Fact]
    public void ACameraRefusesAnAnchorBesideARankedListWhileEitherAlonePasses() {
        var seat = new WorldAnchor.Seat();
        var ranked = new WorldCameraAnchorCandidate[] { new(Anchor: new WorldAnchor.RecentSpeaker(), When: new OverlayPredicate.Speaking(Subject: new OverlaySubject.RecentSpeaker(), WindowSeconds: 2f)), new(Anchor: seat) };

        Laws.RefusalWithControl(
            lawId: "camera.anchor-xor-anchors",
            deniedOutcome: () => Validates(definition: WithCameras(Camera(anchor: seat, anchors: ranked))),
            controlOutcome: () => (Validates(definition: WithCameras(Camera(anchors: ranked))) && Validates(definition: WithCameras(Camera(anchor: seat)))));
    }
    [Fact]
    public void ACameraRefusesAnEmptyRankedListWhileOneCandidatePasses() {
        Laws.RefusalWithControl(
            lawId: "camera.anchors-non-empty",
            deniedOutcome: () => Validates(definition: WithCameras(Camera(anchors: []))),
            controlOutcome: () => Validates(definition: WithCameras(Camera(anchors: [new WorldCameraAnchorCandidate(Anchor: new WorldAnchor.Seat())]))));
    }
    [Fact]
    public void ASeatAnchorRefusesSeatZeroWhileSeatOneAndTheEnclosingSeatPass() {
        Laws.RefusalWithControl(
            lawId: "camera.seat-anchor-number",
            deniedOutcome: () => Validates(definition: WithCameras(Camera(anchor: new WorldAnchor.Seat(Number: 0)))),
            controlOutcome: () => (Validates(definition: WithCameras(Camera(anchor: new WorldAnchor.Seat(Number: 1)))) && Validates(definition: WithCameras(Camera(anchor: new WorldAnchor.Seat())))));
    }
    [Fact]
    public void ARankedCandidateRefusesAnInvalidConditionWhileAValidOnePasses() {
        static WorldCameraAnchorCandidate Candidate(float window) => new(
            Anchor: new WorldAnchor.RecentSpeaker(),
            When: new OverlayPredicate.Speaking(Subject: new OverlaySubject.RecentSpeaker(), WindowSeconds: window)
        );

        Laws.RefusalWithControl(
            lawId: "camera.anchors-when",
            deniedOutcome: () => Validates(definition: WithCameras(Camera(anchors: [Candidate(window: float.NaN)]))),
            controlOutcome: () => Validates(definition: WithCameras(Camera(anchors: [Candidate(window: 1f)]))));
    }
    [Fact]
    public void AFrameElementRefusesSourceBesideSourcesWhileEitherAlonePasses() {
        var candidates = new WorldHudFrameCandidate[] { new(Source: CaptureSource(title: "a"), When: new OverlayPredicate.Now(Fact: OverlayFact.ConsoleOpen)), new(Source: CaptureSource(title: "b")) };

        Laws.RefusalWithControl(
            lawId: "hud.frame-source-xor-sources",
            deniedOutcome: () => Validates(definition: WithPanel(element: FrameElement(source: CaptureSource(title: "a"), sources: candidates))),
            controlOutcome: () => (Validates(definition: WithPanel(element: FrameElement(sources: candidates))) && Validates(definition: WithPanel(element: FrameElement(source: CaptureSource(title: "a"))))));
    }
    [Fact]
    public void AFrameElementRefusesANegativeFadeWhileZeroPasses() {
        Laws.RefusalWithControl(
            lawId: "hud.frame-fade",
            deniedOutcome: () => Validates(definition: WithPanel(element: FrameElement(source: CaptureSource(title: "a"), fadeSeconds: -1f))),
            controlOutcome: () => Validates(definition: WithPanel(element: FrameElement(source: CaptureSource(title: "a"), fadeSeconds: 0f))));
    }
    [Fact]
    public void SpeakingPresenceHoldsThroughTheWindowEasesAcrossTheFadeThenCuts() {
        var clock = new WorldSpeechClock();
        const ulong spoke = 1_000UL;
        const float window = 1f;
        const float fade = 1f;

        clock.NoteSpoke(bodyIndex: 2, tick: spoke);

        static float At(WorldSpeechClock clock, ulong now) => OverlayRecency.Presence(
            completedTick: now,
            fadeSeconds: fade,
            lastHeldTick: clock.LastSpokeTick(bodyIndex: 2),
            rateHz: RateHz,
            windowSeconds: window
        );

        Assert.Equal(expected: 2, actual: clock.RecentSpeakerBody);
        Assert.Equal(expected: 1f, actual: At(clock: clock, now: spoke));
        Assert.Equal(expected: 1f, actual: At(clock: clock, now: (spoke + (RateHz / 2))));

        var midFade = At(clock: clock, now: ((spoke + RateHz) + (RateHz / 2)));

        Assert.InRange(actual: midFade, high: 0.75f, low: 0.25f);
        Assert.Equal(expected: 0f, actual: At(clock: clock, now: ((spoke + (2 * RateHz)) + 1)));
        Assert.Equal(expected: 0f, actual: OverlayRecency.Presence(completedTick: spoke, fadeSeconds: fade, lastHeldTick: clock.LastSpokeTick(bodyIndex: 3), rateHz: RateHz, windowSeconds: window));
    }
    [Fact]
    public void AZeroFadeCutsAtTheWindowEnd() {
        var clock = new WorldSpeechClock();

        clock.NoteSpoke(bodyIndex: 0, tick: 10UL);

        Assert.Equal(expected: 1f, actual: OverlayRecency.Presence(completedTick: ((10UL + RateHz) - 1), fadeSeconds: 0f, lastHeldTick: clock.LastSpokeTick(bodyIndex: 0), rateHz: RateHz, windowSeconds: 1f));
        Assert.Equal(expected: 0f, actual: OverlayRecency.Presence(completedTick: (10UL + RateHz), fadeSeconds: 0f, lastHeldTick: clock.LastSpokeTick(bodyIndex: 0), rateHz: RateHz, windowSeconds: 1f));
    }
    [Fact]
    public void TheRecentSpeakerIsTheLatestStamp() {
        var clock = new WorldSpeechClock();

        clock.NoteSpoke(bodyIndex: 4, tick: 5UL);
        clock.NoteSpoke(bodyIndex: 1, tick: 9UL);

        Assert.Equal(expected: 1, actual: clock.RecentSpeakerBody);
        Assert.Equal(expected: 5UL, actual: clock.LastSpokeTick(bodyIndex: 4));
        Assert.Equal(expected: 0UL, actual: clock.LastSpokeTick(bodyIndex: -1));
    }
    [Fact]
    public void AStatePredicateComparesTextOrdinallyAndNumbersThroughTheComparison() {
        var definition = WithPanel();

        Assert.True(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.phase", Text: "lobby"), tick: 0UL));
        Assert.False(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.phase", Text: "Lobby"), tick: 0UL));
        Assert.True(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.phase", Comparison: ActionStateComparison.NotEqual, Text: "arena"), tick: 0UL));
        Assert.True(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.score", Comparison: ActionStateComparison.Greater, Value: 2.5f), tick: 0UL));
        Assert.False(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.score", Comparison: ActionStateComparison.Less, Value: 3f), tick: 0UL));
        Assert.True(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.score", Value: 3f), tick: 0UL));
        Assert.False(condition: OverlayStateComparison.Holds(definition: definition, state: new OverlayPredicate.State(Binding: "state.missing", Value: 3f), tick: 0UL));
    }
    [Fact]
    public void TheFirstHoldingCandidateWinsAndNoneHoldingIsNoWinner() {
        var candidates = new OverlayPredicate?[] {
            new OverlayPredicate.Now(Fact: OverlayFact.ConsoleOpen),
            new OverlayPredicate.Now(Fact: OverlayFact.WheelOpen),
            null,
        };
        var evaluator = new FactEvaluator(holding: OverlayFact.WheelOpen);

        Assert.Equal(expected: 1, actual: OverlayRanking.FirstHolding(candidates: candidates, evaluator: evaluator, slot: 0, when: static when => when));
        Assert.Equal(expected: 2, actual: OverlayRanking.FirstHolding(candidates: candidates, evaluator: new FactEvaluator(holding: null), slot: 0, when: static when => when));
        Assert.Equal(expected: -1, actual: OverlayRanking.FirstHolding(candidates: candidates[..2], evaluator: new FactEvaluator(holding: null), slot: 0, when: static when => when));
        Assert.Equal(expected: 0, actual: OverlayRanking.FirstHolding(candidates: candidates, evaluator: null, slot: 0, when: static when => when));
    }
    [Fact]
    public void AWinnerChangeHoldsBothKeysUntilTheFadeCompletes() {
        var fade = new OverlayFrameCrossfade();

        fade.Advance(fadeSeconds: 1f, nowSeconds: 0.0, winner: 7);
        Assert.Equal(expected: (7, -1, 1f), actual: (fade.Current, fade.Outgoing, fade.Mix));

        fade.Advance(fadeSeconds: 1f, nowSeconds: 10.0, winner: 9);
        Assert.Equal(expected: (9, 7), actual: (fade.Current, fade.Outgoing));
        Assert.Equal(expected: 0f, actual: fade.Mix);

        fade.Advance(fadeSeconds: 1f, nowSeconds: 10.5, winner: 9);
        Assert.Equal(expected: 7, actual: fade.Outgoing);
        Assert.InRange(actual: fade.Mix, low: 0.25f, high: 0.75f);

        fade.Advance(fadeSeconds: 1f, nowSeconds: 11.0, winner: 9);
        Assert.Equal(expected: (9, -1, 1f), actual: (fade.Current, fade.Outgoing, fade.Mix));
    }
    [Fact]
    public void AZeroFadeCutsToTheNewWinner() {
        var fade = new OverlayFrameCrossfade();

        fade.Advance(fadeSeconds: 0f, nowSeconds: 0.0, winner: 1);
        fade.Advance(fadeSeconds: 0f, nowSeconds: 0.1, winner: 2);

        Assert.Equal(expected: (2, -1, 1f), actual: (fade.Current, fade.Outgoing, fade.Mix));
    }
    [Fact]
    public void ASeatRelativeCameraRegistersPerSeatWhileASharedCameraRegistersOnce() {
        var seatCamera = Camera(anchors: [new WorldCameraAnchorCandidate(Anchor: new WorldAnchor.RecentSpeaker(), When: new OverlayPredicate.Speaking(Subject: new OverlaySubject.RecentSpeaker(), WindowSeconds: 1f)), new WorldCameraAnchorCandidate(Anchor: new WorldAnchor.Seat())]);
        var sharedCamera = Camera(anchor: new WorldAnchor.Entity(Index: 0));

        Assert.True(condition: seatCamera.IsSeatRelative);
        Assert.False(condition: sharedCamera.IsSeatRelative);
        Assert.NotEqual(expected: WorldSeatAnchors.RegistrationName(camera: seatCamera, seat: 1), actual: WorldSeatAnchors.RegistrationName(camera: seatCamera, seat: 2));
        Assert.Equal(expected: WorldSeatAnchors.RegistrationName(camera: sharedCamera, seat: 1), actual: WorldSeatAnchors.RegistrationName(camera: sharedCamera, seat: 2));
        Assert.Equal(expected: sharedCamera.Name, actual: WorldSeatAnchors.RegistrationName(camera: sharedCamera, seat: 3));
    }
    [Fact]
    public void ASeatAnchorFollowsTheSeatsPerceivedBodyAndARecentSpeakerFollowsTheClock() {
        var perception = new WorldPerceptionAnchor();
        var speech = new WorldSpeechClock();

        perception.Publish(bodyIndex: 9, slot: 1);

        Assert.Equal(expected: 9, actual: WorldSeatAnchors.BodyOf(anchor: new WorldAnchor.Seat(), slot: 1, perception: perception, speech: speech));
        Assert.Equal(expected: 9, actual: WorldSeatAnchors.BodyOf(anchor: new WorldAnchor.Seat(Number: 2), slot: 0, perception: perception, speech: speech));
        Assert.Equal(expected: 0, actual: WorldSeatAnchors.BodyOf(anchor: new WorldAnchor.Seat(Number: 1), slot: 1, perception: perception, speech: speech));
        Assert.Equal(expected: -1, actual: WorldSeatAnchors.BodyOf(anchor: new WorldAnchor.RecentSpeaker(), slot: 0, perception: perception, speech: speech));

        speech.NoteSpoke(bodyIndex: 5, tick: 1UL);

        Assert.Equal(expected: 5, actual: WorldSeatAnchors.BodyOf(anchor: new WorldAnchor.RecentSpeaker(), slot: 0, perception: perception, speech: speech));
        Assert.Equal(expected: -1, actual: WorldSeatAnchors.BodyOf(anchor: new WorldAnchor.Entity(Index: 3), slot: 0, perception: perception, speech: speech));
    }
    [Fact]
    public void ARankedCameraSelectsTheFirstHoldingCandidateAndTheWorldFrameWhenNoneHolds() {
        var speaking = new OverlayPredicate.Now(Fact: OverlayFact.WheelOpen);
        var camera = Camera(anchors: [new WorldCameraAnchorCandidate(Anchor: new WorldAnchor.RecentSpeaker(), When: speaking), new WorldCameraAnchorCandidate(Anchor: new WorldAnchor.Seat(), When: new OverlayPredicate.Now(Fact: OverlayFact.ConsoleOpen))]);

        var winner = WorldSeatAnchors.SelectAnchor(camera: camera, candidateIndex: out var winnerIndex, evaluator: new FactEvaluator(holding: OverlayFact.WheelOpen), slot: 0);
        var none = WorldSeatAnchors.SelectAnchor(camera: camera, candidateIndex: out var noneIndex, evaluator: new FactEvaluator(holding: null), slot: 0);
        var bare = WorldSeatAnchors.SelectAnchor(camera: Camera(anchor: new WorldAnchor.Seat()), candidateIndex: out var bareIndex, evaluator: new FactEvaluator(holding: null), slot: 0);

        Assert.IsType<WorldAnchor.RecentSpeaker>(@object: winner);
        Assert.Equal(actual: winnerIndex, expected: 0);
        Assert.Null(@object: none);
        Assert.Equal(actual: noneIndex, expected: -1);
        Assert.IsType<WorldAnchor.Seat>(@object: bare);
        Assert.Equal(actual: bareIndex, expected: -1);
    }

    private sealed class FactEvaluator(OverlayFact? holding) : IOverlayPredicateEvaluator {
        public bool Evaluate(int slot, OverlayPredicate? predicate) => predicate switch {
            null => true,
            OverlayPredicate.Now now => (now.Fact == holding),
            _ => false,
        };
        public bool EvaluateAnySeat(OverlayPredicate? predicate) => Evaluate(predicate: predicate, slot: 0);
    }
}
