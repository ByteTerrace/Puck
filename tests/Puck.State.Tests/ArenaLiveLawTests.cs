using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: an arena read resolves a cell's value-over-time trait against the clock the arena
/// stores, and an explicit write lands on what a reader sees and rebases that clock.</summary>
public sealed class ArenaLiveLawTests {
    // A retarget at tick zero saves a clock whose every number is zero; that clock is still the cell's clock, so
    // a presentation reader eases from the saved position rather than jumping to the target, while the arena reads stored truth.
    [Fact]
    public void ARetargetAtTickZeroEasesFromTheSavedPositionNotTheTarget() {
        var section = new StateSection(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "ease"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "spring"),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var time = new ArenaTime(
            Dynamics: [new DynamicsRow(
                Damping: 1f,
                Frequency: 1f,
                Name: "spring",
                Response: 0f
            )],
            EngineTick: 0UL,
            Tick: 0UL,
            TicksPerSecond: 30
        );

        Assert.True(condition: arena.TryWriteLive(
            key: slot,
            operand: 10L,
            reason: out var reason,
            rowOrdinal: 0,
            time: time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        Assert.True(condition: arena.TryReadClock(
            epochEngineTick: out _,
            epochTick: out _,
            key: slot,
            rowOrdinal: 0,
            set: out _,
            substepTicks: out _,
            v0: out var v0,
            y0: out var y0
        ));
        Assert.Equal(actual: y0, expected: 0L);
        Assert.Equal(actual: v0, expected: 0L);

        Assert.True(condition: StateReader.TryReadEased(
            dynamics: time.Dynamics,
            engineTick: time.EngineTick,
            key: StateRow.SlotKey.Value,
            rawValue: out var easedRaw,
            row: out _,
            rowName: "ease",
            rows: arena.ToRows(),
            text: out _,
            tick: time.Tick,
            ticksPerSecond: time.TicksPerSecond
        ));
        Assert.Equal(actual: easedRaw, expected: 0L);

        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: 0,
            time: time,
            value: out var atZero
        ));
        Assert.Equal(
            actual: atZero,
            expected: 10L
        );
        Assert.NotNull(@object: arena.ToRows()[0].Cells![0].Clock);
    }
    [Fact]
    public void AnAccumulatingCellReadsItsRateAndAnAddRebasesFromWhatAReaderSees() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var second = FixedTickConversion.TicksPerSecond;

        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: ArenaFixture.Clock,
            time: ArenaTime.At(
                engineTick: 0UL,
                tick: 0UL
            ),
            value: out var atEpoch
        ));
        Assert.Equal(
            actual: atEpoch,
            expected: 10L
        );

        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: ArenaFixture.Clock,
            time: ArenaTime.At(
                engineTick: second,
                tick: 0UL
            ),
            value: out var afterOneSecond
        ));
        Assert.Equal(
            actual: afterOneSecond,
            expected: 70L
        );

        // The stored base is still the authored 10: nothing per tick materializes.
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Clock
            )!.Value.AsInt,
            expected: 10L
        );

        Assert.True(condition: arena.TryWriteLive(
            key: slot,
            operand: 3L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Clock,
            time: ArenaTime.At(
                engineTick: second,
                tick: 0UL
            ),
            write: StateWriteKind.Add
        ), userMessage: reason);
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Clock
            )!.Value.AsInt,
            expected: 73L
        );

        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: ArenaFixture.Clock,
            time: ArenaTime.At(
                engineTick: second,
                tick: 0UL
            ),
            value: out var rebased
        ));
        Assert.Equal(
            actual: rebased,
            expected: 73L
        );

        // The new base accumulates from the engine tick the write landed at, not from zero.
        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: ArenaFixture.Clock,
            time: ArenaTime.At(
                engineTick: (second * 2UL),
                tick: 0UL
            ),
            value: out var afterTwoSeconds
        ));
        Assert.Equal(
            actual: afterTwoSeconds,
            expected: 133L
        );
    }
    [Fact]
    public void ASlotCellKeepsItsRowsTraitAndATraitFreeRowResolvesNone() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );

        // The slot key never carries an override, so the row's default still governs it.
        Assert.True(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: slot,
            rowOrdinal: ArenaFixture.Clock
        ));
        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: ArenaFixture.Clock,
            time: ArenaTime.At(
                engineTick: FixedTickConversion.TicksPerSecond,
                tick: 0UL
            ),
            value: out var advancing
        ));
        Assert.Equal(
            actual: advancing,
            expected: 70L
        );

        // A trait-free row resolves no behavior at all, whatever its cells carry.
        Assert.True(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: a,
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.LiveBehavior(
            key: a,
            rowOrdinal: ArenaFixture.Tokens
        ).IsNone);
    }
    [Fact]
    public void ATraitFreeRowTakesALiveWriteAsAnOrdinaryWrite() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var time = ArenaTime.At(
            engineTick: (FixedTickConversion.TicksPerSecond * 9UL),
            tick: 4UL
        );

        Assert.True(condition: arena.TryWriteLive(
            key: slot,
            operand: 2L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Score,
            time: time,
            write: StateWriteKind.Add
        ), userMessage: reason);
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )!.Value.AsInt,
            expected: 7L
        );
        Assert.True(condition: arena.TryReadClock(
            epochEngineTick: out var epochEngineTick,
            epochTick: out var epochTick,
            key: slot,
            rowOrdinal: ArenaFixture.Score,
            set: out _,
            substepTicks: out _,
            v0: out _,
            y0: out _
        ));
        Assert.Equal(
            actual: (epochTick + epochEngineTick),
            expected: 0L
        );
    }
    [Fact]
    public void ALiveWriteRefusedByTheEnvelopeLeavesTheCellAndItsClockAlone() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.False(condition: arena.TryWriteLive(
            key: slot,
            operand: 1000L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Score,
            time: ArenaTime.At(
                engineTick: 5UL,
                tick: 5UL
            ),
            write: StateWriteKind.Set
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "score"
        );
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )!.Value.AsInt,
            expected: 5L
        );
    }
    [Fact]
    public void ALiveWriteToAnAccumulatingCellRewindsWithItsScope() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var time = ArenaTime.At(
            engineTick: FixedTickConversion.TicksPerSecond,
            tick: 3UL
        );
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWriteLive(
            key: slot,
            operand: 3L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Clock,
            time: time,
            write: StateWriteKind.Add
        ), userMessage: reason);

        arena.Rewind(mark: mark);

        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Clock
            )!.Value.AsInt,
            expected: 10L
        );
        Assert.True(condition: arena.TryReadClock(
            epochEngineTick: out var epochEngineTick,
            epochTick: out var epochTick,
            key: slot,
            rowOrdinal: ArenaFixture.Clock,
            set: out _,
            substepTicks: out _,
            v0: out _,
            y0: out _
        ));
        Assert.Equal(
            actual: (epochTick + epochEngineTick),
            expected: 0L
        );
    }
    // A load is a birth: a cycling cell the document hands over with no clock of its own turns from the tick it was
    // installed at. Without the birth the untouched clock column reads zero and the same cell would already be
    // eight steps around at the moment it appeared.
    [Fact]
    public void ACyclingCellLoadedAtATickTurnsFromThatTickRatherThanFromZero() {
        var section = new StateSection(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "spin"),
            Kind: CellKind.Int,
            Cycle: new StateCycle(
                Output: CycleOutput.Step,
                TicksPerStep: 20L
            ),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.At(
                engineTick: 100UL,
                tick: 100UL
            )
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.True(condition: arena.TryReadClock(
            epochEngineTick: out _,
            epochTick: out var epochTick,
            key: slot,
            rowOrdinal: 0,
            set: out _,
            substepTicks: out _,
            v0: out _,
            y0: out _
        ));
        Assert.Equal(
            actual: epochTick,
            expected: 100L
        );

        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: 0,
            time: ArenaTime.At(
                engineTick: 160UL,
                tick: 160UL
            ),
            value: out var sixtyTicksOn
        ));
        Assert.Equal(
            actual: sixtyTicksOn,
            expected: 3L
        );
        Assert.Equal(
            actual: arena.ToRows()[0].Cells![0].Clock?.EpochTick,
            expected: 100L
        );
    }
    // A rotating cell's stored value is a phase and its live value the rotation the trait carried that phase to, so
    // an add turns the PHASE by the operand and leaves the clock where it is. Adding to the rotation instead would
    // bake the sixty ticks already turned into the stored phase and turn them again on the next read.
    [Fact]
    public void AnAddOnARotatingCellTurnsItsStoredPhaseRatherThanItsRotation() {
        var section = new StateSection(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "spin"),
            Kind: CellKind.Int,
            Cycle: new StateCycle(
                Output: CycleOutput.Step,
                TicksPerStep: 20L
            ),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.At(
                engineTick: 100UL,
                tick: 100UL
            )
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var turned = ArenaTime.At(
            engineTick: 160UL,
            tick: 160UL
        );

        Assert.True(condition: arena.TryWriteLive(
            key: slot,
            operand: 2L,
            reason: out var reason,
            rowOrdinal: 0,
            time: turned,
            write: StateWriteKind.Add
        ), userMessage: reason);
        Assert.Equal(
            actual: arena.ToRows()[0].Cells![0].Value.AsInt,
            expected: 2L
        );
        Assert.Equal(
            actual: arena.ToRows()[0].Cells![0].Clock?.EpochTick,
            expected: 100L
        );
        Assert.True(condition: arena.TryReadLiveNumber(
            key: slot,
            rowOrdinal: 0,
            time: turned,
            value: out var live
        ));
        Assert.Equal(
            actual: live,
            expected: 5L
        );
    }
}
