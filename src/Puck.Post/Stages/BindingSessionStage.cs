using Puck.Commands;
using Puck.Input;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. The guided-rebind protocol self-check (pure CPU): proves <see cref="BindingSession"/> — the
/// engine seam a binding tutorial builds on — implements the hardware-calibration wizard protocol over a
/// scripted signal sequence:
/// <list type="number">
/// <item><description>the walkthrough: each step prompts one command and the suggested press locks in after the
/// triple-press confirmation, releases and stick noise ignored (the drain rule);</description></item>
/// <item><description>deviation: a first press off the suggestion is reported loudly, then confirms onto the
/// player's choice like any other capture;</description></item>
/// <item><description>redo-on-mismatch: a confirmation press that wanders resets the whole step;</description></item>
/// <item><description>refusals: a reserved source (a page modifier) and a source an earlier step captured are
/// rejected, never bound;</description></item>
/// <item><description>analog capture: a trigger binds via the press/release hysteresis band — resting inside the
/// band is neither a press nor a release;</description></item>
/// <item><description>the round-trip: the result rewrites the profile page (deviations applied, the displaced
/// uncaptured entry dropped and reported) and the rewritten document still compiles;</description></item>
/// <item><description>determinism: two identical sessions produce a bit-for-bit identical event stream.</description></item>
/// </list>
/// </summary>
internal sealed class BindingSessionStage : IPostStage {
    private const string InteractCommand = "bindsession.interact";
    private const string JumpCommand = "bindsession.jump";
    private const string MenuCommand = "bindsession.menu";
    private const string PageId = "base";
    private const string TargetCommand = "bindsession.target";

    /// <inheritdoc/>
    public string Name => "binding-session";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // Two identical sessions must produce identical event streams (the machine is a pure function of its
        // signal sequence — no wall clock anywhere, per the calibration protocol's no-timers rule).
        var first = RunSession();
        var second = RunSession();

        if (Fold(events: first.Events) != Fold(events: second.Events)) {
            return PostStageOutcome.Fail(detail: "non-deterministic: two identical binding sessions produced different event streams");
        }

        // The protocol assertions: every scripted signal must have meant exactly what the protocol says.
        var expected = ExpectedEvents().ToArray();

        if (first.Events.Count != expected.Length) {
            return PostStageOutcome.Fail(detail: $"event count wrong: expected {expected.Length}, got {first.Events.Count}");
        }

        for (var index = 0; (index < expected.Length); index++) {
            var (kind, source, pressesRemaining) = expected[index];
            var actual = first.Events[index];

            if ((actual.Kind != kind) || !string.Equals(a: actual.Source, b: source, comparisonType: StringComparison.Ordinal) || (actual.PressesRemaining != pressesRemaining)) {
                return PostStageOutcome.Fail(detail: $"event {index} wrong: expected {kind}({source}, {pressesRemaining} remaining), got {actual.Kind}({actual.Source}, {actual.PressesRemaining} remaining)");
            }
        }

        if (first.Session.Status != BindingSessionStatus.Completed) {
            return PostStageOutcome.Fail(detail: $"the session did not complete (status {first.Session.Status}, step {first.Session.CurrentStepIndex})");
        }

        // The capture record: jump confirmed the suggestion; interact and target deviated.
        var captures = first.Session.Result.Captures;

        if ((captures.Count != 3) ||
            !Matches(capture: captures[0], command: JumpCommand, source: InputSources.Gamepad.ButtonSouth, matchedSuggestion: true) ||
            !Matches(capture: captures[1], command: InteractCommand, source: InputSources.Gamepad.ButtonEast, matchedSuggestion: false) ||
            !Matches(capture: captures[2], command: TargetCommand, source: InputSources.Gamepad.RightTrigger, matchedSuggestion: false)) {
            return PostStageOutcome.Fail(detail: $"capture record wrong: [{string.Join(separator: ", ", values: captures.Select(selector: static capture => $"{capture.Command}←{capture.Source}({(capture.MatchedSuggestion ? "suggested" : "deviated")})"))}]");
        }

        // The round-trip: apply onto the document, displacing the uncaptured menu entry (interact took its east).
        var applied = first.Session.Result.Apply(
            document: BuildProfile(),
            displaced: out var displaced,
            pageId: PageId
        );

        if ((displaced.Count != 1) || !string.Equals(a: displaced[0].Command, b: MenuCommand, comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Fail(detail: $"displacement wrong: expected exactly [{MenuCommand}], got [{string.Join(separator: ", ", values: displaced.Select(selector: static entry => entry.Command))}]");
        }

        var page = applied.Pages.Single(predicate: static page => string.Equals(a: page.Id, b: PageId, comparisonType: StringComparison.Ordinal));
        var sourceOf = page.Entries.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static entry => entry.Source,
            keySelector: static entry => entry.Command
        );

