using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="StateRow.TryAdmitWrite"/> is the one door every write path decides
/// through — refuse or saturate at an authored bound, at <see cref="long.MinValue"/>/<see cref="long.MaxValue"/> on
/// a side that declares none, for <see cref="StateWriteKind.Set"/> and <see cref="StateWriteKind.Add"/> alike, on a
/// row carrying both bounds, one bound, or none at all. Every expectation here is derived independently in
/// <see cref="Int128"/> arithmetic — a second, separately written statement of the same refuse/saturate rule the
/// method must obey — never by calling the method under test.</summary>
public sealed class StateRowEnvelopeLawTests {
    // Independently derives (admitted, stored) from the row's own Min/Max/Overflow: the exact Int128 sum or
    // replacement, then the contract's own refuse/saturate policy restated from scratch.
    private static (bool Admitted, long Stored) Oracle(long current, long operand, StateWriteKind write, long? min, long? max, StateOverflow overflow) {
        var exact = ((write == StateWriteKind.Add)
            ? (((Int128)current) + operand)
            : ((Int128)operand)
        );
        var lower = ((Int128)(min ?? long.MinValue));
        var upper = ((Int128)(max ?? long.MaxValue));

        if (
            (exact < lower) ||
            (exact > upper)
        ) {
            return ((overflow == StateOverflow.Saturate)
                ? (true, ((long)((exact < lower) ? lower : upper)))
                : (false, 0L)
            );
        }

        return (true, ((long)exact));
    }
    private static StateRow Row(long? min, long? max, StateOverflow overflow = StateOverflow.Refuse) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: min,
        Max: max,
        Overflow: overflow,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    );
    private static void Verify(long current, long operand, StateWriteKind write, long? min, long? max, StateOverflow overflow) {
        var (expectedAdmitted, expectedStored) = Oracle(
            current: current,
            max: max,
            min: min,
            operand: operand,
            overflow: overflow,
            write: write
        );
        var row = Row(
            max: max,
            min: min,
            overflow: overflow
        );
        var admitted = row.TryAdmitWrite(
            current: current,
            operand: operand,
            write: write,
            stored: out var stored,
            reason: out var reason
        );

        Assert.Equal(
            actual: admitted,
            expected: expectedAdmitted
        );
        Assert.Equal(
            actual: stored,
            expected: expectedStored
        );
        Assert.Equal(
            actual: reason,
            expected: (admitted
                ? string.Empty
                : reason
            )
        );
        Assert.Equal(
            expected: admitted,
            actual: (reason.Length == 0)
        );
    }

    // Both bounds present, the default Refuse policy: inside the range admits exactly; a Set outside it, and an
    // Add that crosses it from inside, both refuse — the mutation is never silently clamped.
    [InlineData(50L, 40L, StateWriteKind.Set, 0L, 100L)]
    [InlineData(50L, -60L, StateWriteKind.Set, 0L, 100L)]
    [InlineData(50L, 200L, StateWriteKind.Set, 0L, 100L)]
    [InlineData(50L, 40L, StateWriteKind.Add, 0L, 100L)]
    [InlineData(90L, 20L, StateWriteKind.Add, 0L, 100L)]
    [InlineData(10L, -20L, StateWriteKind.Add, 0L, 100L)]
    [InlineData(0L, 0L, StateWriteKind.Set, 0L, 100L)]
    [InlineData(100L, 100L, StateWriteKind.Set, 0L, 100L)]
    [Theory]
    public void ARowWithBothBoundsRefusesOutOfRangeAndAdmitsInRange(long current, long operand, StateWriteKind write, long min, long max) {
        Verify(
            current: current,
            max: max,
            min: min,
            operand: operand,
            overflow: StateOverflow.Refuse,
            write: write
        );
    }
    // Saturate clamps the true (exact, unwrapped) result to whichever authored bound it crossed, for Set and Add.
    [InlineData(50L, 200L, StateWriteKind.Set, 0L, 100L, 100L)]
    [InlineData(50L, -60L, StateWriteKind.Set, 0L, 100L, 0L)]
    [InlineData(90L, 50L, StateWriteKind.Add, 0L, 100L, 100L)]
    [InlineData(10L, -50L, StateWriteKind.Add, 0L, 100L, 0L)]
    [InlineData(long.MaxValue, 1L, StateWriteKind.Add, null, null, long.MaxValue)]
    [InlineData(long.MinValue, -1L, StateWriteKind.Add, null, null, long.MinValue)]
    [Theory]
    public void SaturateClampsTheExactResultToTheCrossedBound(long current, long operand, StateWriteKind write, long? min, long? max, long expectedStored) {
        Verify(
            current: current,
            max: max,
            min: min,
            operand: operand,
            overflow: StateOverflow.Saturate,
            write: write
        );

        var row = Row(
            max: max,
            min: min,
            overflow: StateOverflow.Saturate
        );

        Assert.True(condition: row.TryAdmitWrite(
            current: current,
            operand: operand,
            write: write,
            stored: out var stored,
            reason: out _
        ));
        Assert.Equal(
            actual: stored,
            expected: expectedStored
        );
    }
    // A one-sided range (a floor with no ceiling, or the reverse) is legal — the undeclared side behaves as if
    // bounded by long.MinValue/MaxValue, on both Refuse and Saturate.
    [InlineData(5L, -3L, StateWriteKind.Add, 0L, null, true, 2L)]
    [InlineData(2L, -5L, StateWriteKind.Add, 0L, null, false, 0L)]
    [InlineData(0L, long.MinValue, StateWriteKind.Add, 0L, null, false, 0L)]
    [InlineData(-5L, 5L, StateWriteKind.Set, null, 10L, true, 5L)]
    [InlineData(5L, 11L, StateWriteKind.Set, null, 10L, false, 0L)]
    [Theory]
    public void AOneSidedRangeBoundsOnlyItsDeclaredSide(long current, long operand, StateWriteKind write, long? min, long? max, bool expectedAdmitted, long expectedStored) {
        Verify(
            current: current,
            max: max,
            min: min,
            operand: operand,
            overflow: StateOverflow.Refuse,
            write: write
        );

        var row = Row(
            max: max,
            min: min,
            overflow: StateOverflow.Refuse
        );

        Assert.Equal(
            expected: expectedAdmitted,
            actual: row.TryAdmitWrite(
                current: current,
                operand: operand,
                write: write,
                stored: out var stored,
                reason: out _
            )
        );
        Assert.Equal(
            actual: stored,
            expected: expectedStored
        );
    }
    // A one-sided range saturates only on its declared side; the undeclared side still clamps to the 64-bit limit.
    [Fact]
    public void AOneSidedRangeSaturatesItsDeclaredSideAndTheUndeclaredSideAtTheStorageLimit() {
        Verify(
            current: 5L,
            max: null,
            min: 0L,
            operand: -100L,
            overflow: StateOverflow.Saturate,
            write: StateWriteKind.Add
        );
        Verify(
            current: long.MaxValue,
            max: null,
            min: 0L,
            operand: 1L,
            overflow: StateOverflow.Saturate,
            write: StateWriteKind.Add
        );
        Verify(
            current: -5L,
            max: 10L,
            min: null,
            operand: 100L,
            overflow: StateOverflow.Saturate,
            write: StateWriteKind.Set
        );
    }
    // A row declaring no envelope at all still refuses a genuine 64-bit arithmetic overflow under Refuse — the
    // deliberate replacement for a silent wrap, not merely the absence of a declared range.
    [InlineData(long.MaxValue, 1L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    [InlineData((long.MinValue + 1L), -2L)]
    [InlineData(long.MinValue, -1L)]
    [Theory]
    public void ARowWithNoBoundsStillRefusesA64BitOverflow(long current, long operand) {
        Verify(
            current: current,
            max: null,
            min: null,
            operand: operand,
            overflow: StateOverflow.Refuse,
            write: StateWriteKind.Add
        );

        var row = Row(
            max: null,
            min: null,
            overflow: StateOverflow.Refuse
        );

        Assert.False(condition: row.TryAdmitWrite(
            current: current,
            operand: operand,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        ));
        Assert.Equal(
            actual: stored,
            expected: 0L
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "overflow"
        );
    }
    // A row with no bounds admits Set at, and Add up to, the exact 64-bit storage limits — the boundary itself is
    // never mistaken for an overflow.
    [InlineData(long.MaxValue, 0L, StateWriteKind.Add)]
    [InlineData(long.MinValue, 0L, StateWriteKind.Add)]
    [InlineData(0L, long.MaxValue, StateWriteKind.Set)]
    [InlineData(0L, long.MinValue, StateWriteKind.Set)]
    [InlineData(0L, long.MaxValue, StateWriteKind.Add)]
    [InlineData(0L, long.MinValue, StateWriteKind.Add)]
    [Theory]
    public void ARowWithNoBoundsAdmitsExactlyAtTheStorageLimits(long current, long operand, StateWriteKind write) {
        Verify(
            current: current,
            max: null,
            min: null,
            operand: operand,
            overflow: StateOverflow.Refuse,
            write: write
        );

        var row = Row(
            max: null,
            min: null,
            overflow: StateOverflow.Refuse
        );

        Assert.True(condition: row.TryAdmitWrite(
            current: current,
            operand: operand,
            write: write,
            stored: out var stored,
            reason: out _
        ));
        Assert.Equal(
            actual: stored,
            expected: ((write == StateWriteKind.Add)
                ? (current + operand)
                : operand)
        );
    }
    // A refusal's reason distinguishes a genuine 64-bit overflow from an ordinary out-of-declared-range result.
    [Fact]
    public void ARefusalNamesOverflowSeparatelyFromAnOrdinaryRangeViolation() {
        var bounded = Row(
            min: 0L,
            max: 100L
        );

        Assert.False(condition: bounded.TryAdmitWrite(
            current: 50L,
            operand: 200L,
            write: StateWriteKind.Set,
            stored: out _,
            reason: out var rangeReason
        ));
        Assert.Contains(
            actualString: rangeReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "envelope"
        );

        var unbounded = Row(
            min: null,
            max: null
        );

        Assert.False(condition: unbounded.TryAdmitWrite(
            current: long.MaxValue,
            operand: 1L,
            write: StateWriteKind.Add,
            stored: out _,
            reason: out var overflowReason
        ));
        Assert.Contains(
            actualString: overflowReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "overflow"
        );
    }
}
