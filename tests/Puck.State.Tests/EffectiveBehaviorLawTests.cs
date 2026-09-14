using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="EffectiveBehavior.Resolve"/> — a cell's own trait replaces its row's
/// default wholesale, <see cref="StateCellBehavior.None"/> opts a cell out regardless of what the row declares, a
/// cell that declares neither inherits the row's default (including a key that does not exist yet, resolved the same
/// way a bare row read would), and a row's own slot cell never carries an override of its own. A row declaring a
/// behavior before any cell exists gains no cell by normalization.</summary>
public sealed class EffectiveBehaviorLawTests {
    private static StateAdvance Advance(long numerator = 1L, long denominator = 1L) => new(
        PerSecondNumerator: numerator,
        PerSecondDenominator: denominator
    );
    private static StateCycle Cycle() => new();
    private static StateDynamics Dynamics(string row = "spring") => new(Row: row);
    private static CellName Key(string value) => CellName.Parse(candidate: value);
    private static StateRow Row(StateAdvance? advance = null, StateDynamics? dynamics = null, StateCycle? cycle = null, IReadOnlyList<StateCell>? cells = null) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Advance: advance,
        Dynamics: dynamics,
        Cycle: cycle,
        Cells: cells
    );

    [Fact]
    public void ACellDeclaringItsOwnAdvanceReplacesTheRowsDynamicsDefaultWholesale() {
        var advance = Advance();
        var row = Row(dynamics: Dynamics());
        var cell = new StateCell(
            Key: Key(value: "a"),
            Advance: advance
        );
        var resolved = EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );

        Assert.Same(
            expected: advance,
            actual: resolved.Advance
        );
        Assert.Null(@object: resolved.Dynamics);
        Assert.Null(@object: resolved.Cycle);
        Assert.False(condition: resolved.IsNone);
    }
    [Fact]
    public void ACellOptingOutIsNoneRegardlessOfTheRowsOwnDefault() {
        var row = Row(advance: Advance());
        var opted = new StateCell(
            Key: Key(value: "a"),
            Behavior: StateCellBehavior.None
        );

        var resolved = EffectiveBehavior.Resolve(
            cell: opted,
            row: row
        );

        Assert.True(condition: resolved.IsNone);
        Assert.Null(@object: resolved.Advance);
    }
    [Fact]
    public void ACellThatInheritsAndDeclaresNoTraitOfItsOwnTakesTheRowsDefault() {
        var advance = Advance();
        var row = Row(advance: advance);
        var inheriting = new StateCell(Key: Key(value: "a"));

        var resolved = EffectiveBehavior.Resolve(
            cell: inheriting,
            row: row
        );

        Assert.Same(
            expected: advance,
            actual: resolved.Advance
        );
        Assert.False(condition: resolved.IsNone);
    }
    // A row's declared behavior applies to every cell, including a key a write mints later — modeled here by
    // resolving against `cell: null`, the same answer StateReader/StateFrame give a key that does not exist yet.
    [Fact]
    public void AKeyThatDoesNotExistYetInheritsTheRowsDefaultTheSameWayAnExistingUnoverriddenCellDoes() {
        var advance = Advance(
            denominator: 3L,
            numerator: -2L
        );
        var row = Row(advance: advance);

        var forAMintedKey = EffectiveBehavior.Resolve(
            cell: null,
            row: row
        );
        var forAnOrdinaryInheritingCell = EffectiveBehavior.Resolve(
            cell: new StateCell(Key: Key(value: "existing")),
            row: row
        );

        Assert.Same(
            expected: advance,
            actual: forAMintedKey.Advance
        );
        Assert.Equal(
            expected: forAnOrdinaryInheritingCell,
            actual: forAMintedKey
        );
    }
    // A row declaring a behavior before any cell exists (the plain constructor with no Cells authored) gains no
    // manufactured cell — Cells stays exactly what was authored.
    [Fact]
    public void ARowThatDeclaresABehaviorBeforeAnyCellExistsGainsNoCellByNormalization() {
        var row = Row(advance: Advance());

        Assert.Null(@object: row.Cells);
        Assert.Null(@object: StateRows.FindCell(
            cells: row.Cells,
            key: StateRow.SlotKey
        ));

        var emptied = Row(
            advance: Advance(),
            cells: []
        );

        Assert.Empty(collection: emptied.Cells!);
    }
    [Fact]
    public void ARowWithNoBehaviorAtAllAndACellThatDoesNotOverrideResolvesToNone() {
        var row = Row();
        var plain = new StateCell(Key: Key(value: "a"));

        Assert.True(condition: EffectiveBehavior.Resolve(
            cell: null,
            row: row
        ).IsNone);
        Assert.True(condition: EffectiveBehavior.Resolve(
            cell: plain,
            row: row
        ).IsNone);
        Assert.Equal(
            expected: EffectiveBehavior.None,
            actual: EffectiveBehavior.Resolve(
                cell: plain,
                row: row
            )
        );
    }
    // The slot key is never eligible for its own override — a slot row's one cell is always governed by the row's
    // own default, even if it happens to carry a trait of its own (an authoring mistake the validator refuses
    // separately; the resolver's own rule is unconditional on the key alone).
    [Fact]
    public void ASlotKeyedCellNeverCarriesAnOverrideOfItsOwnEvenIfOneIsPresent() {
        var rowAdvance = Advance();
        var row = Row(advance: rowAdvance);
        var slotCellCarryingItsOwnCycle = new StateCell(
            Key: StateRow.SlotKey,
            Cycle: Cycle()
        );

        var resolved = EffectiveBehavior.Resolve(
            cell: slotCellCarryingItsOwnCycle,
            row: row
        );

        Assert.Same(
            expected: rowAdvance,
            actual: resolved.Advance
        );
        Assert.Null(@object: resolved.Cycle);
    }
    [Fact]
    public void EveryTraitKindIsWrappedAndExposedByExactlyOneOfTheThreeProperties() {
        var advance = EffectiveBehavior.OfAdvance(advance: Advance());
        var dynamics = EffectiveBehavior.OfDynamics(dynamics: Dynamics());
        var cycle = EffectiveBehavior.OfCycle(cycle: Cycle());

        Assert.NotNull(@object: advance.Advance);
        Assert.Null(@object: advance.Dynamics);
        Assert.Null(@object: advance.Cycle);

        Assert.NotNull(@object: dynamics.Dynamics);
        Assert.Null(@object: dynamics.Advance);
        Assert.Null(@object: dynamics.Cycle);

        Assert.NotNull(@object: cycle.Cycle);
        Assert.Null(@object: cycle.Advance);
        Assert.Null(@object: cycle.Dynamics);

        Assert.True(condition: EffectiveBehavior.None.IsNone);
    }
}