        if ((page.Entries.Count != 3) ||
            !string.Equals(a: sourceOf[JumpCommand], b: InputSources.Gamepad.ButtonSouth, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: sourceOf[InteractCommand], b: InputSources.Gamepad.ButtonEast, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: sourceOf[TargetCommand], b: InputSources.Gamepad.RightTrigger, comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Fail(detail: $"applied page wrong: [{string.Join(separator: ", ", values: page.Entries.Select(selector: static entry => $"{entry.Command}←{entry.Source}"))}]");
        }

        // The rewritten document must still be a valid profile (the session never forks the format).
        _ = BindingProfile.Compile(document: applied);

        return PostStageOutcome.Pass(detail: $"guided rebind verified over {expected.Length} events: triple-press lock, deviation callout, redo-on-mismatch, reserved/conflict refusal, analog hysteresis capture, displaced-entry round-trip, and two identical sessions folding bit-for-bit (0x{Fold(events: first.Events):X16})");
    }

    // The test profile: one trigger modifier (reserved by the plan) and a four-entry base page — three of them
    // walked by the plan, the fourth (menu, on East) present only to be displaced when interact deviates onto East.
    private static BindingProfileDocument BuildProfile() {
        return new BindingProfileDocument(
            Modifiers: [new BindingModifierDefinition(Id: "left", Source: InputSources.Gamepad.LeftTrigger),],
            Pages: [
                new BindingPageDefinition(
                    Chord: [],
                    Entries: [
                        new BindingPageEntryDefinition(Command: JumpCommand, Source: InputSources.Gamepad.ButtonSouth),
                        new BindingPageEntryDefinition(Command: InteractCommand, Source: InputSources.Gamepad.ButtonWest),
                        new BindingPageEntryDefinition(Command: TargetCommand, Source: InputSources.Gamepad.LeftShoulder),
                        new BindingPageEntryDefinition(Command: MenuCommand, Source: InputSources.Gamepad.ButtonEast),
                    ],
                    Id: PageId
                ),
                new BindingPageDefinition(
                    Chord: ["left"],
                    Entries: [new BindingPageEntryDefinition(Command: MenuCommand, Source: InputSources.Gamepad.ButtonSouth),],
                    Id: "left"
                ),
            ],
            Version: BindingProfileDocument.CurrentVersion
        );
    }
    private static bool Matches(BindingSessionCapture capture, string command, string source, bool matchedSuggestion) {
        return (string.Equals(a: capture.Command, b: command, comparisonType: StringComparison.Ordinal) &&
            string.Equals(a: capture.Source, b: source, comparisonType: StringComparison.Ordinal) &&
            (capture.MatchedSuggestion == matchedSuggestion));
    }

    // One session: the plan walks jump/interact/target (menu is deliberately not planned), reserving the page
    // modifier's trigger, and every scripted signal is applied in capture order.
    private static (BindingSession Session, IReadOnlyList<BindingSessionEvent> Events) RunSession() {
        var plan = new BindingSessionPlan(
            ReservedSources: [InputSources.Gamepad.LeftTrigger,],
            Steps: [
                new BindingSessionStep(Command: JumpCommand, SuggestedSource: InputSources.Gamepad.ButtonSouth),
                new BindingSessionStep(Command: InteractCommand, SuggestedSource: InputSources.Gamepad.ButtonWest),
                new BindingSessionStep(Command: TargetCommand, SuggestedSource: InputSources.Gamepad.LeftShoulder),
            ]
        );
        var session = new BindingSession(plan: plan);
        var events = new List<BindingSessionEvent>();

        foreach (var signal in Script()) {
            events.Add(item: session.Advance(signal: signal));
        }

        return (session, events);
    }

