using Xunit;

namespace Puck.State.Tests;

/// <summary>Guards 128-bit boundary arithmetic, saturation, and refusal in <see cref="StateRow.TryAdmitWrite"/> at
/// <see cref="long.MinValue"/> and <see cref="long.MaxValue"/>.</summary>
public sealed class StateBoundsLawTests {
    private static StateRow MakeRow(long? min = null, long? max = null, StateOverflow overflow = StateOverflow.Refuse) =>
        new(
            Name: CellName.Parse(candidate: "boundary"),
            Kind: CellKind.Int,
            Min: min,
            Max: max,
            Overflow: overflow,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 0L))]
        );

    [Fact]
    public void Add_AtMax_WithPositive_RefusesByDefault() {
        var row = MakeRow(max: null, min: null, overflow: StateOverflow.Refuse);
        var admitted = row.TryAdmitWrite(
            current: long.MaxValue,
            operand: 1L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.False(condition: admitted);
        Assert.Equal(actual: stored, expected: 0L);
        Assert.NotEmpty(collection: reason);
    }
    [Fact]
    public void Add_AtMax_WithPositive_SaturatesToMax() {
        var row = MakeRow(max: null, min: null, overflow: StateOverflow.Saturate);
        var admitted = row.TryAdmitWrite(
            current: long.MaxValue,
            operand: 1000L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.True(condition: admitted);
        Assert.Equal(actual: stored, expected: long.MaxValue);
        Assert.Empty(value: reason);
    }
    [Fact]
    public void Add_AtMin_WithNegative_RefusesByDefault() {
        var row = MakeRow(max: null, min: null, overflow: StateOverflow.Refuse);
        var admitted = row.TryAdmitWrite(
            current: long.MinValue,
            operand: -1L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.False(condition: admitted);
        Assert.Equal(actual: stored, expected: 0L);
        Assert.NotEmpty(collection: reason);
    }
    [Fact]
    public void Add_AtMin_WithNegative_SaturatesToMin() {
        var row = MakeRow(max: null, min: null, overflow: StateOverflow.Saturate);
        var admitted = row.TryAdmitWrite(
            current: long.MinValue,
            operand: -9999L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.True(condition: admitted);
        Assert.Equal(actual: stored, expected: long.MinValue);
        Assert.Empty(value: reason);
    }
    [Fact]
    public void Add_MaxToMax_DoesNotOverflowInt128_SaturatesOrRefuses() {
        var saturateRow = MakeRow(overflow: StateOverflow.Saturate);
        var refuseRow = MakeRow(overflow: StateOverflow.Refuse);

        Assert.True(condition: saturateRow.TryAdmitWrite(
            current: long.MaxValue,
            operand: long.MaxValue,
            write: StateWriteKind.Add,
            stored: out var satStored,
            reason: out var satReason
        ));
        Assert.Equal(actual: satStored, expected: long.MaxValue);
        Assert.Empty(value: satReason);

        Assert.False(condition: refuseRow.TryAdmitWrite(
            current: long.MaxValue,
            operand: long.MaxValue,
            write: StateWriteKind.Add,
            stored: out var refStored,
            reason: out var refReason
        ));
        Assert.Equal(actual: refStored, expected: 0L);
        Assert.NotEmpty(collection: refReason);
    }
    [Fact]
    public void Add_MinToMin_DoesNotUnderflowInt128_SaturatesOrRefuses() {
        var saturateRow = MakeRow(overflow: StateOverflow.Saturate);
        var refuseRow = MakeRow(overflow: StateOverflow.Refuse);

        Assert.True(condition: saturateRow.TryAdmitWrite(
            current: long.MinValue,
            operand: long.MinValue,
            write: StateWriteKind.Add,
            stored: out var satStored,
            reason: out var satReason
        ));
        Assert.Equal(actual: satStored, expected: long.MinValue);
        Assert.Empty(value: satReason);

        Assert.False(condition: refuseRow.TryAdmitWrite(
            current: long.MinValue,
            operand: long.MinValue,
            write: StateWriteKind.Add,
            stored: out var refStored,
            reason: out var refReason
        ));
        Assert.Equal(actual: refStored, expected: 0L);
        Assert.NotEmpty(collection: refReason);
    }
    [Fact]
    public void Set_BeyondAuthoredBounds_RefusesOrSaturates() {
        var refuseRow = MakeRow(max: 100L, min: -100L, overflow: StateOverflow.Refuse);
        var saturateRow = MakeRow(max: 100L, min: -100L, overflow: StateOverflow.Saturate);

        Assert.False(condition: refuseRow.TryAdmitWrite(
            current: 0L,
            operand: 101L,
            write: StateWriteKind.Set,
            stored: out var rStoredHigh,
            reason: out var rReasonHigh
        ));
        Assert.Equal(actual: rStoredHigh, expected: 0L);
        Assert.NotEmpty(collection: rReasonHigh);

        Assert.False(condition: refuseRow.TryAdmitWrite(
            current: 0L,
            operand: -101L,
            write: StateWriteKind.Set,
            stored: out var rStoredLow,
            reason: out var rReasonLow
        ));
        Assert.Equal(actual: rStoredLow, expected: 0L);
        Assert.NotEmpty(collection: rReasonLow);

        Assert.True(condition: saturateRow.TryAdmitWrite(
            current: 0L,
            operand: 500L,
            write: StateWriteKind.Set,
            stored: out var sStoredHigh,
            reason: out var sReasonHigh
        ));
        Assert.Equal(actual: sStoredHigh, expected: 100L);
        Assert.Empty(value: sReasonHigh);

        Assert.True(condition: saturateRow.TryAdmitWrite(
            current: 0L,
            operand: -500L,
            write: StateWriteKind.Set,
            stored: out var sStoredLow,
            reason: out var sReasonLow
        ));
        Assert.Equal(actual: sStoredLow, expected: -100L);
        Assert.Empty(value: sReasonLow);
    }
    [Fact]
    public void ExactAuthoredBounds_AdmitsWithoutClamping() {
        var row = MakeRow(max: 50L, min: -50L, overflow: StateOverflow.Refuse);

        Assert.True(condition: row.TryAdmitWrite(
            current: 0L,
            operand: 50L,
            write: StateWriteKind.Set,
            stored: out var s50,
            reason: out var r50
        ));
        Assert.Equal(actual: s50, expected: 50L);
        Assert.Empty(value: r50);

        Assert.True(condition: row.TryAdmitWrite(
            current: 0L,
            operand: -50L,
            write: StateWriteKind.Set,
            stored: out var sNeg50,
            reason: out var rNeg50
        ));
        Assert.Equal(actual: sNeg50, expected: -50L);
        Assert.Empty(value: rNeg50);
    }
}
