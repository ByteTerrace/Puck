using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: re-declaring a row settles every cell it carries across whatever the declaration
/// changed about its value-over-time behavior — one case per row of the transition table.</summary>
public sealed class ArenaRedeclareSettleLawTests {
    private const ulong Second = FixedTickConversion.TicksPerSecond;

    private static readonly DynamicsRow[] Rows = [
        new(
        Damping: 1f,
        Frequency: 1f,
        Name: "slow",
        Response: 0f
    ),
        new(
        Damping: 1f,
        Frequency: 8f,
        Name: "fast",
        Response: 0f
    ),
    ];

    private static StateRow Fixed(long rawBits, StateCycle? cycle, StateCellClock? clock = null) => new(
        Cells: [new StateCell(
                Clock: clock,
                Key: StateRow.SlotKey,
                Value: CellValue.Fixed(rawBits: rawBits)
            )],
        Cycle: cycle,
        Kind: CellKind.Fixed,
        Name: CellName.Parse(candidate: "gauge")
    );
    private static StateRow Keyed(StateCell cell, StateAdvance rowAdvance) => new(
        Advance: rowAdvance,
        Capacity: 8,
        Cells: [cell],
        Kind: CellKind.Int,
        Name: CellName.Parse(candidate: "gauge")
    );
    private static StateRow Row(StateCell cell, StateAdvance? advance = null, StateDynamics? dynamics = null, StateCycle? cycle = null) => new(
        Advance: advance,
        Cells: [cell],
        Cycle: cycle,
        Dynamics: dynamics,
        Kind: CellKind.Int,
        Name: CellName.Parse(candidate: "gauge")
    );
    private static StateCell Settled(StateRow declared, StateRow? previous, in ArenaTime time) {
        var section = new StateSection(Rows: [declared]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.True(condition: arena.TrySettleRedeclared(
            previous: previous,
            rowOrdinal: 0,
            time: in time
        ));

        return arena.ToRows()[0].Cells![0];
    }
    private static ArenaTime Time(ulong tick, ulong engineTick) => new(
        Dynamics: Rows,
        EngineTick: engineTick,
        Tick: tick,
        TicksPerSecond: 30
    );
    private static StateCell Value(long value, StateCellClock? clock = null, StateAdvance? advance = null, StateDynamics? dynamics = null, StateCycle? cycle = null) => new(
        Advance: advance,
        Clock: clock,
        Cycle: cycle,
        Dynamics: dynamics,
        Key: StateRow.SlotKey,
        Value: CellValue.Int(value: value)
    );

    // Advance, unchanged: a declaration restating the stored base has not written the cell, so the accumulation the
    // cell had actually reached becomes the new base and both epochs move to the declaring tick — the read is
    // continuous across the declaration rather than restarting at the base it was accumulating away from.
    [Fact]
    public void AnAdvancingCellRebasesBothEpochsToTheDeclaringTick() {
        var advance = new StateAdvance(
            PerSecondDenominator: 1,
            PerSecondNumerator: 60
        );
        var settled = Settled(
            declared: Row(
                advance: advance,
                cell: Value(value: 10L)
            ),
            previous: Row(
                advance: advance,
                cell: Value(
                    clock: new StateCellClock(),
                    value: 10L
                )
            ),
            time: Time(
                engineTick: Second,
                tick: 5UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 70L
        );
        Assert.Equal(
            actual: settled.Clock?.EpochEngineTick,
            expected: ((long)Second)
        );
        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 5L
        );
    }
    // Dynamics entered from none: the follower has nothing to ease from, so it rests on the carried value.
    [Fact]
    public void ACellEnteringDynamicsFromNoneRestsTheFollowerOnItsCarriedValue() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 20L),
                dynamics: new StateDynamics(Row: "slow")
            ),
            previous: Row(cell: Value(value: 20L)),
            time: Time(
                engineTick: 90UL,
                tick: 3UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 20L
        );
        Assert.Equal(
            actual: settled.Clock?.Y0,
            expected: (20L << FixedQ4816.FractionBitCount)
        );
        Assert.Equal(
            actual: settled.Clock?.V0,
            expected: 0L
        );
        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 3L
        );
    }
    // Dynamics entered from advance: the target rests on the accumulation the advancing cell had actually reached,
    // never on the base it was accumulating away from.
    [Fact]
    public void ACellEnteringDynamicsFromAdvanceRestsTheTargetOnTheLiveAccumulation() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 10L),
                dynamics: new StateDynamics(Row: "slow")
            ),
            previous: Row(
                advance: new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: 60
                ),
                cell: Value(
                    clock: new StateCellClock(),
                    value: 10L
                )
            ),
            time: Time(
                engineTick: Second,
                tick: 4UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 70L
        );
    }
    // Dynamics retuned: the follower is chasing a target the declaration did not touch, so it keeps it and carries
    // its sampled position rather than jumping to it.
    [Fact]
    public void ARetunedDynamicsFollowerKeepsItsTargetAndCarriesItsSampledPosition() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 100L),
                dynamics: new StateDynamics(Row: "fast")
            ),
            previous: Row(
                cell: Value(
                    clock: new StateCellClock(Y0: (20L << FixedQ4816.FractionBitCount)),
                    value: 100L
                ),
                dynamics: new StateDynamics(Row: "slow")
            ),
            time: Time(
                engineTick: 300UL,
                tick: 10UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 100L
        );
        Assert.NotNull(@object: settled.Clock);
        Assert.InRange(
            actual: settled.Clock!.Y0,
            high: ((100L << FixedQ4816.FractionBitCount) - 1L),
            low: (20L << FixedQ4816.FractionBitCount)
        );
        Assert.Equal(
            actual: settled.Clock.EpochTick,
            expected: 10L
        );
    }
    // A key the previous declaration did not carry has no prior clock to carry forward, so its rotation starts at
    // the declaring tick rather than at the origin the load stamped.
    [Fact]
    public void AFreshCyclingKeyStartsTurningAtTheDeclaringTick() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 0L),
                cycle: new StateCycle(
                    Output: CycleOutput.Step,
                    TicksPerStep: 20L
                )
            ),
            previous: Row(cell: new StateCell(
                Key: CellName.Parse(candidate: "other"),
                Value: CellValue.Int(value: 0L)
            )),
            time: Time(
                engineTick: 210UL,
                tick: 7UL
            )
        );

        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 7L
        );
    }
    // Switching into cycle: the old behavior's value becomes the starting phase and the rotation starts now.
    [Fact]
    public void ACellSwitchingIntoCycleStartsFromTheValueItCarried() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 3L),
                cycle: new StateCycle(
                    Output: CycleOutput.Step,
                    TicksPerStep: 20L
                )
            ),
            previous: Row(cell: Value(value: 3L)),
            time: Time(
                engineTick: 240UL,
                tick: 8UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 3L
        );
        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 8L
        );
        Assert.Equal(
            actual: settled.Clock?.SubstepTicks,
            expected: 0L
        );
    }
    // A changed step length is a changed trait: the phase the old rotation had actually reached is settled into the
    // stored value and its part-turn is carried as the new clock's substep, so the rotation neither jumps nor
    // repeats the part-turn it had already made.
    [Fact]
    public void ACycleWhoseStepLengthChangedSettlesItsPhaseAndCarriesItsPartTurn() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 0L),
                cycle: new StateCycle(
                    Output: CycleOutput.Step,
                    TicksPerStep: 30L
                )
            ),
            previous: Row(
                cell: Value(
                    clock: new StateCellClock(),
                    value: 0L
                ),
                cycle: new StateCycle(
                    Output: CycleOutput.Step,
                    TicksPerStep: 20L
                )
            ),
            time: Time(
                engineTick: 1500UL,
                tick: 50UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 2L
        );
        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 50L
        );
        Assert.Equal(
            actual: settled.Clock?.SubstepTicks,
            expected: 10L
        );
    }
    // An unchanged cycle is not a transition: the phase write IS the operation, so the clock stays exactly where the
    // declaration left it and the rotation keeps its own reckoning.
    [Fact]
    public void ARewriteUnderAnUnchangedCycleLeavesTheClockWhereItWas() {
        var cycle = new StateCycle(
            Output: CycleOutput.Step,
            TicksPerStep: 20L
        );
        var settled = Settled(
            declared: Row(
                cell: Value(
                    clock: new StateCellClock(EpochTick: 3L),
                    value: 9L
                ),
                cycle: cycle
            ),
            previous: Row(
                cell: Value(
                    clock: new StateCellClock(EpochTick: 3L),
                    value: 5L
                ),
                cycle: cycle
            ),
            time: Time(
                engineTick: 3000UL,
                tick: 100UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 9L
        );
        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 3L
        );
    }
    // A cell carrying its own advance is untouched by a change to the row's default, so its accumulation continues:
    // the row default is a fallback for cells that declare none, never a reason to rebase one that does.
    [Fact]
    public void ChangingARowDefaultDoesNotRestartACellsOwnAdvance() {
        var cell = new StateCell(
            Advance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: (2L * ((long)Second))
            ),
            Key: CellName.Parse(candidate: "a"),
            Value: CellValue.Int(value: 10L)
        );
        var declared = Keyed(
            cell: cell,
            rowAdvance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: (3L * ((long)Second))
            )
        );
        var settled = Settled(
            declared: declared,
            previous: Keyed(
                cell: (cell with { Clock = new StateCellClock() }),
                rowAdvance: new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: ((long)Second)
                )
            ),
            time: Time(
                engineTick: Second,
                tick: 5UL
            )
        );

        // Two units per engine tick throughout, never the row default's one or three.
        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: (10L + (2L * ((long)Second)))
        );
        Assert.Equal(
            actual: settled.Clock?.EpochEngineTick,
            expected: ((long)Second)
        );
    }
    // A cycling cell stores a phase and displays an output derived from it, so switching the rotation off must store
    // what the cell was displaying: the stored phase alone would move the value a reader can see.
    [Fact]
    public void ACellSwitchingOffACycleFreezesTheValueItDisplayed() {
        var cycle = new StateCycle(Output: CycleOutput.Cos);
        var settled = Settled(
            declared: Fixed(
                cycle: null,
                rawBits: 0L
            ),
            previous: Fixed(
                clock: new StateCellClock(),
                cycle: cycle,
                rawBits: 0L
            ),
            time: Time(
                engineTick: 0UL,
                tick: 0UL
            )
        );

        // The rotation has not turned, and its cosine at rest is one — the stored phase of zero is not what a reader
        // was seeing.
        Assert.Equal(
            actual: settled.Value.Raw,
            expected: FixedQ4816.One.Value
        );
    }
    // A retuned rotation re-derives its own output from a phase, so the phase — not the value the old rotation
    // displayed — is what carries across.
    [Fact]
    public void ARetunedCycleCarriesItsPhaseRatherThanTheValueItDisplayed() {
        var settled = Settled(
            declared: Fixed(
                cycle: new StateCycle(
                    Output: CycleOutput.Cos,
                    TicksPerStep: 30L
                ),
                rawBits: 0L
            ),
            previous: Fixed(
                clock: new StateCellClock(),
                cycle: new StateCycle(
                    Output: CycleOutput.Cos,
                    TicksPerStep: 20L
                ),
                rawBits: 0L
            ),
            time: Time(
                engineTick: 1500UL,
                tick: 50UL
            )
        );

        // Fifty ticks at twenty ticks a step is two whole steps, stored as a fixed row's whole-numbered phase.
        Assert.Equal(
            actual: settled.Value.Raw,
            expected: (2L << FixedQ4816.FractionBitCount)
        );
        Assert.Equal(
            actual: settled.Clock?.SubstepTicks,
            expected: 10L
        );
    }
    // Switching a trait off freezes the value the old behavior had actually reached, not the base it was
    // accumulating away from, and clears the clock to the change tick.
    [Fact]
    public void ACellSwitchingToNoTraitFreezesAtTheValueItHadReached() {
        var settled = Settled(
            declared: Row(cell: Value(value: 10L)),
            previous: Row(
                advance: new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: 60
                ),
                cell: Value(
                    clock: new StateCellClock(),
                    value: 10L
                )
            ),
            time: Time(
                engineTick: Second,
                tick: 6UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 70L
        );
        Assert.Equal(
            actual: settled.Clock?.EpochTick,
            expected: 6L
        );
    }
    // A row redeclared from text hands over a cell that carries no number, so there is nothing to settle from: the
    // cell settles as a new one instead of reading a number the old declaration never stored.
    [Fact]
    public void ACellWhoseRowWasTextSettlesAsANewCell() {
        var settled = Settled(
            declared: Row(
                cell: Value(value: 20L),
                dynamics: new StateDynamics(Row: "slow")
            ),
            previous: new StateRow(
                Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Text(value: "twenty")
                )],
                Kind: CellKind.Text,
                Name: CellName.Parse(candidate: "gauge")
            ),
            time: Time(
                engineTick: 90UL,
                tick: 3UL
            )
        );

        Assert.Equal(
            actual: settled.Value.AsInt,
            expected: 20L
        );
    }
}