    // The scripted session. Comments give the protocol rule each signal exercises; ExpectedEvents() asserts the
    // machine's verdict on every one of them, releases and noise included.
    private static IEnumerable<InputSignal> Script() {
        var tick = 0UL;

        InputSignal Press(string source) => InputSignal.Press(captureTick: tick++, source: source);
        InputSignal Release(string source) => InputSignal.Release(captureTick: tick++, source: source);
        InputSignal Trigger(string source, float value) => new(
            CaptureTick: tick++,
            DeviceId: default,
            Phase: CommandPhase.Active,
            Source: source,
            Value: CommandValue.Axis(value: value)
        );

        // Step 0 (jump): the suggested press, confirmed twice more — the happy path.
        yield return Press(source: InputSources.Gamepad.ButtonSouth);
        yield return Release(source: InputSources.Gamepad.ButtonSouth);
        yield return Press(source: InputSources.Gamepad.ButtonSouth);
        yield return Release(source: InputSources.Gamepad.ButtonSouth);
        // Stick noise between confirmations: a 2-D axis is never a press (the drain rule).
        yield return new InputSignal(
            CaptureTick: tick++,
            DeviceId: default,
            Phase: CommandPhase.Active,
            Source: InputSources.Gamepad.LeftStick,
            Value: CommandValue.Axis(value: new System.Numerics.Vector2(x: 0.9f, y: 0.2f))
        );
        yield return Press(source: InputSources.Gamepad.ButtonSouth);
        yield return Release(source: InputSources.Gamepad.ButtonSouth);

        // Step 1 (interact, suggested West): the player presses East — the deviation callout.
        yield return Press(source: InputSources.Gamepad.ButtonEast);
        yield return Release(source: InputSources.Gamepad.ButtonEast);
        // A wandering confirmation (West, the suggestion itself!) — redo the whole step.
        yield return Press(source: InputSources.Gamepad.ButtonWest);
        yield return Release(source: InputSources.Gamepad.ButtonWest);
        // South already belongs to jump — refused.
        yield return Press(source: InputSources.Gamepad.ButtonSouth);
        yield return Release(source: InputSources.Gamepad.ButtonSouth);
        // The redo: East three times, cleanly.
        yield return Press(source: InputSources.Gamepad.ButtonEast);
        yield return Release(source: InputSources.Gamepad.ButtonEast);
        yield return Press(source: InputSources.Gamepad.ButtonEast);
        yield return Release(source: InputSources.Gamepad.ButtonEast);
        yield return Press(source: InputSources.Gamepad.ButtonEast);
        yield return Release(source: InputSources.Gamepad.ButtonEast);

        // Step 2 (target, suggested left bumper): the reserved page modifier is refused...
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.9f);
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0f);
        // ...and the player deviates onto the RIGHT trigger — an analog capture through the hysteresis band.
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.6f);
        // 0.45 sits between release (0.4) and press (0.5): still held — neither a press nor a release.
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.45f);
        // Rising again without having released: NOT a second press (no machine-gunned confirmations).
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.7f);
        // 0.3 crosses the release threshold — re-armed.
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.3f);
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.8f);
        // The release EDGE (Completed, value 0) re-arms just like a below-threshold sample.
        yield return new InputSignal(
            CaptureTick: tick++,
            DeviceId: default,
            Phase: CommandPhase.Completed,
            Source: InputSources.Gamepad.RightTrigger,
            Value: CommandValue.Axis(value: 0f)
        );
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.9f);

        // The session is complete: further presses mean nothing.
        yield return Press(source: InputSources.Gamepad.ButtonNorth);
    }

    // The machine's expected verdict on every scripted signal, in order.
    private static IEnumerable<(BindingSessionEventKind Kind, string? Source, int PressesRemaining)> ExpectedEvents() {
        const string east = InputSources.Gamepad.ButtonEast;
        const string leftTrigger = InputSources.Gamepad.LeftTrigger;
        const string rightTrigger = InputSources.Gamepad.RightTrigger;
        const string south = InputSources.Gamepad.ButtonSouth;
        const string west = InputSources.Gamepad.ButtonWest;

        // Step 0 (jump).
        yield return (BindingSessionEventKind.SuggestedCaptured, south, 2);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.ConfirmationProgress, south, 1);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.StepConfirmed, south, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        // Step 1 (interact).
        yield return (BindingSessionEventKind.DeviationCaptured, east, 2);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.Mismatch, west, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.ConflictRejected, south, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.DeviationCaptured, east, 2);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.ConfirmationProgress, east, 1);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.StepConfirmed, east, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        // Step 2 (target).
        yield return (BindingSessionEventKind.ReservedRejected, leftTrigger, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.DeviationCaptured, rightTrigger, 2);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.ConfirmationProgress, rightTrigger, 1);
        yield return (BindingSessionEventKind.None, null, 0);
        yield return (BindingSessionEventKind.SessionCompleted, rightTrigger, 0);
        // After completion.
        yield return (BindingSessionEventKind.None, null, 0);
    }

    // An explicit FNV-1a fold over the event stream (not System.HashCode, whose seed is per-process).
    private static ulong Fold(IReadOnlyList<BindingSessionEvent> events) {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;

        void FoldValue(ulong value) {
            for (var shift = 0; (shift < 64); shift += 8) {
                hash = ((hash ^ ((value >> shift) & 0xFFUL)) * prime);
            }
        }

        void FoldText(string? text) {
            FoldValue(value: ((ulong)(text?.Length ?? -1)));

            if (text is not null) {
                foreach (var character in text) {
                    FoldValue(value: character);
                }
            }
        }

        foreach (var entry in events) {
            FoldValue(value: ((ulong)entry.Kind) | (((ulong)((uint)entry.StepIndex)) << 8) | (((ulong)((uint)entry.PressesRemaining)) << 40));
            FoldText(text: entry.Source);
            FoldText(text: entry.ConflictingCommand);
        }

        return hash;
    }
}
