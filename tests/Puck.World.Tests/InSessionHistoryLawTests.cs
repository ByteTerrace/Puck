using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The laws behind <c>world.history</c>: a seek to any tick of the window, backward or forward, reproduces the
/// authoritative hash the live run reached there, across seeds and worlds; the proof every seek runs fails on a
/// keyframe from the wrong span and on input fed in the wrong order; a branch resumed behind the head is the run that
/// took that path directly; a diff reports exactly the changed rows; a replay-edit names the tick its edit first moves
/// the world; and recording holds a bounded, counted budget without allocating on a steady tick.
/// </summary>
public sealed class InSessionHistoryLawTests {
    // A world with a declared int row the laws write cells of, the edit a builder makes between ticks.
    private const string ScoreRow = "score";

    public static TheoryData<string, ulong> WorldsAndSeeds() {
        var data = new TheoryData<string, ulong>();

        foreach (var world in new[] { "fixture", "snake", "nexus" }) {
            foreach (var seed in new[] { 3UL, 77UL, 0x5EEDUL }) {
                data.Add(p1: world, p2: seed);
            }
        }

        return data;
    }

    private static WorldDefinition? Definition(string world) => (world switch {
        "nexus" => AuthoredGameFixtures.Nexus,
        "snake" => AuthoredGameFixtures.Program(module: "snake"),
        _ => null,
    });
    // A world declaring an int row with two cells the laws write, the edit a builder makes between ticks.
    private static WorldHistoryHarness ScoredHarness(ulong seed = 1UL) => new(
        definition: (Fixtures.BuildDocument() with {
            StateRaw = new(World: [new WorldStateRow(
                CellName.Parse(candidate: ScoreRow),
                CellKind.Int,
                Cells: [
                    new StateCell(CellName.Parse(candidate: "p1"), CellValue.Int(value: 0L)),
                    new StateCell(CellName.Parse(candidate: "p2"), CellValue.Int(value: 0L)),
                ]
            )]),
        }),
        seed: seed
    );
    private static WorldMutation Score(string key, long value) => new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: key,
        Principal: Principal.Console,
        Row: ScoreRow,
        Value: value
    );

    [MemberData(nameof(WorldsAndSeeds))]
    [Theory]
    public void SeekingBackThenForwardReproducesEveryRecordedTick(string world, ulong seed) {
        using var harness = new WorldHistoryHarness(
            definition: Definition(world: world),
            seed: seed
        );
        var ticks = ((world == "nexus") ? 150 : 240);

        harness.Steps(count: ticks);

        var status = harness.History.Status();
        var oldest = status.Oldest!.Value;
        var head = status.Head!.Value;

        Assert.True(
            condition: (status.Keyframes >= 2),
            userMessage: $"the window holds {status.Keyframes} keyframe(s); the law needs a seek that crosses spans"
        );

        // Back to the oldest tick, then forward one tick at a time: every recorded tick is reached and proven.
        _ = harness.SeekAndProve(target: oldest);

        for (var tick = (oldest + 1UL); (tick <= head); tick++) {
            _ = harness.SeekAndProve(target: tick);
        }

        // Then a seeded scatter of jumps in both directions, each restoring whichever keyframe sits nearest.
        var random = seed;

        for (var jump = 0; (jump < 12); jump++) {
            random = ((random * 6364136223846793005UL) + 1442695040888963407UL);

            var target = (oldest + ((random >> 33) % ((head - oldest) + 1UL)));

            _ = harness.SeekAndProve(target: target);
        }

        var counters = harness.History.Status().Counters;

        Assert.True(condition: (counters.InPlaceRestores > 0L));
        Assert.Equal(
            expected: 0L,
            actual: counters.RebuildRestores
        );
    }
    [Fact]
    public void ASeekAcrossAStructuralEditRestoresThroughTheLoadDoorAndStillReproduces() {
        using var harness = ScoredHarness(seed: 9UL);

        harness.Steps(count: 40);
        harness.Submit(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Kind: CellKind.Int,
                Name: CellName.Parse(candidate: "bonus")
            )
        ));
        harness.Steps(count: 120);

        var oldest = harness.History.Status().Oldest!.Value;
        var report = harness.SeekAndProve(target: (oldest + 5UL));

        Assert.True(condition: report.RebuiltDocument);
        _ = harness.SeekAndProve(target: harness.History.HeadTick!.Value);
    }
    [Fact]
    public void AKeyframeFromTheWrongSpanFailsTheProof() {
        using var harness = new WorldHistoryHarness(seed: 21UL);

        harness.Steps(count: 240);

        var keyframes = harness.History.KeyframeTicks;
        var first = keyframes[0];

        // The control: the right keyframe re-simulates the span without a divergence.
        using (var right = harness.History.OpenShadow(documentPath: null, keyframeTick: first)) {
            Assert.Null(@object: harness.History.Resimulate(
                from: first,
                shadow: right,
                to: (first + 20UL)
            ));
        }

        // The planted defect: the next span's keyframe stands in for the first.
        using var wrong = harness.History.OpenShadow(documentPath: null, keyframeTick: keyframes[1]);

        Assert.NotNull(@object: harness.History.Resimulate(
            from: first,
            shadow: wrong,
            to: (first + 20UL)
        ));
    }
    [Fact]
    public void InputFedInTheWrongOrderFailsTheProof() {
        using var harness = new WorldHistoryHarness(seed: 33UL);

        harness.Steps(count: 120);

        var first = harness.History.KeyframeTicks[0];
        var a = (first + 4UL);
        var b = (first + 5UL);
        var atA = harness.History.RecordedInput(tick: a);
        var atB = harness.History.RecordedInput(tick: b);

        using var shadow = harness.History.OpenShadow(documentPath: null, keyframeTick: first);

        // The planted defect: two consecutive ticks' intents swap places, each keeping its own tick stamp.
        Assert.Equal(
            expected: a,
            actual: harness.History.Resimulate(
                from: first,
                rewrite: (tick, input) => ((tick == a)
                    ? (atA with { Intents = [.. atB.Intents.Select(selector: intent => (intent with { Tick = a }))] })
                    : ((tick == b)
                        ? (atB with { Intents = [.. atA.Intents.Select(selector: intent => (intent with { Tick = b }))] })
                        : input)),
                shadow: shadow,
                to: (first + 20UL)
            )
        );
    }
    [Fact]
    public void ABranchResumedBehindTheHeadIsTheRunThatTookThatPath() {
        const ulong ForkAt = 50UL;

        using var traveled = new WorldHistoryHarness(seed: 5UL);

        traveled.Steps(count: 100);

        var future = new ulong[50];

        for (var index = 0; (index < future.Length); index++) {
            future[index] = traveled.Live[((ForkAt + 1UL) + ((ulong)index))];
        }

        _ = traveled.SeekAndProve(target: ForkAt);
        Assert.True(
            condition: traveled.History.TryArmBranch(
                name: "first-try",
                refusal: out var refusal
            ),
            userMessage: refusal
        );

        // A different input stream from the fork onward: the harness reseeds by stepping a fresh generator.
        using var direct = new WorldHistoryHarness(
            on: false,
            seed: 5UL
        );

        direct.Steps(count: ((int)ForkAt));
        Assert.Equal(
            expected: traveled.Hash(),
            actual: direct.Hash()
        );

        for (var tick = 0; (tick < 30); tick++) {
            var axis = FixedQ4816FromTick(tick: tick);

            foreach (var harness in new[] { traveled, direct }) {
                harness.Transport.SubmitIntent(submission: new IntentSubmission(
                    EntityIndex: 0,
                    Intent: harness.Channels.RoleOrdinals.Intent(moveAdvance: axis),
                    Principal: Principal.Seat(slot: 0),
                    Tick: harness.Fixture.Server.NextInputTick
                ));
                harness.StepWithoutInput();
            }

            Assert.Equal(
                expected: direct.Hash(),
                actual: traveled.Hash()
            );
        }

        var branch = Assert.Single(collection: traveled.History.Status().Branches);

        Assert.Equal(expected: "first-try", actual: branch.Name);
        Assert.Equal(expected: ForkAt, actual: branch.ForkTick);
        Assert.Equal(expected: future, actual: branch.AuthoritativeHashes);

        // The new timeline is the window now: seeking inside it reproduces it.
        _ = traveled.SeekAndProve(target: (ForkAt + 10UL));
        _ = traveled.SeekAndProve(target: traveled.History.HeadTick!.Value);
    }
    [Fact]
    public void TheDiffReportsExactlyTheChangedRows() {
        using var harness = ScoredHarness(seed: 13UL);

        harness.Steps(count: 10);
        harness.Submit(mutation: Score(key: "p2", value: 4L));
        harness.Steps(count: 10);

        var from = harness.Tick;
        var before = WorldHistoryImage.Capture(server: harness.Fixture.Server);

        harness.Submit(mutation: Score(key: "p1", value: 5L));
        harness.Steps(count: 3);
        harness.Submit(mutation: Score(key: "p2", value: 7L));
        harness.Steps(count: 3);

        var to = harness.Tick;
        var after = WorldHistoryImage.Capture(server: harness.Fixture.Server);

        Assert.True(
            condition: harness.History.TryDiff(
                diff: out var diff,
                divergedAt: out var divergedAt,
                documentPath: null,
                from: from,
                refusal: out var refusal,
                to: to
            ),
            userMessage: refusal
        );
        Assert.Null(@object: divergedAt);
        Assert.Equal(
            expected: [(ScoreRow, "p1", ((long?)0L), ((long?)5L)), (ScoreRow, "p2", ((long?)4L), ((long?)7L))],
            actual: diff!.Cells.Select(selector: static cell => (cell.Row, cell.Key, cell.From?.Raw, cell.To?.Raw))
        );

        // The re-simulated diff is the diff of what the live run held at those ticks, body for body.
        var live = WorldHistoryDiff.Between(from: before, to: after);

        Assert.Equal(
            expected: live.Bodies.Select(selector: static body => (body.Index, string.Join(separator: ",", values: body.Lanes))),
            actual: diff.Bodies.Select(selector: static body => (body.Index, string.Join(separator: ",", values: body.Lanes)))
        );
        Assert.Equal(expected: live.Components, actual: diff.Components);
        Assert.True(condition: WorldHistoryDiff.Between(from: after, to: after).Empty);
    }
    [Fact]
    public void ADiffSeesOneChangedCellAndNothingElse() {
        using var harness = ScoredHarness(seed: 17UL);

        harness.Submit(mutation: Score(key: "p1", value: 2L));
        harness.Steps(count: 4);

        var image = WorldHistoryImage.Capture(server: harness.Fixture.Server);
        var cells = new SortedDictionary<(string Row, string Key), WorldHistoryCellImage>(dictionary: image.Cells.ToDictionary());
        var key = (ScoreRow, "p1");

        // The planted change: one cell's value, nothing else.
        cells[key] = (cells[key] with { Raw = 9L });

        var diff = WorldHistoryDiff.Between(
            from: image,
            to: (image with { Cells = cells })
        );

        var cell = Assert.Single(collection: diff.Cells);

        Assert.Equal(expected: (2L, 9L), actual: (cell.From!.Value.Raw!.Value, cell.To!.Value.Raw!.Value));
        Assert.Empty(collection: diff.Bodies);
        Assert.Empty(collection: diff.Fields);
    }
    [Fact]
    public void AReplayEditNamesTheTickItsEditFirstMovesTheWorld() {
        using var harness = ScoredHarness(seed: 41UL);

        harness.Steps(count: 60);
        harness.Submit(mutation: Score(key: "p1", value: 8L));

        Assert.True(
            condition: harness.History.TryReplayEdit(
                documentPath: null,
                refusal: out var refusal,
                report: out var report,
                ticksBack: 20
            ),
            userMessage: refusal
        );
        Assert.Null(@object: report!.ControlDivergedAt);
        Assert.Equal(expected: (harness.Tick - 19UL), actual: report.LandedAt);
        // The cell is authoritative state the moment the edit applies, so the first tick that consumed it diverges.
        Assert.Equal(expected: report.LandedAt, actual: report.DivergedAt);

        var cell = Assert.Single(collection: report.Diff!.Cells);

        Assert.Equal(expected: ("p1", ((long?)8L)), actual: (cell.Key, cell.To?.Raw));
        Assert.Contains(expectedSubstring: "accepted", actualString: Assert.Single(collection: report.Edits));
    }
    [Fact]
    public void AReplayEditWhoseEditChangesNothingNamesNoDivergence() {
        using var harness = ScoredHarness(seed: 43UL);

        harness.Submit(mutation: Score(key: "p1", value: 6L));
        harness.Steps(count: 60);
        // The control: writing the value the cell already holds moves no authoritative state at any tick.
        harness.Submit(mutation: Score(key: "p1", value: 6L));

        Assert.True(
            condition: harness.History.TryReplayEdit(
                documentPath: null,
                refusal: out var refusal,
                report: out var report,
                ticksBack: 20
            ),
            userMessage: refusal
        );
        Assert.Null(@object: report!.ControlDivergedAt);
        Assert.Null(@object: report.DivergedAt);
    }
    [Fact]
    public void RecordingHoldsItsBudgetAndCountsWhatItDropped() {
        using var harness = new WorldHistoryHarness(seed: 7UL);

        harness.Steps(count: 30);

        var keyframeBytes = harness.History.Status().KeyframeBytes;
        var budget = Math.Max(
            val1: (1024L * 1024L),
            val2: (keyframeBytes * 3L)
        );

        Assert.True(condition: harness.History.TryOn(budgetBytes: budget, refusal: out _));
        harness.Steps(count: 3000);

        var status = harness.History.Status();

        Assert.True(condition: (status.Counters.SegmentsEvicted > 0L), userMessage: "the budget never forced an eviction");
        Assert.True(condition: (status.Oldest > 30UL));
        // The budget holds after every tick; only a window shrunk to its newest span may overhang it.
        Assert.True(condition: ((status.BytesHeld <= budget) || (status.Keyframes == 1)), userMessage: $"{status.BytesHeld} bytes held over a {budget}-byte budget across {status.Keyframes} span(s)");
        Assert.Equal(
            expected: ((long)(status.Head!.Value - 0UL)),
            actual: (status.Counters.TicksRecorded + 1L)
        );
        _ = harness.SeekAndProve(target: status.Oldest!.Value);
    }
    [Fact]
    public void ASteadyRecordedTickAllocatesNothing() {
        using var harness = new WorldHistoryHarness(seed: 11UL);

        harness.Steps(count: 200);

        var allocated = 0L;
        var measured = 0;

        for (var tick = 0; (tick < 120); tick++) {
            var keyframes = harness.History.Status().Counters.KeyframesCaptured;

            harness.Transport.SubmitIntent(submission: new IntentSubmission(
                EntityIndex: 0,
                Intent: harness.Channels.RoleOrdinals.Intent(moveAdvance: FixedQ4816FromTick(tick: tick)),
                Principal: Principal.Seat(slot: 0),
                Tick: harness.Fixture.Server.NextInputTick
            ));
            harness.Fixture.Step();

            var before = GC.GetAllocatedBytesForCurrentThread();

            harness.Tape.NoteTick();

            var after = GC.GetAllocatedBytesForCurrentThread();

            // A keyframe tick pays its checkpoint by design; every other tick is the steady state.
            if (harness.History.Status().Counters.KeyframesCaptured == keyframes) {
                allocated += (after - before);
                measured++;
            }
        }

        Assert.True(condition: (measured > 60), userMessage: $"only {measured} steady ticks were measured");
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void ASeekRefusesByNameWhileARecordingCapturesTheTimeline() {
        using var harness = new WorldHistoryHarness(seed: 2UL);

        harness.Steps(count: 20);
        Assert.True(condition: harness.Tape.TryBeginRecording(name: $"history-{Guid.NewGuid():N}", refusal: out _));
        harness.Steps(count: 5);

        Assert.False(condition: harness.History.TrySeek(
            documentPath: null,
            refusal: out var refusal,
            report: out _,
            target: 10UL
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "replay.record");
        _ = harness.Tape.CancelRecording();
        // The control: with the recording gone the same seek runs.
        _ = harness.SeekAndProve(target: 10UL);
    }

    private static Puck.Maths.FixedQ4816 FixedQ4816FromTick(int tick) => Puck.Maths.FixedQ4816.FromRawBits(value: ((((long)(tick % 5)) - 2L) * 16384L));
}
