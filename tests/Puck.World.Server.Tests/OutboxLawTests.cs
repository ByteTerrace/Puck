using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
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
        public void DeliverPresentation(WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) { }
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

    // The pump is one thread, as the host loop's is: the law registers it, opens and closes its window and collects on
    // it, and makes each socket worker's deliveries on a thread of their own, joined before the pump reads on. An await
    // here would resume the pump's half on whatever thread the continuation lands on, which no host loop does.
    [Fact]
    public void OffPumpMirrorAndRemoteRouteDeliveriesWaitForTheBoundaryWindowCollect() {
        var definition = CountingTable() with {
            CamerasRaw = [new WorldCamera(
                Name: "cam",
                RenderHeight: 72U,
                RenderWidth: 128U,
                Rig: new WorldCameraProgram(
                    Name: "cam-rig",
                    Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(binding: "state.ticks"))],
                    Version: WorldCameraProgram.CurrentVersion
                )
            )],
        };
        var boundary = new WorldLinkBoundary();
        var hub = new WorldOutputHub { Boundary = boundary };
        var mirror = new WorldSessionMirror(placeholder: definition);
        var stateMirror = mirror.FollowState();
        var bodyCursor = new GpuRowCursor();
        var bodyRuns = new GpuUploadRuns(capacity: 8);
        _ = mirror.CollectBodyChanges(cursor: bodyCursor, into: bodyRuns);
        bodyCursor.Commit();
        var stateRevisionBefore = stateMirror.Revision;
        var changedSlots = new List<int>();

        var route = hub.RouteTo(sink: mirror, fault: static exception => throw exception);
        var observedRoutes = new List<(WorldAuthorityRouteDescription Route, int ThreadId)>();
        var keyPath = Path.Combine(Path.GetTempPath(), $"puck-outbox-law-{Guid.NewGuid():N}.pk8");
        var origin = new WorldEntityAddress("source", 0, 1);
        var authenticator = new WorldAttestedAuthenticator(trustEntries: null);
        using var network = new WorldPeerNetwork(identityFile: keyPath);
        using var rootRemote = new WorldRemoteAuthority(
            endpoint: "127.0.0.1:49152",
            placeholder: definition,
            security: authenticator,
            observerAuthority: "source",
            expectedAuthority: "peer",
            network: network,
            boundary: boundary
        );
        using var travelerRemote = new WorldRemoteAuthority(
            endpoint: "127.0.0.1:49152",
            placeholder: definition,
            security: authenticator,
            observerAuthority: "source",
            expectedAuthority: "peer",
            submissionAuthority: rootRemote,
            submissionCredential: new WorldRemoteRouteCredential(0, "source", new WorldMobilityIdentity(Incarnation: origin, Epoch: 1UL, DepartedFrom: origin)),
            routeChanged: observed => observedRoutes.Add((observed, Environment.CurrentManagedThreadId)),
            boundary: boundary
        );

        var pumpThread = Environment.CurrentManagedThreadId;
        var stampedOnWire = 0L;
        var version = new WorldDocumentVersion(Guid.NewGuid(), 1);
        var ticksOrdinal = definition.StateCatalog.Descriptors.Select(static (row, index) => (row.Name, index)).First(static item => item.Name == "ticks").index;
        boundary.RegisterPump();
        try {
            // While the pump stands in its frame section (window closed), a socket worker delivers a stream and two
            // onward route updates. Nothing touches the mirror's body journal, its state mirror, or the route listener.
            OnASocketWorker(() => {
                const ulong StreamId = 77UL;
                route.DeliverDefinition(definition, version, new WorldStreamStamp(StreamId, 1, 1));
                var movedScratch = new[] { ticksOrdinal };
                route.DeliverState(definition, version,
                    new WorldStateStamp(Tick: 2, EngineTick: 1680, Everything: true, MovedRows: movedScratch),
                    new WorldStreamStamp(StreamId, 1, 2));
                movedScratch[0] = 99;

                stampedOnWire = Stopwatch.GetTimestamp() - (Stopwatch.Frequency / 4);
                using (WorldDeliveryClock.ReplayAt(stampedOnWire)) {
                    var entries = new[] {
                        new EntitySnapshot(0, Vector3.UnitX, Quaternion.Identity, Vector3.One, true, 0, 0, 0,
                            new EntityContinuity(EntityContinuityKind.Continuous, 1), 1, null, 0f, default, default),
                    };
                    var cells = new[] {
                        new FieldCellDelta(Cell: 0, Field: 0, Raw: 0, Row: ticksOrdinal, Image: new ArenaCellImage(CellName.Parse("$value"), CellValue.Int(7), default, default)),
                    };
                    route.DeliverSnapshot(new WorldSnapshot(
                        Tick: 2UL,
                        Revision: 1,
                        StepTicks: 840UL,
                        Entries: entries,
                        Authority: "peer",
                        FieldCells: cells,
                        FieldsFull: true,
                        EngineTick: 1680UL
                    ), new WorldStreamStamp(StreamId, 1, 3));
                    entries[0] = default;
                }

                travelerRemote.ObserveRoute(new WorldAuthorityRouteDescription(
                    "127.0.0.1:49152", new WorldEntityAddress("hop-1", 0, 1), 3UL, default, FixedQuaternion.Identity,
                    Vector3.One, 0, 0, 0, FixedQ4816.One, null, definition, version));
                travelerRemote.ObserveRoute(new WorldAuthorityRouteDescription(
                    "127.0.0.1:49152", new WorldEntityAddress("hop-2", 2, 4), 5UL, default, FixedQuaternion.Identity,
                    Vector3.One, 0, 0, 0, FixedQ4816.FromInteger(2), null, definition, version));
            });

            Assert.False(mirror.IsActive(0));
            Assert.False(mirror.CollectBodyChanges(cursor: bodyCursor, into: bodyRuns));
            Assert.Equal(0, bodyRuns.Count);
            Assert.Equal(stateRevisionBefore, mirror.FollowState().Revision);
            Assert.Empty(observedRoutes);
            Assert.Equal(1, travelerRemote.PendingDeliveries);

            // Opening the boundary window and collecting replays the stream and the latest route on the pump thread.
            boundary.Open(drain: false);
            try {
                hub.Collect();
                travelerRemote.CollectDeliveries();
            } finally {
                boundary.Close();
            }

            Assert.True(mirror.IsActive(0));
            Assert.True(mirror.CollectBodyChanges(cursor: bodyCursor, into: bodyRuns) || (bodyRuns.Count > 0));
            Assert.Equal(stampedOnWire, mirror.SnapshotArrivalTimestamp);
            Assert.True(mirror.FollowState().TryChangedSince(stateRevisionBefore, changedSlots));
            Assert.NotEmpty(changedSlots);
            var deliveredRoute = Assert.Single(observedRoutes);
            Assert.Equal(pumpThread, deliveredRoute.ThreadId);
            Assert.Equal(new WorldEntityAddress("hop-2", 2, 4), deliveredRoute.Route.Entity);

            // A dropped remote session closes its span through the same outbox, so withdrawal also waits for the window.
            OnASocketWorker(() => route.CloseSpan(stream: 77UL));
            Assert.True(mirror.IsActive(0));

            boundary.Open(drain: false);
            try {
                hub.Collect();
            } finally {
                boundary.Close();
            }

            Assert.False(mirror.IsActive(0));
        } finally {
            boundary.UnregisterPump();
            try { File.Delete(keyPath); } catch (IOException) { }
        }

        static void OnASocketWorker(Action deliver) {
            Exception? fault = null;
            var worker = new Thread(start: () => {
                try { deliver(); } catch (Exception exception) { fault = exception; }
            });

            worker.Start();
            worker.Join();
            if (fault is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source: fault); }
        }
    }
}
