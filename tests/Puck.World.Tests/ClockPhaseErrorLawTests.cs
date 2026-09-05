using Xunit;

using Puck.Physics.Motion;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>$clock:&lt;music&gt;:phaseError</c> — the signed tick distance from the world's musical
/// clock's current position to the nearest beat. A hit window is an ordinary <c>compareState</c> range over this
/// read, so there is no dedicated effect or asset family (see <c>ActionEffect.Judge</c>/<c>WorldJudgeRow</c>'s
/// retirement in favour of this operand). Two families of law: the compiler resolves <c>music</c> against the
/// document's declared music row (refusing an undeclared or absent one by name), and a real clock's live value at
/// specific elapsed ticks matches the signed formula exactly, including the sign flip past half a beat.
/// </summary>
public sealed class ClockPhaseErrorLawTests {
    // Divides FixedTickConversion.TicksPerSecond (50400) exactly, matching CheckMusic's own divisibility check.
    private const long TicksPerBeat = 2100;

    [Fact]
    public void AnUndeclaredMusicNameRefuses() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-clock-law-").FullName;

        try {
            var declared = ClockDocument(assetDirectory: directory);

            Assert.False(WorldDefinitionValidator.TryValidateLocally(declared with {
                Rules = [Reader(state: "phaseError", clockName: "not-the-declared-name")],
            }, out var undeclaredReason));
            Assert.Contains("does not name the document's declared music row", undeclaredReason);

            // Control: the declared name compiles.
            Assert.True(WorldDefinitionValidator.TryValidateLocally(declared, out var okReason), okReason);
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    [Fact]
    public void AWorldWithNoMusicRefusesTheOperand() {
        var reason = default(string);
        var refused = !WorldDefinitionValidator.TryValidateLocally(Fixtures.BuildDocument() with {
            Rules = [Reader(state: "phaseError", clockName: "anything")],
            StateRaw = new(World: [Slot("phaseError")]),
        }, out reason);

        Assert.True(refused);
        Assert.Contains("does not name the document's declared music row", reason);
    }

    [Fact]
    public void PhaseErrorIsZeroExactlyOnTheBeat() {
        // Ten steps of Fixtures.StepTicks land ElapsedTicks at 10 * 210 == 2100, an exact beat boundary.
        Assert.Equal(0L, ReadPhaseErrorAfterSteps(steps: 10));
    }

    [Fact]
    public void PhaseErrorIsPositiveAndSmallJustAfterTheBeat() {
        // Eleven steps land at 2310 — 210 ticks past the 2100 beat, well inside half a beat (1050): late, so
        // positive, and small enough to be what a tight "perfect" window would admit.
        Assert.Equal(210L, ReadPhaseErrorAfterSteps(steps: 11));
    }

    [Fact]
    public void PhaseErrorFlipsSignPastHalfABeatReadingAsEarlyForTheNextBeat() {
        // Eighteen steps land at 3780 — 1680 past the 2100 beat, past half a beat (1050) toward the 4200 beat:
        // 1680 - 2100 == -420, negative (early for the NEXT beat) rather than a raw 1680 "late" reading.
        Assert.Equal(-420L, ReadPhaseErrorAfterSteps(steps: 18));
    }

    [Fact]
    public void AnAuthoredCompareStateRangeGradesAPressExactlyLikeADedicatedHitWindowWould() {
        // The doctrine's own claim: a hit window is nothing but an authored compareState range over phaseError,
        // gated on the SAME press edge a kit action would fire on. "perfect" within 100 ticks either side, "good"
        // within 300 — the same two tolerances the retired judge asset family exercised, now two ordinary rules
        // instead of a declared windows list; the tighter one is authored second so it overwrites on a tick both
        // hold, the "tightest wins" grading the windows list gave by declaration order.
        Assert.Equal("perfect", ReadGradeAfterPress(restSteps: 9));
        Assert.Equal("good", ReadGradeAfterPress(restSteps: 10));
    }

    private static long ReadPhaseErrorAfterSteps(int steps) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-clock-law-").FullName;

        try {
            using var fixture = Fixtures.FreshServer(definition: ClockDocument(assetDirectory: directory));

            for (var step = 0; (step < steps); step++) {
                fixture.Step();
            }

            return Value(fixture, "phaseError");
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    private static string ReadGradeAfterPress(int restSteps) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-clock-law-").FullName;

        try {
            var document = ClockDocument(assetDirectory: directory);
            var pressOrdinal = document.Channels.Count;
            var pressed = new ActionPredicate.CompareState(State: $"$channel:1:press", Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m);

            document = document with {
                ChannelsRaw = [.. document.Channels, new WorldChannel(Name: "press", Shape: ChannelShape.Binary, Composition: true)],
                StateRaw = new(World: [Slot("grade")]),
                Rules = [
                    new WorldRule(Name("gradeGood"), [new ActionEffect.SetState(State: "grade", Value: 2)], Gate: WithinTicks(pressed: pressed, tolerance: 300), Mode: ActionTriggerMode.Edge),
                    new WorldRule(Name("gradePerfect"), [new ActionEffect.SetState(State: "grade", Value: 1)], Gate: WithinTicks(pressed: pressed, tolerance: 100), Mode: ActionTriggerMode.Edge),
                ],
            };

            using var fixture = Fixtures.FreshServer(definition: document);
            var actor = WorldPrincipal.Seat(slot: 0);

            Assert.True(fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

            var body = fixture.Server.Body(index: actor.Index)!;

            for (var step = 0; (step < restSteps); step++) {
                fixture.Step();
            }

            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: pressOrdinal, value: Puck.Maths.FixedQ4816.One));
            fixture.Step();

            return Value(fixture, "grade") switch { 1 => "perfect", 2 => "good", _ => "miss" };
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    // phaseError is signed; a tolerance window is symmetric, so the gate is press AND |phaseError| <= tolerance —
    // the bound read as two ANDed comparisons, the ordinary compareState composition every other authored range in
    // this repository uses.
    private static ActionPredicate WithinTicks(ActionPredicate pressed, long tolerance) => new ActionPredicate.All(Predicates: [
        pressed,
        new ActionPredicate.CompareState(State: "$clock:test-score:phaseError", Comparison: ActionStateComparison.GreaterOrEqual, Value: -tolerance),
        new ActionPredicate.CompareState(State: "$clock:test-score:phaseError", Comparison: ActionStateComparison.LessOrEqual, Value: tolerance),
    ]);

    private static WorldRule Reader(string state, string clockName) => new(Name(state + "Reader"), [new ActionEffect.SetState(State: state, FromState: $"$clock:{clockName}:phaseError")]);

    private static WorldDefinition ClockDocument(string assetDirectory) {
        var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: "test-score",
            Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: ((int)TicksPerBeat)),
            Segments: [new MusicSegmentDocument(Id: "calm", Transitions: null)]
        ));
        var musicPath = Path.Combine(path1: assetDirectory, path2: "score.puck.music.v1.json");

        File.WriteAllBytes(path: musicPath, bytes: music.Bytes);

        return Fixtures.BuildDocument() with {
            Music = [new WorldMusicRow(Name: "test-score", Source: musicPath, Hash: music.Hash)],
            StateRaw = new(World: [Slot("phaseError")]),
            Rules = [Reader(state: "phaseError", clockName: "test-score")],
        };
    }
    private static CellName Name(string value) => CellName.Parse(value);
    private static WorldStateRow Slot(string name) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
