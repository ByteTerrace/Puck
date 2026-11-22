using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The outbox hands a sink that receives on the host loop what its composer composed, at the collect: nothing
/// while the tick runs, then every record in order, intact, even when the composer reused its scratch for a second
/// delivery in the same tick.</summary>
public sealed class OutboxLawTests {
    // Every record a sink received, rendered whole: the stream's own identity aside, two sinks composed alike render
    // alike.
    private class Recorder : IClientSink {
        public List<string> Records { get; } = [];
        public int Snapshots { get; private set; }

        private static string Stamp(in WorldStreamStamp stream) => string.Create(CultureInfo.InvariantCulture, $"g{stream.Generation}#{stream.Ordinal}");

        public virtual void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
            Snapshots++;
            var cells = string.Join(" ", snapshot.FieldCells.ToArray().Select(static cell => string.Create(CultureInfo.InvariantCulture,
                $"{cell.Row}:{cell.Cell}:{cell.Raw}:{cell.Image.Key}={cell.Image.Value}")));
            var bodies = string.Join(" ", snapshot.Entries.ToArray().Select(static entry => string.Create(CultureInfo.InvariantCulture,
                $"{entry.Index}/{entry.Generation}@{entry.Position}[{string.Join(",", entry.State.ToArray())}]")));
            Records.Add(string.Create(CultureInfo.InvariantCulture,
                $"snapshot {Stamp(stream)} tick={snapshot.Tick} full={snapshot.FieldsFull} revision={snapshot.Revision} cells=[{cells}] bodies=[{bodies}] scales={snapshot.SolidScales.Length} cues={snapshot.Cues.Length}"));
        }
        public void DeliverAnswer(in QueryAnswer answer) => Records.Add($"answer {answer.Text}");
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) =>
            Records.Add($"definition {Stamp(stream)} rows={definition.State.Count()}");
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) =>
            Records.Add(string.Create(CultureInfo.InvariantCulture,
                $"state {Stamp(stream)} tick={stamp.Tick} everything={stamp.Everything} moved=[{string.Join(",", stamp.MovedRows.ToArray())}]"));
        public void DeliverCut(in WorldStreamStamp stream, bool terminal) => Records.Add($"cut {Stamp(stream)} terminal={terminal}");
        public void CloseSpan(ulong stream) => Records.Add("close");
        public void DeliverComposition(WorldComposition composition) => Records.Add("composition");
        public void DeliverSessionLever(WorldSessionLever lever) => Records.Add("lever");
    }

    // Receives on the thread composing the delivery, so it records what was composed at the moment it was composed.
    private sealed class InlineRecorder(Action onSnapshot) : Recorder, IWorldInlineSink {
        public override void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
            onSnapshot();
            base.DeliverSnapshot(in snapshot, in stream);
        }
    }

    // The card table, with a rule that counts every tick, so each tick's own snapshot carries a cell.
    private static WorldDefinition CountingTable() {
        var table = StreamGenerationLawTests.Table();
        return table with {
            StateRaw = new WorldStateSection(World: [.. table.State, StateFixtures.IntSlot(name: "ticks")]),
            Rules = [new WorldRule(CellName.Parse("count"), [new ActionEffect.AddState("ticks", Value: 1m)])],
        };
    }

    [Fact]
    public void ASessionSinkDeliveringTwiceInOneTickHandsItsDeferredObserverBothRecordsIntactAtTheCollect() {
        using var fixture = Fixtures.FreshServer(CountingTable());
        var deferred = new Recorder();
        var deferredBeforeTick = 0;
        var deferredDuringTick = new List<int>();
        var inline = new InlineRecorder(onSnapshot: () => deferredDuringTick.Add(deferred.Records.Count - deferredBeforeTick));

        using var inlineObservation = fixture.Server.TryObserveAsSession(refusal: out var inlineRefusal, sink: inline, sourceAuthority: "spectator-inline");
        using var deferredObservation = fixture.Server.TryObserveAsSession(refusal: out var deferredRefusal, sink: deferred, sourceAuthority: "spectator-deferred");

        Assert.True(inlineObservation is not null, inlineRefusal);
        Assert.True(deferredObservation is not null, deferredRefusal);
        fixture.Step();

        // A shape edit applied inside the tick replaces each spectator's document, which its session sink completes with
        // a primer snapshot before the tick delivers its own: two snapshots composed through one disclosure scratch.
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal: Principal.Console,
            Row: new WorldStateRow(Name: CellName.Parse("outbox"), Kind: CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), CellValue.Int(1))])));
        var inlineFrom = inline.Records.Count;
        var deferredFrom = deferredBeforeTick = deferred.Records.Count;
        var snapshotsFrom = deferred.Snapshots;
        deferredDuringTick.Clear();
        fixture.Step();

        Assert.True((deferred.Snapshots - snapshotsFrom) >= 2, string.Join("\n", deferred.Records.Skip(deferredFrom)));
        Assert.NotEmpty(deferredDuringTick);
        Assert.All(deferredDuringTick, static reached => Assert.Equal(0, reached));
        Assert.Equal(inline.Records.Skip(inlineFrom), deferred.Records.Skip(deferredFrom));
    }
}
