using System.Numerics;

using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the reads that go through the state mirror on behalf of a holder rather than a document binding: a body's
/// per-body reads (look lanes, drivers, poses, effectors, body scale) acquire one slot per body, refresh only when their
/// rows move, read the delivered engine tick, and retire when the body leaves; a seat routed to another authority reads
/// that authority's rows through that authority's own mirror; and a session mirror carries each delivery's stamp, so
/// its presentation reads only the slots bound to rows that moved.
/// </summary>
public sealed class WorldStateReadRoutingLawTests {
    private const int OtherRows = 15;
    private const int Repetitions = 64;
    private const string ScaleRow = "scale";

    private sealed class Lease : IDisposable {
        public void Dispose() {
        }
    }

    private static long Reads(WorldStateMirror mirror) {
        Assert.True(condition: ((IWorkCounterSource)mirror).TryRead(
            kind: WorldStateMirror.Reads,
            value: out var value
        ));

        return value;
    }
    private static WorldStateRow IntRow(string name, long value, StateAdvance? advance = null) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Advance: advance,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    // A keyed scale row with a cell per body, beside rows no body reads.
    private static WorldDefinition Scales(long first, long second, long other = 0L) {
        var rows = new List<WorldStateRow> {
            new(
                Name: CellName.Parse(candidate: ScaleRow),
                Kind: CellKind.Int,
                Min: 0,
                Max: 1000,
                Cells: [
                    new StateCell(
                        Key: CellName.Parse(candidate: "0"),
                        Value: CellValue.Int(value: first)
                    ),
                    new StateCell(
                        Key: CellName.Parse(candidate: "1"),
                        Value: CellValue.Int(value: second)
                    ),
                ]
            ),
        };

        for (var index = 0; (index < OtherRows); index++) {
            rows.Add(item: IntRow(
                name: $"other{index}",
                value: (other + index)
            ));
        }

        return Fixtures.BuildDocument().WithWorldState(rows: [.. rows]);
    }
    private static int Ordinal(WorldDocumentStateView view, string row) {
        Assert.True(condition: view.TryResolveRow(
            ordinal: out var ordinal,
            rowName: row
        ));

        return ordinal;
    }
    private static WorldStateStamp Stamp(ulong tick, params int[] moved) => new(
        EngineTick: (tick * 1680UL),
        Everything: false,
        MovedRows: moved,
        Tick: tick
    );

    [Fact]
    public void EachBodyReadsItsOwnSlot_RefreshedOnlyWhenItsRowMoves_AndRetiredWhenTheBodyLeaves() {
        var definition = Scales(
            first: 2,
            second: 3
        );
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var first = new WorldStateLease();
        var second = new WorldStateLease();

        first.Bind(
            bodyIndex: 0,
            mirror: mirror
        );
        second.Bind(
            bodyIndex: 1,
            mirror: mirror
        );
        Assert.Equal(
            actual: WorldGaitDrivers.LiveBodyScale(
                reads: first,
                scaleRow: ScaleRow
            ),
            expected: 2f
        );
        Assert.Equal(
            actual: WorldGaitDrivers.LiveBodyScale(
                reads: second,
                scaleRow: ScaleRow
            ),
            expected: 3f
        );
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: 2
        );

        var scale = Ordinal(
            row: ScaleRow,
            view: view
        );
        var unrelated = Ordinal(
            row: "other4",
            view: view
        );
        var before = Reads(mirror: mirror);

        definition = Scales(
            first: 2,
            other: 50,
            second: 3
        );
        mirror.Refresh(stamp: Stamp(
            moved: [unrelated],
            tick: 1UL
        ));
        Assert.Equal(
            actual: (Reads(mirror: mirror) - before),
            expected: 0L
        );

        definition = Scales(
            first: 4,
            other: 50,
            second: 5
        );
        before = Reads(mirror: mirror);
        mirror.Refresh(stamp: Stamp(
            moved: [scale],
            tick: 2UL
        ));
        Assert.Equal(
            actual: (Reads(mirror: mirror) - before),
            expected: 2L
        );
        Assert.Equal(
            actual: WorldGaitDrivers.LiveBodyScale(
                reads: second,
                scaleRow: ScaleRow
            ),
            expected: 5f
        );

