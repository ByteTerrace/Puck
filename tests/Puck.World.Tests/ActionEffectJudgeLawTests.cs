using Xunit;

using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>ActionEffect.Judge</c> — the action-effect kind that stages a rhythm-judge grading fact
/// on press (bodyIndex, judgeRef), drained and graded by <c>WorldServer.Step</c> against
/// <c>Puck.Audio.Simulation.MusicClock</c>/<c>RhythmJudge</c> immediately after the body step. Two laws: the
/// validator resolves a press's <c>judgeRef</c> against the declared <c>judges</c> table (refusing an undeclared
/// name by name), and a real press against a real clock lands the documented grade on the tick it fired.
/// </summary>
public sealed class ActionEffectJudgeLawTests {
    // The one composition channel these laws bind the judge effect to — see PlanarImpulseUnitDirectionLawTests'
    // own remarks on why appending one channel to Fixtures.BuildDocument's three gives it this ordinal. Internal
    // (not private): MusicJudgeReplayReDerivabilityLawTests reuses this whole fixture shape rather than forking it.
    internal const int JudgeOrdinal = 3;
    // Divides FixedTickConversion.TicksPerSecond (50400) exactly, matching CheckMusic's own divisibility check.
    internal const long TicksPerBeat = 2100;
    internal const string JudgeRowName = "test-judge";

