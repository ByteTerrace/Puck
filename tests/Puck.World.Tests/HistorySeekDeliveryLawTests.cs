using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// A history seek delivers once: a viewer attached before the seek observes exactly one definition and exactly one
/// snapshot, at the target tick, whichever door the seek restores through (in place, or the load door after a
/// structural edit) and however many ticks it re-simulates, zero included. The restore, the load door's install and
/// every re-simulated tick reach no one; the seek's own presentation of the restored timeline is the one delivery.
/// </summary>
public sealed class HistorySeekDeliveryLawTests {
    private const string ScoreRow = "score";

    // Every timeline delivery, in arrival order.
    private sealed class CountingSink : IClientSink {
        public List<string> Log { get; } = [];
        public List<ulong> SnapshotTicks { get; } = [];

        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) => Log.Add(item: "composition");
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => Log.Add(item: "definition");
        public void DeliverSessionLever(WorldSessionLever lever) { }
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            Log.Add(item: "snapshot");
            SnapshotTicks.Add(item: snapshot.Tick);
        }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => Log.Add(item: "state");
        public void Clear() {
            Log.Clear();
            SnapshotTicks.Clear();
        }
    }

    private static WorldHistoryHarness ScoredHarness(ulong seed) => new(
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
    // Seeks with a viewer attached since before the seek, and asserts it observed one definition, then one snapshot
    // at the target.
    private static WorldHistorySeekReport SeekDeliversOnce(WorldHistoryHarness harness, ulong target) {
        var sink = new CountingSink();

        using var lease = harness.Fixture.Server.AttachSink(sink: sink);

        sink.Clear();

        var report = harness.SeekAndProve(target: target);

        Assert.Equal(
            expected: ["definition", "snapshot"],
            actual: sink.Log
        );
        Assert.Equal(
            expected: [target],
            actual: sink.SnapshotTicks
        );

        return report;
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AnInPlaceSeekBackOverAKeyframeDeliversOnce(bool resimulates) {
        using var harness = ScoredHarness(seed: 31UL);

        harness.Steps(count: 200);

        var keyframes = harness.History.KeyframeTicks;

        Assert.True(
            condition: (keyframes.Count >= 2),
            userMessage: $"the window holds {keyframes.Count} keyframe(s); the law needs a backward seek over one"
        );

        var keyframe = keyframes[^2];

        var report = SeekDeliversOnce(
            harness: harness,
            target: (resimulates
                ? (keyframe + 6UL)
                : keyframe)
        );

        Assert.False(condition: report.RebuiltDocument);
        Assert.Equal(
            expected: keyframe,
            actual: report.KeyframeTick
        );
        Assert.Equal(
            expected: (resimulates ? 6 : 0),
            actual: report.TicksResimulated
        );
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ALoadDoorSeekBackOverAKeyframeDeliversOnce(bool resimulates) {
        using var harness = ScoredHarness(seed: 37UL);

        harness.Steps(count: 40);

        // The structural edit applies on the next tick; a re-simulated span that crosses it installs it again.
        var edit = (harness.Tick + 1UL);

        harness.Submit(mutation: Score(key: "p1", value: 3L));
        harness.Submit(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Kind: CellKind.Int,
                Name: CellName.Parse(candidate: "bonus")
            )
        ));
        harness.Steps(count: 120);

        var keyframe = harness.History.KeyframeTicks[0];

        Assert.True(
            condition: (keyframe < 35UL),
            userMessage: $"the first keyframe sits at {keyframe}; the law needs one before the structural edit"
        );

        var target = (resimulates
            ? (edit + 3UL)
            : keyframe);
        var report = SeekDeliversOnce(
            harness: harness,
            target: target
        );

        Assert.True(condition: report.RebuiltDocument);
        Assert.Equal(
            expected: keyframe,
            actual: report.KeyframeTick
        );
        Assert.Equal(
            expected: ((int)(target - keyframe)),
            actual: report.TicksResimulated
        );
    }
    [Fact]
    public void AForwardSeekFromTheCursorDeliversOnce() {
        using var harness = ScoredHarness(seed: 41UL);

        harness.Steps(count: 60);

        var head = harness.Tick;
        var report = harness.SeekAndProve(target: (head - 8UL));

        Assert.NotNull(@object: report.KeyframeTick);

        var forward = SeekDeliversOnce(
            harness: harness,
            target: (head - 2UL)
        );

        Assert.Null(@object: forward.KeyframeTick);
        Assert.Equal(
            expected: 6,
            actual: forward.TicksResimulated
        );
    }
    // The history records a composition through the transport's tap and a re-simulated tick re-applies it. A
    // composition is a presentation override the history does not rewind, so a seek over one delivers it to no viewer:
    // the viewer keeps the composition it holds and observes only the restored timeline.
    [Fact]
    public void ASeekOverARecordedCompositionDeliversNoComposition() {
        using var harness = ScoredHarness(seed: 43UL);

        harness.Steps(count: 200);

        // The composition applies on the next tick, which the seek below must re-simulate.
        var applied = (harness.Tick + 1UL);

        harness.Transport.SubmitComposition(
            composition: new WorldComposition.SetActiveLayout(Name: null),
            principal: Principal.Console
        );
        harness.Steps(count: 3);

        var composed = harness.Tick;

        // The control: the composition the seek re-simulates reaches a viewer live.
        var live = new CountingSink();

        using (harness.Fixture.Server.AttachSink(sink: live)) {
            live.Clear();
            harness.Transport.SubmitComposition(
                composition: new WorldComposition.SetActiveLayout(Name: null),
                principal: Principal.Console
            );
        }

        Assert.Equal(
            expected: ["composition"],
            actual: live.Log
        );
        harness.Steps(count: 3);

        var report = SeekDeliversOnce(
            harness: harness,
            target: composed
        );

        Assert.True(
            condition: (report.KeyframeTick < applied),
            userMessage: $"the seek restored the keyframe at {report.KeyframeTick}, after the composition at {applied}"
        );
    }
}