        // The second body leaves: its slot retires, and the row's next move reads only the body still here.
        second.Release();
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: 1
        );

        definition = Scales(
            first: 6,
            other: 50,
            second: 7
        );
        before = Reads(mirror: mirror);
        mirror.Refresh(stamp: Stamp(
            moved: [scale],
            tick: 3UL
        ));
        Assert.Equal(
            actual: (Reads(mirror: mirror) - before),
            expected: 1L
        );
        Assert.Equal(
            actual: WorldGaitDrivers.LiveBodyScale(
                reads: first,
                scaleRow: ScaleRow
            ),
            expected: 6f
        );
    }
    [Fact]
    public void ARegisteredSlotOutlivesTheHoldersThatAcquiredIt() {
        var definition = Scales(
            first: 2,
            second: 3
        );
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var binding = new StateBinding(
            Key: "0",
            Row: ScaleRow,
            Target: false
        );
        var held = mirror.Acquire(
            binding: in binding,
            conversion: WorldStateConversion.Number
        );

        Assert.Equal(
            actual: mirror.Register(
                binding: in binding,
                conversion: WorldStateConversion.Number
            ),
            expected: held
        );

        mirror.Release(slot: held);
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: 1
        );
        Assert.Throws<InvalidOperationException>(testCode: () => mirror.Release(slot: held));
    }
    [Fact]
    public void ALeaseLetsItsSlotsGoWhenTheMirrorInstallsADocument() {
        var definition = Scales(
            first: 2,
            second: 3
        );
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var reads = new WorldStateLease();

        reads.Bind(
            bodyIndex: 0,
            mirror: mirror
        );

        // Each installed document carries its own objects: a lease reading through them must not keep the last
        // document's slots alive beside the new ones.
        for (var install = 0; (install < 8); install++) {
            definition = Scales(
                first: (2 + install),
                second: 3
            );
            mirror.Install(
                engineTick: 0UL,
                tick: ((ulong)install)
            );

            var lane = ExpressionProgram.Parse(text: "scale[$body]");

            Assert.Equal(
                actual: WorldLookLaneEvaluator.Evaluate(
                    expression: lane,
                    reads: reads
                ),
                expected: (2f + install)
            );
            Assert.Equal(
                actual: reads.Count,
                expected: 1
            );
            Assert.Equal(
                actual: mirror.SlotCount,
                expected: 1
            );
        }

        // The body leaves: the one slot it still holds retires.
        reads.Release();
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: 0
        );
    }
    [Fact]
    public void ALookLaneReadsTheDeliveredEngineTick() {
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            advance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: 1
            ),
            name: "phase",
            value: 0
        )]);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var reads = new WorldStateLease();
        var lane = ExpressionProgram.Parse(text: "phase");

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        reads.Bind(
            bodyIndex: 0,
            mirror: mirror
        );
        Assert.Equal(
            actual: WorldLookLaneEvaluator.Evaluate(
                expression: lane,
                reads: reads
            ),
            expected: 0f
        );

        // Two seconds of engine ticks advance the row by two, whatever simulation tick carries them.
        mirror.Advance(
            engineTick: (2UL * FixedTickConversion.TicksPerSecond),
            tick: 60UL
        );
        mirror.Apply(fraction: 1f);
        Assert.Equal(
            actual: WorldLookLaneEvaluator.Evaluate(
                expression: lane,
                reads: reads
            ),
            expected: 2f
        );
    }
    [Fact]
    public void PerBodyReadsAllocateNothingInSteadyState() {
        var definition = Scales(
            first: 2,
            second: 3
        );
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var reads = new WorldStateLease();
        var lane = ExpressionProgram.Parse(text: "scale[$body] * 2");
        IReadOnlyList<string> gate = ["state.scale.$body"];
        var sink = 0f;

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        reads.Bind(
            bodyIndex: 1,
            mirror: mirror
        );

        void Frames() {
            for (var repetition = 0; (repetition < Repetitions); repetition++) {
                reads.Bind(
                    bodyIndex: 1,
                    mirror: mirror
                );
                sink += WorldLookLaneEvaluator.Evaluate(
                    expression: lane,
                    reads: reads
                );
                sink += WorldGaitDrivers.LiveBodyScale(
                    reads: reads,
                    scaleRow: ScaleRow
                );
                sink += (WorldGaitDrivers.GateHolds(
                    facts: BodyFacts.Grounded,
                    gate: gate,
                    moving: false,
                    reads: reads
                )
                    ? 1f
                    : 0f);
                sink += reads.Changed;
            }
        }

        Frames();
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Frames)
        );
        Assert.True(condition: (sink > 0f));
    }
    [Fact]
    public void ASeatRoutedToAnotherAuthorityReadsThatAuthoritysRows() {
        var local = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            name: "heading",
            value: 0
        )]);
        var remote = Fixtures.BuildDocument().WithWorldState(rows: [
            IntRow(
                name: "padding",
                value: 9
            ),
            IntRow(
                name: "heading",
                value: 1
            ),
        ]);
        var client = ClientFixtures.Client(definition: local);
        IClientSink? remoteSink = null;

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "boot",
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            Revision: 0,
            StepTicks: 1680UL,
            Tick: 1UL
        ));

        using var endpoint = new WorldAuthorityEndpoint(
            adjacencies: static () => null,
            clockOwnedHere: false,
            definition: () => remote,
            identity: "north",
            nextInputTick: static () => 6UL,
            observe: sink => {
                remoteSink = sink;
                sink.DeliverDefinition(definition: remote);
                sink.DeliverSnapshot(snapshot: new WorldSnapshot(
                    Authority: "north",
                    EngineTick: (5UL * 1680UL),
                    Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
                    Revision: 0,
                    StepTicks: 1680UL,
                    Tick: 5UL
                ));

                return new Lease();
            },
            submissions: new SilentLink(definition: remote)
        );
        using var boot = new WorldAuthorityEndpoint(
            adjacencies: static () => null,
            clockOwnedHere: true,
            definition: () => local,
            identity: "boot",
            nextInputTick: static () => 2UL,
            observe: sink => {
                sink.DeliverDefinition(definition: local);
                sink.DeliverSnapshot(snapshot: new WorldSnapshot(
                    Authority: "boot",
                    Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
                    Revision: 0,
                    StepTicks: 1680UL,
                    Tick: 1UL
                ));

                return new Lease();
            },
            submissions: new SilentLink(definition: local)
        );

        // The authority this client observes reads through the client's own mirror.
        Assert.Same(
            actual: client.StateMirrorFor(endpoint: boot),
            expected: client.StateMirror
        );

        var routed = client.StateMirrorFor(endpoint: endpoint);

        Assert.NotSame(
            actual: routed,
            expected: client.StateMirror
        );

        // The seat's rig comes from the remote document, and its bound yaw reads the remote row: the camera turns by
        // the remote heading, where the local mirror's heading would leave it unturned.
        var program = new WorldCameraProgram(
            Name: "turning",
            Operations: [new WorldCameraProgramOp.Orbit(
                Distance: 5f,
                Pitch: new BindableScalar(literal: 0f),
                Yaw: new BindableScalar(binding: "state.heading")
            )],
            Version: WorldCameraProgram.CurrentVersion
        );
        var anchor = new SdfAnchor(
            Orientation: Quaternion.Identity,
            Position: Vector3.Zero
        );
        var clock = new SdfCameraClock(
            AuthoritativeTick: 0UL,
            PresentationSeconds: 0f
        );

        var (routedEye, _, _) = WorldCameraRigCompiler.Compile(
            definition: remote,
            mirror: routed,
            program: program
        ).Resolve(
            anchor: in anchor,
            clock: in clock
        );
        var (localEye, _, _) = WorldCameraRigCompiler.Compile(
            definition: remote,
            mirror: client.StateMirror,
            program: program
        ).Resolve(
            anchor: in anchor,
            clock: in clock
        );

        Assert.True(
            condition: (Vector3.Distance(value1: routedEye, value2: localEye) > 1f),
            userMessage: $"the routed camera sat at {routedEye}, where the local mirror put it at {localEye}."
        );

        // A later delivery from the remote authority moves the routed read, and only through the remote stamp.
        var slot = routed.RegisterToken(
            conversion: WorldStateConversion.Number,
            token: "state.heading"
        );

        Assert.True(condition: routed.TryNumber(
            slot: slot,
            value: out var before
        ));
        Assert.Equal(
            actual: before,
            expected: 1f
        );

        remote = Fixtures.BuildDocument().WithWorldState(rows: [
            IntRow(
                name: "padding",
                value: 9
            ),
            IntRow(
                name: "heading",
                value: 2
            ),
        ]);
        remoteSink!.DeliverState(
            definition: remote,
            stamp: new WorldStateStamp(
                EngineTick: (5UL * 1680UL),
                Everything: false,
                MovedRows: new[] { 1 },
                Tick: 5UL
            )
        );
        Assert.Same(
            actual: client.StateMirrorFor(endpoint: endpoint),
            expected: routed
        );
        Assert.True(condition: routed.TryNumber(
            slot: slot,
            value: out var after
        ));
        Assert.Equal(
            actual: after,
            expected: 2f
        );
    }
    [Fact]
    public void ASessionMirrorReadsOnlyTheSlotsItsStampsMoved() {
        var rows = new WorldStateRow[16];

        for (var index = 0; (index < rows.Length); index++) {
            rows[index] = IntRow(
                name: $"row{index}",
                value: index
            );
        }

        var definition = Fixtures.BuildDocument().WithWorldState(rows: rows);
        var session = new WorldSessionMirror(placeholder: definition);

        session.DeliverDefinition(definition: definition);

        var state = session.FollowState();
        var view = new WorldDocumentStateView(definition: () => definition);
        var slots = new int[rows.Length];

        for (var index = 0; (index < rows.Length); index++) {
            slots[index] = state.Register(
                binding: new StateBinding(
                    Key: null,
                    Row: $"row{index}",
                    Target: false
                ),
                conversion: WorldStateConversion.Number
            );
        }

        // A follow with nothing delivered reads nothing.
        var before = Reads(mirror: state);

        Assert.Same(
            actual: session.FollowState(),
            expected: state
        );
        Assert.Equal(
            actual: (Reads(mirror: state) - before),
            expected: 0L
        );

        var moved = Ordinal(
            row: "row3",
            view: view
        );

        for (var tick = 1UL; (tick <= 4UL); tick++) {
            rows[3] = IntRow(
                name: "row3",
                value: (100L + ((long)tick))
            );
            definition = Fixtures.BuildDocument().WithWorldState(rows: rows);
            session.DeliverState(
                definition: definition,
                stamp: Stamp(
                moved: [moved],
                tick: tick
            )
            );
            before = Reads(mirror: state);
            _ = session.FollowState();
            Assert.Equal(
                actual: (Reads(mirror: state) - before),
                expected: 1L
            );
            Assert.True(condition: state.TryNumber(
                slot: slots[3],
                value: out var value
            ));
            Assert.Equal(
                actual: value,
                expected: (100f + tick)
            );
        }

        // Two deliveries between follows are read once each row, not once each delivery.
        session.DeliverState(
            definition: definition,
            stamp: Stamp(
            moved: [moved],
            tick: 5UL
        )
        );
        session.DeliverState(
            definition: definition,
            stamp: Stamp(
            moved: [moved],
            tick: 6UL
        )
        );
        before = Reads(mirror: state);
        _ = session.FollowState();
        Assert.Equal(
            actual: (Reads(mirror: state) - before),
            expected: 1L
        );
    }
    [Fact]
    public void ASessionMirrorsDeliveryAndFollowAllocateNothingInSteadyState() {
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            name: "gauge",
            value: 1
        )]);
        var session = new WorldSessionMirror(placeholder: definition);

        session.DeliverDefinition(definition: definition);

        var state = session.FollowState();

        _ = state.RegisterToken(
            conversion: WorldStateConversion.Number,
            token: "state.gauge"
        );

        int[] moved = [0];
        var tick = 0UL;

        void Deliveries() {
            for (var repetition = 0; (repetition < Repetitions); repetition++) {
                tick++;
                session.DeliverState(
                    definition: definition,
                    stamp: new WorldStateStamp(
                        EngineTick: tick,
                        Everything: false,
                        MovedRows: moved,
                        Tick: tick
                    )
                );
                _ = session.FollowState();
            }
        }

        Deliveries();
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Deliveries)
        );
    }
}