    [Fact]
    public void UndeclaredJudgeRefRefusesByName() {
        var document = JudgeActionDocument(judgeRows: []);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "an undeclared judgeRef was expected to refuse");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "judgeRef");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: JudgeRowName);
    }
    [Fact]
    public void DeclaredJudgeRefControl() {
        // The row's own Source does not resolve on disk, so the document as a whole still refuses — but on the
        // ROW's own asset check, never on the action's judgeRef resolution, which is the one fact this control
        // discriminates against the denial case above (same document shape, only the declaration differs).
        var document = JudgeActionDocument(judgeRows: [new WorldJudgeRow(Hash: "0000000000000000000000000000000000000000000000000000000000000000", Name: JudgeRowName, Source: "does-not-exist.puck.judge.v1.json")]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason));
        Assert.DoesNotContain(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "judgeRef");
    }
    [Fact]
    public void PressOnTheBeatProducesPerfect() {
        // Nine rest steps, THEN the press — the rising edge (and so the judge firing) lands on the tenth call,
        // whose ElapsedTicks (10 * Fixtures.StepTicks == 10 * 210 == 2100) is an EXACT beat boundary: distance 0,
        // inside the 100-tick "perfect" window.
        var answer = PressAfterRestStepsAndGrade(restSteps: 9);

        Assert.Contains(actualString: answer, comparisonType: StringComparison.Ordinal, expectedSubstring: $"body0.{JudgeRowName}.grade=perfect body0.{JudgeRowName}.tick=2100");
    }
    [Fact]
    public void PressOffTheBeatProducesGood() {
        // Ten rest steps, then the press on the eleventh call: ElapsedTicks lands at 2310 — 210 ticks past the 2100
        // beat, outside the 100-tick "perfect" window but inside the 300-tick "good" one. Same mechanism as the
        // control above; only the timing differs, so the two facts together discriminate the tolerance boundary
        // rather than merely proving "it grades".
        var answer = PressAfterRestStepsAndGrade(restSteps: 10);

        Assert.Contains(actualString: answer, comparisonType: StringComparison.Ordinal, expectedSubstring: $"body0.{JudgeRowName}.grade=good body0.{JudgeRowName}.tick=2310");
    }
    [Fact]
    public void LastGradeRoundTripsThroughCheckpoint() {
        // WorldPopulation.Capture already asserts the STAGED fact list is empty at capture time (drained within the
        // same Step call the fact was raised in — never a checkpoint concern of its own); only the persistent
        // last-grade table is sim state a checkpoint owes, proven here the same way Activation_roundtrip_identity
        // proves every other server-section field: capture, encode/decode through the wire codec, restore into a
        // FRESH server, and read the SAME judge.state back.
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-judge-law-").FullName;

        try {
            using var fixture = PressedFixture(assetDirectory: directory, restSteps: 9);

            Assert.True(condition: fixture.Server.TryCaptureCheckpoint(hostRow: EmptyHostRow(), checkpoint: out var checkpoint, reason: out var refusal), userMessage: refusal);
            Assert.NotNull(@object: checkpoint);

            var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

            Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: encoded, checkpoint: out var decoded, reason: out var decodeReason), userMessage: decodeReason);

            var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
            using var restoredMachines = new WorldMachineHost(engines: [], screens: definition.Screens);

            var (restoredServer, _) = WorldServer.FromCheckpoint(
                checkpoint: decoded,
                instanceIdentity: "boot",
                machines: restoredMachines,
                profiles: new WorldOwnedWorlds(directory: Directory.CreateTempSubdirectory(prefix: "puck-judge-law-restored-").FullName, machineId: Guid.NewGuid(), template: definition)
            );

            var expected = fixture.Server.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text;
            var restored = restoredServer.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text;

            Assert.Contains(actualString: expected, comparisonType: StringComparison.Ordinal, expectedSubstring: $"body0.{JudgeRowName}.grade=perfect body0.{JudgeRowName}.tick=2100");
            Assert.Equal(actual: restored, expected: expected);
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static string PressAfterRestStepsAndGrade(int restSteps) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-judge-law-").FullName;

        try {
            using var fixture = PressedFixture(assetDirectory: directory, restSteps: restSteps);

            return fixture.Server.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text;
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    /// <summary>Builds a fresh server over <see cref="BuildJudgePressDocument"/>, joins seat 0, and drives the judge
    /// press exactly as <see cref="PressAfterRestStepsAndGrade"/> does — factored out so a caller needing the LIVE
    /// server afterward (a checkpoint capture) can keep driving it rather than only reading its answer text back.
    /// Caller owns disposal.</summary>
    private static WorldFixture PressedFixture(string assetDirectory, int restSteps) {
        var fixture = Fixtures.FreshServer(definition: BuildJudgePressDocument(assetDirectory: assetDirectory));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        for (var step = 0; (step < restSteps); step++) {
            fixture.Step();
        }

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: JudgeOrdinal, value: Puck.Maths.FixedQ4816.One));
        fixture.Step();

        return fixture;
    }

    internal static WorldDefinition JudgeActionDocument(IReadOnlyList<WorldJudgeRow> judgeRows) {
        var document = Fixtures.BuildDocument();
        var judgeChannel = new WorldChannel(Name: "judge", Shape: ChannelShape.Binary, Composition: true);
        var judgeAction = new ActionSpec(
            OnPress: new ActionTrigger(
                Gate: null,
                LatchSeconds: 0f,
                Effects: [new ActionEffect.Judge(JudgeRef: JudgeRowName)]
            ),
            OnRelease: null
        );

        return document with {
            ChannelsRaw = [.. document.Channels, judgeChannel],
            Judges = judgeRows,
            KitRowsRaw = [document.Kits[0] with { ActionsRaw = new Dictionary<string, ActionSpec> { ["judge"] = judgeAction } }],
        };
    }
    /// <summary>Builds <see cref="JudgeActionDocument"/> further, with a REAL <c>puck.music.v1</c>/<c>puck.judge.v1</c>
    /// pair written to <paramref name="assetDirectory"/> and referenced by absolute path — the only way a
    /// <see cref="WorldServer"/> construction actually resolves <see cref="Puck.Audio.Simulation.MusicClock"/>/the
    /// judge window set this law grades against (see <c>WorldAssetRowLoader</c>: a rooted <c>Source</c>
    /// bypasses <see cref="AppContext.BaseDirectory"/> resolution entirely).</summary>
    internal static WorldDefinition BuildJudgePressDocument(string assetDirectory) {
        var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: "test-score",
            Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: ((int)TicksPerBeat)),
            Segments: [new MusicSegmentDocument(Id: "calm", Transitions: null)]
        ));
        var judge = JudgeCanonicalizer.Canonicalize(document: new JudgeDocument(
            Schema: JudgeDocument.CurrentSchema,
            Name: JudgeRowName,
            Windows: [
                new JudgeWindowDocument(Grade: "perfect", ToleranceTicks: 100),
                new JudgeWindowDocument(Grade: "good", ToleranceTicks: 300),
            ]
        ));

        var musicPath = Path.Combine(path1: assetDirectory, path2: "score.puck.music.v1.json");
        var judgePath = Path.Combine(path1: assetDirectory, path2: "windows.puck.judge.v1.json");

        File.WriteAllBytes(path: musicPath, bytes: music.Bytes);
        File.WriteAllBytes(path: judgePath, bytes: judge.Bytes);

        var document = JudgeActionDocument(judgeRows: [new WorldJudgeRow(Name: JudgeRowName, Source: judgePath, Hash: judge.Hash)]);

        return document with {
            Music = [new WorldMusicRow(Name: "test-score", Source: musicPath, Hash: music.Hash)],
        };
    }
}
