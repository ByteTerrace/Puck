using System.Numerics;

using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the presentation's state mirror (<see cref="WorldStateMirror"/>): a tick reads only the bound rows its
/// stamp moved plus the trait-bearing slots still moving, so a tick moving one bound row of many reads once and a
/// tick moving nothing reads nothing; an easing slot keeps refreshing after its row stops moving and stops once its
/// follower rests; the eased and <c>.$target</c> forms of one row differ while the follower moves and agree at rest;
/// an advancing row's camera operand moves with the engine tick; and the refresh and the apply allocate nothing in
/// steady state.
/// </summary>
public sealed class WorldStateMirrorLawTests {
    private const int BoundRows = 16;
    private const int Repetitions = 64;

    private static readonly DynamicsRow Critical = new(
        Damping: 1f,
        Frequency: 4f,
        Name: "critical",
        Response: 0f
    );

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
    private static WorldDefinition Rows(long movedValue) {
        var rows = new WorldStateRow[BoundRows];

        for (var index = 0; (index < BoundRows); index++) {
            rows[index] = IntRow(
                name: $"row{index}",
                value: ((index == 3)
                    ? movedValue
                    : index)
            );
        }

        return Fixtures.BuildDocument().WithWorldState(rows: rows);
    }
    // A gauge whose easing follower starts displaced from its stored target, at the 240 Hz rate the follower steps at.
    private static WorldDefinition Easing() => (Fixtures.BuildDocument() with {
        DynamicsRaw = [Critical],
        Simulation = new WorldSimulationDefaults(RateHz: 240),
    }).WithWorldState(rows: [new WorldStateRow(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 300),
                Clock: new StateCellClock(
                    Y0: 0,
                    V0: 0
                )
            )],
        Dynamics: new StateDynamics(Row: Critical.Name)
    )]);
    private static WorldStateStamp Stamp(ulong tick, params int[] moved) => new(
        EngineTick: (tick * 1680UL),
        Everything: false,
        MovedRows: moved,
        Tick: tick
    );

    [Fact]
    public void ATickMovingOneBoundRowOfManyReadsOnce_AndATickMovingNothingReadsNothing() {
        var definition = Rows(movedValue: 3);
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);
        var slots = new int[BoundRows];

        for (var index = 0; (index < BoundRows); index++) {
            slots[index] = mirror.Bind(
                binding: new StateBinding(
                Key: null,
                Row: $"row{index}",
                Target: false
            ),
                conversion: WorldStateConversion.Number
            );
        }

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        Assert.True(condition: view.TryResolveRow(
            ordinal: out var moved,
            rowName: "row3"
        ));

        for (var tick = 1UL; (tick <= 4UL); tick++) {
            definition = Rows(movedValue: (100L + ((long)tick)));

            var before = Reads(mirror: mirror);

            mirror.Refresh(stamp: Stamp(
                moved: [moved],
                tick: tick
            ));
            Assert.Equal(
                expected: 1L,
                actual: (Reads(mirror: mirror) - before)
            );
            Assert.True(condition: mirror.TryNumber(
                slot: slots[3],
                value: out var value
            ));
            Assert.Equal(
                actual: value,
                expected: (100f + tick)
            );
        }

        var still = Reads(mirror: mirror);

        mirror.Refresh(stamp: Stamp(tick: 5UL));
        mirror.Advance(
            engineTick: (6UL * 1680UL),
            tick: 6UL
        );
        Assert.Equal(
            expected: still,
            actual: Reads(mirror: mirror)
        );
    }
    [Fact]
    public void AnEasingSlotKeepsRefreshingAfterItsRowStopsMoving_AndStopsOnceItsFollowerRests() {
        var definition = Easing();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var slot = mirror.Bind(
            binding: new StateBinding(
            Key: null,
            Row: "gauge",
            Target: false
        ),
            conversion: WorldStateConversion.Number
        );

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var easingTicks = 0;
        var restedAt = 0UL;

        // No stamp names the row after the install: every read below is the trait keeping the slot restless.
        for (var tick = 1UL; (tick <= 4000UL); tick++) {
            var before = Reads(mirror: mirror);

            mirror.Advance(
                engineTick: (tick * 210UL),
                tick: tick
            );

            var read = (Reads(mirror: mirror) - before);

            if (read == 1L) {
                Assert.Equal(
                    actual: restedAt,
                    expected: 0UL
                );
                easingTicks++;
            } else {
                Assert.Equal(
                    actual: read,
                    expected: 0L
                );

                if (restedAt == 0UL) {
                    restedAt = tick;
                }
            }
        }

        Assert.True(
            condition: (easingTicks > 10),
            userMessage: $"the follower refreshed for {easingTicks} ticks; a displaced follower keeps moving for many."
        );
        Assert.NotEqual(
            actual: restedAt,
            expected: 0UL
        );
        Assert.Equal(
            expected: WorldStateMotion.Still,
            actual: mirror.Sample(slot: slot).Motion
        );
        Assert.True(condition: mirror.TryNumber(
            slot: slot,
            value: out var rested
        ));
        Assert.Equal(
            actual: rested,
            expected: 300f
        );
    }
    [Fact]
    public void TheEasedAndTargetFormsOfADynamicsRowDifferMidFollower_AndAgreeAtRest() {
        var definition = Easing();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var eased = mirror.Bind(
            binding: new StateBinding(
            Key: null,
            Row: "gauge",
            Target: false
        ),
            conversion: WorldStateConversion.Number
        );
        var target = mirror.Bind(
            conversion: WorldStateConversion.Number,
            token: "state.gauge.$target"
        );

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        mirror.Advance(
            engineTick: 0UL,
            tick: 24UL
        );
        Assert.True(condition: mirror.TryNumber(
            slot: eased,
            value: out var easedMid
        ));
        Assert.True(condition: mirror.TryNumber(
            slot: target,
            value: out var targetMid
        ));
        Assert.Equal(
            actual: targetMid,
            expected: 300f
        );
        Assert.True(
            condition: (easedMid < targetMid),
            userMessage: $"mid-follower the eased form reads {easedMid}, which should trail the stored {targetMid}."
        );

        mirror.Advance(
            engineTick: 0UL,
            tick: 4000UL
        );
        Assert.True(condition: mirror.TryNumber(
            slot: eased,
            value: out var easedRest
        ));
        Assert.True(condition: mirror.TryNumber(
            slot: target,
            value: out var targetRest
        ));
        Assert.Equal(
            actual: easedRest,
            expected: targetRest
        );
    }
    [Fact]
    public void AnAdvancingRowsCameraOperandMovesWithTheEngineTick() {
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            advance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: 1
            ),
            name: "heading",
            value: 0
        )]);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var rig = WorldCameraRigCompiler.Compile(
            definition: definition,
            mirror: mirror,
            program: new WorldCameraProgram(
                Name: "turning",
                Operations: [new WorldCameraProgramOp.Orbit(
                    Distance: 5f,
                    Pitch: new BindableScalar(literal: 0f),
                    Yaw: new BindableScalar(binding: "state.heading")
                )],
                Version: WorldCameraProgram.CurrentVersion
            )
        );
        var anchor = new SdfAnchor(
            Orientation: Quaternion.Identity,
            Position: Vector3.Zero
        );
        // The presentation clock holds still: only the delivered engine tick advances.
        var clock = new SdfCameraClock(
            AuthoritativeTick: 0UL,
            PresentationSeconds: 0f
        );

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        // The program is not the document's, so its operand is registered as the document's manifest would register it.
        _ = mirror.Bind(
            conversion: WorldStateConversion.Number,
            token: "state.heading"
        );

        var (atRest, _, _) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        // One second of engine ticks advances the row by one: the orbit turns a radian.
        mirror.Advance(
            engineTick: FixedTickConversion.TicksPerSecond,
            tick: 30UL
        );
        mirror.Apply(fraction: 1f);

        var (turned, _, _) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        Assert.True(
            condition: (Vector3.Distance(value1: atRest, value2: turned) > 1f),
            userMessage: $"the eye stayed at {turned} although the bound heading advanced a radian from {atRest}."
        );
    }
    [Fact]
    public void TheRefreshAndTheApplyAllocateNothingInSteadyState() {
        var definition = Rows(movedValue: 3);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        for (var index = 0; (index < BoundRows); index++) {
            _ = mirror.Bind(
                binding: new StateBinding(
                Key: null,
                Row: $"row{index}",
                Target: false
            ),
                conversion: WorldStateConversion.Number
            );
        }

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var tick = 0UL;
        int[] moved = [3];

        void Refresh() {
            for (var repetition = 0; (repetition < Repetitions); repetition++) {
                tick++;
                mirror.Refresh(stamp: new WorldStateStamp(
                    EngineTick: tick,
                    Everything: false,
                    MovedRows: moved,
                    Tick: tick
                ));
            }
        }
        void Apply() {
            for (var repetition = 0; (repetition < Repetitions); repetition++) {
                mirror.Apply(fraction: (repetition / ((float)Repetitions)));
            }
        }

        Refresh();
        Apply();
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Refresh)
        );
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Apply)
        );
    }
}
